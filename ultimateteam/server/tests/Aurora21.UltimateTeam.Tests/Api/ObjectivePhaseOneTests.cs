using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Objectives and Season Progress in phase 1 (pack-odds-2026-09 PLAN.md 5.5 and 6): the Welcome group's three untradeable
/// 82+ picks, the permanent New Club group, the rescaled grind groups, the Season reward cap at L10 and the dated gift.
/// </summary>
public sealed class ObjectivePhaseOneTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Thu 1 Oct 2026 12:00 UTC, inside the gift window.</summary>
    private const long TestWeek = 1790856000;

    /// <summary>Sun 4 Oct 2026 23:59:59 UK time (BST), the gift's last claimable second.</summary>
    private const long GiftDeadline = 1791154799;

    private static ObjectiveRules Rules => GameRules.Load(TestRuntime.DataRoot).Objectives;

    /// <summary>The club's unopened packs with their grant source and tradeability, in grant order.</summary>
    private static List<(long Pack, string Source, bool Untradeable)> UnopenedPacks(TestRuntime rt) =>
        rt.Game.ReadAsync(w => w.QueryAsync("SELECT pack_id, source, untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club ORDER BY id",
            r => (r.GetInt64(0), r.GetString(1), r.GetBoolean(2))), CancellationToken.None).GetAwaiter().GetResult();

    private static List<(long Pack, string Source, bool Untradeable)> Claim(TestRuntime rt, string path)
    {
        var before = UnopenedPacks(rt).Count;
        rt.Call("POST", $"{G}/scmp/campaign/{path}");
        return UnopenedPacks(rt).Skip(before).ToList();
    }

    /// <summary>Asserts the claim is refused with <paramref name="reason"/> and grants nothing.</summary>
    private static void Refused(TestRuntime rt, string path, string reason = "objective reward not earned yet")
    {
        var before = UnopenedPacks(rt).Count;
        var credits = rt.Game.Store().Credits();
        var (status, body, _) = rt.CallRaw("POST", $"{G}/scmp/campaign/{path}");
        Assert.Equal((409, "409", reason), (status, body["code"].S(), body["reason"].S()));
        Assert.Equal(before, UnopenedPacks(rt).Count);
        Assert.Equal(credits, rt.Game.Store().Credits());
    }

    [Fact]
    public void TheWelcomeGroupPaysThreeUntradeable82PicksAndItsXp()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(TestWeek));
        // Kick Off is earned with one match (owner, 2026-09-29): before it the group is tracked and pays nothing.
        var welcome = rt.CallObject("GET", $"{G}/scmp/campaign/active")["categories"]![0]!["groups"].Objects().First(g => g["id"].L() == 1);
        Assert.Equal(rt.Game.Rules.Objectives.GroupStates.InProgress, welcome["state"].L());
        Refused(rt, "group/1/rewards");
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        var xp = rt.Game.Store().Objectives().UserXp;
        var granted = Claim(rt, "group/1/rewards");
        Assert.Equal(new[] { (513L, "objective", true), (513L, "objective", true), (513L, "objective", true) }, granted);
        Assert.Equal(xp + 1500, rt.Game.Store().Objectives().UserXp);
        Assert.Empty(Claim(rt, "group/1/rewards"));
    }

    [Fact]
    public void TheNewClubGroupPaysAnInformAPickA543AndAn86Pick()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(TestWeek));
        var group = rt.Game.Rules.Objectives.GrindGroups.Single(g => g.Title == "New Club");
        Assert.Equal(new (string, long)[] { ("matches", 3), ("wins", 5), ("sbcs", 3) }, group.Objectives.Select(o => (o.Stat, o.Target)));

        // Nothing is claimable before the stats get there; the group waits for every objective.
        Refused(rt, $"group/{group.Id}/objective/{group.Objectives[0].Id}/rewards");
        for (var win = 0; win < 5; win++)
        {
            rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 2, 0, draft: false));
        }
        Refused(rt, $"group/{group.Id}/rewards");
        for (var sbc = 0; sbc < 3; sbc++)
        {
            rt.Game.InUnit(rt.Fut.Objectives.RecordSbcCompletedAsync);
        }

        var details = rt.CallObject("GET", $"{G}/scmp/active/category/1/details")["groupsList"].Objects().Single(g => g["groupId"].L() == group.Id);
        Assert.All(details["objectives"].Objects(), o => Assert.Equal("COMPLETED", o["state"].S()));
        Assert.Equal(new[] { (542L, "objective", true) }, Claim(rt, $"group/{group.Id}/objective/801/rewards"));
        Assert.Equal(new[] { (514L, "objective", true) }, Claim(rt, $"group/{group.Id}/objective/802/rewards"));
        // Every objective is complete but one is unclaimed: the group still waits for it, and says so.
        Refused(rt, $"group/{group.Id}/rewards");
        Assert.Equal(new[] { (543L, "objective", false) }, Claim(rt, $"group/{group.Id}/objective/803/rewards"));
        Assert.Equal(new[] { (515L, "objective", true) }, Claim(rt, $"group/{group.Id}/rewards"));
        Assert.Empty(Claim(rt, $"group/{group.Id}/rewards"));
        // An objective of another group, or a group the campaign does not have, is refused too.
        Refused(rt, $"group/{group.Id}/objective/401/rewards", "objective reward not offered");
        Refused(rt, "group/999/rewards", "objective reward not offered");
    }

    [Fact]
    public void TheGrindGroupsPayThePhaseOneValues()
    {
        var rules = Rules;
        var objectives = rules.GrindGroups.Where(group => group.SourceKey is null).SelectMany(group => group.Objectives).ToDictionary(o => o.Name);
        var coins = new Dictionary<string, long>
        {
            ["Warm Up"] = 5000, ["Winner"] = 15000, ["On The Scoresheet"] = 10000, ["Starter Collection"] = 2500,
            ["Getting Started"] = 10000, ["Draft Debut"] = 5000, ["Centurion"] = 50000,
        };
        Assert.All(coins, pair => Assert.Equal(pair.Value, objectives[pair.Key].Award.Coins));
        Assert.Equal(50000, rules.GrindGroups.Single(group => group.Id == 7).Award.Coins);

        // The three former 88+ picks are untradeable 84+ picks; no objective, group or level pays a 516 or an Ultimate group award.
        foreach (var name in new[] { "Regular Starter", "Brick Wall", "SBC Grinder" })
        {
            Assert.Equal((514L, true), (objectives[name].Award.Pack, objectives[name].Award.Untradeable));
        }
        var every = rules.GrindGroups.SelectMany(group => group.Objectives.Select(o => o.Award).Append(group.Award))
            .Concat(rules.SeasonProgress.LevelRewards).Concat(rules.Gifts.Select(gift => gift.Award)).ToList();
        Assert.DoesNotContain(every, award => award.Pack == 516);
        Assert.All(new long[] { 2, 3, 5, 6 }, id => Assert.Equal(403, rules.GrindGroups.Single(group => group.Id == id).Award.Pack));
        // Every pick an objective pays is untradeable.
        Assert.All(every.Where(award => award.Pack is >= 513 and <= 516 or 542), award => Assert.True(award.Untradeable, $"pack {award.Pack}"));

        // The pack-opening group is now Collector and counts completed SBCs.
        var collector = rules.GrindGroups.Single(group => group.Id == 4);
        Assert.Equal("Collector", collector.Title);
        Assert.All(collector.Objectives, o => Assert.Equal("sbcs", o.Stat));
        Assert.DoesNotContain(rules.GrindGroups.SelectMany(group => group.Objectives.Select(o => o.Name).Append(group.Title)), name => name.Contains("Addict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SeasonRewardsStopAtLevelTenUntilTheCapIsLifted()
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(TestWeek));
        var progress = rt.Game.Rules.Objectives.SeasonProgress;
        Assert.Equal((10, 10), (progress.RewardLevelCap, progress.ServedLevels));
        // The plan's first ten levels: 10k coins at odd levels, packs at even levels.
        var expected = new long?[] { null, 304, null, 305, null, 308, null, 319, null, 404 };
        for (var level = 1; level <= 10; level++)
        {
            var reward = progress.RewardFor(level);
            Assert.Equal(level % 2 == 1 ? 10000 : null, reward.Coins);
            Assert.Equal(expected[level - 1], reward.Pack);
        }

        rt.Game.Store().AddObjectivesXp(12 * progress.XpPerLevel);
        Assert.Equal(10, rt.CallObject("GET", $"{G}/scmp/campaign/active")["levels"].A().Count);
        var credits = rt.Game.Store().Credits();
        Assert.True(rt.CallObject("POST", $"{G}/scmp/campaign/level/11/reward/0")["alreadyClaimed"] is not null);
        Assert.Equal(credits, rt.Game.Store().Credits());
        Assert.DoesNotContain(11L, rt.Game.Store().Objectives().ClaimedLevels);
        var tenth = rt.CallObject("POST", $"{G}/scmp/campaign/level/10/reward/0");
        Assert.Equal(404, tenth["awards"]![0]!["packId"].L());

        // Lifting the cap is a rules change only: every level is served and claimable again.
        progress.RewardLevelCap = null;
        Assert.Equal(progress.LevelCount, rt.CallObject("GET", $"{G}/scmp/campaign/active")["levels"].A().Count);
        rt.Call("POST", $"{G}/scmp/campaign/level/11/reward/0");
        Assert.Contains(11L, rt.Game.Store().Objectives().ClaimedLevels);
        Assert.Equal(credits + 10000, rt.Game.Store().Credits());
    }

    [Theory]
    [InlineData(TestWeek, true)]
    [InlineData(GiftDeadline, true)]
    [InlineData(GiftDeadline + 1, false)]
    public void TheThankYouGiftIsAnUntradeableInformClaimableOnceUntilSundayNight(long now, bool open)
    {
        using var rt = TestRuntime.Create(time: new TestRuntime.FixedTime(now));
        var gift = rt.Game.Rules.Objectives.Gifts.Single();
        Assert.Equal((542L, true), (gift.Award.Pack, gift.Award.Untradeable));

        JsonObject? Listed() => rt.CallObject("GET", $"{G}/scmp/campaign/active")["categories"]![0]!["groups"].Objects().FirstOrDefault(g => g["id"].L() == gift.Id);
        Assert.Equal(open, Listed() is not null);
        // The club earns it with five matches (owner, 2026-09-29): until then it is a tracked objective and pays nothing.
        Assert.Equal(("matches", 5L), (gift.Stat, gift.Target));
        if (open)
        {
            var tracked = Listed()!;
            Assert.Equal(rt.Game.Rules.Objectives.GroupStates.InProgress, tracked["state"].L());
            var objective = tracked["objectives"]!.AsArray()[0]!.AsObject();
            Assert.Equal(("IN_PROGRESS", 0L), (objective["state"]!.GetValue<string>(), objective["progress"].L()));
        }
        // Before five matches the claim is refused as not earned; once the window has closed, as not offered.
        var refusal = open ? "objective reward not earned yet" : "objective reward not offered";
        for (var match = 1; match <= 5; match++)
        {
            Refused(rt, $"group/{gift.Id}/rewards", refusal);
            rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        }
        if (open)
        {
            Assert.Equal(new[] { (542L, "gift", true) }, Claim(rt, $"group/{gift.Id}/rewards"));
            Assert.Empty(Claim(rt, $"group/{gift.Id}/rewards"));
        }
        else
        {
            Refused(rt, $"group/{gift.Id}/rewards", refusal);
        }
        // Claim-all never pays it twice either.
        Assert.DoesNotContain(Claim(rt, "rewards/all"), pack => pack.Source == "gift");
    }
}
