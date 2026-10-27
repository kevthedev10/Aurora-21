using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// A Draft match end settles once: FIFA sends some match ends twice about 440 ms apart (production Python server, the
/// match-end replay guard), and a replay must not count a second win or loss or pay again, while the next match (after its
/// own CreateMatch) still counts.
/// </summary>
public sealed class DraftMatchEndReplayTests
{
    private const string G = "/ut/game/fifa21";
    private const long DraftSquad = 900001;

    private static JsonObject Run(TestRuntime rt, DraftSlot slot) => rt.Game.InUnit(uow => uow.DraftRunAsync(slot))!;

    private static long Credits(TestRuntime rt) => rt.Game.InUnit(uow => uow.CreditsAsync());

    private static JsonObject End(TestRuntime rt, string reason, long? gid = null)
    {
        rt.PlayedRealSeconds();
        return rt.CallObject("POST", $"{G}/match/end", gid is null
            ? new JsonObject { ["endReason"] = reason, ["myMatchStats"] = new JsonObject { ["goals"] = 2 } }
            : new JsonObject { ["endReason"] = reason, ["gid"] = gid, ["myMatchStats"] = new JsonObject { ["goals"] = 2 } });
    }

    private static void AssertReplay(JsonObject first, JsonObject replay)
    {
        var settled = first.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, replay));
    }

    private static void StartDraft(TestRuntime rt, bool online)
    {
        var mode = online ? 0 : 1;
        rt.Call("POST", $"{G}/purchase/mode/{mode}/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/{mode}/draft/autocomplete");
    }

    private static void CreateMatch(TestRuntime rt, bool online) =>
        rt.Call("POST", $"{G}/match", online ? new JsonObject { ["squadId"] = DraftSquad, ["type"] = "ONLINE" } : new JsonObject { ["squadId"] = DraftSquad });

    [Fact]
    public void AReplayedWinCountsOnceAndTheNextMatchStillCounts()
    {
        using var rt = TestRuntime.Create();
        StartDraft(rt, online: false);
        CreateMatch(rt, online: false);
        var before = Credits(rt);

        var first = End(rt, "WIN");
        Assert.Equal(before + first.Int("matchCoins"), Credits(rt));
        Assert.Equal(1, Run(rt, DraftSlot.SinglePlayer).Int("wins"));
        Assert.True(rt.Game.UserSessionRefresh.IsSet);
        rt.Game.UserSessionRefresh.Clear();

        // The same end again (an offline end names no game): the first reply, and nothing counted or paid twice.
        var replay = End(rt, "WIN");
        Assert.True(first.ContainsKey("dynamicObjectivesUpdates"));
        AssertReplay(first, replay);
        Assert.Equal(before + first.Int("matchCoins"), Credits(rt));
        Assert.Equal(("READY_FOR_MATCH", 1L), (Run(rt, DraftSlot.SinglePlayer).Text("state"), Run(rt, DraftSlot.SinglePlayer).Int("wins")));
        Assert.False(rt.Game.UserSessionRefresh.IsSet);

        // The next match has its own CreateMatch, and its end counts.
        CreateMatch(rt, online: false);
        var second = End(rt, "WIN");
        Assert.Equal(2, second.Int("draftWins"));
        Assert.Equal(2, Run(rt, DraftSlot.SinglePlayer).Int("wins"));
        Assert.Equal(before + first.Int("matchCoins") + second.Int("matchCoins"), Credits(rt));
    }

    [Fact]
    public void AReplayedLossNeitherCountsASecondLossNorPaysTwice()
    {
        using var rt = TestRuntime.Create();
        StartDraft(rt, online: false);
        CreateMatch(rt, online: false);
        var first = End(rt, "LOSS");
        Assert.True(first.Bool("draftEliminated"));
        var credits = Credits(rt);
        var run = Run(rt, DraftSlot.SinglePlayer);
        Assert.Equal(("READY_FOR_REWARDS", 1L), (run.Text("state"), run.Int("losses")));

        // The first response includes one-time objective progress; the stored settlement
        // replays its match fields without awarding or sending that progress again.
        Assert.True(first.ContainsKey("dynamicObjectivesUpdates"));
        var settled = first.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        foreach (var reason in new[] { "LOSS", "WIN" })
        {
            var replay = End(rt, reason);
            Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
            Assert.True(JsonNode.DeepEquals(settled, replay));
        }
        Assert.Equal(credits, Credits(rt));
        var after = Run(rt, DraftSlot.SinglePlayer);
        Assert.Equal((1L, 0L, run.Int("pendingCoinReward")), (after.Int("losses"), after.Int("wins"), after.Int("pendingCoinReward")));
    }

    [Fact]
    public void AnOnlineReplayIsIgnoredAndACancelledSearchDoesNotUseUpTheMatch()
    {
        using var rt = TestRuntime.Create();
        StartDraft(rt, online: true);
        CreateMatch(rt, online: true);
        var credits = Credits(rt);

        // A cancelled search ends in DNF with gid 0: no match, and the created match is still to be played.
        var cancelled = End(rt, "DNF", gid: 0);
        Assert.Equal((0L, false), (cancelled.Int("matchCoins"), cancelled.Bool("draftEliminated")));
        Assert.Equal(credits, Credits(rt));

        var won = End(rt, "WIN", gid: 5);
        Assert.Equal(1, Run(rt, DraftSlot.Online).Int("wins"));
        Assert.Equal(credits + won.Int("matchCoins"), Credits(rt));

        // FIFA's second copy of the same end.
        AssertReplay(won, End(rt, "WIN", gid: 5));
        Assert.Equal(1, Run(rt, DraftSlot.Online).Int("wins"));
        Assert.Equal(credits + won.Int("matchCoins"), Credits(rt));
    }

    [Fact]
    public void AnEndForARunNotReadyToPlayIsRefusedAndSettlesNothingElse()
    {
        using var rt = TestRuntime.Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        // A Draft CreateMatch for a run still drafting is refused, but the club's last create is now a Draft one.
        Assert.Equal(409, rt.CallRaw("POST", $"{G}/match", new JsonObject { ["squadId"] = DraftSquad }).Status);
        var credits = Credits(rt);
        var user = rt.CallObject("GET", $"{G}/user");

        var refused = End(rt, "LOSS");

        Assert.Equal(("LOSS", 0L, false), (refused.Text("endReason"), refused.Int("matchCoins"), refused.Bool("draftEliminated")));
        Assert.Equal(credits, Credits(rt));
        var run = Run(rt, DraftSlot.SinglePlayer);
        Assert.Equal(("PICK_DIFFICULTY", 0L), (run.Text("state"), run.Int("losses")));
        // Not a normal match either: the club record is unchanged.
        var after = rt.CallObject("GET", $"{G}/user");
        Assert.Equal((user.Int("won"), user.Int("loss")), (after.Int("won"), after.Int("loss")));
    }
}
