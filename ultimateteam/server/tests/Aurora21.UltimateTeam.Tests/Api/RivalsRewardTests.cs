using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// The phase-1 Division Rivals rewards (pack-odds-2026-09 PLAN.md 5.1, rules/rivals_rewards.json): the weekly table with its coin,
/// tradeable and untradeable options, the two FUT Champions Points fixes, the pooled table for small divisions, and promotion
/// rewards paid once per division.
/// </summary>
public sealed class RivalsRewardTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Thursday 2026-10-01 12:00 UTC: week 2961 (Thursday 07:00 turnover).</summary>
    private const long Week2961 = 1_790_856_000;

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

    private static JsonObject RatedMatch(TestRuntime rt, long gameId, string reason, long opponentRating)
    {
        rt.Game.RunAsync("pair", uow => rt.Fut.Rivals.NotePairingAsync(uow, new OnlineOpponent(true, "opponent", 1_234_567, gameId), opponentRating),
            CancellationToken.None).GetAwaiter().GetResult();
        rt.PlayedRealSeconds();
        return rt.CallObject("POST", $"{G}/match/end", new JsonObject
        {
            ["endReason"] = reason,
            ["gid"] = gameId,
            // A match the server's clock watched past its minimum length (secondsPlayed is only a client claim).
            ["secondsPlayed"] = 5461,
            ["myMatchStats"] = new JsonObject { ["goals"] = reason == "WIN" ? 1 : 0 },
            ["opponentMatchStats"] = new JsonObject { ["goals"] = reason == "WIN" ? 0 : 1 },
        });
    }

    /// <summary>The club's unopened packs in grant order, without the Match Packs every eligible match also grants.</summary>
    private static List<(long PackId, string Source, bool Untradeable)> Packs(TestRuntime rt) =>
        rt.Game.InUnit(uow => uow.QueryAsync("SELECT pack_id, source, untradeable FROM ultimateteam.unopened_packs WHERE club_id = @club AND pack_id <> @matchPack ORDER BY id",
            reader => (reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)), ("matchPack", rt.Game.Rules.Seasons.MatchCompletionRewards["rivals"].PackId!.Value)));

    private static Dictionary<long, long> Counts(IEnumerable<RewardPack> packs) =>
        packs.GroupBy(pack => pack.PackId).ToDictionary(group => group.Key, group => group.Sum(pack => pack.Count));

    [Fact]
    public void TheWeeklyTableIsThePhaseOneTable()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.RivalsRewards;
        // Rank I coins by division; Ranks II-IV pay 0.7, 0.5 and 0.35 of them, to the nearest 500 (PLAN.md 5.1).
        var rankOne = new long[] { 60_000, 50_000, 42_000, 35_000, 28_000, 22_000, 17_000, 13_000, 10_000, 8_000 };
        var factors = new[] { 1.0, 0.7, 0.5, 0.35 };
        for (var division = 1; division <= 10; division++)
        {
            for (var rank = 1; rank <= 4; rank++)
            {
                var options = rules.Divisions[division][rank - 1];
                Assert.Equal(3, options.Count);
                var (coins, packs, untradeable) = (options[0], options[1], options[2]);
                Assert.Equal((long)(Math.Round(rankOne[division - 1] * factors[rank - 1] / 500, MidpointRounding.AwayFromZero) * 500), coins.Coins);
                Assert.Empty(coins.Packs);
                // Option 2: tradeable packs only. Option 3: the same packs and more (1.5x), every one untradeable, under its own source.
                Assert.Equal(0, packs.Coins);
                Assert.All(packs.Packs, pack => Assert.Equal((false, (string?)null), (pack.Untradeable, pack.Source)));
                Assert.Equal(0, untradeable.Coins);
                Assert.All(untradeable.Packs, pack => Assert.Equal((true, "rivals_option_c"), (pack.Untradeable, pack.Source)));
                var more = Counts(untradeable.Packs);
                Assert.All(Counts(packs.Packs), pair => Assert.True(more.GetValueOrDefault(pair.Key) >= pair.Value, $"Div {division} rank {rank} pack {pair.Key}"));
                Assert.True(untradeable.Packs.Sum(pack => pack.Count) > packs.Packs.Sum(pack => pack.Count));
                // FUT Champions Points are the same whichever option is chosen.
                Assert.Single(options.Select(option => option.FcPoints).Distinct());
            }
            foreach (var rank in new[] { 5, 6 })
            {
                var fixedReward = Assert.Single(rules.Divisions[division][rank - 1]);
                Assert.Equal(0, fixedReward.Coins);
                Assert.NotEmpty(fixedReward.Packs);
                Assert.All(fixedReward.Packs, pack => Assert.False(pack.Untradeable));
            }
        }
        // Spot checks against PLAN.md 5.1's table.
        Assert.Equal(new Dictionary<long, long> { [405] = 1, [304] = 1 }, Counts(rules.Option(1, 1, 2).Packs));
        Assert.Equal(new Dictionary<long, long> { [404] = 2 }, Counts(rules.Option(2, 1, 2).Packs));
        Assert.Equal(new Dictionary<long, long> { [402] = 1 }, Counts(rules.Option(5, 2, 2).Packs));
        Assert.Equal(new Dictionary<long, long> { [319] = 1 }, Counts(rules.Option(9, 2, 2).Packs));
        Assert.Equal(new Dictionary<long, long> { [308] = 2 }, Counts(rules.Option(6, 3, 2).Packs));
        Assert.Equal(new Dictionary<long, long> { [400] = 1 }, Counts(rules.Option(1, 5, 1).Packs));
        Assert.Equal(new Dictionary<long, long> { [304] = 2 }, Counts(rules.Option(8, 5, 1).Packs));
        Assert.Equal(new Dictionary<long, long> { [305] = 1 }, Counts(rules.Option(4, 6, 1).Packs));
    }

    [Fact]
    public void TheFcPointsSlipsAreFixedAndTheRestUnchanged()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.RivalsRewards;
        // Retail paid Division 1 Rank I nothing, and Division 4 Rank I's untradeable option nothing (PLAN.md 2 item 20).
        Assert.All(rules.Divisions[1][0], option => Assert.Equal(1_250, option.FcPoints));
        Assert.Equal(new long[] { 240, 240, 240 }, rules.Divisions[4][0].Select(option => option.FcPoints));
        // Everything else as retail paid it, for example Division 1 Rank II and V and Division 5 Rank I.
        Assert.All(rules.Divisions[1][1], option => Assert.Equal(1_000, option.FcPoints));
        Assert.Equal(250, rules.Option(1, 5, 1).FcPoints);
        Assert.All(rules.Divisions[5][0], option => Assert.Equal(195, option.FcPoints));
        Assert.Equal(0, rules.Option(10, 6, 1).FcPoints);
    }

    [Fact]
    public void DivisionsUnderTwentyClubsRankOnOnePooledTable()
    {
        using var rt = TestRuntime.Create();
        var rules = rt.Game.Rules.RivalsRewards;
        Assert.Equal(20, rules.PoolBelowClubs);
        // Two clubs in Division 5, three in Division 3: every division is small, so all five rank on one server-wide table.
        var rows = new List<RivalsWeekRow>
        {
            new(1, 5, 3000, 1), new(2, 5, 2500, 1),
            new(3, 3, 1000, 1), new(4, 3, 900, 1), new(5, 3, 800, 1),
        };
        var tables = rules.WeekTables(rows);
        Assert.True(tables[5].Pooled);
        Assert.Equal((1, 2), (tables[5].Ranks[1], tables[5].Ranks[2]));
        Assert.Equal((3, 4, 6), (tables[3].Ranks[3], tables[3].Ranks[4], tables[3].Ranks[5]));
        Assert.Equal(new long[] { 3000, 2500, 1000, 900, 900, 800 }, tables[5].Thresholds);
        Assert.Equal(2, tables[5].Participants);
        // Ranked within the division alone, the best of two clubs would be Rank III and the other Rank VI.
        var alone = rules.DivisionTable(rows.Where(row => row.Division == 5));
        Assert.Equal((3, 6), (alone.Ranks[1], alone.Ranks[2]));

        // A division with twenty clubs ranks within itself; the small one beside it still pools with everyone.
        var big = Enumerable.Range(0, 20).Select(index => new RivalsWeekRow(100 + index, 2, 100 + index * 100, 1)).ToList();
        var mixed = rules.WeekTables(big.Concat(rows).ToList());
        Assert.False(mixed[2].Pooled);
        Assert.Equal(rules.DivisionTable(big).Thresholds, mixed[2].Thresholds);
        Assert.Equal(rules.DivisionTable(big).Ranks, mixed[2].Ranks);
        Assert.True(mixed[5].Pooled);
        Assert.Equal(rules.DivisionTable(big.Concat(rows)).Thresholds, mixed[5].Thresholds);
    }

    [Fact]
    public void AClosedWeekSettlesSmallDivisionsOnThePooledTable()
    {
        var clock = new TestRuntime.MovableTime(Week2961);
        using var rt = TestRuntime.Create(time: clock);
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 6 }, shared: true);
        var others = new[] { ("222222", 5, 2500L), ("333333", 3, 1000L), ("444444", 3, 900L), ("555555", 3, 800L) }
            .Select(entry => (Lease: players.Acquire(PlayerIdentity.Of(entry.Item1)), Division: entry.Item2, Score: entry.Item3)).ToList();
        try
        {
            rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, 5, weeklyScore: 3000)));
            foreach (var other in others)
            {
                other.Lease.Session.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1100, other.Division, weeklyScore: other.Score)));
            }
            // Live: the pooled table already puts this club at Rank I.
            Assert.Equal(1, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("tierLevel"));

            clock.Now = Week2961 + 7 * 86400 + 600;
            var hub = rt.CallObject("GET", $"{G}/rivals/user/hub");
            Assert.Equal((true, 3000L, 5L), (hub.Bool("isPrizeAvailable"), hub.Int("prizesRivalPoints"), hub.Int("prizesDivision")));
            var tiers = hub["prizes"]!["prizeTiers"]!.AsArray().OfType<JsonObject>().ToList();
            Assert.Equal(new long[] { 3000, 2500, 1000, 900, 900, 800 }, tiers.Select(tier => tier.Int("tierStartValue")));
            Assert.Equal(1, rt.Game.InUnit(uow => uow.RivalsResultAsync(2961))!.Rank);
            Assert.Equal(new[] { 2, 3, 4, 6 }, others.Select(other => other.Lease.Session.Game.InUnit(uow => uow.RivalsResultAsync(2961))!.Rank));
            // Rank I's coins in Division 5.
            var claimed = rt.CallObject("PUT", $"{G}/rivals/reward/1")["awardedPrizes"]!.AsArray().OfType<JsonObject>().First();
            Assert.Equal(("coin", 28_000L), (claimed.Text("awardType"), claimed.Int("value")));
        }
        finally
        {
            others.ForEach(other => other.Lease.Dispose());
        }
    }

    [Fact]
    public void PromotionIsPaidOnceForEachDivisionWithItsPack()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));
        var promotion = rt.Game.Rules.RivalsRewards.Promotion;
        // PLAN.md 5.1's promotion table.
        Assert.Equal(new long[] { 30_000, 20_000, 15_000, 12_500, 10_000, 7_500, 5_000, 4_000, 3_000, 2_000 },
            Enumerable.Range(1, 10).Select(division => promotion[division].Coins));
        Assert.Equal(new long[] { 515, 402, 400, 319, 308, 543, 543, 305, 305, 304 },
            Enumerable.Range(1, 10).Select(division => Assert.Single(promotion[division].Packs).PackId));

        // Division 4 starts at 1,250: a win from 1,240 reaches it for the first time.
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(Placed(rt, 1240, 5)));
        var credits = rt.Game.Store().Credits();
        var packs = Packs(rt).Count;
        var promoted = RatedMatch(rt, 1, "WIN", 1240);
        Assert.Equal(12_500, promoted.Int("rivalsDivisionCoinsReward"));
        Assert.Equal(credits + promoted.Int("matchCoins") + 12_500, rt.Game.Store().Credits());
        var granted = Packs(rt).Skip(packs).ToList();
        Assert.Equal((319L, "rivals_promotion", false), Assert.Single(granted));

        // A loss under 1,250 keeps Division 4 (its relegation line is 1,150).
        RatedMatch(rt, 2, "LOSS", 1240);
        Assert.Equal(4, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("crtDivision"));
        // Back below the line and up again: Division 4 was reached before, so nothing more.
        var record = rt.Game.InUnit(uow => uow.RivalsRecordAsync())!;
        record.SkillRating = 1140;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        Assert.Equal(5, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("crtDivision"));
        record = rt.Game.InUnit(uow => uow.RivalsRecordAsync())!;
        record.SkillRating = 1240;
        record.Division = 5;
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(record));
        var again = RatedMatch(rt, 3, "WIN", 1240);
        Assert.Equal(4, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("crtDivision"));
        Assert.Equal(0, again.Int("rivalsDivisionCoinsReward"));
        Assert.Equal(packs + 1, Packs(rt).Count);
    }

    [Fact]
    public void PlacementIntoDivisionOnePaysEveryDivisionAndAnUntradeablePick()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(Week2961));
        rt.Game.InUnit(uow => uow.SaveRivalsRecordAsync(new RivalsRecord
        {
            RatingVersion = rt.Game.Rules.Seasons.Rivals.RatingVersion,
            WeekId = 2961,
            SkillRating = 1950,
            PlacementPlayed = 4,
            WeekDivision = 10,
            BestDivision = 11,
            Division = 10,
        }));
        var packs = Packs(rt).Count;
        // The fifth placement win (K 200) reaches 2,050: Division 1, and every division from 10 up to 1 is paid at once.
        var placed = RatedMatch(rt, 1, "WIN", 1950);
        Assert.Equal(1, rt.CallObject("GET", $"{G}/rivals/user/hub").Int("crtDivision"));
        Assert.Equal(109_000, placed.Int("rivalsDivisionCoinsReward"));
        var granted = Packs(rt).Skip(packs).ToList();
        Assert.Equal(new long[] { 304, 305, 305, 308, 319, 400, 402, 515, 543, 543 }, granted.Select(pack => pack.PackId).Order());
        Assert.All(granted, pack => Assert.Equal("rivals_promotion", pack.Source));
        // Only the 86+ pick is untradeable.
        Assert.Equal(new long[] { 515 }, granted.Where(pack => pack.Untradeable).Select(pack => pack.PackId));
    }
}
