using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Objective progress shown without leaving FUT (owner, 2026-10-05): FIFA loads the campaign once per FUT entry, so match end,
/// pack open and SBC submit replies carry the objectives that moved as retail's <c>dynamicObjectivesUpdates</c>.
/// </summary>
public sealed class ObjectiveLiveUpdateTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Tuesday 2026-09-29 12:00 UTC: Squad Battles week 2960, no Weekend League live.</summary>
    private const long Tuesday = 1_790_683_200;

    private const long FullTime = 5461;

    /// <summary>Every objective FIFA would hold, by (group, objective), with its progress and state.</summary>
    private static Dictionary<(long Group, long Objective), (long Progress, string State)> Details(TestRuntime rt) =>
        rt.CallObject("GET", $"{G}/scmp/active/category/1/details")["groupsList"].Objects()
            .SelectMany(group => group["objectives"].Objects().Select(objective =>
                (Key: (group["groupId"].L(), objective["objectiveId"].L()), Value: (objective["currentProgress"].L(), objective["state"].S()))))
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    /// <summary>The objectives a reply's dynamicObjectivesUpdates sends, by (group, objective).</summary>
    private static Dictionary<(long Group, long Objective), (long Progress, string State)> Sent(JsonObject reply)
    {
        var categories = reply["dynamicObjectivesUpdates"]!["scmpCategoryProgressList"]!.AsArray();
        var category = Assert.Single(categories)!.AsObject();
        Assert.Equal(1, category["categoryId"].L());
        return category["scmpGroupProgressList"].Objects()
            .SelectMany(group => group["objectives"].Objects().Select(objective =>
                (Key: (group["groupId"].L(), objective["objectiveId"].L()), Value: (objective["currentProgress"].L(), objective["state"].S()))))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    /// <summary>The objectives whose progress or state differs between two details reads.</summary>
    private static Dictionary<(long Group, long Objective), (long Progress, string State)> Moved(
        Dictionary<(long, long), (long, string)> before, Dictionary<(long, long), (long, string)> after) =>
        after.Where(entry => !before.TryGetValue(entry.Key, out var was) || was != entry.Value)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static JsonObject Win(TestRuntime rt, TestRuntime.MovableTime clock, long matchId)
    {
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, matchId), 1000));
        clock.Now += 900;
        return rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "WIN",
            ["matchId"] = matchId,
            ["matchStatusFlags"] = 0,
            ["secondsPlayed"] = FullTime,
            ["myMatchStats"] = new JsonObject { ["goals"] = 2 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });
    }

    [Fact]
    public void AMatchEndSendsTheObjectivesItMovedAndTheSeasonXpItEarned()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        var before = Details(rt);
        var xp = rt.Game.Store().Objectives().UserXp;

        var ended = Win(rt, clock, 31);

        var moved = Moved(before, Details(rt));
        Assert.NotEmpty(moved);
        Assert.Equal(moved.OrderBy(e => e.Key), Sent(ended).OrderBy(e => e.Key));
        var earned = rt.Game.Store().Objectives().UserXp - xp;
        Assert.Equal(earned, ended["dynamicObjectivesUpdates"]!["autoRedeemedXp"]?.L() ?? 0);
    }

    [Fact]
    public void OnlyTheObjectivesThatMovedAgainAreSentNextTime()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        Win(rt, clock, 31);
        var before = Details(rt);

        var ended = Win(rt, clock, 32);

        // An objective already at its target (Kick Off's one match) does not go again: FIFA would repeat its notice.
        var moved = Moved(before, Details(rt));
        Assert.Equal(moved.OrderBy(e => e.Key), Sent(ended).OrderBy(e => e.Key));
        Assert.DoesNotContain(Sent(ended), entry => before.TryGetValue(entry.Key, out var was) && was.State != "IN_PROGRESS");
    }

    [Fact]
    public void APackOpenCarriesProgressStillWaitingToBeSent()
    {
        using var rt = TestRuntime.Create();
        var store = rt.Game.Store();
        JsonObject Open()
        {
            store.DeletePile("unassigned");
            store.AddUnopenedPack(70, "objective");
            var (status, opened, _) = rt.CallRaw("POST", $"{G}/purchased/items", new JsonObject { ["packId"] = 70 });
            Assert.Equal(200, status);
            Assert.Equal("PURCHASECOMPLETE", opened["state"].S());
            return opened.AsObject();
        }

        // No live objective counts packs today, so a plain pack open sends nothing.
        Assert.Null(Open()["dynamicObjectivesUpdates"]);

        // Progress recorded outside a reply FIFA reads it from waits for the next one, here the pack open.
        var before = Details(rt);
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordSbcCompletedAsync(uow));
        var moved = Moved(before, Details(rt));
        Assert.NotEmpty(moved);
        Assert.Equal(moved.OrderBy(e => e.Key), Sent(Open()).OrderBy(e => e.Key));
        Assert.Null(Open()["dynamicObjectivesUpdates"]);
    }

    [Fact]
    public void AnSbcSubmitSendsTheSbcProgress()
    {
        using var rt = TestRuntime.Create();
        var challenge = rt.Game.Sbc.Challenges.First(c => rt.Game.Sbc.IsLive(c.SetKey) && SbcFixtures.CanBuildSquad(c));
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SbcFixtures.SquadBody(rt, challenge)).Status);
        var before = Details(rt);

        var submitted = rt.CallObject("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());

        var moved = Moved(before, Details(rt));
        Assert.NotEmpty(moved);
        Assert.Equal(moved.OrderBy(e => e.Key), Sent(submitted).OrderBy(e => e.Key));
    }

    [Fact]
    public void NothingIsSentWhenNothingMovedOrTheSwitchIsOff()
    {
        var clock = new TestRuntime.MovableTime(Tuesday);
        using var rt = TestRuntime.Create(time: clock);
        Win(rt, clock, 31);

        // A quit counts nothing for objectives, so its reply carries no update.
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 32), 1000));
        clock.Now += 900;
        var quit = rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = "QUIT", ["matchId"] = 32, ["matchStatusFlags"] = 0, ["secondsPlayed"] = 600,
            ["myMatchStats"] = new JsonObject { ["goals"] = 0 }, ["opponentMatchStats"] = new JsonObject { ["goals"] = 0 },
        });
        Assert.Null(quit["dynamicObjectivesUpdates"]);

        rt.Game.Rules.Objectives.LiveProgressUpdates = false;
        Assert.Null(Win(rt, clock, 33)["dynamicObjectivesUpdates"]);
    }
}
