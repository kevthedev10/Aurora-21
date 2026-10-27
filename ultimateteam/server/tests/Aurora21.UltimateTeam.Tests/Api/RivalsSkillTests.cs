using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Division Rivals as in FIFA 21 retail (Python server 21bf125, 56145ed): placement, EA's Skill Rating with K by division,
/// the Weekly Score, FUT Champions Points, promotion coins, and each week settled once for every club and claimed per club.
/// </summary>
public sealed class RivalsSkillTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Thursday 2026-10-01 12:00 UTC: week 2961 (Thursday 07:00 turnover), after the first rewarded week.</summary>
    private const long Week2961 = 1_790_856_000;

    /// <summary>A pairing for one game, each against its own opponent (the reward gate stops rating a pair past its daily cap).</summary>
    private static void Pair(TestRuntime rt, long gameId, long opponentRating) =>
        rt.Game.RunAsync("pair", uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_000_000 + gameId, gameId), opponentRating),
            CancellationToken.None).GetAwaiter().GetResult();

    private static JsonObject End(TestRuntime rt, string reason, long gameId, long mine, long theirs)
    {
        rt.PlayedRealSeconds();
        return rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["gid"] = gameId,
            // A match the server's clock watched past its minimum length (secondsPlayed is only a client claim).
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = mine },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = theirs },
        });
    }

    private static JsonObject Score(JsonObject ended) => ended["rivalsScore"]!.AsObject();

    /// <summary>A placed club's record in Division 5 for week 2961.</summary>
    private static RivalsRecord Placed(TestRuntime rt, long rating, int division, long weeklyScore = 0) => new()
    {
        RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion,
        WeekId = 2961,
        SkillRating = rating,
        Placed = true,
        PlacementPlayed = 5,
        WeekDivision = division,
        BestDivision = division,
        Division = division,
        WeeklyScore = weeklyScore,
        WeeklyCounted = weeklyScore > 0 ? 1 : 0,
        WeeklyGames = weeklyScore > 0 ? 1 : 0,
    };

    [Fact]
    public void FivePlacementMatchesPlaceTheClubAndPayEveryDivisionPassed()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));
        var rules = rt.Game.Rules.Seasons.Rivals;

        // Reading the hub of a club that never played creates no record.
        var fresh = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((500L, true, 0L, 5L), (fresh.Int("skillPoints"), fresh.Bool("inPlacementMatches"), fresh.Int("gamesPlayed"), fresh.Int("maxNumberOfMatches")));
        Assert.Null(rt.Game.InUnit(uow => uow.RivalsRecordAsync()));

        var credits = rt.Game.Store().Credits();
        long matchCoins = 0;
        // Placement starts at 500 with K 200: four wins against 500 reach 769, and earn no Weekly Score or FUT Champions Points.
        for (var game = 1; game <= 4; game++)
        {
            Pair(rt, 100 + game, 500);
            var placement = End(rt, "WIN", 100 + game, 1, 0);
            matchCoins += placement.Int("matchCoins");
            Assert.Equal((0L, 0L), (Score(placement).Int("rivalPoints"), Score(placement).Int("weekendLeaguePoints")));
            Assert.Equal(0, placement.Int("rivalsDivisionCoinsReward"));
        }
        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((769L, true, 4L, 10L), (hub.Int("skillPoints"), hub.Bool("inPlacementMatches"), hub.Int("placementMatchesPlayed"), hub.Int("crtDivision")));

        // The fifth places the club: 804 is Division 6, and placement pays Divisions 10 up to 6 at once: their coins (PLAN.md 5.1:
        // 2,000 + 3,000 + 4,000 + 5,000 + 7,500) and each one's pack.
        // Every eligible win also grants a Match Pack; count the promotion packs alone.
        int PromotionPacks() => rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count(id => id != rt.Game.Rules.Seasons.MatchCompletionRewards["rivals"].PackId!.Value);
        var packsBefore = PromotionPacks();
        Pair(rt, 105, 500);
        var placed = End(rt, "WIN", 105, 1, 0);
        Assert.Equal(35, Score(placed).Int("skillPoints"));
        Assert.Equal(500, placed.Int("rivalsOpponentELORating"));
        var coins = Enumerable.Range(6, 5).Sum(division => rt.Game.Rules.RivalsRewards.Promotion[division].Coins);
        Assert.Equal(21_500, coins);
        Assert.Equal(coins, placed.Int("rivalsDivisionCoinsReward"));
        matchCoins += placed.Int("matchCoins");
        Assert.Equal(credits + matchCoins + coins, rt.Game.Store().Credits());
        Assert.Equal(rt.Game.Store().Credits(), placed.Int("credits"));
        Assert.Equal(packsBefore + 5, PromotionPacks());

        hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((804L, false, 6L, 6L), (hub.Int("skillPoints"), hub.Bool("inPlacementMatches"), hub.Int("crtRivalDivision"), hub.Int("crtDivision")));
        Assert.Equal((0L, rules.WeeklyMatches), (hub.Int("gamesPlayed"), hub.Int("maxNumberOfMatches")));
        Assert.Equal("WWWWW", hub.Text("winForm"));
    }

    [Fact]
    public void APlacedClubEarnsWeeklyScoreAndFcPointsByItsWeeksDivision()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1000, division: 5)));

        // EA's formula with Division 5's K of 60: a 3-0 win against 1000 is +30, and 500 + 3 x 90 Weekly Score.
        Pair(rt, 1, 1000);
        var won = End(rt, "WIN", 1, 3, 0);
        Assert.Equal((770L, 30L, 65L), (Score(won).Int("rivalPoints"), Score(won).Int("skillPoints"), Score(won).Int("weekendLeaguePoints")));
        // A 0-1 loss against 1000 from 1030 is -33 (997, inside Division 5's relegation zone), 150 Weekly Score.
        Pair(rt, 2, 1000);
        var lost = End(rt, "LOSS", 2, 0, 1);
        Assert.Equal((150L, -33L, 14L), (Score(lost).Int("rivalPoints"), Score(lost).Int("skillPoints"), Score(lost).Int("weekendLeaguePoints")));
        // A 1-1 draw against 1100 is +9, 300 + 90.
        Pair(rt, 3, 1100);
        var drew = End(rt, "DRAW", 3, 1, 1);
        Assert.Equal((390L, 9L, 26L), (Score(drew).Int("rivalPoints"), Score(drew).Int("skillPoints"), Score(drew).Int("weekendLeaguePoints")));

        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((1006L, 5L, 5L), (hub.Int("skillPoints"), hub.Int("crtRivalDivision"), hub.Int("crtDivision")));
        Assert.Equal((3L, 1L, 1L, 1L), (hub.Int("weeklyGamesPlayed"), hub.Int("weeklyWins"), hub.Int("weeklyDraws"), hub.Int("weeklyLosses")));
        Assert.Equal((1310L, 105L, "WLD"), (hub.Int("rivalPoints"), hub.Int("weekendLeaguePoints"), hub.Text("winForm")));
        // Every division's list, bottom first (FIFA reads division d at count - d), and Division 5's prize table.
        var divisions = hub["divisionsList"]!.AsArray().OfType<JsonObject>().Select(entry => entry.Obj("division")!).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(id => (long)id), divisions.Select(division => division.Int("divisionId")));
        var tiers = hub["prizes"]!["prizeTiers"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, tiers.Select(tier => tier.Int("tierLevel")));
        Assert.Equal(new long[] { 0, 20, 45, 70, 88, 97 }, tiers.Select(tier => tier.Int("tierStart")));
        Assert.Equal(3, tiers[0]["categoryAwardsList"]!.AsArray().Count);
        // The only club with a Weekly Score this week is at the 100th percentile: Rank VI, as in retail's bands.
        Assert.Equal(6, hub.Int("tierLevel"));
    }

    [Fact]
    public void TheWeeksMatchCapStopsTheWeeklyScoreButNotTheRating()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));
        var record = Placed(rt, 1000, division: 5, weeklyScore: 9000);
        record.WeeklyCounted = rt.Game.Rules.RivalsRewards.WeeklyScore.MatchCap;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));

        Pair(rt, 1, 1000);
        var won = End(rt, "WIN", 1, 2, 0);
        Assert.Equal((0L, 30L), (Score(won).Int("rivalPoints"), Score(won).Int("skillPoints")));
    }

    [Fact]
    public void PromotionAndRelegationFollowTheRelegationLinesAtTheNextWeek()
    {
        var clock = new TestRuntime.MovableTime(Week2961);
        using var rt = TestRuntime.Create(time: clock);
        var rules = rt.Game.Rules.Seasons.Rivals;
        // Division 5 runs from 1000; its relegation line is 900.
        Assert.Equal(5, rules.NextWeekDivision(5, 950));
        Assert.Equal(6, rules.NextWeekDivision(5, 899));
        Assert.Equal(4, rules.NextWeekDivision(5, 1260));
        Assert.Equal(10, rules.DivisionFor(0));
        Assert.Equal((1030L, 997L), (rules.Rated(1000, 1000, "win", 60), rules.Rated(1030, 1000, "loss", 60)));

        // A club promoted in week 2961 plays week 2962 in the new division; a record from before the rules version places again.
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1260, division: 5, weeklyScore: 500)));
        // During the week the division holds; the promotion shows as next week's division.
        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((5L, 4L), (hub.Int("crtRivalDivision"), hub.Int("crtDivision")));
        clock.Now = Week2961 + 7 * 86400;
        hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((4L, 0L, 0L), (hub.Int("crtRivalDivision"), hub.Int("rivalPoints"), hub.Int("weeklyGamesPlayed")));
        var kept = rt.Game.InUnit(uow => uow.RivalsRecordAsync())!;
        Assert.Equal((2961L, 5, 500L), (kept.LastWeekId!.Value, kept.LastWeekDivision, kept.LastWeekScore));

        var old = Placed(rt, 1800, division: 2);
        old.RatingVersion = rules.RatingVersion - 1;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(old));
        hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((500L, true), (hub.Int("skillPoints"), hub.Bool("inPlacementMatches")));
    }

    [Fact]
    public void ADivisionReachedMidWeekKeepsItsRelegationBuffer()
    {
        var clock = new TestRuntime.MovableTime(Week2961);
        using var rt = TestRuntime.Create(time: clock);
        // Division 7 for the week; Division 6 runs from 750 with its relegation line at 650.
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 720, division: 7)));

        Pair(rt, 1, 720);
        End(rt, "WIN", 1, 1, 0);
        var promoted = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((750L, 7L, 6L), (promoted.Int("skillPoints"), promoted.Int("crtRivalDivision"), promoted.Int("crtDivision")));

        // A loss back under 750 stays in Division 6 (owner 10-08: it dropped straight back to Division 7).
        Pair(rt, 2, 720);
        End(rt, "LOSS", 2, 0, 1);
        var held = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((717L, 6L), (held.Int("skillPoints"), held.Int("crtDivision")));

        // The next week is played in Division 6.
        clock.Now = Week2961 + 7 * 86400;
        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((6L, 6L), (hub.Int("crtRivalDivision"), hub.Int("crtDivision")));

        // Under the relegation line the club drops to the division that holds its rating.
        var record = rt.Game.InUnit(uow => uow.RivalsRecordAsync())!;
        record.SkillRating = 640;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        Assert.Equal(7L, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("crtDivision"));
    }

    [Fact]
    public void OnlyARecentRivalsPairingIsRated()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));

        // An ordinary match has no Rivals score but joins the club record.
        rt.Call("POST", $"{G}/match", new JsonObject { ["squadId"] = 1 });
        var ordinary = End(rt, "DRAW", 90, 1, 1);
        Assert.False(ordinary.ContainsKey("rivalsScore"));

        // A no-contest takes the pairing without rating it or counting it.
        Pair(rt, 91, 1000);
        Assert.False(End(rt, "NO_CONTEST", 91, 0, 0).ContainsKey("rivalsScore"));
        Assert.False(End(rt, "WIN", 92, 1, 0).ContainsKey("rivalsScore"));
        // A replay of a rated match end rates nothing again.
        Pair(rt, 93, 500);
        var won = End(rt, "WIN", 93, 1, 0);
        Assert.True(won.ContainsKey("dynamicObjectivesUpdates"));
        var settled = won.DeepClone().AsObject();
        settled.Remove("dynamicObjectivesUpdates");
        var replay = End(rt, "WIN", 93, 1, 0);
        Assert.False(replay.ContainsKey("dynamicObjectivesUpdates"));
        Assert.True(JsonNode.DeepEquals(settled, replay));

        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((600L, 1L), (hub.Int("skillPoints"), hub.Int("placementMatchesPlayed")));
        var user = rt.CallObject("GET", $"{G}/user");
        Assert.Equal((2L, 1L, 0L), (user.Int("won"), user.Int("draw"), user.Int("loss")));
    }

    [Fact]
    public void AClosedWeekIsSettledOnceByPercentileAndClaimedOnce()
    {
        var clock = new TestRuntime.MovableTime(Week2961);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var second = players.Acquire(PlayerIdentity.Of("222222"));
        using var third = players.Acquire(PlayerIdentity.Of("333333"));
        // Three clubs in Division 5 with 3,000, 2,000 and 1,000: positions at the 34th, 67th and 100th percentile.
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 3000)));
        second.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 2000)));
        third.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 1000)));
        var live = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.False(live.Bool("isPrizeAvailable"));
        Assert.Equal(2, live.Int("tierLevel"));
        Assert.Empty(rt.CallObject("PUT", $"{G}/rivals/reward/1")["awardedPrizes"]!.AsArray());

        // Week 2962, after the settle delay: the week settles for every club with the options of its rank.
        clock.Now = Week2961 + 7 * 86400 + 600;
        var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
        Assert.Equal((true, 3000L, 5L, 2961L), (hub.Bool("isPrizeAvailable"), hub.Int("prizesRivalPoints"), hub.Int("prizesDivision"), hub["prizes"]!.AsObject().Int("competitionId")));
        var tiers = hub["prizes"]!["prizeTiers"]!.AsArray().OfType<JsonObject>().ToList();
        // Rank I is empty (its threshold is above the best score); Rank II starts at 3,000, Rank III at 2,000, Rank VI at 1,000.
        Assert.Equal(new long[] { 3001, 3000, 2000, 2000, 2000, 1000 }, tiers.Select(tier => tier.Int("tierStartValue")));
        Assert.Equal(new long[] { int.MaxValue, 3000, 2999, 1999, 1999, 1999 }, tiers.Select(tier => tier.Int("tierEndValue")));
        Assert.Equal((3, 6), (Rank(second, 2961), Rank(third, 2961)));
        var previous = rt.CallObject("GET", $"{G}/rivals/user/leaderboard/previous");
        Assert.Equal((2L, 3000L), (previous.Int("tierLevel"), previous.Int("rivalPoints")));

        // Rank II's first option in Division 5 (PLAN.md 5.1): 19,500 coins and 130 FUT Champions Points.
        var credits = rt.Game.Store().Credits();
        var claimed = rt.CallObject("PUT", $"{G}/rivals/reward/1")["awardedPrizes"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(("coin", 19_500L), (claimed[0].Text("awardType"), claimed[0].Int("value")));
        Assert.Equal(("champions_qualification_points", 130L), (claimed[1].Text("awardType"), claimed[1].Int("value")));
        Assert.Equal(credits + 19_500, rt.Game.Store().Credits());
        Assert.Equal(130, rt.Game.InUnit(uow => uow.RivalsRecordAsync())!.FcPoints);
        // Claimed once: the next claim is empty and the hub is back to the live week.
        Assert.Empty(rt.CallObject("PUT", $"{G}/rivals/reward/1")["awardedPrizes"]!.AsArray());
        Assert.False(rt.CallObject("GET", $"{G}/rivals/user/hub").Bool("isPrizeAvailable"));
        Assert.Equal(credits + 19_500, rt.Game.Store().Credits());
    }

    /// <summary>A club's settled rank in a week.</summary>
    private static int Rank(PlayerLease lease, long week) => lease.Session.Game.InUnit(uow => uow.RivalsResultAsync(week))!.Rank;

    [Fact]
    public void TheUntradeableRewardOptionGrantsUntradeablePacksUnderItsOwnSource()
    {
        var clock = new TestRuntime.MovableTime(Week2961);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 6 }, shared: true);
        var others = new[] { "222222", "333333", "444444", "555555" }.Select(id => players.Acquire(PlayerIdentity.Of(id))).ToList();
        try
        {
            // Five clubs: the best of them is at the 20th percentile, Rank I.
            rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 5000)));
            foreach (var (other, index) in others.Select((other, index) => (other, index)))
            {
                other.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 1000 + index * 500)));
            }
            clock.Now = Week2961 + 7 * 86400 + 600;
            // Option 3 is option 2's packs and half as much again, all untradeable.
            var option = rt.Game.Rules.RivalsRewards.Option(5, 1, 3);
            Assert.NotEmpty(option.Packs);
            Assert.All(option.Packs, pack => Assert.Equal((true, "rivals_option_c"), (pack.Untradeable, pack.Source)));
            var before = rt.Game.Store().Run(uow => uow.UnopenedPackIdsAsync()).Count;
            var claimed = rt.CallObject("PUT", $"{G}/rivals/reward/3")["awardedPrizes"]!.AsArray().OfType<JsonObject>().Where(award => award.Text("awardType") == "pack").ToList();
            Assert.Equal(option.Packs.Count, claimed.Count);
            Assert.All(claimed, award => Assert.True(award.Bool("untradeable")));
            var granted = rt.Game.InUnit(uow => uow.QueryAsync("SELECT source, untradeable FROM ultimateteam.unopened_packs WHERE club_id = @club ORDER BY id",
                reader => (Source: reader.GetString(0), Untradeable: reader.GetBoolean(1))));
            Assert.Equal(before + option.Packs.Sum(pack => pack.Count), granted.Count);
            Assert.All(granted.Skip(before), pack => Assert.Equal(("rivals_option_c", true), (pack.Source, pack.Untradeable)));
        }
        finally
        {
            others.ForEach(other => other.Dispose());
        }
    }
}
