using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Modes;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

public sealed class CompletionRewardTests
{

    [Fact]
    public void EveryModePaysItsOwnFlatAmountRegardlessOfScoreOrResult()
    {
        using var rt = TestRuntime.Create();
        var seasons = rt.Game.Rules.Seasons;
        foreach (var mode in RewardModes.All)
        foreach (var outcome in new[] { MatchService.MatchOutcome.Win, MatchService.MatchOutcome.Draw, MatchService.MatchOutcome.Loss })
        {
            var body = new JsonObject { ["endReason"] = outcome.ToString().ToUpperInvariant(), ["secondsPlayed"] = 5461,
                ["myMatchStats"] = new JsonObject { ["goals"] = 10, ["redCards"] = 3, ["passingPercentage"] = 100 } };
            var facts = MatchEndFacts.From(body, 480, seasons.RewardEligibility);
            var decision = RewardEligibility.Decide(seasons, mode, facts, outcome, false, false);
            var (award, earned, partials) = RewardEligibility.Coins(seasons, decision, outcome, body);
            Assert.Equal((seasons.MatchCompletionRewards[mode].Coins, award), (award, earned));
            Assert.Empty(partials);
            Assert.True(decision.CountsProgress);
        }
    }

    [Fact]
    public void EveryEligibleMatchGrantsItsPackWithoutTheRetiredDailyCap()
    {
        using var rt = TestRuntime.Create();
        var paid = new RewardDecision(RewardVerdict.Paid, RewardModes.Rivals, 0, true, true);
        for (var match = 0; match < 25; match++)
            Assert.Equal(75L, rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, paid, 1234567, null, 2500)));
        Assert.Equal(25, rt.Game.Store().UnopenedPackIds().Count(id => id == 75));
    }

    [Fact]
    public void AModePaysItsFifaPointsOnlyForAPaidMatch()
    {
        using var rt = TestRuntime.Create();
        var reward = rt.Game.Rules.Seasons.MatchCompletionRewards[RewardModes.SquadBattles];
        Assert.All(rt.Game.Rules.Seasons.MatchCompletionRewards.Values, mode => Assert.Equal(0, mode.FifaPoints));
        var before = rt.Game.InUnit(uow => uow.FifaPointsAsync());
        var paid = new RewardDecision(RewardVerdict.Paid, RewardModes.SquadBattles, 0, true, reward.PackId is not null);
        rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, paid, null, null, reward.Coins));
        Assert.Equal(before, rt.Game.InUnit(uow => uow.FifaPointsAsync()));

        reward.FifaPoints = 40;
        rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, paid, null, null, reward.Coins));
        Assert.Equal(before + 40, rt.Game.InUnit(uow => uow.FifaPointsAsync()));
        var capped = RewardDecision.Nothing(RewardVerdict.PairCapped, RewardModes.SquadBattles);
        rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, capped, 1234567, null, 0));
        Assert.Equal(before + 40, rt.Game.InUnit(uow => uow.FifaPointsAsync()));
    }

    [Fact]
    public void ACompletedMatchEarnsItsModesSeasonXpWhateverTheResult()
    {
        using var rt = TestRuntime.Create();
        var rewards = rt.Game.Rules.Seasons.MatchCompletionRewards;
        Assert.All(rewards.Values, mode => Assert.Equal(300, mode.Xp));
        rewards[RewardModes.Rivals].Xp = 450;
        long Xp() => rt.Game.InUnit(uow => uow.ObjectivesAsync()).UserXp;
        var start = Xp();
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 3, 0, won: true, draft: false, mode: RewardModes.Rivals));
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 0, 2, won: false, draft: false, mode: RewardModes.Rivals));
        Assert.Equal(start + 900, Xp());
        // Counting a match outside a paying mode adds its stats but no XP.
        rt.Game.InUnit(uow => rt.Fut.Objectives.RecordMatchAsync(uow, 1, 0, draft: false));
        Assert.Equal(start + 900, Xp());
    }

    [Fact]
    public void SbcsAndOpenedPacksCountForObjectivesButEarnNoSeasonXp()
    {
        using var rt = TestRuntime.Create();
        var before = rt.Game.InUnit(uow => uow.ObjectivesAsync());
        rt.Game.InUnit(rt.Fut.Objectives.RecordSbcCompletedAsync);
        rt.Game.InUnit(rt.Fut.Objectives.RecordPackOpenedAsync);
        var after = rt.Game.InUnit(uow => uow.ObjectivesAsync());
        Assert.Equal(before.UserXp, after.UserXp);
        Assert.Equal((before.Stat("sbcs") + 1, before.Stat("packs") + 1), (after.Stat("sbcs"), after.Stat("packs")));
    }


    [Fact]
    public void RestartKeepsEditedMatchXpForEveryMode()
    {
        using var rt = TestRuntime.Create();
        rt.Game.InUnit(uow => uow.ExecuteAsync("""
            UPDATE ultimateteam.content_rules SET value='250'
            WHERE file='seasons' AND jsonb_array_length(path)=3
                AND path->>0='matchCompletionRewards' AND path->>2='xp';
            """));
        using var reopened = TestRuntime.Create(connectionString: rt.Detach());
        Assert.All(reopened.Game.Rules.Seasons.MatchCompletionRewards.Values, mode => Assert.Equal(250, mode.Xp));
        var stored = reopened.Game.InUnit(async uow => JsonNode.Parse((await RuleRows.DocumentsAsync(uow))["objectives"])!.AsObject());
        Assert.False(stored.ContainsKey("xp"));
    }

    [Theory]
    [InlineData(74, false)]
    [InlineData(74, true)]
    public void CompletionRewardPackTradeabilitySurvivesASettingChangeAndOpening(long packId, bool untradeable)
    {
        using var rt = TestRuntime.Create();
        var reward = rt.Game.Rules.Seasons.MatchCompletionRewards[RewardModes.Rivals];
        reward.PackId = packId;
        reward.Untradeable = untradeable;
        var paid = new RewardDecision(RewardVerdict.Paid, RewardModes.Rivals, 0, true, true);
        var grant = rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, paid, 1234567, null, reward.Coins));
        Assert.NotNull(grant);
        Assert.Equal(untradeable, rt.Game.InUnit(uow => uow.ScalarAsync<bool>(
            "SELECT untradeable FROM ultimateteam.unopened_packs WHERE club_id=@club AND pack_id=@pack", ("pack", packId))));
        reward.Untradeable = !untradeable;
        reward.PackId = null;
        rt.Game.Store().DeletePile("unassigned");
        var opened = rt.CallObject("POST", "/ut/game/fifa21/purchased/items", new JsonObject
            { ["packId"] = packId, ["useCredits"] = 0, ["usePreOrder"] = 1, ["currency"] = "PREORDER" });
        Assert.NotEmpty(opened["itemList"]!.AsArray());
        var items = rt.Game.Store().ListItems("unassigned");
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(untradeable, item.Bool("untradeable")));
        if (packId == 74 && !untradeable)
            Assert.Contains(items, item => item.Int("discardValue") > 0);
    }

    [Fact]
    public void AModeCanPayCoinsWithoutAPackAndZeroCoinsWithAPack()
    {
        using var rt = TestRuntime.Create();
        var seasons = rt.Game.Rules.Seasons;
        var body = new JsonObject { ["endReason"] = "WIN", ["secondsPlayed"] = 5461 };
        var facts = MatchEndFacts.From(body, 480, seasons.RewardEligibility);
        seasons.MatchCompletionRewards[RewardModes.Rivals].PackId = null;
        var decision = RewardEligibility.Decide(seasons, RewardModes.Rivals, facts, MatchService.MatchOutcome.Win, false, false);
        Assert.False(decision.GrantsPack);
        Assert.Null(rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, decision, 1234567, null, 2500)));
        var reward = seasons.MatchCompletionRewards[RewardModes.SquadBattles];
        reward.Coins = 0;
        decision = RewardEligibility.Decide(seasons, RewardModes.SquadBattles, facts, MatchService.MatchOutcome.Loss, false, false);
        Assert.True(decision.GrantsPack);
        Assert.Equal(0, RewardEligibility.Coins(seasons, decision, MatchService.MatchOutcome.Loss, body).Earned);
        Assert.NotNull(rt.Game.InUnit(uow => rt.Fut.Matches.SettleRewardAsync(uow, decision, null, null, 0)));
    }
}
