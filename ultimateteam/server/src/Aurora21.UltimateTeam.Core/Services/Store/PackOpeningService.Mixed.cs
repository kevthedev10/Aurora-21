using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Store;

public sealed partial class PackOpeningService
{
    private sealed record MixedItem(string Category, string Kind, long Resource, long Rating, long Rareflag,
        string Name, long Asset = 0, long Team = 0, long KitType = 0)
    {
        /// <summary>Whether the item takes one of the pack's rare slots: only rare players do.</summary>
        public bool RareSlot => Kind == "player" && Rareflag != 0;

        public static MixedItem From(string category, NonPlayerDrop drop) => new(category, drop.Kind, drop.Resource, drop.Rating, drop.Rareflag,
            drop.Name, drop.Asset, drop.Team, drop.KitType);
    }

    /// <summary>The drop pools each non-player category of a mixed pack draws from (<see cref="MixedPackContents.Categories"/> 1-3).</summary>
    private static readonly string[][] MixedPools = [[], NonPlayerDropTable.Consumables, NonPlayerDropTable.Cosmetics, NonPlayerDropTable.Staff];

    private static IReadOnlyList<MixedPackContents.Capacity> MixedCapacities(JsonObject config, MixedPackContents contents,
        ContentSnapshot snapshot, DateTimeOffset at)
    {
        var draw = GameContext.CaptureCardDraw(snapshot, at, config);
        HashSet<long>? Filter(string key) => config.Arr(key)?.Select(node => JsonValues.Int(node)).ToHashSet();
        var flags = Filter("allowed_rareflags");
        var teams = Filter("allowed_team_ids");
        var nations = Filter("allowed_nations");
        var players = snapshot.Players.Entries.Where(entry => snapshot.Rules.Packs.TierFor(entry.Value.Rating, entry.Value.Tier) == contents.Tier &&
            entry.Value.Rating >= config.Int("min_rating") && entry.Value.Rating <= config.Int("max_rating", Cards.MaxRating) &&
            draw.General(entry.Key) && !snapshot.Cards.IsGrantOnly(entry.Key) &&
            (flags is null || flags.Contains(entry.Value.Rareflag)) &&
            (teams is null || teams.Contains(entry.Value.Int("teamid"))) &&
            (nations is null || nations.Contains(entry.Value.Int("nation")))).ToArray();
        var common = players.Where(entry => entry.Value.Rareflag == 0).Select(entry => entry.Value.Int("assetId")).Distinct().Count();
        var rare = players.Where(entry => entry.Value.Rareflag != 0).Select(entry => entry.Value.Int("assetId")).Distinct().Count();
        return MixedPackContents.Categories.Select(category => category == "players"
            ? new MixedPackContents.Capacity(common, rare)
            // Separate consumable units and non-player duplicates are allowed. Every draw still uses a real definition.
            : new MixedPackContents.Capacity(snapshot.Drops.CanDraw(MixedPools[Array.IndexOf(MixedPackContents.Categories, category)],
                item => item.Tier == contents.Tier) ? contents.Size : 0, 0)).ToArray();
    }

    /// <summary>Used by both pack edits and lineup publication; disabled drafts may await native item data.</summary>
    public static string? MixedAvailability(JsonObject config, ContentSnapshot snapshot, DateTimeOffset at)
    {
        try
        {
            if (MixedPackContents.Read(config) is not { } contents) return null;
            _ = contents.Draw(MixedCapacities(config, contents, snapshot, at), GameRandom.Seeded("mixed-pack-validation"));
            return null;
        }
        catch (InvalidOperationException error) { return error.Message; }
    }

    private JsonArray PreviewMixedPack(JsonObject config, MixedPackContents contents, GameRandom random, ContentSnapshot snapshot, DateTimeOffset at)
    {
        var items = DrawMixedItems(config, contents, random, snapshot, at);
        return new JsonArray(items.Select(item => (JsonNode?)new JsonObject
        {
            ["itemType"] = item.Kind, ["category"] = item.Category, ["resourceId"] = item.Resource,
            ["assetId"] = item.Asset, ["rating"] = item.Rating, ["rareflag"] = item.Rareflag, ["name"] = item.Name, ["rareSlot"] = item.RareSlot,
        }).ToArray());
    }

    private List<MixedItem> DrawMixedItems(JsonObject config, MixedPackContents contents, GameRandom random, ContentSnapshot snapshot, DateTimeOffset at)
    {
        var counts = contents.Draw(MixedCapacities(config, contents, snapshot, at), random);
        var items = new List<MixedItem>();
        if (counts[0].Total > 0)
        {
            var playerConfig = config.Copy();
            playerConfig.Remove("mixed_contents");
            playerConfig["size"] = counts[0].Total;
            playerConfig["rare_slots"] = counts[0].Rare;
            foreach (var tier in new[] { "gold", "silver", "bronze" }) playerConfig[$"{tier}_weight"] = tier == contents.Tier ? 100 : 0;
            var draw = GameContext.CaptureCardDraw(snapshot, at, playerConfig);
            var entries = new PlayerPackRoll(snapshot.Players, snapshot.Cards, snapshot.Rules.Packs, playerConfig,
                available: draw.General, icon: draw.Icon,
                promoSelector: draw.Selector, activePromoIds: draw.ActivePromos, random: random).Roll();
            foreach (var entry in entries)
            {
                if (entry.ForcedRare) throw new InvalidOperationException("Mixed packs cannot manufacture rare players from common definitions.");
                var player = snapshot.Players.Find(entry.ResourceId)!;
                items.Add(new("players", "player", entry.ResourceId, player.Rating, player.Rareflag, player.Text("name"), player.Int("assetId")));
            }
        }
        // Each category blends its pools within the pack: a style, a modifier and a league card before a second style.
        for (var category = 1; category < 4; category++)
            items.AddRange(snapshot.Drops.DrawBlended(random, MixedPools[category], counts[category].Total, item => item.Tier == contents.Tier)
                .Select(drop => MixedItem.From(MixedPackContents.Categories[category], drop)));
        if (items.Count != contents.Size || items.Count(item => item.RareSlot) != contents.Rare)
            throw new InvalidOperationException("The item draw did not satisfy the mixed pack's size and rare count.");
        return items;
    }

    private async ValueTask<JsonObject> CreateMixedPackAsync(IUnitOfWork work, long packId, JsonObject config, MixedPackContents contents)
    {
        var snapshot = Game.Live.Snapshot;
        var drawn = DrawMixedItems(config, contents, Random, snapshot, Game.Time.GetUtcNow());
        drawn = drawn.OrderBy(entry => entry.Kind == "player" ? 1 : 0).ThenBy(entry => entry.Rareflag != 0 ? 1 : 0)
            .ThenBy(entry => entry.Rating).ToList();
        var firstId = await work.ReserveItemIdsAsync(drawn.Count);
        var items = drawn.Select((entry, index) => entry.Kind switch
        {
            "player" => _fut.Items.BuildPlayer(entry.Resource, firstId + index, "unassigned"),
            "manager" => _fut.Items.BuildManager(entry.Resource, firstId + index, "unassigned"),
            "consumable" => _fut.Items.BuildConsumable(entry.Resource, firstId + index, "unassigned"),
            // A kit's category is 2 + its kitType, as every other kit card has it; FIFA draws a blank "KIT" card for the kit card subtype 9.
            "kit" => ItemFactory.KitItem(firstId + index, Game.NowSeconds, entry.Resource, "free", "unassigned", entry.Team, entry.KitType, 2 + entry.KitType, entry.Name),
            _ => ItemFactory.ClubCosmeticItem(entry.Kind, firstId + index, Game.NowSeconds, entry.Resource, entry.Asset, "free", "unassigned", entry.Name,
                entry.Rating, entry.Rareflag, snapshot.Stadiums.Find(entry.Asset)?.Capacity ?? snapshot.Rules.Club.DefaultStadiumCapacity)!,
        }).ToList();
        // Kits' established template has a fixed gold rating. Mixed packs retain their catalogue tier and rarity.
        for (var index = 0; index < items.Count; index++)
        {
            // Catalogue pack contents start tradeable; the owned reward grant applies its stored restriction later.
            ItemFactory.MakeTradeable(items[index]);
            items[index]["rating"] = drawn[index].Rating;
            items[index]["timestamp"] = Game.NowSeconds - (items.Count - 1 - index);
            items[index]["rareflag"] = drawn[index].Rareflag;
            if (items[index].ContainsKey("rareFlag")) items[index]["rareFlag"] = drawn[index].Rareflag;
        }
        MarkDuplicates(items, await ClubInventoryService.OwnedCardIndexAsync(work, items));
        await work.SaveItemsAsync(items);
        return await OpenedPackAsync(work, packId, "CUSTOM_PACK", ItemOrdering.RevealOrder(items));
    }
}
