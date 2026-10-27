using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class RelationalProgressionTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;

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

    [Fact]
    public async Task ObjectiveStateSavesChangesAndRemovalsAndReadsBackExactly()
    {
        using var rt = Create();
        var states = new[]
        {
            new ObjectivesState(10, false, new long[] { 3, 1 })
            {
                Stats = new Dictionary<string, long> { ["matches"] = 1, ["wins"] = 1 },
                ClaimedObjectives = new long[] { 7 },
            },
            // One counter changes, one is removed and one added; level claims are reordered and extended;
            // the objective claim is removed and a group claim added; XP and the reward flag change.
            new ObjectivesState(25, true, new long[] { 1, 3, 5 })
            {
                Stats = new Dictionary<string, long> { ["matches"] = 2, ["goals"] = 4 },
                ClaimedGroups = new long[] { 9 },
            },
            // Everything cleared.
            new ObjectivesState(0, false, Array.Empty<long>()),
        };
        foreach (var state in states)
        {
            await rt.Game.RunAsync("objectives-round-trip", w => w.SaveObjectivesAsync(state), CancellationToken.None);
            AssertObjectives(state, await Read(rt, w => w.ObjectivesAsync()));
            // The stored rows are exactly this state's counters and claims: nothing stale survives.
            Assert.Equal(state.Stats.Count, await Scalar(rt, "SELECT count(*) FROM ultimateteam.objective_counters WHERE club_id=@club"));
            Assert.Equal(state.ClaimedLevels.Count + state.ClaimedObjectives.Count + state.ClaimedGroups.Count,
                await Scalar(rt, "SELECT count(*) FROM ultimateteam.objective_claims WHERE club_id=@club"));
        }
        // A club without a header row reads as zero XP and unclaimed.
        await rt.Game.RunAsync("objectives-drop-header", w => w.ExecuteAsync("DELETE FROM ultimateteam.objectives WHERE club_id=@club"),
            CancellationToken.None);
        AssertObjectives(new ObjectivesState(0, false, Array.Empty<long>()), await Read(rt, w => w.ObjectivesAsync()));
    }

    private static void AssertObjectives(ObjectivesState expected, ObjectivesState actual)
    {
        Assert.Equal(expected.UserXp, actual.UserXp);
        Assert.Equal(expected.RewardClaimed, actual.RewardClaimed);
        Assert.Equal(expected.ClaimedLevels, actual.ClaimedLevels);
        Assert.Equal(expected.ClaimedObjectives, actual.ClaimedObjectives);
        Assert.Equal(expected.ClaimedGroups, actual.ClaimedGroups);
        Assert.Equal(expected.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal), actual.Stats.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private static JsonObject EndBody(long matchId, int mine = 2, int theirs = 0) => new()
    {
        ["endReason"] = mine > theirs ? "WIN" : "LOSS",
        ["matchId"] = matchId,
        ["secondsPlayed"] = 5461,
        ["myMatchStats"] = new JsonObject { ["goals"] = mine },
        ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs },
    };

    /// <summary>A progression route sequence that only reads and writes the converted tables.</summary>
    private static List<(string Step, JsonNode Body)> ProgressionFlow(TestRuntime rt)
    {
        var responses = new List<(string, JsonNode)>();
        void Step(string name, string method, string path, JsonNode? body = null) => responses.Add((name, rt.Call(method, path, body)));
        // Submission validates the saved squad, so the flow needs a set whose challenges this club can
        // actually field, and a real squad saved before each one. The empty squad it used to send was
        // refused with SBC_WRONG_PLAYER_COUNT and the progression writes below were never reached.
        // The seeded club is identical in both runtimes, so the chosen item ids still compare equal.
        var set = rt.Game.Sbc.Sets.First(s => s.Challenges.Count > 1 && s.Challenges.All(SbcFixtures.CanBuildSquad));
        var first = set.Challenges[0];
        Step("sets", "GET", G + "/sbs/sets");
        Step("sbc-squad", "PUT", $"{G}/sbs/challenge/{first.Id}/squad", SbcFixtures.SquadBody(rt, first));
        Step("sbc-submit", "PUT", $"{G}/sbs/challenge/{first.Id}", new JsonObject());
        Step("sbc-repeat", "PUT", $"{G}/sbs/challenge/{first.Id}", new JsonObject());
        foreach (var challenge in set.Challenges.Skip(1))
        {
            Step($"sbc-squad-{challenge.Id}", "PUT", $"{G}/sbs/challenge/{challenge.Id}/squad", SbcFixtures.SquadBody(rt, challenge));
            Step($"sbc-submit-{challenge.Id}", "PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
        }
        Step("sets-after", "GET", G + "/sbs/sets");
        Step("match-end", "POST", G + "/match/end", EndBody(41));
        Step("match-end-replay", "POST", G + "/match/end", EndBody(41, 0, 3));
        Step("match-end-loss", "POST", G + "/match/end", EndBody(42, 0, 1));
        // Kick Off is earned with one counted match; a claim before that is refused (409), not answered empty.
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        Step("campaign", "GET", G + "/scmp/campaign/active");
        Step("welcome-claim", "POST", G + "/scmp/campaign/group/1/rewards");
        Step("welcome-repeat", "POST", G + "/scmp/campaign/group/1/rewards");
        Step("level-claim", "POST", G + "/scmp/campaign/level/2/reward/0");
        Step("rivals-hub", "GET", G + "/rivals/user/hub");
        Step("champion-hub", "GET", G + "/champion/user/hub");
        Step("champion-stats", "GET", G + "/champion/user/stats");
        Step("leaderboard", "GET", G + "/leaderboards/period/Weekly/category/TOP/view/Current");
        return responses;
    }

    [Fact]
    public async Task ProgressionRoutesMatchLegacyResponsesAndReopen()
    {
        using var rt = Create();
        using var legacy = TestRuntime.Create(time: new TestRuntime.FixedTime(Now));
        var expected = ProgressionFlow(legacy);
        var actual = ProgressionFlow(rt);
        Assert.Equal(expected.Select(e => e.Step), actual.Select(a => a.Step));
        foreach (var ((step, body), (_, observed)) in expected.Zip(actual))
        {
            Assert.True(JsonNode.DeepEquals(body, observed), $"{step}: legacy {body.ToJsonString()} relational {observed.ToJsonString()}");
        }
        var reads = new[] { "/sbs/sets", "/scmp/campaign/active", "/rivals/user/hub", "/champion/user/hub" };
        var before = reads.Select(path => rt.Call("GET", G + path)).ToList();
        var replay = rt.Call("POST", G + "/match/end", EndBody(41));
        var connection = rt.Detach();
        using var reopened = Create(connection: connection);
        foreach (var (path, body) in reads.Zip(before))
        {
            Assert.True(JsonNode.DeepEquals(body, reopened.Call("GET", G + path)), path);
        }
        // ProgressionFlow records objective progress directly after its final match. The next
        // match response drains those one-time updates; reopening must not resend them.
        Assert.NotNull(replay["dynamicObjectivesUpdates"]);
        var settled = replay.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        var reopenedReplay = reopened.CallObject("POST", G + "/match/end", EndBody(41));
        Assert.False(reopenedReplay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, reopenedReplay));
        Assert.Equal(0, await Scalar(reopened, "SELECT count(*) FROM pg_tables WHERE schemaname='public'"));
    }

    [Fact]
    public async Task SeasonProgressRoundTripsTypedColumnsAndRejectsUnmappedFields()
    {
        using var rt = Create();
        var rivals = new JsonObject { ["seasonId"] = 3, ["division"] = 5, ["skillRating"] = 812, ["weeklyGames"] = 4, ["weeklyWins"] = 3,
            ["weeklyPoints"] = 9, ["wins"] = 20, ["draws"] = 2, ["losses"] = 7, ["titleWins"] = 1, ["placementMatchesPlayed"] = 5 };
        var champion = new JsonObject { ["playoffGames"] = 3, ["playoffWins"] = 2, ["finalsGames"] = 10, ["finalsWins"] = 7 };
        var emptyHub = rt.CallObject("GET", G + "/rivals/user/hub");
        await rt.Game.RunAsync("seasons", async w =>
        {
            await w.SaveSeasonProgressAsync("rivals", rivals);
            await w.SaveSeasonProgressAsync("champion", champion);
        }, CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(rivals, await Read(rt, w => w.SeasonProgressAsync("rivals"))));
        Assert.True(JsonNode.DeepEquals(champion, await Read(rt, w => w.SeasonProgressAsync("champion"))));

        // A partial document leaves absent fields NULL.
        await rt.Game.RunAsync("partial", w => w.SaveSeasonProgressAsync("rivals", new JsonObject { ["wins"] = 1, ["draws"] = null }), CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["wins"] = 1 }, await Read(rt, w => w.SeasonProgressAsync("rivals"))));

        foreach (var (competition, bad) in new[]
        {
            ("rivals", new JsonObject { ["rankPoints"] = 4 }), // a Python field with no column (matchesPlayed has one since 055)
            ("rivals", new JsonObject { ["wins"] = 1.5 }),
            ("rivals", new JsonObject { ["wins"] = "3" }),
            ("champion", new JsonObject { ["wins"] = 1 }),
            ("weekendleague", new JsonObject()),
        })
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => rt.Game.RunAsync("bad-season", w => w.SaveSeasonProgressAsync(competition, bad), CancellationToken.None));
        }
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["wins"] = 1 }, await Read(rt, w => w.SeasonProgressAsync("rivals"))));
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(rt, w => w.SeasonProgressAsync("weekendleague")));

        // A record without the current rules version is from before the retail Rivals (Python server 56145ed): the first
        // hub read clears it, so the club places again from the defaults.
        var partialHub = rt.CallObject("GET", G + "/rivals/user/hub");
        Assert.Equal(emptyHub.Int("skillPoints"), partialHub.Int("skillPoints"));
        Assert.Equal(emptyHub.Int("division"), partialHub.Int("division"));
        Assert.True(partialHub.Bool("inPlacementMatches"));
        var cleared = (await Read(rt, w => w.RivalsRecordAsync()))!;
        Assert.Equal((0L, rt.Game.Rules.Seasons.Rivals.RatingVersion), (cleared.Wins, cleared.RatingVersion));
    }

    [Fact]
    public async Task RivalsStandingsReadEveryNamedClubInClubOrder()
    {
        using var rt = Create();
        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000021, 900000022), 0, "Second Club"), CancellationToken.None)).Account.ClubId;
        var unnamed = (await rt.Game.RunAsync("unnamed", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000023, 900000024), 0), CancellationToken.None)).Account.ClubId;
        await rt.Game.UnitOfWork.RunAsync(other, "other-rivals", w => w.SaveSeasonProgressAsync("rivals", new JsonObject { ["skillRating"] = 1500, ["wins"] = 9 }), CancellationToken.None);
        await rt.Game.UnitOfWork.RunAsync(unnamed, "unnamed-rivals", w => w.SaveSeasonProgressAsync("rivals", new JsonObject { ["skillRating"] = 9999 }), CancellationToken.None);
        var standings = await Read(rt, w => w.RivalsStandingsAsync());
        Assert.Equal(new long[] { rt.Game.Config.PersonaId, 900000021 }, standings.Select(s => s.PersonaId));
        Assert.Empty(standings[0].Progress);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["skillRating"] = 1500, ["wins"] = 9 }, standings[1].Progress));
        Assert.Empty(await Read(rt, w => w.SeasonProgressAsync("rivals")));
        // The leaderboard ranks the retail record: a placed club's Skill Rating (a record without the rules version counts 0).
        RivalsRecord Placed(long rating) => new()
        {
            RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion, SkillRating = rating, Placed = true, Wins = 9, WeekDivision = 3, BestDivision = 3, Division = 3,
        };
        await rt.Game.UnitOfWork.RunAsync(other, "other-rivals-retail", w => w.SaveRivalsRecordAsync(Placed(1500)), CancellationToken.None);
        await rt.Game.UnitOfWork.RunAsync(unnamed, "unnamed-rivals-retail", w => w.SaveRivalsRecordAsync(Placed(9999)), CancellationToken.None);
        // Select the Skill Rating category explicitly; the current default is Match Earnings.
        var board = rt.CallObject("GET", G + "/leaderboards/period/Weekly/category/rivals/view/TOP100");
        var entries = board["entries"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.Equal(new[] { "Second Club", rt.Game.Config.DisplayName }, entries.Select(entry => entry.Text("clubName")));
        Assert.Equal(1500, entries[0]["score"]!.AsObject().Int("value"));
        Assert.DoesNotContain(entries, entry => entry["score"]!.AsObject().Int("value") == 9999);
    }

    [Fact]
    public async Task MatchSettlementRollsBackLateFailuresAndSettlesOnceAcrossRunnersAndClubs()
    {
        var fail = false;
        using var rt = Create(sql => { if (fail && sql.StartsWith("UPDATE ultimateteam.match_settlements", StringComparison.Ordinal)) throw new InvalidOperationException("synthetic late settlement failure"); });
        // One settled 2-0 Rivals win (a match the reward gate pays): the result award plus its coin rows (two goals and
        // nothing else reported).
        var win = rt.Game.Rules.Seasons.MatchCompletionRewards["rivals"].Coins;
        Task Pair(long gameId) => rt.Game.RunAsync("pair", w => rt.Fut.Rivals.NotePairingAsync(w, new OnlineOpponent(true, "opponent", 1_234_567, gameId), 1000), CancellationToken.None);
        await Pair(51);
        rt.PlayedRealSeconds();
        var credits = await Read(rt, w => w.CreditsAsync());
        fail = true;
        Assert.Throws<InvalidOperationException>(() => rt.CallRaw("POST", G + "/match/end", EndBody(51)));
        fail = false;
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(0, await Scalar(rt, "SELECT count(*) FROM ultimateteam.match_settlements WHERE club_id=@club"));
        Assert.Equal(0, (await Read(rt, w => w.ObjectivesAsync())).Stats.GetValueOrDefault("matches"));

        var settled = rt.CallObject("POST", G + "/match/end", EndBody(51));
        Assert.Equal(credits + win, settled.Int("credits"));
        // Objective deltas are attached by the route once, outside the stored settlement.
        static JsonObject MatchFields(JsonObject response)
        {
            var copy = response.DeepClone().AsObject();
            copy.Remove("dynamicObjectivesUpdates");
            return copy;
        }
        Assert.True(settled.ContainsKey("dynamicObjectivesUpdates"));
        var replay = rt.CallObject("POST", G + "/match/end", EndBody(51, 0, 5));
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(MatchFields(settled), replay));
        Assert.Equal(credits + win, await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(1, (await Read(rt, w => w.ObjectivesAsync())).Stats["matches"]);

        // Two independent runners (another server process) receive one report at the same moment.
        var second = TestRuntime.Create(connectionString: rt.ConnectionString, relationalSlice: true, time: new TestRuntime.FixedTime(Now));
        try
        {
            await Pair(52);
            rt.PlayedRealSeconds();
            using var barrier = new Barrier(2);
            var responses = new JsonObject[2];
            var runtimes = new[] { rt, second };
            Parallel.For(0, 2, index =>
            {
                barrier.SignalAndWait();
                responses[index] = runtimes[index].CallObject("POST", G + "/match/end", EndBody(52));
            });
            Assert.Single(responses, response => response.ContainsKey("dynamicObjectivesUpdates"));
            Assert.True(JsonNode.DeepEquals(MatchFields(responses[0]), MatchFields(responses[1])));
            Assert.Equal(credits + 2 * win, await Read(rt, w => w.CreditsAsync()));
            Assert.Equal(2, (await Read(rt, w => w.ObjectivesAsync())).Stats["matches"]);
        }
        finally
        {
            second.Detach();
        }

        // Match keys are club-scoped: another club settles the same key independently.
        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000031, 900000032), 0), CancellationToken.None)).Account.ClubId;
        Assert.True(await rt.Game.UnitOfWork.RunAsync(other, "other-claim", w => w.TryBeginMatchSettlementAsync("id:52"), CancellationToken.None));
        Assert.Null(await rt.Game.UnitOfWork.RunReadAsync(other, w => w.MatchSettlementAsync("id:52"), CancellationToken.None));
        Assert.NotNull(await Read(rt, w => w.MatchSettlementAsync("id:52")));
    }

    [Fact]
    public async Task MatchCreateStateAndDraftSlotUseTypedClubColumns()
    {
        using var rt = Create();
        Assert.False(await Read(rt, w => w.LastMatchCreateIsDraftAsync()));
        Assert.Equal(DraftSlot.SinglePlayer, await Read(rt, w => w.LastDraftSlotAsync()));
        Assert.Equal(200, rt.CallRaw("POST", G + "/match", new JsonObject { ["squadId"] = 1, ["sqbtEventId"] = 1 }).Status);
        Assert.False(await Read(rt, w => w.LastMatchCreateIsDraftAsync()));
        await rt.Game.RunAsync("state", async w =>
        {
            await w.SetLastMatchCreateIsDraftAsync(true);
            await w.SetLastDraftSlotAsync(DraftSlot.Online);
        }, CancellationToken.None);
        Assert.True(await Read(rt, w => w.LastMatchCreateIsDraftAsync()));
        Assert.Equal(DraftSlot.Online, await Read(rt, w => w.LastDraftSlotAsync()));
        Assert.Equal(1, await Scalar(rt, "SELECT count(*) FROM ultimateteam.clubs WHERE id=@club AND last_match_create_is_draft AND last_draft_slot=2"));
    }

    [Fact]
    public async Task SbcSubmissionRollsBackLateFailuresPaysOnceAndKeepsCountsClubScoped()
    {
        var fail = false;
        using var rt = Create(sql => { if (fail && sql.StartsWith("INSERT INTO ultimateteam.unopened_packs", StringComparison.Ordinal)) throw new InvalidOperationException("synthetic late SBC failure"); });
        var challenge = rt.Game.Sbc.Sets.SelectMany(s => s.Challenges)
            .First(c => rt.Game.Sbc.FindSet(c.SetId)!.Challenges.Count > 1
                && c.Source.ToJsonString().Contains("PACK", StringComparison.OrdinalIgnoreCase)
                && SbcFixtures.CanBuildSquad(c));
        // Submission validates the saved squad before it reaches the pack insert the fault targets,
        // so the fixture has to own a squad the challenge actually accepts.
        SbcFixtures.SaveSatisfyingSquad(rt, challenge);
        var credits = await Read(rt, w => w.CreditsAsync());
        fail = true;
        var failed = rt.CallRaw("PUT", $"{G}/sbs/challenge/{challenge.Id}", new JsonObject());
        fail = false;
        Assert.Equal(500, failed.Status);
        Assert.Empty(await Read(rt, w => w.SbcCompletedAsync()));
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
        Assert.Empty(await Read(rt, w => w.UnopenedPackIdsAsync()));
        Assert.Equal(0, (await Read(rt, w => w.ObjectivesAsync())).Stats.GetValueOrDefault("sbcs"));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rt.Fut.Sbc.SubmitAsync(checked((int)challenge.Id)))));
        Assert.All(results, r => Assert.Equal(200, r.Status));
        Assert.Single(results, r => ((JsonObject)r.Body!).Int("grantedItemsCount") > 0);
        Assert.Equal(new[] { challenge.Key }, await Read(rt, w => w.SbcCompletedAsync()));
        Assert.Equal(1, (await Read(rt, w => w.ObjectivesAsync())).Stats["sbcs"]);
        var paid = await Read(rt, w => w.UnopenedPackIdsAsync());
        Assert.NotEmpty(paid);

        // A short saved squad blocks another challenge; the count is typed and club-scoped. The card
        // has to be one the club owns: saving the squad checks ownership too, so the old literal
        // 900000000001 was refused at the save and the block below was never reached. Submission now
        // refuses the short squad outright instead of returning a 200 that grants nothing.
        // A challenge that takes more than one player, so a single card is short (First Exchange takes exactly one).
        var next = rt.Game.Sbc.FindSet(challenge.SetId)!.Challenges.First(c => c.Id != challenge.Id
            && c.Source.Obj("requirements")!.Arr("rules")!.OfType<JsonObject>().Any(rule => rule.Text("type") == "PLAYERS" && rule.Obj("attributes")!.Int("max") > 1));
        rt.Call("PUT", $"{G}/sbs/challenge/{next.Id}/squad", new JsonObject { ["players"] = new JsonArray(new JsonObject { ["index"] = SbcFixtures.FreeSlots(rt, next)[0], ["itemData"] = new JsonObject { ["id"] = SbcFixtures.AnyOwnedPlayerItemId(rt) } }) });
        Assert.Equal(1, await Read(rt, w => w.SbcSquadCountAsync(next.Id)));
        var shortSquad = rt.CallRaw("PUT", $"{G}/sbs/challenge/{next.Id}", new JsonObject());
        Assert.Equal(200, shortSquad.Status);
        Assert.Equal("INVALID", shortSquad.Body["status"]!.S());
        Assert.Contains("Squad needs", shortSquad.Body["message"]!.S());
        Assert.DoesNotContain(next.Key, await Read(rt, w => w.SbcCompletedAsync()));
        var other = (await rt.Game.RunAsync("other", w => w.EnsureRelationalClubAsync(new ClubIdentity(900000041, 900000042), 0), CancellationToken.None)).Account.ClubId;
        Assert.Equal(0, await rt.Game.UnitOfWork.RunReadAsync(other, w => w.SbcSquadCountAsync(next.Id), CancellationToken.None));
        Assert.Empty(await rt.Game.UnitOfWork.RunReadAsync(other, w => w.SbcCompletedAsync(), CancellationToken.None));
        Assert.True(await rt.Game.UnitOfWork.RunAsync(other, "other-sbc", w => w.TryMarkSbcCompletedAsync(challenge.Key), CancellationToken.None));
        Assert.Equal(paid, await Read(rt, w => w.UnopenedPackIdsAsync()));
    }

    [Fact]
    public async Task ObjectiveClaimsPayOnceUnderConcurrencyAndKeepClaimOrder()
    {
        using var rt = Create();
        // The Welcome group is earned with one match.
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        var credits = await Read(rt, w => w.CreditsAsync());
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rt.Call("POST", G + "/scmp/campaign/group/1/rewards"))));
        Assert.Single(claims, c => !(c.AsObject()["alreadyClaimed"]?.GetValue<bool>() ?? false));
        var state = await Read(rt, w => w.ObjectivesAsync());
        Assert.True(state.RewardClaimed);
        var xpPerLevel = rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel;
        await rt.Game.RunAsync("xp", w => w.AddObjectivesXpAsync(xpPerLevel * 3), CancellationToken.None);
        foreach (var level in new[] { 3, 2 })
        {
            rt.Call("POST", $"{G}/scmp/campaign/level/{level}/reward/0");
        }
        Assert.True(rt.CallObject("POST", G + "/scmp/campaign/level/2/reward/0")["alreadyClaimed"]!.GetValue<bool>());
        Assert.Equal(new long[] { 2, 3 }, (await Read(rt, w => w.ObjectivesAsync())).ClaimedLevels);
        Assert.Equal(new long[] { 2, 3 }, await Read(rt, w => w.QueryAsync(
            "SELECT claim_id FROM ultimateteam.objective_claims WHERE club_id=@club AND scope='level' ORDER BY ordinal", r => r.GetInt64(0))));
        Assert.True(await Read(rt, w => w.CreditsAsync()) >= credits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SeasonLevelPaysOnceAcrossConcurrentRoutesAndRuntimeReload(int level)
    {
        using var rt = Create();
        await rt.Game.RunAsync("earned-level", w => w.AddObjectivesXpAsync(
            rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel * level), CancellationToken.None);
        using var other = Create(connection: rt.ConnectionString);
        var credits = await Read(rt, w => w.CreditsAsync());
        var packCount = (await Read(rt, w => w.UnopenedPackIdsAsync())).Count;
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? rt : other).CallObject(index % 2 == 0 ? "POST" : "PUT",
                $"{G}/scmp/campaign/level/{level}/reward/0"))));
        Assert.Single(replies, reply => !reply.ContainsKey("alreadyClaimed"));
        Assert.All(replies.Where(reply => reply.ContainsKey("alreadyClaimed")),
            reply => Assert.True(reply["alreadyClaimed"]!.GetValue<bool>()));
        var reward = rt.Game.Rules.Objectives.SeasonProgress.RewardFor(level);
        Assert.Equal(credits + (reward.Coins ?? 0), await Read(rt, w => w.CreditsAsync()));
        Assert.Equal(packCount + (reward.Pack is null ? 0 : 1), (await Read(rt, w => w.UnopenedPackIdsAsync())).Count);
        using var reloaded = Create(connection: rt.ConnectionString);
        Assert.True(reloaded.CallObject("POST", $"{G}/scmp/campaign/level/{level}/reward/0")["alreadyClaimed"]!.GetValue<bool>());
        Assert.Equal(await Read(rt, w => w.UnopenedPackIdsAsync()), await Read(reloaded, w => w.UnopenedPackIdsAsync()));
        Assert.Equal(credits + (reward.Coins ?? 0), await Read(reloaded, w => w.CreditsAsync()));
    }

    /// <summary>
    /// A claimed Season Progress level must still read as claimed after FUT reloads: CardsDLL shows level state 2 as
    /// "Claimed" (with the option named by chosenOption) and works out a missing state (-1) from XP, which is "Claim Reward".
    /// </summary>
    [Fact]
    public async Task ClaimedSeasonLevelsReadAsClaimedAfterAFreshLoad()
    {
        using var rt = Create();
        await rt.Game.RunAsync("earned-levels", w => w.AddObjectivesXpAsync(
            rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel * 3), CancellationToken.None);
        rt.Call("PUT", $"{G}/scmp/campaign/level/1/reward/0");
        rt.Call("PUT", $"{G}/scmp/campaign/level/3/reward/0");

        using var reloaded = Create(connection: rt.ConnectionString);
        foreach (var path in new[] { "/scmp/campaign?type=active", "/scmp/campaign/active" })
        {
            var levels = reloaded.CallObject("GET", G + path)["levels"]!.AsArray().Select(level => level!.AsObject()).ToList();
            foreach (var level in levels)
            {
                var id = level["levelId"]!.GetValue<long>();
                var claimed = id is 1 or 3;
                Assert.Equal(claimed ? 2 : -1, level["state"]!.GetValue<long>());
                Assert.Equal(claimed ? 0 : -1, level["chosenOption"]!.GetValue<long>());
                if (claimed)
                {
                    // The tile looks the claimed option up by optionId.
                    Assert.Contains(level["rewardOptionList"]!.AsArray(), option => option!["optionId"]!.GetValue<long>() == 0);
                }
            }
            Assert.Equal(2, levels.Count(level => level["state"]!.GetValue<long>() == 2));
        }
    }

    [Theory]
    [InlineData("xp")]
    [InlineData("counter")]
    [InlineData("pack")]
    public async Task ObjectiveProgressUpdatesCannotEraseAConcurrentLevelClaim(string update)
    {
        using var rt = Create();
        await rt.Game.RunAsync("earned-level", w => w.AddObjectivesXpAsync(
            rt.Game.Rules.Objectives.SeasonProgress.XpPerLevel * 2), CancellationToken.None);
        using var lockReached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var armed = false;
        // Independent runners model two server connections; their in-process club gates are separate.
        using var other = Create(sql =>
        {
            if (!armed || !sql.Contains("club.lock.exclusive", StringComparison.Ordinal)) return;
            lockReached.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Progress lock was not released.");
        }, rt.ConnectionString);
        armed = true;
        var progress = Task.Run(() => other.Game.RunAsync("concurrent-progress", async w =>
        {
            if (update == "xp") await w.AddObjectivesXpAsync(1);
            else if (update == "counter") await w.BumpObjectiveStatAsync("packs", 1, 1);
            else await other.Fut.Objectives.RecordPackOpenedAsync(w);
        }, CancellationToken.None));
        try
        {
            Assert.True(lockReached.Wait(TimeSpan.FromSeconds(20)), "Progress never reached its club lock.");
            var claim = await rt.Fut.Objectives.ClaimLevelAsync(2);
            Assert.False(claim.ContainsKey("alreadyClaimed"));
        }
        finally
        {
            release.Set();
            await progress;
        }
        Assert.Contains(2L, (await Read(rt, w => w.ObjectivesAsync())).ClaimedLevels);
        var packs = await Read(rt, w => w.UnopenedPackIdsAsync());
        var credits = await Read(rt, w => w.CreditsAsync());
        var retry = await rt.Fut.Objectives.ClaimLevelAsync(2);
        Assert.True(retry["alreadyClaimed"]!.GetValue<bool>());
        Assert.Equal(packs, await Read(rt, w => w.UnopenedPackIdsAsync()));
        Assert.Equal(credits, await Read(rt, w => w.CreditsAsync()));
    }
}
