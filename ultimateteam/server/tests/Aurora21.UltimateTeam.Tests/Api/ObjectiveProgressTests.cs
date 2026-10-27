using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Objective progress from the events players report as "not tracking" (2026-10-02): a match's win is the result the match
/// end settled on (a shootout or an opponent who left), and an SBC saved the way FIFA saves it (its whole 23-slot squad
/// buffer, empty slots as id 0) can be submitted and counts towards the SBC objectives.
/// </summary>
public sealed class ObjectiveProgressTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: Squad Battles week 2960, no Weekend League live.</summary>
    private const long Tuesday = 1_790_683_200;

    /// <summary>FIFA's game clock at full time (09-24 captures: 5,461-5,621).</summary>
    private const long FullTime = 5461;

    /// <summary>FIFA's SBC save request holds 23 slots (CardsDLL save request, vtable 0x4635b0).</summary>
    private const int FifaSquadSlots = 23;

    private static JsonObject End(TestRuntime rt, long matchId, string reason, long mine, long theirs, long flags = 0,
        long myPens = 0, long theirPens = 0) =>
        rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["matchId"] = matchId,
            ["matchStatusFlags"] = flags,
            ["secondsPlayed"] = FullTime,
            ["myMatchStats"] = new JsonObject { ["goals"] = mine, ["penaltyGoals"] = myPens },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs, ["penaltyGoals"] = theirPens },
        });

    private static (long Matches, long Wins, long Goals, long CleanSheets) Stats(TestRuntime rt)
    {
        var state = rt.Game.Store().Objectives();
        return (state.Stat("matches"), state.Stat("wins"), state.Stat("goals"), state.Stat("cleanSheets"));
    }

    [Fact]
    public void ASquadBattlesShootoutWinCountsTowardsTheWinObjectives()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        rt.Call("POST", $"{G}/match", new JsonObject { ["sqbtEventId"] = 2960, ["sqbtOpponentSquadId"] = 0, ["sqbtMatchDifficulty"] = 3 });
        clock.Now += 900;

        // 1-1 after extra time, won 4-3 on penalties: Squad Battles settles it as a win, and so do the objectives.
        var ended = End(rt, 1, "WIN", 1, 1, myPens: 4, theirPens: 3);

        Assert.Equal(1, ended.Int("won"));
        Assert.Equal((1L, 1L, 1L, 0L), Stats(rt));
    }

    [Fact]
    public void ARivalsWinByTheOpponentLeavingCountsTowardsTheWinObjectives()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 21), 1000));
        clock.Now += 900;

        // The opponent quit (matchStatusFlags 2) at 1-1 past the 10th minute: the player who stayed wins the match and the
        // objective (losing at the time, it would not count: OpponentLeftTests).
        var ended = End(rt, 21, "DNF_WIN", 1, 1, flags: 2);

        Assert.Equal(1, ended.Int("won"));
        Assert.Equal((1L, 1L, 1L, 0L), Stats(rt));
    }

    [Fact]
    public void AScoreWinStillCountsItsGoalsAndCleanSheet()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 31), 1000));
        clock.Now += 900;

        End(rt, 31, "WIN", 3, 0);

        Assert.Equal((1L, 1L, 3L, 1L), Stats(rt));
    }

    [Fact]
    public void AnSbcSavedAsFifaSavesItCanBeSubmittedAndCountsTowardsTheSbcObjectives()
    {
        using var rt = TestRuntime.Create();
        var challenge = FirstBuildableChallenge(rt);
        var body = FifaSquadBody(SbcFixtures.SquadBody(rt, challenge));
        Assert.Equal(FifaSquadSlots, body["players"]!.AsArray().Count);

        var save = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", body);
        Assert.Equal(200, save.Status);
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject()).Status);

        Assert.Contains(challenge.Key, rt.Game.Store().SbcCompleted());
        Assert.Equal(1, rt.Game.Store().Objectives().Stat("sbcs"));
    }

    [Fact]
    public void AnSbcSaveStillRefusesMoreFilledSlotsThanTheSquadSize()
    {
        using var rt = TestRuntime.Create();
        // An open challenge: on a brick wall the first locked slot refuses the save before the count does.
        var challenge = rt.Game.Sbc.Sets.Single(set => set.Key == "FND_HYBRID_NATIONS").Challenges[0];
        var squadSize = (int)rt.Game.Rules.Sbc.SquadSize;
        var owned = rt.Game.Store().ListItems("club").Concat(rt.Game.Store().ListItems("squad"))
            .Where(item => item.Text("itemType") == "player").Select(item => item.Int("id")).Distinct().Take(squadSize + 1).ToList();
        Assert.Equal(squadSize + 1, owned.Count);
        var players = new JsonArray(owned.Select((id, index) => (JsonNode)Slot(index, id)).ToArray());

        var save = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", new JsonObject { ["players"] = players });

        // The twelfth card sits on the bench (index 11): FIFA keeps subs in the SBC buffer, so it is ignored, not refused.
        Assert.Equal(200, save.Status);
        Assert.Equal(squadSize, rt.Game.Store().SbcSquadCount(challenge.Id));
    }

    [Fact]
    public void AnSbcRewardPackCountsForNeitherThePacksStatNorSeasonXp()
    {
        // 10-04 live: the repeatable Upgrades pay a pack per repeat, and opening those packs farmed 50 Season XP each.
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        var before = store.Objectives();

        store.DeletePile("unassigned");
        store.AddUnopenedPack(70, "sbc");
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 70 }).Status);
        Assert.Equal((before.Stat("packs"), before.UserXp), (store.Objectives().Stat("packs"), store.Objectives().UserXp));

        // Any other owned pack still counts, but opened packs earn no Season XP (migration 146).
        store.DeletePile("unassigned");
        store.AddUnopenedPack(70, "objective");
        Assert.Equal(200, rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 70 }).Status);
        Assert.Equal((before.Stat("packs") + 1, before.UserXp),
            (store.Objectives().Stat("packs"), store.Objectives().UserXp));
    }

    private static SbcChallenge FirstBuildableChallenge(TestRuntime rt) =>
        rt.Game.Sbc.Challenges.First(challenge => rt.Game.Sbc.IsLive(challenge.SetKey) && SbcFixtures.CanBuildSquad(challenge));

    private static JsonObject Slot(int index, long id) =>
        new() { ["index"] = index, ["itemData"] = new JsonObject { ["id"] = id, ["dream"] = false } };

    /// <summary>
    /// The body FIFA sends (09-24 capture on the Python server): chemistry, rating, formation and manager, then every slot of
    /// its squad buffer in index order, an empty slot as id 0.
    /// </summary>
    private static JsonObject FifaSquadBody(JsonObject fixture)
    {
        var ids = fixture["players"]!.AsArray().OfType<JsonObject>().ToDictionary(slot => (int)slot.Int("index"), slot => slot.Obj("itemData")!.Int("id"));
        return new JsonObject
        {
            ["chemistry"] = 0,
            ["rating"] = 0,
            ["formation"] = "f442",
            ["manager"] = new JsonArray(new JsonObject { ["id"] = 0, ["dream"] = false }),
            ["players"] = new JsonArray(Enumerable.Range(0, FifaSquadSlots)
                .Select(index => (JsonNode)Slot(index, ids.GetValueOrDefault(index))).ToArray()),
        };
    }
}
