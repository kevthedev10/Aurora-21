using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Match ends a client can craft (2026-10-06 audit): a CPU match has no opponent who can leave, so a DNF_WIN there pays
/// nothing before the minimum length; Squad Battles points and single-player Draft run wins need that length too; and the
/// goals one match end reports count for objectives only up to rules/objectives.json maxGoalsPerMatch.
/// </summary>
public sealed class CraftedMatchEndTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: Squad Battles week 2960, no Weekend League live.</summary>
    private const long Tuesday = 1_790_683_200;

    private static JsonObject End(TestRuntime rt, long matchId, string reason, long mine, long theirs, long secondsPlayed) =>
        rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["matchId"] = matchId,
            ["secondsPlayed"] = secondsPlayed,
            ["myMatchStats"] = new JsonObject { ["goals"] = mine },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs },
        });

    private static void SquadBattles(TestRuntime rt, long slot) =>
        rt.Call("POST", $"{G}/match", new JsonObject { ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = slot, ["sqbtMatchDifficulty"] = 6 });

    private static int CompletionPacks(TestRuntime rt) =>
        rt.Game.Store().UnopenedPackIds().Count(id => rt.Game.Rules.Seasons.MatchCompletionRewards.Values.Any(reward => reward.PackId == id));

    [Fact]
    public void AShortSquadBattlesDnfWinPaysAndScoresNothing()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        var credits = rt.Game.Store().Credits();

        // 100 real seconds and a claimed 15 minutes: the opponent-left rule would call it minute 14, past the win minute.
        SquadBattles(rt, 0);
        clock.Now += 100;
        var crafted = End(rt, 1, "DNF_WIN", 3, 0, 900);

        Assert.Equal(0, crafted.Int("matchCoins"));
        Assert.Equal(0, crafted["squadBattlesScore"]?.AsObject().Int("pointsWon") ?? 0);
        Assert.Equal((credits, 0L, 0), (rt.Game.Store().Credits(), rt.Game.Store().Objectives().Stat("matches"), CompletionPacks(rt)));

        // A real match still pays.
        SquadBattles(rt, 1);
        rt.PlayedRealSeconds();
        Assert.Equal(rt.Game.Rules.Seasons.MatchCompletionRewards[RewardModes.SquadBattles].Coins, End(rt, 2, "WIN", 2, 0, 5461).Int("matchCoins"));
        Assert.Equal(1, CompletionPacks(rt));
    }

    [Fact]
    public void TheStayerOfAnOnlineMatchStillWinsBeforeTheMinimum()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 21), 1000));
        clock.Now += 100;

        var stayed = End(rt, 21, "DNF_WIN", 3, 0, 900);

        Assert.Equal((1L, rt.Game.Rules.Seasons.MatchCompletionRewards[RewardModes.Rivals].Coins), (stayed.Int("won"), stayed.Int("matchCoins")));
    }

    [Fact]
    public void ASinglePlayerDraftWinNeedsTheMinimumLength()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.MovableTime(Tuesday));
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "PRO" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        var credits = rt.Game.Store().Credits();

        // A WIN straight after the CreateMatch: no result, the run waits for its match.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        var crafted = End(rt, 1, "WIN", 1, 0, 5461);
        Assert.Equal((0L, 0L), (crafted.Int("matchCoins"), crafted.Int("wins")));
        var run = rt.Game.Store().DraftRun()!;
        Assert.Equal((0L, 0L, "READY_FOR_MATCH"), (run.Int("wins"), run.Int("losses"), run.Text("state")));
        Assert.Equal((credits, 0), (rt.Game.Store().Credits(), CompletionPacks(rt)));

        // The same match played out counts.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        End(rt, 2, "WIN", 1, 0, 5461);
        Assert.Equal(1, rt.Game.Store().DraftRun()!.Int("wins"));
    }

    [Fact]
    public void OneMatchCountsAtMostTheGoalCapForObjectives()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.MovableTime(Tuesday));
        var cap = rt.Game.Rules.Objectives.MaxGoalsPerMatch;

        SquadBattles(rt, 0);
        rt.PlayedRealSeconds();
        End(rt, 1, "WIN", 500, 0, 5461);

        Assert.Equal(20, cap);
        Assert.Equal(cap, rt.Game.Store().Objectives().Stat("goals"));
    }
}
