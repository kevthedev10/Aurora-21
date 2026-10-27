using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Store;

/// <summary>Opens previously earned, pre-rolled packs. No new Match Packs can be generated.</summary>
public sealed class LegacyMatchPackService
{
    public const long PackId = 544;
    private readonly FutServices _fut;
    private readonly ILogger _log;
    public LegacyMatchPackService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.LegacyMatchPack");
    }

    internal static LegacyMatchPackContents FromJson(JsonObject stored)
    {
        static long? Optional(JsonNode? node) => node is not null && JsonValues.TryInt(node, out var value) ? value : null;
        return new LegacyMatchPackContents(stored.Text("tier"), stored.Bool("highlight"), JsonValues.Longs(stored.Get("players")),
            Optional(stored.Get("chemistryStyle")), Optional(stored.Get("positionModifier")), Optional(stored.Get("kit")),
            Optional(stored.Get("managerLeagueCard")));
    }

    /// <summary>
    /// Opens a pack whose contents were rolled at grant (unopened_packs.contents) into Unassigned as the store's reveal reads a
    /// pack: the players (best first), the chemistry style, the modifier (position modifier or manager league card) and the kit,
    /// every item using the stored grant tradeability (its source is applied by the store),
    /// a player the club has come to own since the grant marked a duplicate. A card no longer in the catalogue is skipped.
    /// </summary>
    public async ValueTask<JsonObject> OpenRolledAsync(IUnitOfWork uow, long packId, JsonObject stored, bool untradeable)
    {
        var game = _fut.Game;
        var roll = FromJson(stored);
        var kit = roll.Kit is { } kitId ? game.Kits.Kits.FirstOrDefault(candidate => candidate.Int("resourceId") == kitId) : null;
        var players = roll.Players.Where(game.Players.Contains).ToList();
        var consumables = new[] { roll.ChemistryStyle, roll.PositionModifier, roll.ManagerLeagueCard }.OfType<long>().Where(game.Cards.IsConsumableDefinition).ToList();
        var count = players.Count + consumables.Count + (kit is null ? 0 : 1);
        var items = new List<JsonObject>();
        if (count > 0)
        {
            var next = await uow.ReserveItemIdsAsync(count);
            items.AddRange(players.Select(id => _fut.Items.BuildPlayer(id, next++, "unassigned")));
            items.AddRange(consumables.Select(id => _fut.Items.BuildConsumable(id, next++, "unassigned")));
            if (kit is not null)
            {
                items.Add(ItemFactory.KitItem(next, game.NowSeconds, kit.Int("resourceId"), "free", "unassigned", kit.Int("teamid"), ItemFactory.KitType(kit),
                    2 + kit.Int("kitType"), kit.Text("name", "FIFA 21 Kit")));
            }
        }
        var ownedIndex = players.Count == 0 ? new Dictionary<long, long>() : await _fut.Inventory.OwnedPlayerIndexAsync(uow, players.ToArray());
        var duplicates = new JsonArray();
        foreach (var item in items)
        {
            item["untradeable"] = untradeable;
            if (item.Text("itemType") is "training" or "development" or "consumable")
            {
                item["untradeableCount"] = untradeable ? item.Int("stackCount", 1) : 0;
            }
            if (ClubInventoryService.IsPlayer(item) && ownedIndex.TryGetValue(ClubInventoryService.ResourceOrAsset(item), out var ownedId))
            {
                duplicates.Add(new JsonObject { ["itemId"] = item.Int("id"), ["duplicateItemId"] = ownedId });
                ClubInventoryService.SetDuplicateMarker(item);
            }
        }
        await uow.SaveItemsAsync(items);
        _log.LogInformation("MATCH PACK opened club={Club} pack={Pack} tier={Tier} items={Items}", uow.ClubId, packId, roll.Tier, items.Count);
        return TemplateLibrary.Core.Object("packs/opened", new TemplateArgs
        {
            ["packId"] = packId,
            ["items"] = ClubPresentationService.ToArray(items),
            ["newCards"] = items.Count - duplicates.Count,
            ["numberItems"] = items.Count,
            ["duplicates"] = duplicates,
            ["credits"] = await uow.CreditsAsync(),
        });
    }
}
