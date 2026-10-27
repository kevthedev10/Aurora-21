using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>Generates pack contents from a pack configuration and stores them in Unassigned.</summary>
public sealed partial class PackOpeningService
{
    private readonly FutServices _fut;
    private readonly ILogger _log;

    public PackOpeningService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Packs");
    }

    /// <summary>How many kits the kit-and-stadium pack holds.</summary>
    private const int KitStadiumPackKits = 3;

    internal GameContext Game => _fut.Game;
    private static GameRandom Random => GameRandom.Shared;

    /// <summary>
    /// Whether a pack draws club items, consumables, managers, stadiums or swap tokens rather than players, mixed items
    /// or picks: the operator panel can neither preview nor edit those generators.
    /// </summary>
    public static bool UsesSpecialistGenerator(JsonObject config) =>
        config.Get("identity_pack") is not null || config.Int("random_consumable_count") > 0 ||
        config.Int("random_manager_count") > 0 || config.Bool("all_consumables") ||
        config.Bool("all_kits") || config.Bool("kit_stadium_pack") || config.Int("set_stadium_id") > 0 ||
        config.Int("swap_token_rareflag") > 0 || VanityPack(config) is not null;

    /// <summary>A My Stadium pack's spec (store_packs.vanity_pack, migration 123), or null; an empty object clears it.</summary>
    public static JsonObject? VanityPack(JsonObject config) => config.Obj("vanity_pack") is { Count: > 0 } spec ? spec : null;

    /// <summary>Draws a standard player pack for the operator panel without creating items or changing a club.</summary>
    public JsonArray PreviewPlayerPack(JsonObject draft, GameRandom? random = null, ContentSnapshot? snapshot = null, DateTimeOffset? at = null, long sourcePackId = 0)
    {
        snapshot ??= Game.Live.Snapshot;
        if (MixedPackContents.Read(draft) is { } mixed)
            return PreviewMixedPack(draft, mixed, random ?? Random, snapshot, at ?? Game.Time.GetUtcNow());
        if (draft.Arr("player_picks") is { Count: > 0 } || draft.Int("player_pick_count") > 0)
            return PreviewPickPack(draft, random, snapshot, at, sourcePackId);
        if (VanityPack(draft) is { } vanity)
            return PreviewVanityPack(vanity, random ?? Random);
        if (UsesSpecialistGenerator(draft))
            throw new NotSupportedException("A contents preview is available for player packs; this pack uses a different item generator.");
        var config = draft.Copy();
        var draw = GameContext.CaptureCardDraw(snapshot, at ?? Game.Time.GetUtcNow(), config);
        var roll = new PlayerPackRoll(snapshot.Players, snapshot.Cards, snapshot.Rules.Packs, config,
            pool: _fut.Pools.Named(config.Text("resource_pool"), snapshot),
            available: AvailabilityForPack(config, draw.General, snapshot), icon: draw.Icon,
            promoSelector: draw.Selector, activePromoIds: draw.ActivePromos, random: random);
        var items = new JsonArray();
        foreach (var entry in roll.Roll())
        {
            var player = snapshot.Players.Find(entry.ResourceId)!;
            items.Add(new JsonObject
            {
                ["itemType"] = "player", ["name"] = player.Text("name"), ["assetId"] = player.Int("assetId"),
                ["resourceId"] = entry.ResourceId, ["rating"] = player.Rating, ["rareflag"] = player.Rareflag,
                ["position"] = player.Position,
            });
        }
        return items;
    }

    /// <summary>A My Stadium pack's draw for a club that owns none of its items, without creating items: the panel preview.</summary>
    private JsonArray PreviewVanityPack(JsonObject spec, GameRandom random)
    {
        var vanity = Game.Rules.Club.Vanity;
        if (vanity.PackIssue(spec) is { } issue) throw new InvalidOperationException(issue);
        return new JsonArray(vanity.DrawPack(spec, new HashSet<long>(), random).Select(item => (JsonNode?)new JsonObject
        {
            ["itemType"] = "vanity", ["name"] = $"{vanity.SubtypeName(item.Subtype)} {item.Asset}",
            ["subtype"] = item.Subtype, ["assetId"] = item.Asset,
        }).ToArray());
    }

    /// <summary>One draw of a configured player pack's cards, without creating items: the odds tests roll packs by the thousand.</summary>
    internal List<long> RollPlayerPack(long packId)
    {
        var config = Game.Packs.GetPackConfig(packId);
        var draw = Game.CaptureCardDrawAt(Game.Time.GetUtcNow(), config);
        return new PlayerPackRoll(Game.Players, Game.Cards, Game.Packs.Rules, config, _fut.Pools.Named(config.Text("resource_pool")),
            AvailabilityForPack(config, draw.General), draw.Icon, draw.Selector, draw.ActivePromos)
            .Roll().Select(entry => entry.ResourceId).ToList();
    }

    /// <summary>
    /// Generates a pack into Unassigned inside the caller's unit of work. Items already in Unassigned stay: clearing the
    /// pile deleted duplicates the player had kept, unpaid (Python server 328a5b1); the store refuses a pack while the pile
    /// holds anything.
    /// </summary>
    public async ValueTask<JsonObject> CreatePackAsync(IUnitOfWork uow, long packId = 1, JsonObject? configOverride = null)
    {
        var config = configOverride ?? Game.Packs.GetPackConfig(packId);
        if (MixedPackContents.Read(config) is { } mixed)
            return await CreateMixedPackAsync(uow, packId, config, mixed);
        if (config.Arr("player_picks") is { Count: > 0 } pickGroups)
        {
            return await CreatePickGroupPackAsync(uow, packId, config, pickGroups);
        }
        if (config.Int("player_pick_count") > 0)
        {
            return await CreatePlayerPickPackAsync(uow, packId, config);
        }
        if (JsonValues.HasValue(config.Get("set_stadium_id")))
        {
            return await ApplyStadiumSelectorAsync(uow, packId, config);
        }
        if (config.Int("random_consumable_count") > 0)
        {
            return await CreateRandomConsumablesPackAsync(uow, packId, (int)config.Int("random_consumable_count"), config.Obj("consumable_mix"));
        }
        if (config.Int("random_manager_count") > 0)
        {
            return await CreateRandomManagersPackAsync(uow, packId, (int)config.Int("random_manager_count"));
        }
        if (config.Obj("identity_pack") is { } identity)
        {
            return await CreateIdentityPackAsync(uow, packId, identity);
        }
        if (config.Int("swap_token_rareflag") is var tokenRareflag and > 0)
        {
            return await CreateSwapTokenPackAsync(uow, packId, tokenRareflag);
        }
        if (VanityPack(config) is { } vanity)
        {
            // My Stadium items are owned on the spot (club_vanity_items); the reveal lists them.
            return await OpenedPackAsync(uow, packId, "vanity", await _fut.Vanity.DrawPackAsync(uow, vanity));
        }
        if (JsonValues.HasValue(config.Get("all_consumables")))
        {
            return await CreateAllConsumablesPackAsync(uow);
        }
        if (JsonValues.HasValue(config.Get("kit_stadium_pack")))
        {
            return await CreateKitStadiumPackAsync(uow, packId);
        }
        if (JsonValues.HasValue(config.Get("all_kits")))
        {
            return await CreateAllKitsPackAsync(uow, config);
        }
        if (config.Count == 0)
        {
            throw new InvalidOperationException($"Unknown pack id {packId}");
        }
        return await CreatePlayerPackAsync(uow, packId, config);
    }

    private Func<long, bool> AvailabilityForPack(JsonObject config, Func<long, bool> general, ContentSnapshot? snapshot = null)
    {
        // Authored TOTW 1–3 rewards keep their named pool even outside a general schedule.
        if (config.Text("resource_pool") != ResourcePools.TotwOneToThree) return general;
        var named = _fut.Pools.Named(ResourcePools.TotwOneToThree, snapshot)!;
        return id => general(id) || named.Contains(id);
    }

    private static JsonObject GeneratedPack(JsonArray items) =>
        TemplateLibrary.Core.Object("packs/generated", new TemplateArgs { ["items"] = items, ["numberItems"] = items.Count });

    private async ValueTask<JsonObject> CreatePlayerPickPackAsync(IUnitOfWork uow, long packId, JsonObject config)
    {
        var group = new JsonObject
        {
            ["min_rating"] = config.Int("min_rating"), ["max_rating"] = config.Int("max_rating", Cards.MaxRating),
            ["choice_count"] = config.Int("player_pick_count", 4), ["promo_only"] = config.Bool("promo_only"),
        };
        var rules = PlayerPickDraw.Configuration(config, group);
        var count = (int)group.Int("choice_count");
        var preview = PlayerPickDraw.Draw(_fut, rules, count, packId)[0];
        var flags = rules.Arr("allowed_rareflags")?.Select(node => JsonValues.Int(node)).ToArray();
        var token = await _fut.Items.MakePlayerPickAsync(uow, group.Int("min_rating"), count, "unassigned", packId, preview,
            group.Bool("promo_only"), flags, maxRating: group.Int("max_rating"), drawConfig: rules);
        return TemplateLibrary.Core.Object("packs/player-pick-opened", new TemplateArgs
        {
            ["packId"] = packId, ["token"] = token, ["credits"] = await uow.CreditsAsync(),
        });
    }

    private JsonArray PreviewPickPack(JsonObject config, GameRandom? random, ContentSnapshot snapshot, DateTimeOffset? at, long sourcePackId)
    {
        var groups = config.Arr("player_picks") ?? new JsonArray(new JsonObject
        {
            ["count"] = 1, ["choice_count"] = config.Int("player_pick_count", 4),
            ["min_rating"] = config.Int("min_rating"), ["max_rating"] = config.Int("max_rating", Cards.MaxRating),
            ["promo_only"] = config.Bool("promo_only"),
        });
        var items = new JsonArray();
        var pick = 0;
        foreach (var group in groups.OfType<JsonObject>())
        {
            var count = (int)group.Int("choice_count", snapshot.Rules.Cards.PlayerPickToken.MinChoices);
            for (var index = 0; index < group.Int("count", 1); index++)
            {
                var choices = PlayerPickDraw.Draw(_fut, PlayerPickDraw.Configuration(config, group), count, sourcePackId, random: random, snapshot: snapshot, at: at);
                foreach (var id in choices)
                {
                    var player = snapshot.Players.Find(id)!;
                    items.Add(new JsonObject { ["itemType"] = "player", ["name"] = player.Text("name"),
                        ["resourceId"] = id, ["assetId"] = player.Int("assetId"), ["rating"] = player.Rating,
                        ["rareflag"] = player.Rareflag, ["pickIndex"] = pick, ["choiceCount"] = count });
                }
                pick++;
            }
        }
        if (config.Int("bonus_player_count") > 0)
            foreach (var id in PlayerPickDraw.Draw(_fut, BonusDrawConfig(config), (int)config.Int("bonus_player_count"), random: random, snapshot: snapshot, at: at))
            {
                var player = snapshot.Players.Find(id)!;
                items.Add(new JsonObject { ["itemType"] = "player", ["name"] = player.Text("name"),
                    ["resourceId"] = id, ["assetId"] = player.Int("assetId"), ["rating"] = player.Rating, ["rareflag"] = player.Rareflag });
            }
        return items;
    }

    private static JsonObject BonusDrawConfig(JsonObject config) => PlayerPickDraw.Configuration(config, new JsonObject
    {
        ["min_rating"] = config.Int("bonus_min_rating"), ["promo_only"] = config.Bool("bonus_promo_only"),
    });

    /// <summary>A pack's items as FIFA's store reveal reads them (the Python server's multi-item pack reply).</summary>
    private async ValueTask<JsonObject> OpenedPackAsync(IUnitOfWork uow, long packId, string packType, IReadOnlyCollection<JsonObject> items) =>
        TemplateLibrary.Core.Object("packs/items-opened", new TemplateArgs
        {
            ["packId"] = packId,
            ["packType"] = packType,
            ["items"] = new JsonArray(items.Select(item => (JsonNode?)item).ToArray()),
            ["count"] = items.Count,
            ["credits"] = await uow.CreditsAsync(),
        });

    /// <summary>
    /// A pack of player pick tokens (store packs 48-65, <c>player_picks</c>): each group adds <c>count</c> tokens of its
    /// rating, choice count and filters, then <c>bonus_player_count</c> unique players rated <c>bonus_min_rating</c> or more.
    /// </summary>
    private async ValueTask<JsonObject> CreatePickGroupPackAsync(IUnitOfWork uow, long packId, JsonObject config, JsonArray groups)
    {
        var tokenRules = Game.Rules.Cards.PlayerPickToken;
        var items = new List<JsonObject>();
        foreach (var node in groups)
        {
            if (node is not JsonObject group) throw new InvalidOperationException($"Pack {packId} has an invalid player pick group");
            var minRating = Math.Clamp(group.Int("min_rating"), 0, Cards.MaxRating);
            var choiceCount = Math.Clamp(group.Int("choice_count", tokenRules.MinChoices), tokenRules.MinChoices, tokenRules.MaxChoices);
            var count = Math.Clamp(group.Int("count", 1), 1, tokenRules.MaxTokensPerGroup);
            var rules = PlayerPickDraw.Configuration(config, group);
            var choices = PlayerPickDraw.Draw(_fut, rules, (int)choiceCount, packId);
            var flags = rules.Arr("allowed_rareflags")?.Select(node => JsonValues.Int(node)).ToArray();
            for (var token = 0; token < count; token++)
                items.Add(await _fut.Items.MakePlayerPickAsync(uow, minRating, choiceCount, "unassigned", packId, Random.Choice(choices),
                    rules.Bool("promo_only"), flags, group.Text("label") is { Length: > 0 } label ? label : null,
                    rules.Int("max_rating", Cards.MaxRating), rules.Text("resource_pool") is { Length: > 0 } pool ? pool : null, rules));
        }
        var bonusCount = (int)Math.Clamp(config.Int("bonus_player_count"), 0, Game.Packs.Rules.MaxPackSize);
        if (bonusCount > 0)
            foreach (var resourceId in PlayerPickDraw.Draw(_fut, BonusDrawConfig(config), bonusCount, packId))
                items.Add(await _fut.Items.MakePlayerAsync(uow, resourceId, "unassigned"));
        return await OpenedPackAsync(uow, packId, "PLAYER_PICK", items);
    }

    /// <summary>
    /// Distinct random consumables (<c>random_consumable_count</c>): position modifiers and chemistry styles, or, when the pack
    /// has a <c>consumable_mix</c> (migration 071), that many of each kind: <c>manager_league</c>, <c>position_modifier</c> and
    /// <c>chemistry_style</c>, adding up to the count. Each is drawn from the non-player drop table.
    /// </summary>
    private async ValueTask<JsonObject> CreateRandomConsumablesPackAsync(IUnitOfWork uow, long packId, int count, JsonObject? mix)
    {
        var drops = Game.Live.Snapshot.Drops;
        var wanted = mix is null
            ? new List<(string[] Pools, int Count, string Label)> { ([NonPlayerDropTable.PositionModifier, NonPlayerDropTable.ChemistryStyle], count, "random consumables") }
            : mix.Select(entry => NonPlayerDropTable.Consumables.Contains(entry.Key)
                ? (Pools: new[] { entry.Key }, Count: (int)JsonValues.Int(entry.Value), Label: $"random {entry.Key} cards")
                : throw new InvalidOperationException($"Pack {packId} consumable_mix names an unknown kind {entry.Key}")).ToList();
        if (wanted.Sum(entry => entry.Count) != count)
        {
            throw new InvalidOperationException($"Pack {packId} consumable_mix adds up to {wanted.Sum(entry => entry.Count)}, not its {count} random consumables");
        }
        var drawn = new List<long>();
        foreach (var (pools, wantedCount, label) in wanted)
        {
            var available = pools.Sum(pool => drops.Pool(pool).Count);
            if (wantedCount < 0 || available < wantedCount)
            {
                throw new InvalidOperationException($"Pack {packId} asks for {wantedCount} {label}; only {available} exist");
            }
            drawn.AddRange(drops.DrawDistinct(Random, pools, wantedCount).Select(drop => drop.Resource));
        }
        var firstItemId = await uow.ReserveItemIdsAsync(count);
        var items = drawn.Select((resourceId, index) => _fut.Items.BuildConsumable(resourceId, firstItemId + index, "unassigned")).ToList();
        await uow.SaveItemsAsync(items);
        return await OpenedPackAsync(uow, packId, "CUSTOM_PACK", items);
    }

    /// <summary>Distinct random managers (<c>random_manager_count</c>), from those with a league, as packs and Draft offer them.</summary>
    private async ValueTask<JsonObject> CreateRandomManagersPackAsync(IUnitOfWork uow, long packId, int count)
    {
        var drops = Game.Live.Snapshot.Drops;
        var available = drops.Pool(NonPlayerDropTable.Manager).Count;
        if (available < count)
        {
            throw new InvalidOperationException($"Pack {packId} asks for {count} random managers; only {available} exist");
        }
        var items = new List<JsonObject>();
        foreach (var resourceId in drops.DrawDistinct(Random, NonPlayerDropTable.Staff, count).Select(drop => drop.Resource))
        {
            items.Add(await _fut.Items.MakeManagerAsync(uow, resourceId, "unassigned"));
        }
        return await OpenedPackAsync(uow, packId, "CUSTOM_PACK", items);
    }

    /// <summary>
    /// The identity pack (pack 66, <c>identity_pack</c>): match balls, club badges, kits and stadium cards, each drawn from the
    /// non-player drop table among those the club does not own yet while enough remain (Python server e2e098f, ceb35f0). The club's own default
    /// cosmetics are never drawn: every club already has them.
    /// </summary>
    private async ValueTask<JsonObject> CreateIdentityPackAsync(IUnitOfWork uow, long packId, JsonObject counts)
    {
        var defaults = Game.Rules.Club.RenderedDefaultActiveItems(uow.ClubId).Select(item => item.Int("resourceId")).ToHashSet();
        var owned = (await uow.QueryAsync("""
            -- query: packs.identity.owned
            SELECT DISTINCT d.native_resource_id FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
            WHERE i.club_id=@club AND i.family_id=4 AND d.native_resource_id IS NOT NULL
            """, reader => reader.GetInt64(0))).ToHashSet();
        var drops = Game.Live.Snapshot.Drops;
        List<NonPlayerDrop> Draw(string pool, string countKey)
        {
            var count = (int)Math.Max(0, counts.Int(countKey));
            bool Usable(NonPlayerDrop item) => !defaults.Contains(item.Resource);
            bool Fresh(NonPlayerDrop item) => Usable(item) && !owned.Contains(item.Resource);
            return drops.DrawDistinct(Random, [pool], count, drops.Pool(pool).Count(Fresh) >= count ? Fresh : Usable);
        }
        var kitsByResource = Game.Kits.Kits.GroupBy(kit => kit.IntKeep("resourceId")).ToDictionary(group => group.Key, group => group.First());
        var kits = Draw(NonPlayerDropTable.Kit, "kits").Select(drop => kitsByResource[drop.Resource]).ToList();
        var chosen = Draw(NonPlayerDropTable.Ball, "balls").Concat(Draw(NonPlayerDropTable.Badge, "badges")).Concat(Draw(NonPlayerDropTable.Stadium, "stadiums")).ToList();
        var total = chosen.Count + kits.Count;
        var items = new List<JsonObject>();
        if (total > 0)
        {
            var firstItemId = await uow.ReserveItemIdsAsync(total);
            var timestamp = Game.NowSeconds;
            foreach (var (entry, index) in chosen.Select((entry, index) => (entry, index)))
            {
                items.Add(ItemFactory.ClubCosmeticItem(entry.Kind, firstItemId + index, timestamp, entry.Resource, entry.Asset, "free", "unassigned",
                    entry.Name, entry.Rating, entry.Rareflag, Game.Stadiums.Find(entry.Asset)?.Capacity ?? Game.Rules.Club.DefaultStadiumCapacity)!);
            }
            foreach (var (kit, index) in kits.Select((kit, index) => (kit, index)))
            {
                items.Add(ItemFactory.KitItem(firstItemId + chosen.Count + index, timestamp, kit.IntKeep("resourceId"), "free", "unassigned",
                    kit.Int("teamid"), ItemFactory.KitType(kit), 2 + kit.Int("kitType"), kit.Text("name")));
            }
            await uow.SaveItemsAsync(items);
        }
        return await OpenedPackAsync(uow, packId, "CUSTOM_PACK", items);
    }

    /// <summary>
    /// An Icon Swaps token pack (packs 560-563, <c>swap_token_rareflag</c>; migration 064): one untradeable token of that
    /// round the club does not hold in any pile, or any of them once it holds all. An SBC squad takes each footballer
    /// once, so a second copy of a held token could never be swapped.
    /// </summary>
    private async ValueTask<JsonObject> CreateSwapTokenPackAsync(IUnitOfWork uow, long packId, long rareflag)
    {
        var tokens = Game.Players.Entries.Where(entry => entry.Value.Rareflag == rareflag).Select(entry => entry.Key).Order().ToList();
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException($"Pack {packId} opens rarity {rareflag} tokens; the catalogue has none");
        }
        var held = (await uow.QueryAsync("""
            -- query: packs.swap-token.held
            SELECT DISTINCT d.native_resource_id FROM ultimateteam.items i JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
            WHERE i.club_id=@club AND d.native_resource_id = ANY(@tokens)
            """, reader => reader.GetInt64(0), ("tokens", tokens.ToArray()))).ToHashSet();
        var fresh = tokens.Where(id => !held.Contains(id)).ToList();
        var token = Random.Choice(fresh.Count > 0 ? fresh : tokens);
        var item = _fut.Items.Restrict(await _fut.Items.MakePlayerAsync(uow, token, "unassigned"));
        await uow.SaveItemAsync(item);
        _log.LogInformation("SWAP TOKEN pack={Pack} rareflag={Rareflag} token={Token} fresh={Fresh}", packId, rareflag, token, fresh.Count);
        return await OpenedPackAsync(uow, packId, "CUSTOM_PACK", new[] { item });
    }

    /// <summary>Stadium-selector pack: opening it sets the club's home venue (the in-game editor crashes).</summary>
    private async ValueTask<JsonObject> ApplyStadiumSelectorAsync(IUnitOfWork uow, long packId, JsonObject config)
    {
        var stadiumId = JsonValues.TryInt(config.Get("set_stadium_id"), out var parsed) ? parsed : Game.Stadiums.DefaultId;
        if (Game.Stadiums.Find(stadiumId) is null)
        {
            stadiumId = Game.Stadiums.DefaultId;
        }
        await uow.SetSelectedStadiumIdAsync(stadiumId);
        _log.LogInformation("Home stadium set via pack {Pack} -> {Stadium} ({Name})", packId, stadiumId, Game.Stadiums.Find(stadiumId)?.Name ?? "?");
        return GeneratedPack(new JsonArray());
    }

    /// <summary>One of every native FIFA 21 position-change card.</summary>
    private async ValueTask<JsonObject> CreateAllConsumablesPackAsync(IUnitOfWork uow)
    {
        var definitions = Game.Cards.PositionModifiers.ToList();
        var items = new JsonArray();
        if (definitions.Count > 0)
        {
            var firstItemId = await uow.ReserveItemIdsAsync(definitions.Count);
            foreach (var (modifier, index) in definitions.Select((modifier, index) => (modifier, index)))
            {
                items.Add(_fut.Items.BuildConsumable(modifier.ResourceId, firstItemId + index, "unassigned"));
            }
            await uow.SaveItemsAsync(items.OfType<JsonObject>());
        }
        return GeneratedPack(items);
    }

    /// <summary>Three kits the club does not own yet (any kits when fewer remain) plus a copy of the active stadium.</summary>
    private async ValueTask<JsonObject> CreateKitStadiumPackAsync(IUnitOfWork uow, long packId)
    {
        if (Game.Kits.Count < KitStadiumPackKits)
        {
            throw new InvalidOperationException("FIFA 21 kit catalog is unavailable");
        }
        var ownedResources = (await uow.ListItemsAsync()).Where(item => item.Text("itemType") == "kit").Select(item => item.Int("resourceId")).ToHashSet();
        var candidates = Game.Kits.Kits.Where(kit => !ownedResources.Contains(kit.Int("resourceId"))).ToList();
        if (candidates.Count < KitStadiumPackKits)
        {
            candidates = Game.Kits.Kits.ToList();
        }
        var items = new List<JsonObject>();
        var selected = Random.Sample(candidates, KitStadiumPackKits);
        var firstItemId = await uow.ReserveItemIdsAsync(selected.Count + 1);
        foreach (var (kit, index) in selected.Select((kit, index) => (kit, index)))
        {
            var itemId = firstItemId + index;
            var kitType = ItemFactory.KitType(kit);
            var item = ItemFactory.KitItem(itemId, Game.NowSeconds, kit.IntKeep("resourceId"), "free", "unassigned",
                kit.Int("teamid"), kitType, 2 + kit.Int("kitType"), kit.Text("name", "FIFA 21 Kit"));
            item.Update(new JsonObject { ["itemId"] = itemId });
            items.Add(item);
        }
        var stadium = (await _fut.Club.ActiveClubItemsAsync(uow)).First(item => item.Text("itemType") == "stadium").Copy();
        var stadiumId = firstItemId + selected.Count;
        stadium.Update(new JsonObject
        {
            ["id"] = stadiumId,
            ["itemId"] = stadiumId,
            ["timestamp"] = Game.NowSeconds,
            ["itemState"] = "free",
            ["pile"] = "unassigned",
            ["islocked"] = 0,
        });
        items.Add(stadium);
        await uow.SaveItemsAsync(items);
        // The item copies, duplicate list and counts are built for this document alone.
        return TemplateLibrary.Core.Object("packs/opened", new TemplateArgs
        {
            AdoptNodes = true,
            ["packId"] = packId,
            ["items"] = ClubPresentationService.ToArray(items),
            ["newCards"] = items.Count,
            ["numberItems"] = items.Count,
            ["duplicates"] = new JsonArray(),
            ["credits"] = await uow.CreditsAsync(),
        });
    }

    private async ValueTask<JsonObject> CreateAllKitsPackAsync(IUnitOfWork uow, JsonObject config)
    {
        if (Game.Kits.Count == 0)
        {
            throw new InvalidOperationException("FIFA 21 kit catalog is unavailable");
        }
        var maxSize = Game.Packs.Rules.MaxKitPackSize;
        var packSize = (int)Math.Clamp(config.Int("size", maxSize), 1, maxSize);
        var ownedResources = (await uow.ListItemsAsync()).Where(item => item.Text("itemType") == "kit").Select(item => item.Int("resourceId")).ToHashSet();
        var candidates = Game.Kits.Kits.Where(kit => !ownedResources.Contains(kit.Int("resourceId"))).ToList();
        if (candidates.Count < packSize)
        {
            candidates = Game.Kits.Kits.ToList();
        }
        var selected = Random.Sample(candidates, Math.Min(packSize, candidates.Count));
        var items = new JsonArray();
        if (selected.Count > 0)
        {
            var firstItemId = await uow.ReserveItemIdsAsync(selected.Count);
            foreach (var (kit, index) in selected.Select((kit, index) => (kit, index)))
            {
                var itemId = firstItemId + index;
                var item = ItemFactory.KitItem(itemId, Game.NowSeconds, kit.IntKeep("resourceId"), "free", "unassigned",
                    kit.Int("teamid"), ItemFactory.KitType(kit), 2 + kit.Int("kitType"), kit.Text("name", "FIFA 21 Kit"));
                items.Add(item);
            }
            await uow.SaveItemsAsync(items.OfType<JsonObject>());
        }
        return GeneratedPack(items);
    }

    private async ValueTask<JsonObject> CreatePlayerPackAsync(IUnitOfWork uow, long packId, JsonObject config)
    {
        var draw = Game.CaptureCardDrawAt(Game.Time.GetUtcNow(), config);
        var roll = new PlayerPackRoll(Game.Players, Game.Cards, Game.Packs.Rules, config, _fut.Pools.Named(config.Text("resource_pool")),
            AvailabilityForPack(config, draw.General), draw.Icon, draw.Selector, draw.ActivePromos);
        if (roll.PoolIgnored)
        {
            _log.LogWarning("PACK POOL {Pool} too small for pack {Pack}; pool ignored", config.Text("resource_pool"), packId);
        }
        var entries = roll.Roll();
        // A pack's manager slot is a manager league card manager_league_card_share of the time (bronze and silver store packs,
        // migration 071); the extra draw happens only for a pack that has a share, so every other pack rolls as before.
        var drops = Game.Live.Snapshot.Drops;
        var leagueCardShare = Math.Clamp(config.Float("manager_league_card_share"), 0.0, 1.0);
        var managerSlot = entries.Count > 0 && Random.NextDouble() < roll.ManagerChance;
        if (managerSlot && leagueCardShare > 0 && Random.NextDouble() < leagueCardShare && drops.Draw(Random, [NonPlayerDropTable.ManagerLeague]) is { } leagueCard)
        {
            entries[^1] = new PackEntry(PackEntryKind.ManagerLeagueCard, leagueCard.Resource, ForcedRare: false);
        }
        else if (managerSlot && drops.Draw(Random, NonPlayerDropTable.Staff, manager => roll.EnabledTiers.Contains(manager.Tier)) is { } manager)
        {
            // Replace only the lowest-priority common player.
            entries[^1] = new PackEntry(PackEntryKind.Manager, manager.Resource, ForcedRare: false);
        }
        // Chemistry styles drop the same way, into the lowest remaining common slot, so they never
        // displace a guaranteed rare or special.
        var commonSlots = Enumerable.Range(roll.RareSlots, Math.Max(0, entries.Count - roll.RareSlots))
            .Where(index => entries[index].Kind == PackEntryKind.Player)
            .ToList();
        var chemistryChance = Math.Clamp(config.Float("chem_style_chance"), 0.0, 1.0);
        if (commonSlots.Count > 0 && Random.NextDouble() < chemistryChance && drops.Draw(Random, [NonPlayerDropTable.ChemistryStyle]) is { } style)
        {
            entries[commonSlots[^1]] = new PackEntry(PackEntryKind.ChemistryStyle, style.Resource, ForcedRare: false);
        }
        // FIFA 21 PC can rebuild New Items from item metadata instead of the array order, so the
        // reveal order is encoded into ids and timestamps too: items are created worst to best,
        // giving the best card the highest id and the newest timestamp.
        var items = new List<JsonObject>();
        if (entries.Count > 0)
        {
            var firstItemId = await uow.ReserveItemIdsAsync(entries.Count);
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                var itemId = firstItemId + items.Count;
                var item = entry.Kind switch
                {
                    PackEntryKind.Manager => _fut.Items.BuildManager(entry.ResourceId, itemId, "unassigned"),
                    PackEntryKind.ChemistryStyle or PackEntryKind.ManagerLeagueCard => _fut.Items.BuildConsumable(entry.ResourceId, itemId, "unassigned"),
                    _ => _fut.Items.BuildPlayer(entry.ResourceId, itemId, "unassigned"),
                };
                if (entry.ForcedRare)
                {
                    item["rareflag"] = 1;
                }
                items.Add(item);
            }
            items.Reverse();
            var baseTimestamp = Game.NowSeconds;
            for (var rank = 0; rank < items.Count; rank++)
            {
                items[rank]["timestamp"] = baseTimestamp - rank;
            }
        }

        // The pack's items are not saved yet, so the index holds only the copies the club owned before it, read for this
        // pack's own cards instead of every owned card in the club.
        var ownedBefore = await ClubInventoryService.OwnedCardIndexAsync(uow, items);
        var (duplicateList, duplicateCount) = MarkDuplicates(items, ownedBefore);
        await uow.SaveItemsAsync(items);
        if (duplicateCount > 0)
        {
            var refreshed = (await uow.GetItemsAsync(items.Select(item => item.IntKeep("id")).ToArray())).ToDictionary(item => item.Int("id"));
            items = items.Select(item => refreshed.GetValueOrDefault(item.Int("id")) ?? item).ToList();
        }
        items = ItemOrdering.RevealOrder(items);

        _log.LogInformation(
            "CUSTOM PACK id={Pack} name={Name} size={Size} rareSlots={Rare} minRating={Min} guaranteedSpecials={Guaranteed} specialChance={Special:0.000}% managerChance={Manager:0.0}%",
            packId, config.Text("name", "None"), roll.PackSize, roll.RareSlots, roll.MinRating, roll.GuaranteedSpecials, roll.SpecialChance * 100, roll.ManagerChance * 100);
        if (items.Count > 0)
        {
            _log.LogInformation("PACK HEADLINE CANDIDATE id={Id} resource={Resource} rating={Rating} rareflag={Rareflag} discardValue={Discard}",
                items[0].Get("id"), items[0].Get("resourceId"), items[0].Get("rating"), items[0].Get("rareflag"), items[0].Get("discardValue"));
        }

        return TemplateLibrary.Core.Object("packs/opened", new TemplateArgs
        {
            ["packId"] = packId,
            ["items"] = ClubPresentationService.ToArray(items),
            ["newCards"] = items.Count - duplicateCount,
            ["numberItems"] = items.Count,
            ["duplicates"] = duplicateList,
            ["credits"] = await uow.CreditsAsync(),
        });
    }

    /// <summary>Flags new player cards the club already owns and lists them with the owned item they duplicate.</summary>
    private static (JsonArray List, int Count) MarkDuplicates(List<JsonObject> items, Dictionary<(string Kind, long Resource), long> ownedBefore)
    {
        var duplicates = items.Where(item => ClubInventoryService.IsUniqueCard(item) && ownedBefore.ContainsKey(ClubInventoryService.CardKey(item))).ToList();
        var list = new JsonArray();
        foreach (var item in duplicates)
        {
            var ownedId = ownedBefore.GetValueOrDefault(ClubInventoryService.CardKey(item));
            list.Add(ownedId != 0
                ? new JsonObject { ["itemId"] = item.Int("id"), ["duplicateItemId"] = ownedId }
                : new JsonObject { ["itemId"] = item.Int("id") });
        }
        foreach (var item in duplicates)
        {
            ClubInventoryService.SetDuplicateMarker(item);
        }
        return (list, duplicates.Count);
    }

    private enum PackEntryKind
    {
        Player,
        Manager,
        ChemistryStyle,
        ManagerLeagueCard,
    }

    private readonly record struct PackEntry(PackEntryKind Kind, long ResourceId, bool ForcedRare);

    /// <summary>
    /// The random draw for one player pack (pack odds v3, docs/pack-odds-v3.md): guaranteed rare slots (some forced special)
    /// and common slots, from tier pools built once per pack. A rare slot chooses its tier, then one row of the pack's odds
    /// table (Icon, TOTW, another special category, upgraded specials, or the standard rest), then a saved bucket and a
    /// card. A row with no eligible card falls back to a standard rare. The sequence of random calls is part of the pack
    /// statistics, so steps must keep their order.
    /// </summary>
    private sealed class PlayerPackRoll
    {
        private static readonly string[] TierNames = { "gold", "silver", "bronze" };

        private readonly PlayerCatalog _players;
        private readonly Cards _cards;
        private readonly Func<long, bool> _available;
        private readonly Func<long, bool> _icon;
        private readonly double _iconChance;
        private readonly double _upgradedChance;
        private readonly PromoCardSelector? _promoSelector;
        private readonly IReadOnlySet<long>? _activePromoIds;
        private readonly bool _namedTotwOneToThree;
        private readonly List<(string Category, double Chance)> _categoryChances = new();
        private readonly List<long> _icons = new();
        private readonly List<(string Tier, double Weight)> _tierWeights;
        private readonly Dictionary<string, double> _forcedCategoryWeights = new(StringComparer.Ordinal);
        private static bool IsStandardCategory(string category) => category is
            "gold_common" or "gold_rare" or "silver_common" or "silver_rare" or "bronze_common" or "bronze_rare";
        private readonly Dictionary<string, TierPool> _pools;
        private readonly HashSet<long> _used = new();

        // One version per player: every rarity of a player shares its base asset id.
        private readonly HashSet<long> _usedPlayers = new();
        private readonly GameRandom _random;
        private GameRandom Random => _random;

        public PlayerPackRoll(PlayerCatalog players, Cards cards, PackRules rules, JsonObject config, IReadOnlySet<long>? pool = null,
            Func<long, bool>? available = null, Func<long, bool>? icon = null,
            PromoCardSelector? promoSelector = null, IReadOnlySet<long>? activePromoIds = null, GameRandom? random = null)
        {
            _random = random ?? GameRandom.Shared;
            _players = players;
            _cards = cards;
            _rules = rules;
            _available = available ?? (_ => true);
            _icon = icon ?? (_ => false);
            _promoSelector = promoSelector;
            _activePromoIds = activePromoIds;
            _namedTotwOneToThree = config.Text("resource_pool") == ResourcePools.TotwOneToThree;
            PackSize = (int)Math.Clamp(config.IntKeep("size", 12), 1, rules.MaxPackSize);
            RareSlots = (int)Math.Clamp(config.IntKeep("rare_slots", 3), 0, PackSize);
            MinRating = Math.Clamp(config.Int("min_rating"), 0, Cards.MaxRating);
            MaxRating = Math.Clamp(config.Int("max_rating", Cards.MaxRating), MinRating, Cards.MaxRating);
            GuaranteedSpecials = (int)Math.Clamp(config.Int("guaranteed_specials"), 0, RareSlots);
            // The pack's own odds table is the only source of special odds; a category it leaves out has no chance.
            foreach (var (category, value) in config.Obj("category_chances") ?? new JsonObject())
            {
                if (IsStandardCategory(category) || !double.TryParse(value?.ToJsonString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
                    throw new InvalidOperationException($"Invalid pack category chance for {category}");
                if (category == "icon") _iconChance = probability;
                else if (category == "totw") SpecialChance = probability;
                else if (category == PackOddsRules.UpgradedCategory) _upgradedChance = probability;
                else _categoryChances.Add((category, probability));
                _forcedCategoryWeights.Add(category, probability);
            }
            if (_iconChance + SpecialChance + _upgradedChance + _categoryChances.Sum(entry => entry.Chance) > 1.0000001)
                throw new InvalidOperationException("Pack category chances exceed 100% of a rare slot");
            ManagerChance = Math.Clamp(config.Float("manager_chance"), 0.0, 1.0);
            PromoOnly = config.Bool("promo_only");
            if (PromoOnly && RareSlots != PackSize)
                throw new InvalidOperationException("A promo-only player pack must make every player slot special");

            _tierWeights = TierNames.Select(tier => (tier, Math.Max(0.0, config.Float($"{tier}_weight")))).ToList();
            if (_tierWeights.Sum(entry => entry.Weight) <= 0)
            {
                _tierWeights = new List<(string, double)> { ("gold", 100.0), ("silver", 0.0), ("bronze", 0.0) };
            }
            HashSet<long>? AllowedIds(string key) => config.Arr(key)?
                .Select(node => JsonValues.TryInt(node, out var value) ? (long?)value : null)
                .OfType<long>()
                .ToHashSet();
            // A named TOTW reward pool limits special slots; ordinary Gold Rare cards fill its other slots.
            PoolIgnored = pool is not null && pool.Count < GuaranteedSpecials;
            _pools = BuildPools(rules, AllowedIds("allowed_rareflags"), AllowedIds("allowed_team_ids"), AllowedIds("allowed_nations"), PoolIgnored ? null : pool);
            // The minimum rating of each rare slot after the guaranteed specials (rated_rare_slots: pack 405's 84+ slots).
            _slotFloors = new long[RareSlots];
            var slot = GuaranteedSpecials;
            foreach (var group in (config.Arr("rated_rare_slots") ?? new JsonArray()).OfType<JsonObject>())
            {
                for (var count = 0; count < group.Int("count") && slot < RareSlots; count++)
                {
                    _slotFloors[slot++] = Math.Clamp(group.Int("min_rating"), 0, Cards.MaxRating);
                }
            }
        }

        private readonly PackRules _rules;
        private readonly long[] _slotFloors;


        public int PackSize { get; }

        /// <summary>Whether a named pool was too small for the pack and so ignored.</summary>
        public bool PoolIgnored { get; }
        public int RareSlots { get; }
        public long MinRating { get; }
        public long MaxRating { get; }
        public int GuaranteedSpecials { get; }
        public double SpecialChance { get; }
        public double ManagerChance { get; }

        /// <summary>Specials only: every rare slot draws a special, and no common or rare card is in any pool.</summary>
        public bool PromoOnly { get; }

        public IReadOnlySet<string> EnabledTiers => _tierWeights.Where(entry => entry.Weight > 0).Select(entry => entry.Tier).ToHashSet();

        /// <summary>Draws the pack's cards, ordered for the reveal (specials first, then rating, rareflag, id).</summary>
        public List<PackEntry> Roll()
        {
            var selected = new List<PackEntry>();
            for (var slot = 0; slot < RareSlots; slot++)
            {
                var id = RollRareSlot(mustBeSpecial: slot < GuaranteedSpecials, _slotFloors[slot]);
                selected.Add(new PackEntry(PackEntryKind.Player, id, ForcedRare: _players.Find(id)!.Rareflag == 0));
            }
            for (var slot = 0; slot < PackSize - RareSlots; slot++)
            {
                var tier = ChooseTier();
                var id = PopCommon(tier)
                         ?? OtherEnabledTiers(tier).Select(PopCommon).FirstOrDefault(found => found is not null)
                         ?? throw new InvalidOperationException("Configured common pool is empty");
                selected.Add(new PackEntry(PackEntryKind.Player, id, ForcedRare: false));
            }
            return selected
                .OrderBy(entry => _players.Find(entry.ResourceId)!.IsSpecial ? 0 : 1)
                .ThenBy(entry => -_players.Find(entry.ResourceId)!.Rating)
                .ThenBy(entry => -_players.Find(entry.ResourceId)!.Rareflag)
                .ThenBy(entry => -entry.ResourceId)
                .ToList();
        }

        private long RollRareSlot(bool mustBeSpecial, long floor = 0)
        {
            var tier = ChooseTier();
            var forcedSpecial = PromoOnly || mustBeSpecial;
            long? Special(string from) => ChooseSpecial(from, floor);
            long? id;
            if (forcedSpecial)
            {
                id = ChooseForcedSpecial(tier, floor)
                    ?? OtherEnabledTiers(tier).Select(other => ChooseForcedSpecial(other, floor)).FirstOrDefault(found => found is not null)
                    ?? throw new InvalidOperationException("Configured forced special categories cannot fill this player slot");
                return id.Value;
            }
            else
            {
                var iconChance = tier == "gold" && _icons.Any(icon => !Taken(icon) && _players.Find(icon)!.Rating >= floor)
                    ? _iconChance : 0;
                var categoryRoll = Random.NextDouble();
                id = categoryRoll < iconChance ? ChooseIcon(floor)
                    : categoryRoll < iconChance + SpecialChance ? Special(tier) : null;
                if (categoryRoll >= iconChance + SpecialChance)
                {
                    var threshold = iconChance + SpecialChance;
                    var chosen = false;
                    foreach (var (category, probability) in _categoryChances)
                    {
                        threshold += probability;
                        if (categoryRoll < threshold)
                        {
                            id = ChooseConfiguredCategory(category, tier, floor);
                            chosen = true;
                            break;
                        }
                    }
                    // Upgraded specials exist only in silver and bronze slots; elsewhere the row falls back to a standard rare.
                    if (!chosen && categoryRoll < threshold + _upgradedChance) id = ChooseUpgraded(tier, floor);
                }
            }
            id ??= PopRare(tier, floor);
            id ??= Special(tier);
            // Fall back only to other enabled tiers, never hidden ones.
            id ??= OtherEnabledTiers(tier)
                .Select(other => PopRare(other, floor) is { } rare && rare != 0 ? rare : Special(other))
                .FirstOrDefault(found => found is not null);
            return id ?? throw new InvalidOperationException("Configured rare/special pool is empty");
        }

        private long? ChooseIcon(long floor)
        {
            var eligible = _icons.Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToList();
            if (eligible.Count == 0) return null;
            var chosen = _promoSelector?.Pick("icon", eligible.ToHashSet(), Random, activePromoIds: _activePromoIds)
                ?? Random.Choice(eligible);
            Take(chosen);
            _icons.Remove(chosen);
            return chosen;
        }

        private long? ChooseConfiguredCategory(string category, string tier, long floor)
        {
            var pool = _pools[tier].Scheduled;
            var eligible = pool.Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToHashSet();
            return _promoSelector?.Pick(category, eligible, Random, activePromoIds: _activePromoIds) is { } chosen
                ? Took(pool, chosen) : null;
        }

        /// <summary>A random eligible upgraded special for a silver or bronze slot: rated above the tier, base card in it.</summary>
        private long? ChooseUpgraded(string tier, long floor)
        {
            var pool = _pools[tier].Upgraded;
            var eligible = pool.Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToList();
            return eligible.Count == 0 ? null : Took(pool, Random.Choice(eligible));
        }

        private long? PopCommon(string tier)
        {
            var pool = _pools[tier].Common;
            if (_promoSelector is null) return PopUnique(pool);
            var eligible = pool.Where(id => !Taken(id)).ToHashSet();
            return _promoSelector.Pick($"{tier}_common", eligible, Random, activePromoIds: _activePromoIds) is { } chosen ? Took(pool, chosen) : null;
        }

        /// <summary>A rare of the tier rated at least <paramref name="floor"/>, selected from saved buckets.</summary>
        private long? PopRare(string tier, long floor)
        {
            var pool = _pools[tier].Rare;
            if (_promoSelector is not null)
            {
                var eligible = pool.Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToHashSet();
                return _promoSelector.Pick($"{tier}_rare", eligible, Random, activePromoIds: _activePromoIds) is { } chosen
                    ? Took(pool, chosen) : null;
            }
            if (tier != "gold")
            {
                return floor > 0
                    ? PickWeighted(pool.Where(candidate => !Taken(candidate) && _players.Find(candidate)!.Rating >= floor)
                        .Select(candidate => (candidate, _cards.PackWeight(candidate)))) is { } chosen ? Took(pool, chosen) : null
                    : PopWeighted(pool);
            }
            return null;
        }

        private long Took(List<long> pool, long id)
        {
            Take(id);
            pool.Remove(id);
            return id;
        }

        private Dictionary<string, TierPool> BuildPools(PackRules rules, IReadOnlySet<long>? allowedRareflags, IReadOnlySet<long>? allowedTeams, IReadOnlySet<long>? allowedNations,
            IReadOnlySet<long>? allowedResources)
        {
            var pools = TierNames.ToDictionary(tier => tier, _ => new TierPool());
            foreach (var (id, meta) in _players.Entries)
            {
                if (!_available(id)) continue;
                if (_cards.Rules.FutChampionsRedRareflags.Contains(meta.Rareflag) && rules.TierFor(meta.Rating) != "gold") continue;
                if (_icon(id))
                {
                    if (meta.Rating >= MinRating && meta.Rating <= MaxRating && rules.TierFor(meta.Rating, meta.Tier) == "gold"
                        && (allowedRareflags is null || allowedRareflags.Contains(meta.Rareflag))
                        && (allowedResources is null || allowedResources.Contains(id))
                        && (allowedTeams is null || allowedTeams.Contains(meta.Int("teamid")))
                        && (allowedNations is null || allowedNations.Contains(meta.Int("nation")))
                        && !_cards.IsGrantOnly(id))
                        _icons.Add(id);
                    continue;
                }
                // Standard cards, scheduled TOTW cards and other scheduled categories obey source filters.
                var inform = (_namedTotwOneToThree && allowedResources?.Contains(id) == true)
                    || (_promoSelector?.Contains("totw", id, _activePromoIds) == true);
                var isBase = meta.Rating > 0 && !meta.Special && meta.Rareflag is 0 or 1 && !_cards.IsGrantOnly(id) && !_cards.IsAquaPro(id);
                var scheduledSpecial = !inform && !isBase && (meta.Special || meta.Rareflag > 1);
                if (meta.Rating < MinRating || meta.Rating > MaxRating || !(inform || isBase || scheduledSpecial)
                    || (allowedResources is not null && !(_namedTotwOneToThree && isBase) && !allowedResources.Contains(id))
                    || (PromoOnly && isBase)
                    || ((allowedRareflags is not null && !allowedRareflags.Contains(meta.Rareflag))
                        || (allowedTeams is not null && !allowedTeams.Contains(meta.Int("teamid")))
                        || (allowedNations is not null && !allowedNations.Contains(meta.Int("nation")))
                        // A reward-only rarity (red picks) only when the pack names it.
                        || !_cards.RarityAllowed(meta.Rareflag, allowedRareflags) || _cards.IsGrantOnly(id)))
                {
                    continue;
                }
                var cardTier = rules.TierFor(meta.Rating, meta.Tier);
                if (!isBase && UpgradedTier(meta, cardTier) is { } baseTier)
                {
                    pools[baseTier].Upgraded.Add(id);
                }
                if (scheduledSpecial)
                {
                    pools[cardTier].Scheduled.Add(id);
                    continue;
                }
                (inform ? pools[cardTier].Special
                    : meta.Rareflag == 1 ? pools[cardTier].Rare
                    : pools[cardTier].Common).Add(id);
            }
            foreach (var tier in TierNames)
            {
                Random.Shuffle(pools[tier].Common);
                Random.Shuffle(pools[tier].Rare);
                Random.Shuffle(pools[tier].Special);
            }
            return pools;
        }

        /// <summary>
        /// The lower tier a special also drops in as an upgraded special: its base card's tier (same asset id), when that is
        /// below the special's own tier. Null for a special without a base card or one in its own tier.
        /// </summary>
        private string? UpgradedTier(PlayerMeta special, string ownTier)
        {
            if (_players.BaseRating(special.Int("assetId")) is not { } baseRating) return null;
            var baseTier = _rules.TierFor(baseRating);
            return Array.IndexOf(TierNames, baseTier) > Array.IndexOf(TierNames, ownTier) ? baseTier : null;
        }

        private string ChooseTier() =>
            PickWeighted(_tierWeights.Select((entry, index) => (index, entry.Weight))) is { } index ? _tierWeights[index].Tier : "gold";

        private IEnumerable<string> OtherEnabledTiers(string tier) =>
            _tierWeights.Where(entry => entry.Weight > 0 && entry.Tier != tier).Select(entry => entry.Tier);

        private long PlayerKey(long resourceId) => _players.Find(resourceId)?.IntOr("assetId", resourceId, resourceId) ?? resourceId;

        private bool Taken(long resourceId) => _used.Contains(resourceId) || _usedPlayers.Contains(PlayerKey(resourceId));

        private void Take(long resourceId)
        {
            _used.Add(resourceId);
            _usedPlayers.Add(PlayerKey(resourceId));
        }

        private long? PopUnique(List<long> pool)
        {
            while (pool.Count > 0)
            {
                var id = pool[^1];
                pool.RemoveAt(pool.Count - 1);
                if (!Taken(id))
                {
                    Take(id);
                    return id;
                }
            }
            return null;
        }

        private long? PopWeighted(List<long> pool)
        {
            var id = PickWeighted(pool.Where(candidate => !Taken(candidate)).Select(candidate => (candidate, _cards.PackWeight(candidate))));
            if (id is not null)
            {
                Take(id.Value);
                pool.Remove(id.Value);
            }
            return id;
        }

        /// <summary>A forced special chooses an eligible category, promo, bucket and uniform member.</summary>
        private long? ChooseForcedSpecial(string tier, long floor)
        {
            if (_promoSelector is null) return null;
            var eligible = _pools[tier].Special.Concat(_pools[tier].Scheduled)
                .Concat(tier == "gold" ? _icons : Enumerable.Empty<long>())
                .Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToHashSet();
            var activePromos = _namedTotwOneToThree ? null : _activePromoIds;
            var categories = _promoSelector.EligibleCategories(eligible, activePromos, includePromoCodes: _forcedCategoryWeights.Count > 0)
                .Where(category => !IsStandardCategory(category)).ToHashSet();
            var weights = _namedTotwOneToThree
                ? new Dictionary<string, double> { ["totw"] = 1 }
                : _forcedCategoryWeights.Count > 0 ? _forcedCategoryWeights
                : categories.ToDictionary(category => category, _ => 1.0);
            // Forced slots use the odds table's special rows rescaled, including upgraded specials in silver and bronze slots.
            var upgraded = !_namedTotwOneToThree && _pools[tier].Upgraded.Any(id => !Taken(id) && _players.Find(id)!.Rating >= floor);
            var options = weights.Where(entry => entry.Value > 0 && (categories.Contains(entry.Key)
                || upgraded && entry.Key == PackOddsRules.UpgradedCategory)).ToArray();
            if (options.Length == 0) return null;
            var category = Random.WeightedChoice(options, options.Select(entry => entry.Value).ToList()).Key;
            if (category == PackOddsRules.UpgradedCategory) return ChooseUpgraded(tier, floor);
            var chosen = _promoSelector.Pick(category, eligible, Random, activePromoIds: activePromos);
            if (chosen is not null) Take(chosen.Value);
            return chosen;
        }

        /// <summary>A TOTW card of the tier from its saved buckets (the odds table's TOTW row).</summary>
        private long? ChooseSpecial(string tier, long floor = 0)
        {
            var pool = _pools[tier].Special;
            var eligible = pool.Where(id => !Taken(id) && _players.Find(id)!.Rating >= floor).ToHashSet();
            return _promoSelector?.Pick("totw", eligible, Random,
                activePromoIds: _namedTotwOneToThree ? null : _activePromoIds) is { } chosen
                ? Took(pool, chosen) : null;
        }

        /// <summary>A weighted pick among positive weights (one random draw), or null when none is positive.</summary>
        private T? PickWeighted<T>(IEnumerable<(T Value, double Weight)> options) where T : struct
        {
            var positive = options.Where(option => option.Weight > 0).ToList();
            if (positive.Count == 0)
            {
                return null;
            }
            return Random.WeightedChoice(positive.Select(option => option.Value).ToList(), positive.Select(option => option.Weight).ToList());
        }

        private sealed class TierPool
        {
            public List<long> Common { get; } = new();
            public List<long> Rare { get; } = new();
            public List<long> Special { get; } = new();
            public List<long> Scheduled { get; } = new();

            /// <summary>Specials rated above this tier whose base card is in it (the odds table's upgraded row).</summary>
            public List<long> Upgraded { get; } = new();
        }
    }
}
