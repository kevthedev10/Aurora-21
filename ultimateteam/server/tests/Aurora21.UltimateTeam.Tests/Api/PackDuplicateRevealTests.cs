using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A pack's reveal names the duplicates PUT /item will keep in Unassigned (2026-10-01 report: a mixed store pack called
/// every card new, Store All sent a duplicate to the club, it stayed in Unassigned and the next pack was refused).
/// </summary>
public sealed class PackDuplicateRevealTests
{
    private const string G = "/ut/game/fifa21";
    private const long BayernMunich = 21;

    [Fact]
    public async Task AMixedStorePackRevealListsEveryDuplicateStoreAllWillKeep()
    {
        // The Gold Pack drawing three players from one club's gold cards, so opening it again soon repeats a card the club owns.
        using var first = TestRuntime.Create(marketTarget: 0);
        await first.Game.RunAsync("narrow-gold-pack", work => work.ExecuteAsync("""
            UPDATE ultimateteam.store_packs SET size = 3, rare_slots = 1, allowed_team_ids = ARRAY[@team]::bigint[],
                mixed_contents = '{"tier": "gold", "players": {"min": 3, "max": 3}, "consumables": {"min": 0, "max": 0},
                                   "stadium": {"min": 0, "max": 0}, "staff": {"min": 0, "max": 0}}'
            WHERE pack_id = 74
            """, ("team", BayernMunich)), CancellationToken.None);
        using var rt = TestRuntime.Create(connectionString: first.Detach(), marketTarget: 0);
        var store = rt.Game.Store();
        store.AddCredits(10_000_000);
        var pool = rt.Game.Players.Entries.Count(entry => entry.Value.Int("teamid") == BayernMunich);

        var duplicatesSeen = 0;
        // Each pack without a duplicate adds three owned cards, so within pool / 3 + 1 packs one must repeat a card.
        for (var pack = 0; pack <= pool / 3 + 1 && duplicatesSeen == 0; pack++)
        {
            var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 74, ["currency"] = "COINS" });
            var revealed = DuplicateIds(opened);
            Assert.Equal(DuplicateIds(rt.CallObject("GET", $"{G}/purchased/items")), revealed);
            foreach (var item in opened["itemData"]!.AsArray().OfType<JsonObject>())
            {
                Assert.Equal(revealed.Contains(item.Int("id")), item["duplicate"]?.GetValue<bool>() ?? false);
            }

            // Store All sends the cards FIFA showed as new; nothing may be left behind in Unassigned.
            var fresh = opened["itemData"]!.AsArray().OfType<JsonObject>().Where(item => !revealed.Contains(item.Int("id")))
                .Select(item => (JsonNode?)new JsonObject { ["id"] = item.Int("id"), ["pile"] = "club" }).ToArray();
            rt.Call("PUT", $"{G}/item", new JsonObject { ["itemData"] = new JsonArray(fresh) });
            Assert.Equal(revealed, store.ListItems("unassigned").Select(item => item.Int("id")).Order().ToList());

            duplicatesSeen += revealed.Count;
            rt.Game.InUnit(uow => uow.DeletePileAsync("unassigned"));
        }
        Assert.True(duplicatesSeen > 0, "no pack repeated an owned card");
    }

    private static List<long> DuplicateIds(JsonObject body) =>
        (body["duplicateItemIdList"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(entry => entry.Int("itemId")).Order().ToList();
}
