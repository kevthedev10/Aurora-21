using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The FUT Champions Weekend League as in FIFA 21 retail (Python server 56145ed): entry for FUT Champions Points, country
/// registration, its own match scoring, and rewards settled once for every club at the rewards time and claimed once.
/// </summary>
public sealed class WeekendLeagueTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Thursday 2026-10-08 12:00 UTC: Weekend League 2962, the first that needs an entry, starts the next day at 07:00.</summary>
    private const long BeforeWeekendLeague = 1_791_460_800;

    /// <summary>Friday 2026-10-09 08:00 UTC: Weekend League 2962 is live and inside its grace hours.</summary>
    private const long DuringWeekendLeague = 1_791_532_800;

    /// <summary>Friday 2026-10-02 08:00 UTC: Weekend League 2961 (the week-one test), open to every club without points.</summary>
    private const long DuringOpenWeekendLeague = 1_790_928_000;

    /// <summary>Monday 2026-10-05 16:00 UTC (17:00 BST): Weekend League 2961's rewards time.</summary>
    private const long MondayRewards2961 = 1_791_216_000;

    /// <summary>Monday 2026-10-12 16:00 UTC (17:00 BST): Weekend League 2962's rewards time.</summary>
    private const long MondayRewards2962 = 1_791_820_800;

    private static RivalsRecord Placed(TestRuntime rt, long fcPoints) => new()
    {
        RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion,
        WeekId = 2961,
        SkillRating = 1100,
        Placed = true,
        PlacementPlayed = 5,
        WeekDivision = 5,
        BestDivision = 5,
        Division = 5,
        FcPoints = fcPoints,
    };

    private static RivalsRecord Rivals(TestRuntime rt) => rt.Game.InUnit(uow => uow.RivalsRecordAsync())!;

    private static JsonObject PlayWeekendLeagueMatch(TestRuntime rt, long matchId, string reason, long mine, long theirs, long myPens = 0, long theirPens = 0,
        long flags = 0, string? gid = null)
    {
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 });
        rt.PlayedRealSeconds();
        var end = new JsonObject
        {
            ["endReason"] = reason,
            ["matchId"] = matchId,
            ["matchStatusFlags"] = flags,
            // A match the server's clock watched past its minimum length (secondsPlayed is only a client claim).
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = mine, ["penaltyGoals"] = myPens },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs, ["penaltyGoals"] = theirPens },
        };
        if (gid is not null)
        {
            end["gid"] = gid;
        }
        return rt.CallObject("POST", $"{G}/match/end", end);
    }

    private static long Played(TestRuntime rt) => rt.CallObject("GET", $"{G}/champion/user/stats").Int("gamesPlayed");

    [Fact]
    public void EveryCompetitionRewardPackExists()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules;
        var packs = rules.RivalsRewards.PackIds.Concat(rules.ChampionsRewards.PackIds).Concat(rules.SquadBattlesRewards.PackIds).Distinct().ToList();
        Assert.Contains(550L, packs);
        Assert.Contains(552L, packs);
        Assert.Contains(553L, packs);
        Assert.All(packs, packId => Assert.True(JsonValues.HasValue(rt.Game.Packs.GetPackConfig(packId).Get("name")), $"pack {packId}"));
    }

    [Fact]
    public void AnEntryCostsTheQualificationPointsOnceForTheNextWeekendLeague()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(BeforeWeekendLeague));
        var access = $"{G}/rivals/user/weekendleague/access";

        var (status, body, _) = rt.CallRaw("POST", access);
        Assert.Equal((400, "PLACEMENT_NOT_COMPLETE"), (status, body["reason"]!.GetValue<string>()));
        // From week two an entry costs 1,300 points (owner decision D11).
        Assert.Equal(1_300, rt.Game.Rules.Seasons.WeekendLeague.QualificationPoints);
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 1299)));
        (status, body, _) = rt.CallRaw("POST", access);
        Assert.Equal((400, "NOT_ENOUGH_POINTS"), (status, body["reason"]!.GetValue<string>()));
        Assert.False(rt.CallObject("GET", $"{G}/rivals/user/hub").Bool("qualifiedInWeekendLeague"));

        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 1400)));
        rt.Call("POST", access);
        Assert.Equal(100, Rivals(rt).FcPoints);
        (status, body, _) = rt.CallRaw("POST", access);
        Assert.Equal((400, "ALREADY_QUALIFIED"), (status, body["reason"]!.GetValue<string>()));
        Assert.Equal(100, Rivals(rt).FcPoints);

        Assert.True(rt.CallObject("GET", $"{G}/rivals/user/hub").Bool("qualifiedInWeekendLeague"));
        // Before it starts the calendar holds only the next Weekend League, qualified; the hub is about it too.
        var home = rt.CallObject("GET", $"{G}/hub")["championEvent"]!.AsObject();
        Assert.Equal(24, home.Int("gracePeriod"));
        var next = Assert.Single(home["upcomingChampionEvents"]!.AsArray().OfType<JsonObject>());
        Assert.Equal((2962L, 1_791_529_200L, false, true), (next.Int("championEventId"), next.Int("startTime"), next.Bool("active"), next.Bool("qualified")));
        var hub = rt.CallObject("GET", $"{G}/champion/user/hub");
        Assert.Equal(new long[] { 2962 }, hub["qualifiedChampionLeagueIds"]!.AsArray().Select(id => id!.GetValue<long>()));
        var league = hub["activeChampionLeagues"]![0]!.AsObject();
        Assert.Equal(2962, league.Int("id"));
        // FIFA is told 20 matches and all six fixed Top 25 bands, even when the previous weekend ranked nobody.
        Assert.Equal((20L, 5L), (league.Int("maxMatches"), league.Int("minMatchesToRank")));
        Assert.Equal(6 + 12, league["prizeTiers"]!.AsArray().Count);
        Assert.Equal("1:1-1,2:2-5,3:6-10,4:11-15,5:16-20,6:21-25", string.Join(",", LeaderboardTiers(league)
            .Select(tier => $"{tier.Int("tierLevel")}:{tier.Int("tierStart")}-{tier.Int("tierEnd")}")));
    }

    [Fact]
    public void AWeekendLeagueMatchIsWonOrLostAndAddsToTheRivalsWeeklyScore()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringWeekendLeague));
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 2000)));
        // Inside the grace hours a redemption enters the live Weekend League.
        rt.Call("POST", $"{G}/rivals/user/weekendleague/access");

        // Country registration: FIFA reads the region; the next hub carries the country.
        Assert.Equal("EU", rt.CallObject("POST", $"{G}/champion/user/registration", new JsonObject { ["competitionCountryCode"] = "nl" }).Text("competitionRegion"));
        Assert.Equal("NA", rt.CallObject("POST", $"{G}/champion/user/registration", new JsonObject { ["competitionCountryCode"] = "us" }).Text("competitionRegion"));
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/champion/user/registration", new JsonObject { ["competitionCountryCode"] = "1" }).Status);
        Assert.Equal("US", rt.CallObject("GET", $"{G}/champion/user/hub")["userRegistration"]!.AsObject().Text("competitionCountryCode"));

        // A level score goes to penalties: 1-1, 4-3 on penalties is a win. A Rivals pairing taken by it is not rated.
        rt.Game.InUnit(uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, 5), 1100));
        var won = PlayWeekendLeagueMatch(rt, 5, "DRAW", 1, 1, myPens: 4, theirPens: 3);
        Assert.Equal((1L, 0L), (won.Int("won"), won.Int("draw")));
        Assert.False(won.ContainsKey("rivalsScore"));
        var stats = rt.CallObject("GET", $"{G}/champion/user/stats");
        Assert.Equal((2962L, 1L, 1L, 19L), (stats.Int("championEventId"), stats.Int("gamesPlayed"), stats.Int("score"), stats.Int("gamesRemaining")));
        var rivals = Rivals(rt);
        Assert.Equal((1100L, 400L, 1L), (rivals.SkillRating, rivals.WeeklyScore, rivals.WeeklyCounted));

        // A quit is a loss whatever the score, and adds no Weekly Score.
        var quit = PlayWeekendLeagueMatch(rt, 6, "QUIT", 2, 0);
        Assert.Equal(1, quit.Int("loss"));
        var hub = rt.CallObject("GET", $"{G}/champion/user/hub");
        // userStats follows previousChampionEvents: weekly previous, weekly current, monthly previous, monthly current.
        Assert.Equal(4, hub["userStats"]!.AsArray().Count);
        Assert.Equal(hub["previousChampionEvents"]!.AsArray().Select(league => league!.AsObject().Int("id")), hub["userStats"]!.AsArray().Select(stat => stat!.AsObject().Int("championEventId")));
        var mine = hub["userStats"]![1]!.AsObject();
        Assert.Equal((2962L, 2L, 1L, 0L), (mine.Int("championEventId"), mine.Int("gamesPlayed"), mine.Int("score"), mine.Int("winForm")));
        Assert.Equal(2L, hub["userStats"]![3]!.AsObject().Int("gamesPlayed"));
        Assert.Equal(400, Rivals(rt).WeeklyScore);
        // The club record counts both.
        var user = rt.CallObject("GET", $"{G}/user");
        Assert.Equal((1L, 1L), (user.Int("won"), user.Int("loss")));
    }

    [Fact]
    public void AWeekendLeagueMatchAfterTheWeeksCountingMatchesAddsNoWeeklyScore()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringWeekendLeague));
        var record = Placed(rt, fcPoints: 2000);
        record.WeekId = rt.Game.Rules.Seasons.Rivals.WeekIndex(DuringWeekendLeague);
        record.WeeklyScore = 9000;
        record.WeeklyCounted = rt.Game.Rules.RivalsRewards.WeeklyScore.MatchCap;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        rt.Call("POST", $"{G}/rivals/user/weekendleague/access");

        PlayWeekendLeagueMatch(rt, 5, "WIN", 2, 0);
        // The Weekend League counts the match; the Rivals week has used its 30 counting matches.
        Assert.Equal(1, Played(rt));
        var rivals = Rivals(rt);
        Assert.Equal((9000L, 30L), (rivals.WeeklyScore, rivals.WeeklyCounted));
    }

    [Fact]
    public async Task WithWeekendLeagueCountsOffAMatchLeavesTheRivalsWeeklyScoreAlone()
    {
        using var first = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringWeekendLeague));
        Assert.True(first.Game.Rules.RivalsRewards.WeeklyScore.WeekendLeagueCounts);
        await using (var source = Npgsql.NpgsqlDataSource.Create(first.ConnectionString))
        await using (var off = source.CreateCommand("""
            UPDATE ultimateteam.content_rules SET value = 'false'::jsonb
            WHERE file = 'rivals_rewards' AND path = '["weeklyScore","weekendLeagueCounts"]'::jsonb
            """))
        {
            Assert.Equal(1, await off.ExecuteNonQueryAsync());
        }
        using var rt = TestRuntime.Create(connectionString: first.ConnectionString, time: new TestRuntime.FixedTime(DuringWeekendLeague));
        Assert.False(rt.Game.Rules.RivalsRewards.WeeklyScore.WeekendLeagueCounts);
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 2000)));
        rt.Call("POST", $"{G}/rivals/user/weekendleague/access");

        var won = PlayWeekendLeagueMatch(rt, 5, "WIN", 2, 0);
        Assert.Equal(1, won.Int("won"));
        Assert.Equal(1, Played(rt));
        var rivals = Rivals(rt);
        Assert.Equal((0L, 0L), (rivals.WeeklyScore, rivals.WeeklyCounted));
    }

    [Fact]
    public void OnlyAnEntryOrAnOpenWeekendLeagueCountsItsMatches()
    {
        using (var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringWeekendLeague)))
        {
            PlayWeekendLeagueMatch(rt, 7, "WIN", 2, 0);
            Assert.Equal(0, rt.CallObject("GET", $"{G}/champion/user/stats").Int("gamesPlayed"));
        }
        // Weekend League 2961, the week-one test, is the only open one: every club plays it without points, placed or not.
        using var open = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        Assert.Equal(new long[] { 2961 }, open.Game.Rules.Seasons.WeekendLeague.OpenEvents);
        PlayWeekendLeagueMatch(open, 8, "WIN", 2, 0);
        var stats = open.CallObject("GET", $"{G}/champion/user/stats");
        Assert.Equal((2961L, 1L, 1L), (stats.Int("championEventId"), stats.Int("gamesPlayed"), stats.Int("score")));
        // An unplaced club's Weekly Score is not counted.
        Assert.Null(open.Game.InUnit(uow => uow.RivalsRecordAsync()));
    }

    [Fact]
    public void AWeekendLeagueIsSettledAtItsRewardsTimeAndClaimedOnce()
    {
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var unranked = players.Acquire(PlayerIdentity.Of("222222"));
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 1990)));
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 6, 4, 0, 2, 2)));
        // Fewer than the minimum matches: not ranked.
        unranked.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 3, 3, 0, 0, 3)));
        Assert.Empty(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray());
        // Still nothing a second before 17:00 UK time on the Monday it ends (BST: 16:00 UTC).
        clock.Now = MondayRewards2961 - 1;
        Assert.Empty(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray());

        clock.Now = MondayRewards2961;
        var home = rt.CallObject("GET", $"{G}/hub")["championEvent"]!.AsObject();
        Assert.Equal(2961, Assert.Single(home["unclaimedPrizesChampionEvents"]!.AsArray().OfType<JsonObject>()).Int("championEventId"));
        var rank = rt.Game.Rules.ChampionsRewards.Bands()[0].Rank;
        Assert.Equal(("Rank 1", 150_000L, 2000L), (rank.Name, rank.Coins, rank.FcPoints));
        var credits = rt.Game.Store().Credits();
        var packs = rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count;

        var prize = Assert.Single(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        // Four wins still receive the Rank 1 band when first in 2961 (the six-win minimum starts with 2962); the minimum
        // matches requirement remains. It shows position 1 and the rank four wins earn (Bronze 1), not Elite 1.
        Assert.False(rank.ByWins);
        Assert.Equal((2961L, 1L, 10L), (prize.Int("eventId"), prize.Int("rank"), prize.Int("tierLevel")));
        Assert.Equal(credits + rank.Coins, rt.Game.Store().Credits());
        Assert.Equal(packs + rank.Packs.Sum(pack => pack.Count), rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count);
        // FUT Champions Points stop at the cap.
        Assert.Equal(Math.Min(2000, 1990 + rank.FcPoints), Rivals(rt).FcPoints);

        Assert.Empty(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray());
        Assert.Equal(credits + rank.Coins, rt.Game.Store().Credits());
        Assert.Empty(rt.CallObject("GET", $"{G}/champion/user/hub")["unclaimedPrizesChampionEvents"]!.AsArray());
        Assert.Empty(unranked.Session.Game.InUnit(uow => uow.UnclaimedChampionResultsAsync(2961, 2961)));
    }

    [Fact]
    public void ATopPlaceNeedsSixWinsFromWeekendLeague2962()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        var rewards = rt.Game.Rules.ChampionsRewards;
        var maxMatches = rt.Game.Rules.Seasons.WeekendLeague.MaxMatches;
        Assert.Equal((6, 2962L), (rewards.MinWinsForTop, rewards.MinWinsForTopFromEvent));
        (long, long, long, long)[] rows = [(1L, 6L, 10L, 0L), (2L, 5L, 10L, 0L), (3L, 0L, 5L, 0L)];
        // From 2962: six wins in first take the Rank 1 band; five in second and none in third take the ranks their wins earn.
        var standings = rewards.Standings(2962, rows, maxMatches, rewards.Bands());
        Assert.Equal(["Rank 1", "Silver 3", "Bronze 3"], standings.Select(standing => standing.Rank.Name));
        Assert.Equal([1L, 2L, 3L], standings.Select(standing => standing.Position));
        // 2961 is not changed retroactively: its Top places need no wins.
        Assert.Equal(["Rank 1", "Rank 2-5", "Rank 2-5"], rewards.Standings(2961, rows, maxMatches, rewards.Bands()).Select(standing => standing.Rank.Name));
    }

    [Fact]
    public void RewardsArriveAt17UkTimeOnTheMondayAWeekendLeagueEnds()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringWeekendLeague));
        var rules = rt.Game.Rules.Seasons.WeekendLeague;
        // BST (UTC+1): 16:00 UTC.  Weekend League 2960 ends Mon 28 Sep 2026 07:00 UTC.
        Assert.Equal(1_790_611_200, rules.RewardsTime(2960));
        Assert.Equal(MondayRewards2961, rules.RewardsTime(2961));
        // GMT after the clocks go back (Sun 25 Oct 2026): 17:00 UTC on Mon 26 Oct and Mon 2 Nov.
        Assert.Equal(1_793_034_000, rules.RewardsTime(2964));
        Assert.Equal(1_793_638_800, rules.RewardsTime(2965));
        // Back to BST after Sun 28 Mar 2027: Mon 29 Mar 2027 16:00 UTC.
        Assert.Equal(1_806_336_000, rules.RewardsTime(2986));
    }

    [Fact]
    public void PidInfoGivesTheRegisteredCountryOrTheDefault()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        var fallback = rt.Game.Rules.Seasons.WeekendLeague.DefaultCountry;
        Assert.Equal(fallback, rt.CallObject("GET", $"{G}/user/pidinfo").Text("competitionCountryCode"));
        var registration = rt.CallObject("GET", $"{G}/champion/user/hub")["userRegistration"]!.AsObject();
        Assert.Equal((fallback, rt.Game.Rules.Seasons.WeekendLeague.RegionFor(fallback)), (registration.Text("competitionCountryCode"), registration.Text("competitionRegion")));
        rt.Call("POST", $"{G}/champion/user/registration", new JsonObject { ["competitionCountryCode"] = "nl" });
        Assert.Equal("NL", rt.CallObject("GET", $"{G}/user/pidinfo").Text("competitionCountryCode"));
    }

    [Fact]
    public void ADesyncIsVoidAndALevelEndTheOpponentQuitIsAWin()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        var flags = rt.Game.Rules.Seasons.WeekendLeague.MatchStatusFlags;
        var credits = rt.Game.Store().Credits();

        // A desync voids even a decided score: nothing counted or recorded, and past the minimum length only the reward
        // gate's desync coins are paid.
        var desyncCoins = rt.Game.Rules.Seasons.RewardEligibility.DesyncCoins;
        var desync = PlayWeekendLeagueMatch(rt, 21, "WIN", 3, 0, flags: flags.Desync);
        Assert.Equal((0L, 0L, desyncCoins), (desync.Int("gamesPlayed"), desync.Int("won"), desync.Int("matchCoins")));
        Assert.Equal(credits + desyncCoins, rt.Game.Store().Credits());
        // An undecided level end (no penalties, no quit) is void too.
        Assert.Equal(0, PlayWeekendLeagueMatch(rt, 22, "", 1, 1).Int("gamesPlayed"));
        Assert.Equal(0, Played(rt));
        Assert.Equal((0L, 0L), (rt.CallObject("GET", $"{G}/user").Int("won"), rt.CallObject("GET", $"{G}/user").Int("loss")));

        // A level end the opponent quit on is a win.
        var quit = PlayWeekendLeagueMatch(rt, 23, "", 0, 0, flags: flags.OpponentQuit);
        Assert.Equal((1L, 1L), (quit.Int("gamesPlayed"), quit.Int("won")));
        Assert.Equal(1, Played(rt));
    }

    [Fact]
    public void ALostConnectionIsAWinOnceTheOpponentHadItsTimeToReport()
    {
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        var rules = rt.Game.Rules.Seasons.WeekendLeague;
        var lost = rules.MatchStatusFlags.ConnectionLost;
        const long Opponent = 987_654;

        // Held: the end itself is void.
        var credits = rt.Game.Store().Credits();
        Assert.Equal(0, PlayWeekendLeagueMatch(rt, 31, "", 1, 1, flags: lost, gid: "wl-31").Int("gamesPlayed"));
        clock.Now += rules.ConnectionWinAfterSeconds - 1;
        Assert.Equal(0, rt.CallObject("GET", $"{G}/champion/user/hub")["userStats"]![1]!.AsObject().Int("gamesPlayed"));
        // After the opponent's time the next hub counts it a win: the record, the win award and the club record.
        clock.Now += 2;
        var mine = rt.CallObject("GET", $"{G}/champion/user/hub")["userStats"]![1]!.AsObject();
        Assert.Equal((1L, 1L), (mine.Int("gamesPlayed"), mine.Int("score")));
        Assert.Equal(credits + rt.Game.Rules.Seasons.MatchCompletionRewards["weekendLeague"].Coins, rt.Game.Store().Credits());
        Assert.Equal(1, rt.CallObject("GET", $"{G}/user").Int("won"));
        // Claimed once.
        rt.Call("GET", $"{G}/champion/user/hub");
        Assert.Equal(1, Played(rt));

        // The opponent sent a result of its own: the claim stays void.
        PlayWeekendLeagueMatch(rt, 32, "", 2, 2, flags: lost, gid: "wl-32");
        rt.Game.Competitions.NoteReport("wl-32", Opponent, conceded: false, clock.Now, rules.ReportMemorySeconds);
        clock.Now += rules.ConnectionWinAfterSeconds + 1;
        rt.Call("GET", $"{G}/champion/user/hub");
        Assert.Equal(1, Played(rt));

        // The opponent already conceded (it quit): a win at once.
        rt.Game.Competitions.NoteReport("wl-33", Opponent, conceded: true, clock.Now, rules.ReportMemorySeconds);
        Assert.Equal(1, PlayWeekendLeagueMatch(rt, 33, "", 0, 0, flags: lost, gid: "wl-33").Int("won"));
        Assert.Equal(2, Played(rt));
    }

    [Fact]
    public void ACancelledSearchDropsThePendingWeekendLeagueMatch()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1, ["type"] = "ONLINE", ["championId"] = 1 });
        var ended = rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "DNF" });
        Assert.Equal(0, ended.Int("gamesPlayed"));
        // The next match end is not a Weekend League match.
        rt.CallObject("POST", $"{G}/match/end", new JsonObject { ["endReason"] = "WIN", ["matchId"] = 41, ["myMatchStats"] = new JsonObject { ["goals"] = 1 } });
        Assert.Equal(0, Played(rt));
    }

    [Fact]
    public void TheBoardsRankEveryClubByWinsWithFifasTiersAndRegions()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague), prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "222222": "Second Team", "333333": "Third Team" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        var maxMatches = rt.Game.Rules.Seasons.WeekendLeague.MaxMatches;
        // Second: only eight wins still earn a Top place; registered in North America. Third: too few matches to rank.
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 6, 5, 0, 1, 3)));
        second.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, maxMatches, 8L, 0, maxMatches - 8L, 5)));
        second.Session.Game.InUnit(uow => uow.SaveChampionRegistrationAsync("US", "NA"));
        third.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 3, 3, 0, 0, 3)));
        var own = rt.Game.Config.PersonaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var secondPersona = PlayerIdentity.Of("222222").PersonaId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static List<string> Personas(JsonObject board) =>
            board["entries"]!.AsArray().OfType<JsonObject>().Select(row => row["encryptedData"]!.AsObject().Text("encryptedPersonaId")).ToList();

        // The Top 200: most wins first; score and tiebreak are objects, remainingMatches counts down this weekend.
        var weekly = rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=base");
        Assert.Equal(3, Personas(weekly).Count);
        Assert.Equal(new[] { secondPersona, own }, Personas(weekly).Take(2));
        var top = weekly["entries"]![0]!.AsObject();
        Assert.Equal((1L, 8L, 0L), (top.Int("rank"), top["score"]!.AsObject().Int("value"), top.Int("remainingMatches")));
        Assert.Equal(maxMatches - 6, weekly["entries"]![1]!.AsObject().Int("remainingMatches"));
        Assert.Equal(2, weekly["entries"]![1]!.AsObject().Int("rank"));
        // Regions: EUR keeps Europe (an unregistered club counts as the default country), AMS the Americas.
        Assert.DoesNotContain(secondPersona, Personas(rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=base&region=EUR")));
        Assert.Equal(new[] { secondPersona }, Personas(rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=base&region=AMS")));
        // The previous weekend has no matches; the month holds this one.
        Assert.Empty(Personas(rt.CallObject("GET", $"{G}/champion/leaderboard?period=prev&type=base")));
        Assert.Equal(3, Personas(rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=aggregate")).Count);

        // Friends: a Top place shows its position (2961 needs no minimum wins); every place shows the rank its wins earn
        // (8 Silver 1, 5 Silver 3), never Elite for few wins.
        var friends = rt.CallObject("GET", $"{G}/champion/type/week/period/curr/friends/stats")["stats"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal((1L, 7L), (friends[0].Int("rank"), friends[0].Int("tierLevel")));
        Assert.Equal((2L, 9L, 6L), (friends[1].Int("rank"), friends[1].Int("tierLevel"), friends[1].Int("gamesPlayed")));
        Assert.Equal(0, friends[2].Int("rank"));

        // Own stats, weekly and monthly (the month's id is monthIdBase + yyyymm).
        var stats = rt.CallObject("GET", $"{G}/champion/user/stats?period=curr&type=base");
        Assert.Equal((2961L, 5L, 2L, 9L), (stats.Int("championEventId"), stats.Int("score"), stats.Int("rank"), stats.Int("tierLevel")));
        var month = rt.CallObject("GET", $"{G}/champion/user/stats?period=curr&type=aggregate");
        Assert.Equal((rt.Game.Rules.Seasons.WeekendLeague.MonthIdBase + 202610, 6L, 2L, 0L), (month.Int("championEventId"), month.Int("gamesPlayed"), month.Int("rank"), month.Int("tierLevel")));

        // The home tile's place comes from the same board.
        var home = rt.CallObject("GET", $"{G}/hub")["championEvent"]!["currentChampionEvent"]!.AsObject();
        Assert.Equal((5L, 2L, 9L), (home.Int("score"), home.Int("rank"), home.Int("userTierLevel")));
    }

    [Fact]
    public void ClientTiersNumberWinRanksFromEliteAndTopPlacesApart()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.ChampionsRewards;
        var byWins = rewards.Ranks.Where(rank => rank.ByWins).OrderByDescending(rank => rank.Wins![0]).ToList();
        Assert.Equal(Enumerable.Range(1, byWins.Count), byWins.Select(rank => rank.ClientTier));
        Assert.True(byWins.Count <= Aurora21.UltimateTeam.Core.Rules.ChampionsRewardRules.ClientWinTiers);
        var bands = rewards.Ranks.Where(rank => !rank.ByWins).OrderBy(rank => rank.Positions![0]).ToList();
        Assert.Equal(Enumerable.Range(1, bands.Count), bands.Select(rank => rank.ClientTier));
        var maxMatches = rt.Game.Rules.Seasons.WeekendLeague.MaxMatches;
        Assert.Equal(1, rewards.ClientTierForWins(maxMatches, maxMatches));
        Assert.Equal(byWins[^1].ClientTier, rewards.ClientTierForWins(byWins[^1].Wins![0], maxMatches));
    }

    [Fact]
    public void TheFrontEndAndCampaignRoutesAnswer()
    {
        using var rt = TestRuntime.Create();
        // The front-end FUT tile's club: year 2021 on PC with the FFA21PCC SKU.
        var persona = rt.CallObject("GET", $"{G}/v2/user/accountinfo")["userAccountInfo"]!["personas"]![0]!.AsObject();
        Assert.Equal(rt.Game.Config.PersonaId, persona.Int("personaId"));
        var club = persona["userClubList"]![0]!.AsObject();
        Assert.Equal(("2021", "pc"), (club.Text("year"), club.Text("platform")));
        Assert.True(club["skuAccessList"]!.AsObject().ContainsKey("FFA21PCC"));
        Assert.Empty(rt.CallObject("POST", $"{G}/user/friends", new JsonObject { ["personaList"] = new JsonArray() }));
        Assert.Equal(0, rt.CallObject("GET", $"{G}/livemessage/template?screen=futlivemsggen4&pagesize=5").Int("messagesAvailable"));
        var world = rt.CallObject("PUT", $"{G}/scmp/world/campaign/rewards/all");
        Assert.Empty(world["worldAwardsList"]!.AsArray());
    }

    // ---- phase 1 (pack-odds-2026-09 PLAN.md 5.2) -----------------------------------------------------------------

    private static List<(long PackId, bool Untradeable)> UnopenedPacks(TestRuntime rt) =>
        rt.Game.InUnit(uow => uow.QueryAsync("SELECT pack_id, untradeable FROM ultimateteam.unopened_packs WHERE club_id = @club ORDER BY id",
            reader => (reader.GetInt64(0), reader.GetBoolean(1))));

    private static List<JsonObject> LeaderboardTiers(JsonObject league) =>
        league["prizeTiers"]!.AsArray().OfType<JsonObject>().Where(tier => tier.Text("tierType") == "LB_RANKING_TYPE").ToList();

    [Fact]
    public void TheTestWeekendIsOpenAndTheNextNeedsAnEntryOf1300Points()
    {
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 0)));
        // 2961: every club plays without points.
        Assert.Equal(1, PlayWeekendLeagueMatch(rt, 51, "WIN", 1, 0).Int("gamesPlayed"));
        Assert.Equal((2961L, 1L), (rt.CallObject("GET", $"{G}/champion/user/stats").Int("championEventId"), Played(rt)));

        // 2962: without an entry nothing counts; 1,299 points do not buy one, 1,300 do.
        clock.Now = DuringWeekendLeague;
        PlayWeekendLeagueMatch(rt, 52, "WIN", 1, 0);
        Assert.Equal((2962L, 0L), (rt.CallObject("GET", $"{G}/champion/user/stats").Int("championEventId"), Played(rt)));
        var record = Rivals(rt);
        record.FcPoints = 1_299;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        Assert.Equal(400, rt.CallRaw("POST", $"{G}/rivals/user/weekendleague/access").Status);
        record.FcPoints = 1_300;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        rt.Call("POST", $"{G}/rivals/user/weekendleague/access");
        Assert.Equal(0, Rivals(rt).FcPoints);
        PlayWeekendLeagueMatch(rt, 53, "WIN", 1, 0);
        Assert.Equal(1, Played(rt));
    }

    [Fact]
    public void ATwentyFirstMatchIsRefused()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(DuringOpenWeekendLeague));
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 0)));
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 19, 10, 0, 9, 1)));
        // The 20th counts, and FIFA is told none remain.
        PlayWeekendLeagueMatch(rt, 61, "WIN", 2, 0);
        var stats = rt.CallObject("GET", $"{G}/champion/user/stats");
        Assert.Equal((20L, 11L, 0L), (stats.Int("gamesPlayed"), stats.Int("score"), stats.Int("gamesRemaining")));
        var weekly = Rivals(rt).WeeklyScore;
        // A 21st is not a Weekend League match: no record, no win, no Rivals Weekly Score.
        PlayWeekendLeagueMatch(rt, 62, "WIN", 3, 0);
        stats = rt.CallObject("GET", $"{G}/champion/user/stats");
        Assert.Equal((20L, 11L, 0L), (stats.Int("gamesPlayed"), stats.Int("score"), stats.Int("gamesRemaining")));
        Assert.Equal(new WeekendLeagueRecord(2961, 20, 11, 0, 9, 2), rt.Game.InUnit(uow => uow.WeekendLeagueRecordAsync(2961)));
        Assert.Equal(weekly, Rivals(rt).WeeklyScore);
    }

    [Fact]
    public void TheRewardTableIsTheTwentyGameTable()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.ChampionsRewards;
        Assert.Equal((5, 20L), (rewards.MinMatchesToRank, rewards.MaxWins));
        Assert.Equal(rewards.MaxWins, rt.Game.Rules.Seasons.WeekendLeague.MaxMatches);
        static string Packs(RewardOption reward) =>
            string.Join("+", reward.Packs.Select(pack => $"{(pack.Count > 1 ? $"{pack.Count}x" : "")}{pack.PackId}{(pack.Untradeable ? "u" : "")}"));
        // Owner, 2026-10-03: six fixed Top 25 bands retain their corresponding old rewards and stable tier ids.
        var expected = new[]
        {
            ("Rank 1", "1-1", 150_000L, "541+405+402+5x550u"),
            ("Rank 2-5", "2-5", 125_000L, "541+402+5x550u"),
            ("Rank 6-10", "6-10", 100_000L, "541+4x550u"),
            ("Rank 16-20", "16-20", 90_000L, "2x540+402+3x550u"),
            ("Elite 1", "19-20", 75_000L, "2x540+402+2x550u"),
            ("Elite 2", "18-18", 60_000L, "540+402+550u"),
            ("Elite 3", "16-17", 50_000L, "540+400+552u"),
            ("Gold 1", "14-15", 40_000L, "542+400+552u"),
            ("Gold 2", "12-13", 32_000L, "542+403+553u"),
            ("Gold 3", "10-11", 25_000L, "402+553u"),
            ("Silver 1", "8-9", 18_000L, "403+553u"),
            ("Silver 2", "6-7", 12_000L, "400+551u"),
            ("Silver 3", "5-5", 8_000L, "319+551u"),
            ("Bronze 1", "4-4", 5_000L, "308"),
            ("Bronze 2", "2-3", 3_000L, "305"),
            ("Bronze 3", "0-1", 1_500L, "304"),
            ("Rank 11-15", "11-15", 100_000L, "541+4x550u"),
            ("Rank 21-25", "21-25", 90_000L, "2x540+402+3x550u"),
        };
        Assert.Equal(expected, rewards.Ranks.Select(rank =>
        {
            var range = rank.Wins ?? rank.Positions!;
            return (rank.Name, $"{range[0]}-{range[1]}", rank.Coins, Packs(ChampionsRewardRules.Reward(rank)));
        }));
        // FIFA shows at most 12 win ranks and numbers them from Elite 1.
        Assert.Equal(12, rewards.Ranks.Count(rank => rank.ByWins));
        Assert.Equal(1, rewards.ClientTierForWins(20, 20));
        Assert.Equal(12, rewards.ClientTierForWins(0, 20));
        Assert.Equal(("Elite 1", "Gold 2", "Bronze 3"), (ChampionsRewardRules.ClientWinTierName(1), ChampionsRewardRules.ClientWinTierName(5),
            ChampionsRewardRules.ClientWinTierName(12)));
        // FIFA numbers the bands 1-6, all six of them.
        Assert.Equal(ChampionsRewardRules.ClientTopBands, rewards.Ranks.Count(rank => !rank.ByWins));
        Assert.Equal(25, rt.Game.Rules.Seasons.WeekendLeague.TopX);
    }

    [Fact]
    public void TheTestWeekendsRankOneIsPaidTheRankOneReward()
    {
        // The test weekend no longer has its own Rank 1 pack (migration 125, owner 2026-10-03): it pays a 541 like any weekend.
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 0)));
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 19, 0, 1, 5)));
        clock.Now = MondayRewards2961;
        var before = UnopenedPacks(rt).Count;
        var prize = Assert.Single(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        // First of one with 19 wins: the Rank 1 band, shown as position 1 and Elite 1 (tier 1).
        Assert.Equal((2961L, 1L, 1L), (prize.Int("eventId"), prize.Int("rank"), prize.Int("tierLevel")));
        var granted = UnopenedPacks(rt).Skip(before).ToList();
        Assert.Equal(new long[] { 402, 405, 541, 550, 550, 550, 550, 550 }, granted.Select(pack => pack.PackId).Order());
        Assert.Equal(new long[] { 550, 550, 550, 550, 550 }, granted.Where(pack => pack.Untradeable).Select(pack => pack.PackId));
    }

    [Fact]
    public void EachRewardLinesOwnTradeabilityIsWhatPaysAndShows()
    {
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var first = TestRuntime.Create(time: clock);
        // An operator marks Rank 1's 541 untradeable (rules/tradeability.json would pay it tradeable).
        Assert.Equal(1, first.Game.InUnit(uow => uow.ExecuteAsync("""
            UPDATE ultimateteam.reward_lines l SET untradeable = true FROM ultimateteam.champions_reward_ranks c
            WHERE c.reward_id = l.reward_id AND c.tier_level = 1 AND l.pack_id = 541
            """)));
        using var rt = TestRuntime.Create(connectionString: first.ConnectionString, time: clock);
        var rankOne = rt.Game.Rules.ChampionsRewards.Ranks[0];
        Assert.True(rankOne.Packs.Single(pack => pack.PackId == 541).Untradeable);
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, fcPoints: 0)));
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 19, 0, 1, 5)));
        clock.Now = MondayRewards2961;
        var before = UnopenedPacks(rt).Count;
        var prize = Assert.Single(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(1, prize.Int("tierLevel"));
        var granted = UnopenedPacks(rt).Skip(before).ToList();
        Assert.Equal([541, 550, 550, 550, 550, 550], granted.Where(pack => pack.Untradeable).Select(pack => pack.PackId).Order());
        Assert.DoesNotContain(granted, pack => pack.PackId is 402 or 405 && pack.Untradeable);
        // The claim's tiles say the same.
        var tile = prize["awards"]!.AsArray().OfType<JsonObject>().Single(award => award.Int("packId") == 541);
        Assert.True(tile.Bool("untradeable"));
    }

    [Fact]
    public void TopBandsAreTheTopSizesPlacesWhateverTheField()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.ChampionsRewards;
        // Band sizing was removed (owner, 2026-10-03; migration 130): the six slots' places come from the Top size alone.
        Assert.Equal(new[] { "Rank 1 1-1", "Rank 2-5 2-5", "Rank 6-10 6-10", "Rank 11-15 11-15", "Rank 16-20 16-20", "Rank 21-25 21-25" },
            rewards.Bands().Select(band => $"{band.Rank.Name} {band.Low}-{band.High}"));
        Assert.Equal(Enumerable.Range(1, 6), rewards.Bands().Select(band => band.ClientTier));
        Assert.Equal(LeaderboardSizes.ChampionsBands(rt.Game.Rules.Seasons.WeekendLeague.TopX).Select(band => (band.Low, band.High)),
            rewards.Bands().Select(band => (band.Low, band.High)));
    }

    [Fact]
    public void TheTop25PreviewLeaderboardAndPayoutUseTheSameCutoff()
    {
        var clock = new TestRuntime.MovableTime(DuringOpenWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 5 }, shared: true);
        // The caller is position 26; the 25 synthetic opponents have higher Skill Ratings and the same six wins (minWinsForTop).
        rt.Game.InUnit(work => work.SaveRivalsRecordAsync(Placed(rt, fcPoints: 0)));
        rt.Game.InUnit(work => work.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 6, 0, 14, 5)));
        for (var position = 1; position <= 25; position++)
        {
            using var lease = players.Acquire(PlayerIdentity.Of((900000 + position).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var record = Placed(rt, fcPoints: 0);
            record.SkillRating = 2000 - position;
            lease.Session.Game.InUnit(work => work.SaveRivalsRecordAsync(record));
            lease.Session.Game.InUnit(work => work.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2961, 20, 6, 0, 14, 5)));
        }
        var league = rt.CallObject("GET", $"{G}/champion/user/hub")["activeChampionLeagues"]![0]!.AsObject();
        var tiers = LeaderboardTiers(league);
        Assert.Equal(["1-1", "2-5", "6-10", "11-15", "16-20", "21-25"], tiers.Select(tier => $"{tier.Int("tierStart")}-{tier.Int("tierEnd")}"));
        Assert.Equal(Enumerable.Range(1, 6).Select(value => (long)value), tiers.Select(tier => tier.Int("tierLevel")));
        Assert.Equal(25, rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=base")["entries"]!.AsArray().Count);
        Assert.Equal(25, rt.CallObject("GET", $"{G}/champion/leaderboard?period=curr&type=aggregate")["entries"]!.AsArray().Count);
        Assert.Equal(0, rt.CallObject("GET", $"{G}/champion/user/stats").Int("rank"));

        clock.Now = MondayRewards2961;
        rt.CallObject("GET", $"{G}/champion/user/hub");
        var results = rt.Game.InUnit(work => work.QueryAsync("""
            SELECT position, rank_name, reward->>'coins' FROM ultimateteam.champion_results
            WHERE event_id = 2961 ORDER BY position
            """, row => (Position: row.GetInt64(0), Name: row.GetString(1), Coins: long.Parse(row.GetString(2)))));
        Assert.Equal(26, results.Count);
        Assert.Equal((25L, "Rank 21-25", 90_000L), results[24]);
        var outside = rt.Game.Rules.ChampionsRewards.RankForWins(6, 20);
        Assert.Equal((26L, outside.Name, outside.Coins), results[25]);
        foreach (var result in results.Take(25))
        {
            var rank = rt.Game.Rules.ChampionsRewards.Ranks.Single(rank => rank.Positions is { } positions
                && positions[0] <= result.Position && result.Position <= positions[1]);
            Assert.Equal((rank.Name, rank.Coins), (result.Name, result.Coins));
        }
        var credits = rt.Game.Store().Credits();
        var prize = Assert.Single(rt.CallObject("POST", $"{G}/champion/user/prize")["awardedPrizes"]!.AsArray().OfType<JsonObject>());
        Assert.Equal(0, prize.Int("rank"));
        Assert.Equal(credits + outside.Coins, rt.Game.Store().Credits());
        var bandRewards = rt.Game.Rules.ChampionsRewards.Ranks.Where(rank => !rank.ByWins).OrderBy(rank => rank.Positions![0]).ToList();
        foreach (var (tier, rank) in tiers.Zip(bandRewards))
        {
            Assert.True(JsonNode.DeepEquals(tier["awards"], rt.Fut.Rivals.RenderOption(ChampionsRewardRules.Reward(rank), RewardSources.WeekendLeague, ownTradeability: true)));
        }
    }

    [Fact]
    public void TopBandsDoNotFollowTheFieldAndArePaidAtSettlement()
    {
        var clock = new TestRuntime.MovableTime(DuringWeekendLeague);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 5 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        // Weekend League 2961 ranked 250 clubs: the bands do not depend on it.
        rt.Game.InUnit(uow => uow.ExecuteAsync(
            "INSERT INTO ultimateteam.champion_settlements(event_id, settled_at, participants) VALUES (2961, @now, 250)", ("now", DuringWeekendLeague)));
        string Bands() => string.Join(",", LeaderboardTiers(rt.CallObject("GET", $"{G}/champion/user/hub")["activeChampionLeagues"]![0]!.AsObject())
            .Select(tier => $"{tier.Int("tierLevel")}:{tier.Int("tierStart")}-{tier.Int("tierEnd")}"));
        const string Top25 = "1:1-1,2:2-5,3:6-10,4:11-15,5:16-20,6:21-25";
        Assert.Equal(Top25, Bands());
        rt.Game.InUnit(uow => uow.ExecuteAsync("UPDATE ultimateteam.champion_settlements SET participants = 5000 WHERE event_id = 2961"));
        Assert.Equal(Top25, Bands());

        // Settled with them: third place with 19 wins is inside Rank 2-5.
        rt.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2962, 20, 20, 0, 0, 5)));
        second.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2962, 20, 20, 0, 0, 5)));
        third.Session.Game.InUnit(uow => uow.SaveWeekendLeagueRecordAsync(new WeekendLeagueRecord(2962, 20, 19, 0, 1, 5)));
        clock.Now = MondayRewards2962;
        rt.Call("GET", $"{G}/champion/user/hub");
        var result = Assert.Single(third.Session.Game.InUnit(uow => uow.UnclaimedChampionResultsAsync(2962, 2962)));
        Assert.Equal((3L, "Rank 2-5"), (result.Position, result.RankName));
        Assert.Equal(new[] { 541L, 402L, 550L }, result.Reward.Packs.Select(pack => pack.PackId));
    }
}
