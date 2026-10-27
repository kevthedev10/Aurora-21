using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalDraftTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;
    private const long Seed = 424242;

    private static TestRuntime Create(Action<string>? observer = null, string? connection = null)
    {
        if (connection is null)
        {
            connection = PostgresTestCluster.CreateInstalledDatabase();
            using var source = NpgsqlDataSource.Create(connection);
            RelationalSchemaBootstrap.InstallFreshAsync(source).GetAwaiter().GetResult();
        }
        return TestRuntime.Create(connectionString: connection, relationalSlice: true, time: new TestRuntime.FixedTime(Now),
            commandObserver: observer, initializeRelationalClub: true);
    }

    private static Task<T> Read<T>(TestRuntime rt, Func<UnitOfWork, ValueTask<T>> read) => rt.Game.ReadAsync(read, CancellationToken.None);

    private static Task<long> Scalar(TestRuntime rt, string sql) => Read(rt, w => w.ScalarAsync<long>(sql));

    /// <summary>Fix the random seed so repeated draft runs make the same choices.</summary>
    private static Task PinSeed(TestRuntime rt, DraftSlot slot = DraftSlot.SinglePlayer) => rt.Game.RunAsync("pin-seed", async w =>
    {
        var run = (await w.DraftRunAsync(slot))!;
        run["seed"] = Seed;
        await w.SaveDraftRunAsync(run, slot);
    }, CancellationToken.None);

    private static JsonObject Swapped(JsonObject squad, int a, int b)
    {
        var players = squad.Arr("players")!.OfType<JsonObject>().Select(s => (JsonObject)s.DeepClone()).ToList();
        (players[a]["itemData"], players[b]["itemData"]) = (players[b]["itemData"]?.DeepClone(), players[a]["itemData"]?.DeepClone());
        return new JsonObject { ["formation"] = squad.Str("formation"), ["players"] = new JsonArray(players.Select(p => (JsonNode?)p).ToArray()) };
    }

    /// <summary>A complete single-player Draft through the actual routes.</summary>
    private static async Task<List<(string Step, JsonNode Body)>> DraftFlowAsync(TestRuntime rt)
    {
        var responses = new List<(string, JsonNode)>();
        JsonNode Step(string name, string method, string path, JsonNode? body = null)
        {
            var result = rt.Call(method, path, body);
            responses.Add((name, result));
            return result;
        }
        Step("purchase", "POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        await PinSeed(rt);
        Step("state-new", "GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER");
        var difficulty = Step("difficulties", "GET", $"{G}/squad/mode/draft/choices/difficulty").AsObject();
        Step("difficulty", "PUT", $"{G}/squad/mode/draft/choose/difficulty",
            new JsonObject { ["difficultyName"] = difficulty.Arr("choices")![1]!.AsObject().Str("difficultyName") });
        Step("formations", "GET", $"{G}/squad/mode/draft/choices/formation");
        Step("formation", "PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 1, ["positionId"] = 0 });
        Step("captains", "GET", $"{G}/squad/mode/draft/choices/captain");
        Step("captains-again", "GET", $"{G}/squad/mode/draft/choices/captain");
        Step("captain", "PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 2, ["positionId"] = 0 });
        for (var position = 1; position <= 3; position++)
        {
            Step($"players-{position}", "PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = position });
            Step($"player-{position}", "PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = position % 5, ["positionId"] = position });
        }
        Step("players-4-open", "PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 4 });
        Step("state-mid", "GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER");
        Step("autocomplete", "POST", $"{G}/squad/mode/1/draft/autocomplete");
        var squad = Step("squad", "GET", $"{G}/squad/900001").AsObject();
        Step("rearrange", "PUT", $"{G}/squad/900001", Swapped(squad, 1, 2));
        Step("squad-after", "GET", $"{G}/squad/900001");
        Step("match", "POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        Step("match-win", "POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN" });
        Step("stats", "GET", $"{G}/squad/mode/1/stats");
        Step("match-2", "POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        Step("match-loss", "POST", $"{G}/match/end", new JsonObject { ["endReason"] = "LOSS" });
        Step("state-rewards", "GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER");
        Step("award", "POST", $"{G}/squad/mode/1/draft/grant/award");
        Step("award-repeat", "POST", $"{G}/squad/mode/1/draft/grant/award");
        Step("state-end", "GET", $"{G}/squad/mode/draft/state?mode=SINGLE_PLAYER");
        return responses;
    }

    [Fact]
    public async Task DraftRoutesCompleteWithoutStoringDocuments()
    {
        using var rt = Create();
        var responses = await DraftFlowAsync(rt);
        Assert.Contains(responses, response => response.Step == "match-win");
        Assert.Contains(responses, response => response.Step == "award");
        Assert.Contains(responses, response => response.Step == "award-repeat");
        Assert.Null(await Read(rt, w => w.DraftRunAsync()));
        Assert.Equal(0, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_runs"));
        Assert.Equal(0, await Scalar(rt, "SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task MidDraftStateReopensIdenticallyFromTypedRows()
    {
        using var rt = Create();
        using var legacy = TestRuntime.Create(time: new TestRuntime.FixedTime(Now));
        foreach (var runtime in new[] { legacy, rt })
        {
            runtime.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
            await PinSeed(runtime);
            runtime.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
            runtime.Call("GET", $"{G}/squad/mode/draft/choices/formation");
            runtime.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
            runtime.Call("GET", $"{G}/squad/mode/draft/choices/captain");
            runtime.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
            runtime.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 });
        }
        // The stored run documents agree field by field (legacy omits the offer map only before its first offer).
        var legacyRun = (await Read(legacy, w => w.DraftRunAsync()))!;
        var run = (await Read(rt, w => w.DraftRunAsync()))!;
        Assert.True(JsonNode.DeepEquals(legacyRun, run), $"legacy {legacyRun.ToJsonString()} relational {run.ToJsonString()}");
        Assert.Equal(5, run.Arr("currentChoices")!.Count);

        Assert.Equal(1, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_picks WHERE pick_kind='player'"));
        Assert.Equal(10, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_choices"));
        Assert.Equal(run.Arr("seenAssets")!.Count, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_seen_footballers"));
        Assert.Equal(run.Arr("formationChoices")!.Count, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_formation_choices"));
        Assert.Equal(0, await Scalar(rt, "SELECT captain_position_id FROM ultimateteam.draft_runs"));

        var offered = rt.CallObject("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 });
        var connection = rt.Detach();
        using var reopened = Create(connection: connection);
        Assert.True(JsonNode.DeepEquals(run, await Read(reopened, w => w.DraftRunAsync())));
        Assert.True(JsonNode.DeepEquals(offered, reopened.CallObject("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 })));
        Assert.True(await Scalar(reopened, "SELECT run_revision FROM ultimateteam.draft_runs") > 1);
    }

    [Fact]
    public async Task DraftSquadTacticsAreKeptOnTheRunAndSentBack()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        await PinSeed(rt);
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        Assert.Null(rt.CallObject("GET", $"{G}/squad/900001").Get("tactics"));

        JsonObject Tactic(string mentality, long width) => new()
        {
            ["squadId"] = 900001, ["tactic"] = mentality, ["formation"] = "f433", ["width"] = width, ["enabled"] = true,
            ["positions"] = new JsonArray(new JsonObject { ["index"] = 0, ["value"] = 3 }, new JsonObject { ["index"] = 2, ["value"] = 7 }),
        };
        // The posted Draft squad was refused SQUAD_NOT_FOUND: it has no club squad row.
        var reply = rt.CallRaw("POST", $"{G}/tactics?squadId=900001", new JsonObject { ["tactics"] = new JsonArray(Tactic("ATTACKING", 60), Tactic("BALANCED", 45)) });
        Assert.True(reply.Status == 200, reply.Body.ToJsonString());
        rt.Call("POST", $"{G}/tactics?squadId=900001", new JsonObject { ["tactics"] = new JsonArray(Tactic("BALANCED", 50)) });

        var tactics = rt.CallObject("GET", $"{G}/squad/900001").Arr("tactics")!;
        Assert.Equal(2, tactics.Count);
        var balanced = tactics.OfType<JsonObject>().Single(t => t.Str("tactic") == "BALANCED");
        Assert.True(JsonNode.DeepEquals(Tactic("BALANCED", 50), balanced), balanced.ToJsonString());
        Assert.Equal(new[] { "squadId", "tactic", "formation", "positions", "enabled", "width" }, balanced.Select(kv => kv.Key));
        Assert.Equal(0, await Scalar(rt, "SELECT count(*) FROM ultimateteam.squad_tactics"));
        Assert.Equal(14, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_tactics"));

        // The match squad carries them as its last key, the run reopens with them and a new run starts without them.
        var match = (await rt.Game.ReadAsync(async w => await rt.Fut.Draft.NativeMatchSquadAsync(w, (await w.DraftRunAsync())!), CancellationToken.None));
        Assert.Equal("tactics", match.Last().Key);
        Assert.True(JsonNode.DeepEquals(tactics, match["tactics"]));
        var connection = rt.Detach();
        using var reopened = Create(connection: connection);
        Assert.True(JsonNode.DeepEquals(tactics, reopened.CallObject("GET", $"{G}/squad/900001")["tactics"]));
        await reopened.Game.RunAsync("clear", w => w.ClearDraftRunAsync(), CancellationToken.None);
        Assert.Equal(0, await Scalar(reopened, "SELECT count(*) FROM ultimateteam.draft_tactics"));
    }

    [Fact]
    public async Task InvalidRunDocumentsAndForeignRearrangementsAreRejectedWithoutChanges()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        var run = (await Read(rt, w => w.DraftRunAsync()))!;
        var revision = await Scalar(rt, "SELECT run_revision FROM ultimateteam.draft_runs");

        JsonObject Variant(Action<JsonObject> change)
        {
            var copy = (JsonObject)run.DeepClone();
            change(copy);
            return copy;
        }
        var firstPick = run.Obj("picked")!.First().Key;
        foreach (var invalid in new[]
        {
            Variant(r => r.Obj("picked")![firstPick]!.AsObject()["rating"] = 99),
            Variant(r => r.Obj("picked")![firstPick]!.AsObject()["resourceId"] = 1),
            Variant(r => r["manager"]!.AsObject()["id"] = "7"),
            Variant(r => r["unknownField"] = 1),
            Variant(r => r["captain"] = 123),
            Variant(r => r["seenAssets"] = new JsonArray(1)),
            Variant(r => r["seenAssets"] = new JsonArray(run.Arr("seenAssets")![0]!.DeepClone(), run.Arr("seenAssets")![0]!.DeepClone())),
            Variant(r => r["formation"] = null),
            Variant(r => r["wins"] = 1.5),
            Variant(r => r["currentChoices"] = new JsonArray(r.Obj("picked")![firstPick]!.DeepClone())),
            Variant(r => r["pendingOffers"] = new JsonObject { ["formation:0"] = new JsonArray() }),
            Variant(r => r["pendingOffers"] = new JsonObject { ["captain:3"] = new JsonArray(r.Obj("picked")![firstPick]!.DeepClone()) }),
        })
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => rt.Game.RunAsync("bad-run", w => w.SaveDraftRunAsync(invalid), CancellationToken.None));
        }
        Assert.True(JsonNode.DeepEquals(run, await Read(rt, w => w.DraftRunAsync())));
        Assert.Equal(revision, await Scalar(rt, "SELECT run_revision FROM ultimateteam.draft_runs"));

        // A rearrangement may only move the run's own picks: a club card in the lineup names no pick and is ignored.
        var squad = rt.CallObject("GET", $"{G}/squad/900001");
        List<long> Cards(JsonObject document) => document.Arr("players")!.OfType<JsonObject>()
            .Select(slot => slot.Obj("itemData")?.Int("id") ?? 0).Where(id => id != 0).Order().ToList();
        var foreign = Swapped(squad, 0, 1);
        var club = (await Read(rt, w => w.ListItemsAsync("club"))).First(i => i.Str("itemType") == "player");
        foreign.Arr("players")![0]!.AsObject()["itemData"] = club.DeepClone();
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/900001", foreign).Status);
        var afterForeign = rt.CallObject("GET", $"{G}/squad/900001");
        Assert.Equal(Cards(squad), Cards(afterForeign));
        Assert.DoesNotContain(club.Int("id"), Cards(afterForeign));
        // Saving the original lineup puts every pick back.
        rt.Call("PUT", $"{G}/squad/900001", new JsonObject { ["formation"] = squad.Str("formation"), ["players"] = squad.Arr("players")!.DeepClone() });
        Assert.True(JsonNode.DeepEquals(squad, rt.CallObject("GET", $"{G}/squad/900001")));
        var tampered = Swapped(squad, 0, 1);
        tampered.Arr("players")![0]!.AsObject()["itemData"]!.AsObject()["rating"] = 1;
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/900001", tampered).Status);
        var moved = rt.CallObject("GET", $"{G}/squad/900001").Arr("players")![0]!.AsObject().Obj("itemData")!;
        Assert.True(JsonNode.DeepEquals(squad.Arr("players")![1]!.AsObject().Obj("itemData"), moved));
    }

    [Theory]
    [InlineData("POINTS")]
    [InlineData("DRAFT_TOKEN")]
    public async Task DraftEntryIsNeverFree(string currency)
    {
        using var rt = Create();
        rt.Game.Rules.Club.FifaPointsPurchases = false; // Verify disabled policy before explicitly enabling it below.
        await rt.Game.RunAsync("fixture-points", w => w.AddFifaPointsAsync(1_000), CancellationToken.None);
        var credits = await Read(rt, w => w.CreditsAsync());
        var points = await Read(rt, w => w.FifaPointsAsync());

        var (status, _, _) = rt.CallRaw("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = currency });

        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        if (currency == "POINTS")
        {
            // FIFA Points buy nothing while rules/club.json fifaPointsPurchases is off (phase 1).
            Assert.Equal(400, status);
            Assert.Null(await Read(rt, w => w.DraftRunAsync()));
            Assert.Equal(points, await Read(rt, w => w.FifaPointsAsync()));
            // Switched on, points pay the points price of the entry.
            rt.Game.Rules.Club.FifaPointsPurchases = true;
            (status, _, _) = rt.CallRaw("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = currency });
            Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
            Assert.Equal(200, status);
            Assert.NotNull(await Read(rt, w => w.DraftRunAsync()));
            Assert.Equal(points - rt.Game.Rules.Draft.Entry.Points, await Read(rt, w => w.FifaPointsAsync()));
        }
        else
        {
            // No Draft token inventory exists, so a token entry is refused rather than given away.
            Assert.NotEqual(200, status);
            Assert.Null(await Read(rt, w => w.DraftRunAsync()));
            Assert.Equal(points, await Read(rt, w => w.FifaPointsAsync()));
        }
    }

    [Fact]
    public async Task RearrangementsKeepEveryPickExactlyOnceAndPicksLandInTheOfferedSlot()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        await PinSeed(rt);
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });

        // A pick lands in the slot its choices were offered for, whatever position the confirmation names.
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 9 });
        var picked = (await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!;
        Assert.True(picked.ContainsKey("5"));
        Assert.False(picked.ContainsKey("9"));

        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        var squad = rt.CallObject("GET", $"{G}/squad/900001");
        async Task<SortedDictionary<string, string>> Layout() => new((await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!
            .ToDictionary(entry => entry.Key, entry => entry.Value!.AsObject().Int("id") + ":" + entry.Value!.AsObject().Int("resourceId")));
        var original = await Layout();

        // Every slot holding a copy of the same pick: the copies name nothing new, so nothing is copied or dropped.
        var copies = (JsonObject)squad.DeepClone();
        var first = copies.Arr("players")![0]!.AsObject().Obj("itemData")!;
        foreach (var slot in copies.Arr("players")!.OfType<JsonObject>())
        {
            slot["itemData"] = first.DeepClone();
        }
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/900001", copies).Status);
        Assert.Equal(original, await Layout());

        // A partial list keeps the picks it leaves out where they were; unknown ids are ignored.
        var partial = (JsonObject)squad.DeepClone();
        partial["players"] = new JsonArray(squad.Arr("players")!.Take(2).Select(slot => slot!.DeepClone())
            .Append(new JsonObject { ["index"] = 5, ["itemData"] = new JsonObject { ["id"] = 123456789, ["dream"] = false } }).ToArray());
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/900001", partial).Status);
        Assert.Equal(original, await Layout());

        // FIFA's own save: bare {id, dream} cards named by card id (resourceId), here with the first two swapped, and
        // the captain named the same way.
        JsonNode Bare(int from, int to) => new JsonObject { ["index"] = to, ["itemData"] = new JsonObject
            { ["id"] = squad.Arr("players")![from]!["itemData"]!["resourceId"]!.DeepClone(), ["dream"] = false } };
        var second = squad.Arr("players")![1]!["itemData"]!.AsObject();
        rt.Call("PUT", $"{G}/squad/900001", new JsonObject
        {
            ["formation"] = squad.Str("formation"),
            ["captain"] = second.Int("resourceId"),
            ["players"] = new JsonArray(Bare(0, 1), Bare(1, 0)),
        });
        var swapped = await Layout();
        Assert.Equal((original["0"], original["1"]), (swapped["1"], swapped["0"]));
        Assert.Equal(original.Values.Order(), swapped.Values.Order());
        Assert.Equal(second.Int("id"), (await Read(rt, w => w.DraftRunAsync()))!.Int("captain"));
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/900001", Swapped(squad, 0, 1)).Status);
    }

    [Fact]
    public async Task ACancelledOnlineDraftSearchSettlesNothing()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/0/draft/autocomplete");
        async Task<JsonObject> Run() => (await Read(rt, w => w.DraftRunAsync(DraftSlot.Online)))!;
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001, ["type"] = "ONLINE" });
        var credits = await Read(rt, w => w.CreditsAsync());

        // FIFA ends a search that found no game with DNF, gid 0 and no match id.
        var cancelled = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["gid"] = 0 });

        Assert.Equal(("DNF", 0L, false), (cancelled.Text("endReason"), cancelled.Int("matchCoins"), cancelled.Bool("draftEliminated")));
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        var run = await Run();
        Assert.Equal(("READY_FOR_MATCH", 0L, 0L), (run.Text("state"), run.Int("wins"), run.Int("losses")));

        // A no-contest is called off before it counts, and does not end the run either (Python server 5b8b918).
        var called = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "NO_CONTEST", ["gid"] = 41 });
        Assert.Equal((0L, false), (called.Int("matchCoins"), called.Bool("draftEliminated")));
        Assert.Equal(("READY_FOR_MATCH", 0L), ((await Run()).Text("state"), (await Run()).Int("losses")));

        // A DNF from a game that was played still ends the run.
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["gid"] = 42 });
        Assert.Equal("READY_FOR_REWARDS", (await Run()).Text("state"));
    }

    [Fact]
    public async Task ASinglePlayerMatchLeftBeforeKickOffKeepsTheRun()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        async Task<JsonObject> Run() => (await Read(rt, w => w.DraftRunAsync(DraftSlot.SinglePlayer)))!;
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["secondsPlayed"] = 5468 });
        Assert.Equal(("READY_FOR_MATCH", 1L), ((await Run()).Text("state"), (await Run()).Int("wins")));

        // Live 10-07 (cl8ve): the second match was left to the squad 4 s after CreateMatch, DNF with no game time.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        var credits = await Read(rt, w => w.CreditsAsync());
        var left = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["secondsPlayed"] = 0 });
        Assert.Equal((0L, false, 1L), (left.Int("matchCoins"), left.Bool("draftEliminated"), left.Int("draftWins")));
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        var run = await Run();
        Assert.Equal(("READY_FOR_MATCH", 1L, 0L), (run.Text("state"), run.Int("wins"), run.Int("losses")));

        // The match is played next: a quit after kick-off still loses and ends the run.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF", ["secondsPlayed"] = 1800 });
        Assert.Equal(("READY_FOR_REWARDS", 1L), ((await Run()).Text("state"), (await Run()).Int("losses")));
    }

    [Fact]
    public async Task ALineupSentWithAPickKeepsTheSwapsMadeWhileDrafting()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 1 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 1 });
        var picked = (await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!;
        var (captain, other) = (picked["0"]!.AsObject(), picked["1"]!.AsObject());

        // FIFA sends the lineup it shows (one card id per slot) with the next pick; the player swapped the two.
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 2 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject
        {
            ["choiceIndex"] = 0, ["positionId"] = 2,
            ["swapPlayerDefIds"] = new JsonArray(other.Int("resourceId"), captain.Int("resourceId")),
        });

        picked = (await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!;
        Assert.Equal((other.Int("id"), captain.Int("id")), (picked["0"]!.AsObject().Int("id"), picked["1"]!.AsObject().Int("id")));
        Assert.Equal(3, picked.Count);
    }

    /// <summary>The club's unopened packs with their grant source and tradeability, in grant order.</summary>
    private static Task<List<(long Pack, string Source, bool Untradeable)>> UnopenedPacks(TestRuntime rt) => Read(rt, w => w.QueryAsync(
        "SELECT pack_id, source, untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club ORDER BY id",
        r => (r.GetInt64(0), r.GetString(1), r.GetBoolean(2))));

    // A finished run pays its Draft reward level (migration 135): the level's coins plus one choice drawn from the run's seed,
    // whatever difficulty it was played at. Single-player pays less than online and its packs are tradeable under
    // "draft_single"; online packs are tradeable under "draft_online".
    [Theory]
    [InlineData(new[] { "WIN", "WIN", "LOSS" }, 2, null, 5000L)]
    [InlineData(new[] { "WIN", "WIN", "WIN", "WIN" }, 4, null, 15000L)]
    [InlineData(new[] { "WIN", "WIN", "WIN", "WIN" }, 4, "LEGENDARY", 15000L)]
    [InlineData(new[] { "LOSS" }, 0, "ULTIMATE", 1500L)]
    public async Task AFinishedRunPaysItsRewardByWinsOnce(string[] results, long wins, string? difficulty, long levelCoins)
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        if (difficulty is not null)
        {
            rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = difficulty });
        }
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        foreach (var result in results)
        {
            rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
            rt.PlayedRealSeconds();
            rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = result });
        }
        var run = (await Read(rt, w => w.DraftRunAsync()))!;
        Assert.Equal(("READY_FOR_REWARDS", wins), (run.Text("state"), run.Int("wins")));
        var payout = rt.Fut.Draft.RunPayout(run, DraftSlot.SinglePlayer);
        var level = rt.Game.Rules.Draft.RewardLevel(DraftRewardLevel.Single, (int)wins)!;
        Assert.Equal(levelCoins, payout.Level.Coins);
        Assert.Contains(level.Choices, choice => choice.Reward.ToJson().ToJsonString() == payout.Drawn.ToJson().ToJsonString());
        var coins = levelCoins + payout.Drawn.Coins;
        Assert.Equal(coins, run.Int("pendingCoinReward"));
        var packsBefore = (await UnopenedPacks(rt)).Count;

        var credits = await Read(rt, w => w.CreditsAsync());
        var award = rt.Call("POST", $"{G}/squad/mode/1/draft/grant/award");
        Assert.Equal(coins, award.AsArray()[0]!["value"]!.GetValue<long>());
        Assert.Equal(credits + coins, await Read(rt, w => w.CreditsAsync()));
        var granted = (await UnopenedPacks(rt)).Skip(packsBefore).ToList();
        Assert.Equal(payout.Packs, granted.Select(p => p.Pack));
        Assert.All(granted, p => Assert.Equal(("draft_single", false), (p.Source, p.Untradeable)));

        Assert.Empty(rt.Call("POST", $"{G}/squad/mode/1/draft/grant/award").AsArray());
        Assert.Equal(credits + coins, await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(packsBefore + payout.Packs.Count, (await UnopenedPacks(rt)).Count);
    }

    // An online run pays its online level, its packs tradeable under "draft_online"; an unclaimed run is paid before the next entry.
    [Theory]
    [InlineData(4, 30000L, false)]
    [InlineData(1, 6000L, true)]
    public async Task AnOnlineRunPaysTheOnlineTableTradeable(int wins, long levelCoins, bool claimByNextEntry)
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("POST", $"{G}/squad/mode/0/draft/autocomplete");
        for (var game = 0; game <= wins && game < 4; game++)
        {
            rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001, ["type"] = "ONLINE" });
            rt.PlayedRealSeconds();
            rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = game < wins ? "WIN" : "LOSS", ["gid"] = 50 + game });
        }
        var run = (await Read(rt, w => w.DraftRunAsync(DraftSlot.Online)))!;
        var payout = rt.Fut.Draft.RunPayout(run, DraftSlot.Online);
        Assert.Equal(levelCoins, payout.Level.Coins);
        var coins = payout.Coins;
        Assert.Equal(("READY_FOR_REWARDS", (long)wins, coins), (run.Text("state"), run.Int("wins"), run.Int("pendingCoinReward")));
        var packsBefore = (await UnopenedPacks(rt)).Count;
        var credits = await Read(rt, w => w.CreditsAsync());

        if (claimByNextEntry)
        {
            rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
            Assert.Equal(credits + coins - rt.Game.Rules.Draft.Entry.Coins, await Read(rt, w => w.CreditsAsync()));
        }
        else
        {
            var award = rt.Call("POST", $"{G}/squad/mode/0/draft/grant/award");
            Assert.Equal(coins, award.AsArray()[0]!["value"]!.GetValue<long>());
            Assert.Equal(credits + coins, await Read(rt, w => w.CreditsAsync()));
        }
        var granted = (await UnopenedPacks(rt)).Skip(packsBefore).ToList();
        Assert.Equal(payout.Packs, granted.Select(p => p.Pack));
        Assert.All(granted, p => Assert.Equal(("draft_online", false), (p.Source, p.Untradeable)));
    }

    [Theory]
    [InlineData("DNF_WIN", true)]
    [InlineData("WIN", true)]
    [InlineData("LOSS", false)]
    public async Task EveryDraftMatchPaysMatchCoinsAndADnfWinCountsAsAWin(string reason, bool won)
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        // Professional and played past the server clock's minimum: the reward gate pays it unscaled.
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "PRO" });
        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        var credits = await Read(rt, w => w.CreditsAsync());
        var body = new JsonObject { ["endReason"] = reason, ["secondsPlayed"] = 5461 };

        var ended = rt.CallObject("POST", $"{G}/match/end", body);

        var award = rt.Game.Rules.Seasons.MatchCompletionRewards["draftSinglePlayer"].Coins;
        Assert.Equal((reason, award, award), (ended.Text("endReason"), ended.Int("participationAward"), ended.Int("matchCoins")));
        Assert.Equal(credits + ended.Int("matchCoins"), await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(won ? 1 : 0, (await Read(rt, w => w.DraftRunAsync()))!.Int("wins"));
    }

    [Fact]
    public async Task ConfirmingAnAlreadyFilledSlotKeepsThePick()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        await PinSeed(rt);
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 3 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 1, ["positionId"] = 3 });
        var picked = (await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!.Obj("3")!.DeepClone();

        // FIFA resumes on the last picked slot, offers it again and confirms another choice.
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 3 });
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 4, ["positionId"] = 3 });

        Assert.True(JsonNode.DeepEquals(picked, (await Read(rt, w => w.DraftRunAsync()))!.Obj("picked")!.Obj("3")));
    }

    [Fact]
    public async Task SavingAnEarlierOrLaterRunRewritesChildRowsExactly()
    {
        using var rt = Create();
        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        await PinSeed(rt);
        rt.Call("PUT", $"{G}/squad/mode/draft/choose/difficulty", new JsonObject { ["difficultyName"] = "EASY" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        var early = (await Read(rt, w => w.DraftRunAsync()))!;
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/captain");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0, ["positionId"] = 0 });
        rt.Call("PUT", $"{G}/squad/mode/draft/choices/player", new JsonObject { ["positionId"] = 5 });
        var late = (await Read(rt, w => w.DraftRunAsync()))!;
        Assert.NotEmpty(early.Arr("formationChoices")!);
        Assert.NotEmpty(late.Arr("currentChoices")!);

        // Each save must add, change and remove child rows until the stored run is exactly the saved one.
        foreach (var state in new[] { early, late, early, late })
        {
            await rt.Game.RunAsync("round-trip", w => w.SaveDraftRunAsync((JsonObject)state.DeepClone()), CancellationToken.None);
            var stored = (await Read(rt, w => w.DraftRunAsync()))!;
            Assert.True(JsonNode.DeepEquals(state, stored), $"saved {state.ToJsonString()} stored {stored.ToJsonString()}");
            Assert.Equal(state.Arr("formationChoices")!.Count, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_formation_choices"));
            Assert.Equal(state.Arr("seenAssets")!.Count, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_seen_footballers"));
        }
    }

    [Fact]
    public async Task LateFailuresRollBackRunChargeAndSettlement()
    {
        string? failOn = null;
        using var rt = Create(sql => { if (failOn is not null && sql.Contains(failOn, StringComparison.Ordinal)) throw new InvalidOperationException("synthetic late draft failure"); });
        var credits = await Read(rt, w => w.CreditsAsync());
        failOn = "INSERT INTO ultimateteam.draft_seen_footballers";
        Assert.Throws<InvalidOperationException>(() => rt.CallRaw("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" }));
        failOn = null;
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        Assert.Null(await Read(rt, w => w.DraftRunAsync()));

        rt.Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
        rt.Call("GET", $"{G}/squad/mode/draft/choices/formation");
        rt.Call("PUT", $"{G}/squad/mode/1/draft/choose", new JsonObject { ["choiceIndex"] = 0 });
        var before = (await Read(rt, w => w.DraftRunAsync()))!;
        failOn = "INSERT INTO ultimateteam.draft_choices";
        Assert.Throws<InvalidOperationException>(() => rt.CallRaw("GET", $"{G}/squad/mode/draft/choices/captain"));
        failOn = null;
        Assert.True(JsonNode.DeepEquals(before, await Read(rt, w => w.DraftRunAsync())));

        rt.Call("POST", $"{G}/squad/mode/1/draft/autocomplete");
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 900001 });
        rt.PlayedRealSeconds();
        var ready = (await Read(rt, w => w.DraftRunAsync()))!;
        var xp = (await Read(rt, w => w.ObjectivesAsync())).UserXp;
        failOn = "INSERT INTO ultimateteam.draft_formation_choices";
        Assert.Throws<InvalidOperationException>(() => rt.CallRaw("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN" }));
        failOn = null;
        Assert.True(JsonNode.DeepEquals(ready, await Read(rt, w => w.DraftRunAsync())));
        Assert.Equal(xp, (await Read(rt, w => w.ObjectivesAsync())).UserXp);
        rt.Call("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN" });
        Assert.Equal(1, (await Read(rt, w => w.DraftRunAsync()))!.Int("wins"));
    }

    [Fact]
    public async Task ConcurrentPurchasesChargeOnceAndSlotsAndClubsStaySeparate()
    {
        using var rt = Create();
        var credits = await Read(rt, w => w.CreditsAsync());
        var second = TestRuntime.Create(connectionString: rt.ConnectionString, relationalSlice: true, time: new TestRuntime.FixedTime(Now));
        try
        {
            using var barrier = new Barrier(2);
            var runtimes = new[] { rt, second };
            Parallel.For(0, 2, index =>
            {
                barrier.SignalAndWait();
                runtimes[index].Call("POST", $"{G}/purchase/mode/1/draft", new JsonObject { ["currency"] = "COINS" });
            });
        }
        finally
        {
            second.Detach();
        }
        Assert.Equal(credits - rt.Game.Rules.Draft.Entry.Coins, await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(1, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_runs"));

        rt.Call("POST", $"{G}/purchase/mode/0/draft", new JsonObject { ["currency"] = "COINS" });
        var single = (await Read(rt, w => w.DraftRunAsync(DraftSlot.SinglePlayer)))!;
        var online = (await Read(rt, w => w.DraftRunAsync(DraftSlot.Online)))!;
        Assert.Equal("SINGLE_PLAYER", single.Str("mode"));
        Assert.Equal("ONLINE", online.Str("mode"));
        Assert.Equal(DraftSlot.Online, await Read(rt, w => w.LastDraftSlotAsync()));
        rt.Call("POST", $"{G}/squad/mode/0/draft/autocomplete");
        Assert.True(JsonNode.DeepEquals(single, await Read(rt, w => w.DraftRunAsync(DraftSlot.SinglePlayer))));

        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000061, 900000062), 0), CancellationToken.None)).Account.ClubId;
        Assert.Null(await rt.Game.UnitOfWork.RunReadAsync(other, w => w.DraftRunAsync(), CancellationToken.None));
        await rt.Game.UnitOfWork.RunAsync(other, "other-draft", w => w.SaveDraftRunAsync(rt.Fut.Draft.NewRun()), CancellationToken.None);
        Assert.Equal(3, await Scalar(rt, "SELECT count(*) FROM ultimateteam.draft_runs"));
        Assert.True(JsonNode.DeepEquals(single, await Read(rt, w => w.DraftRunAsync(DraftSlot.SinglePlayer))));
    }

}
