using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>Per-card season and lifetime stats from the match end (Python server ca228bc).</summary>
public sealed class MatchItemStatsTests
{
    private const string G = "/ut/game/fifa21";

    private static long Stat(JsonObject item, string list, int index) =>
        item[list]!.AsArray().OfType<JsonObject>().Single(row => row.Int("index") == index).Int("value");

    [Fact]
    public void EveryPlayerWhoTookPartGetsTheMatchOnce()
    {
        using var rt = TestRuntime.Create();
        // The matchday squad FIFA reports: owned players, the XI first.
        var squad = rt.Game.Store().ListItems().Where(item => item.Text("itemType") == "player").Select(item => item.Int("id")).Take(13).ToList();
        Assert.Equal(13, squad.Count);
        // The XI shares one fitness value, the bench another; one substitute came on and assisted, one did not play.
        var items = new JsonArray(squad.Take(11).Select((id, index) => (JsonNode?)new JsonObject
        {
            ["id"] = id, ["fitness"] = 1, ["goals"] = index == 10 ? 2 : 0, ["yellowCards"] = index == 4 ? 1 : 0,
        }).Concat(new JsonNode?[]
        {
            new JsonObject { ["id"] = squad[11], ["fitness"] = 1, ["assists"] = 1 },
            new JsonObject { ["id"] = squad[12], ["fitness"] = 35 },
        }).ToArray());
        JsonObject Item(long id) => rt.Game.Store().ListItems().Single(item => item.Int("id") == id);
        var before = squad.Take(13).ToDictionary(id => id, id => Item(id));

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["matchId"] = 77, ["items"] = items });
        // A replay of the same match end changes nothing.
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["matchId"] = 77, ["items"] = items.DeepClone() });

        foreach (var list in new[] { "statsList", "lifetimeStats" })
        {
            Assert.All(squad.Take(12), id => Assert.Equal(Stat(before[id], list, 0) + 1, Stat(Item(id), list, 0)));
            Assert.Equal(Stat(before[squad[12]], list, 0), Stat(Item(squad[12]), list, 0));
            Assert.Equal(Stat(before[squad[10]], list, 1) + 2, Stat(Item(squad[10]), list, 1));
            Assert.Equal(Stat(before[squad[4]], list, 2) + 1, Stat(Item(squad[4]), list, 2));
        }
        Assert.Equal(before[squad[11]].Int("assists") + 1, Item(squad[11]).Int("assists"));
        Assert.Equal(before[squad[11]].Int("lifetimeAssists") + 1, Item(squad[11]).Int("lifetimeAssists"));

        // A quit never counts.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "QUIT", ["matchId"] = 78, ["items"] = items.DeepClone() });
        Assert.Equal(Stat(before[squad[0]], "statsList", 0) + 1, Stat(Item(squad[0]), "statsList", 0));
    }

    /// <summary>
    /// Retail loyalty: a bought card (two owners, no loyalty bonus) becomes loyal on its tenth game for the club, and keeps
    /// the bonus whatever its stat rows say afterwards.
    /// </summary>
    [Fact]
    public void ABoughtCardBecomesLoyalOnItsTenthGame()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        var squad = store.ListItems().Where(item => item.Text("itemType") == "player").Select(item => item.Int("id")).Take(11).ToList();
        JsonObject Item(long id) => rt.Game.Store().ListItems().Single(item => item.Int("id") == id);
        var bought = Item(squad[0]);
        bought["owners"] = 2;
        bought["loyaltyBonus"] = 0;
        bought["statsList"] = rt.Game.Cards.StatRows(new JsonArray(new JsonObject { ["index"] = 0, ["value"] = 8 }));
        store.SaveItem(bought);
        var items = new JsonArray(squad.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["fitness"] = 1 }).ToArray());

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["matchId"] = 91, ["items"] = items });
        Assert.Equal(9, Stat(Item(squad[0]), "statsList", 0));
        Assert.Equal(0, Item(squad[0]).Int("loyaltyBonus"));

        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "LOSS", ["matchId"] = 92, ["items"] = items.DeepClone() });
        Assert.Equal(10, Stat(Item(squad[0]), "statsList", 0));
        Assert.Equal(1, Item(squad[0]).Int("loyaltyBonus"));
    }
}
