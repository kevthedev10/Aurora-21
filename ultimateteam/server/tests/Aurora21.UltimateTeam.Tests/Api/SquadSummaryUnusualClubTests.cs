using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Services.Store;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A club whose owned players carry a stored wire representation takes the "unusual" presentation path, which
/// loads metadata for every player. GET /user then summarises every saved squad slot, so every slot of every
/// squad has to be fetched, not only the active squad's.
/// </summary>
public sealed class SquadSummaryUnusualClubTests
{
    private const string G = "/ut/game/fifa21";
    private const long Tuesday = 1_790_683_200;

    private static readonly RewardDecision Paid = new(RewardVerdict.Paid, RewardModes.Rivals, 0, true, true);

    private static JsonObject Squad(long id, bool active, params long[] players)
    {
        var squad = new JsonObject
        {
            ["id"] = id, ["squadId"] = id, ["name"] = "Squad " + id, ["squadName"] = "Squad " + id, ["formation"] = "f442",
            ["players"] = new JsonArray(players.Select((p, i) => (JsonNode?)new JsonObject { ["index"] = i, ["itemId"] = p, ["kitNumber"] = i + 1 }).ToArray()),
            ["kicktakers"] = new JsonArray(),
        };
        if (active) squad["active"] = true;
        return squad;
    }

    /// <summary>Saves two squads: the active one with starters only, the other with starters and subs.</summary>
    private static (long[] Active, long[] Other) TwoSquads(TestRuntime rt)
    {
        var store = rt.Game.Store();
        var resources = rt.Game.Players.Entries.Where(entry => entry.Key <= 0x00FFFFFF).Select(entry => entry.Key).Skip(200).Take(20).ToArray();
        long Grant(long resource)
        {
            var item = rt.Fut.Items.BuildPlayer(resource, store.NextItemId(), "club");
            store.SaveItem(item);
            return item.Int("id");
        }
        var ids = resources.Select(Grant).ToArray();
        var active = ids.Take(3).ToArray();
        var other = ids.Skip(3).Take(15).ToArray();
        rt.Fut.Inventory.SaveSquadAsync(Squad(1, true, active)).AsTask().GetAwaiter().GetResult();
        rt.Fut.Inventory.SaveSquadAsync(Squad(2, false, other)).AsTask().GetAwaiter().GetResult();
        return (active, other);
    }

    private static void AssertEverySquadSummarised(TestRuntime rt)
    {
        var user = rt.CallObject("GET", $"{G}/user");
        var summaries = user.Obj("squadList")!.Arr("squad")!.OfType<JsonObject>().ToList();
        Assert.Equal(2, summaries.Count);
    }

    [Fact]
    public void UserSurvivesAnUntradeablePickMovedToTheClubWhileAnotherSquadHoldsSubs()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        TwoSquads(rt);
        AssertEverySquadSummarised(rt);

        // The Kick Off welcome pick: pack 513 from an objective, opened, one choice selected, then moved to the club.
        rt.Game.Store().DeletePile("unassigned");
        rt.Game.InUnit(uow => uow.AddRewardPackAsync(rt.Game.Rules.Tradeability, 513, RewardSources.Objective));
        rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 513 });
        var token = rt.Game.Store().ListItems("unassigned").Single(item => item.Int("minRating") == 82);
        // A choice the club does not own yet, so it is not held back in Unassigned as a duplicate.
        var choices = rt.Fut.Picks.Redeem(token.Int("id")).OfType<JsonObject>().ToList();
        var fresh = choices.FindIndex(choice => !choice.Bool("duplicate"));
        var picked = rt.Fut.Picks.Select(token.Int("id"), JsonValue.Create(Math.Max(0, fresh)));
        rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(new JsonObject { ["id"] = picked.Int("id"), ["pile"] = "club" }),
        });
        // (When every random choice duplicates an owned player the pick stays in Unassigned, which is fine here.)

        // A chosen player is a plain player: no stored extension of any kind (it carried untradeableCount and duplicate=false).
        Assert.Equal(0L, ExtensionRows(rt));
        AssertEverySquadSummarised(rt);
    }

    [Fact]
    public void AnEmptyStoredRepresentationDoesNotMakeTheClubUnusualAndUserStillWorks()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var (active, _) = TwoSquads(rt);
        // The rows live club 8 holds today: unknown payload keys with an empty representation.
        rt.Game.InUnit(uow => uow.ExecuteAsync("""
            INSERT INTO ultimateteam.item_extensions(item_id, schema_revision, payload, wire_representation)
            SELECT id, 1, '{"untradeableCount":1,"duplicate":false,"isDuplicate":false}'::json,
                '{"overrides":{},"missing":[],"nulls":[],"stringNumbers":[]}'::json
            FROM ultimateteam.items WHERE club_id=@club AND native_item_id=@id
            """, ("id", active[0])));
        Assert.Equal(1L, ExtensionRows(rt));
        AssertEverySquadSummarised(rt);
    }

    [Fact]
    public void UserSurvivesACompletionPackMovedToTheClubWhileAnotherSquadHoldsSubs()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        TwoSquads(rt);
        AssertEverySquadSummarised(rt);

        // A completion pack is opened and its players moved to the club.
        rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, Paid, 1_234_567, null, rt.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins));
        rt.Game.Store().DeletePile("unassigned");
        var opened = rt.CallObject("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 75, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER" });
        var moving = opened["itemList"]!.AsArray().OfType<JsonObject>().Where(item => item.Text("itemType") == "player").Select(item => item.Int("id")).ToList();
        Assert.NotEmpty(moving);
        rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(moving.Select(id => (JsonNode?)new JsonObject { ["id"] = id, ["pile"] = "club" }).ToArray()),
        });

        // The moved players must not have made the club "unusual" (no stored representation of any kind).
        Assert.Equal(0L, ExtensionRows(rt));
        AssertEverySquadSummarised(rt);
    }

    [Fact]
    public void UserSurvivesAGenuineWireRepresentationWhileAnotherSquadHoldsSubs()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Tuesday));
        var (active, _) = TwoSquads(rt);
        // An imported card whose document really differs from the canonical render.
        var store = rt.Game.Store();
        var item = store.GetItem(active[0])!;
        item["discardValue"] = 12345;
        item["someImportedField"] = "x";
        store.SaveItem(item);
        Assert.True(ExtensionRows(rt) > 0);
        AssertEverySquadSummarised(rt);
    }

    private static long ExtensionRows(TestRuntime rt)
    {
        using var source = NpgsqlDataSource.Create(rt.ConnectionString);
        using var command = source.CreateCommand("SELECT count(*) FROM ultimateteam.item_extensions");
        return (long)command.ExecuteScalar()!;
    }
}
