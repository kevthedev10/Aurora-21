using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// FUT Draft run rewards (migration 135, rewards/draft.json from FIFA 21's published bundles): each mode's level per wins pays
/// its coins plus one choice drawn by weight from the run's seed, difficulty plays no part, single-player pays less and
/// tradeable (migration 175); plus the 15k entry and phase-legal offers. The claim and grant paths are covered end to end in RelationalDraftTests.
/// </summary>
public sealed class DraftRewardTests
{
    private static readonly DraftRules Rules = GameRules.Load(TestRuntime.DataRoot).Draft;

    private static readonly long[] OnlineCoins = [3000, 6000, 10000, 18000, 30000];
    private static readonly long[] SingleCoins = [1500, 3000, 5000, 9000, 15000];

    private static string Text(Reward reward) => reward.ToJson().ToJsonString();

    [Fact]
    public void ARunPaysItsLevelsCoinsAndOneOfItsChoicesTheSameForTheSameSeed()
    {
        foreach (var (online, coins, mode) in new[] { (true, OnlineCoins, DraftRewardLevel.Online), (false, SingleCoins, DraftRewardLevel.Single) })
        {
            for (var wins = 0; wins <= 4; wins++)
            {
                var level = Rules.RewardLevel(mode, wins)!;
                for (var seed = 1; seed <= 20; seed++)
                {
                    var payout = Rules.RunReward(wins, online, seed);
                    Assert.Equal(coins[wins], payout.Level.Coins);
                    Assert.Equal(Text(level.Choices[payout.Choice - 1].Reward), Text(payout.Drawn));
                    Assert.Equal(coins[wins] + payout.Drawn.Coins, payout.Coins);
                    Assert.Equal(online ? "draft_online" : "draft_single", payout.Source);
                    Assert.All(payout.Lines, line => Assert.False(line.Untradeable));
                    Assert.Equal(payout.Choice, Rules.RunReward(wins, online, seed).Choice);
                }
            }
        }
        // Wins past the table pay its last level.
        Assert.Equal(30000, Rules.RunReward(9, online: true, 1).Level.Coins);
    }

    [Fact]
    public void TheDrawFollowsTheWeights()
    {
        var online4 = Rules.RewardLevel(DraftRewardLevel.Online, 4)!;
        var drawn = Enumerable.Range(1, 4000).Select(seed => Rules.RunReward(4, online: true, seed).Choice).ToHashSet();
        Assert.Equal(Enumerable.Range(1, online4.Choices.Count), drawn.Order());

        var weighted = Rules.RewardLevels.Select(level => level.Mode == DraftRewardLevel.Online && level.Wins == 0
            ? new DraftRewardLevel { Mode = level.Mode, Wins = 0, Reward = level.Reward, Choices = [
                new DraftRewardChoice { Weight = 1, Reward = new Reward { Coins = 1 } }, new DraftRewardChoice { Weight = 3, Reward = new Reward { Coins = 2 } }] }
            : level).ToList();
        var rules = GameRules.Load(TestRuntime.DataRoot).Draft.WithRewards(weighted);
        var heavy = Enumerable.Range(1, 4000).Count(seed => rules.RunReward(0, online: true, seed).Choice == 2);
        Assert.InRange(heavy / 4000.0, 0.72, 0.78);
    }

    [Fact]
    public void TheSeedHoldsFifa21sBundlesWithTheirSubstitutes()
    {
        Assert.Equal([6, 7, 7, 12, 19, 2, 1, 3, 7, 18], Rules.RewardLevels.Select(level => level.Choices.Count));
        Assert.All(Rules.RewardLevels.SelectMany(level => level.Choices), choice => Assert.Equal(1, choice.Weight));
        var packs = Rules.RewardLevels.SelectMany(level => level.Choices).SelectMany(choice => choice.Reward.Lines).Select(line => line.PackId!.Value).ToHashSet();
        // Draft Token Pack (500) pays 15,000 coins instead; the reward-only Gold packs were merged into the Players packs.
        Assert.DoesNotContain(500, packs);
        Assert.Empty(packs.Intersect([300L, 301L, 303L]));
        Assert.Contains(Rules.RewardLevel(DraftRewardLevel.Single, 0)!.Choices, choice => choice.Reward is { Coins: 15000, Lines.Count: 0 });
        using var rt = TestRuntime.Create();
        Assert.All(packs, pack => Assert.NotEmpty(rt.Game.Packs.GetPackConfig(pack)));
    }

    [Fact]
    public void LevelsMustCoverEveryWinsCountAndPayOnlyCoinsAndPacks()
    {
        var levels = Rules.RewardLevels.ToList();
        Assert.Contains("0 to 4 wins", Assert.Throws<InvalidDataException>(() =>
            GameRules.Load(TestRuntime.DataRoot).Draft.WithRewards(levels.Where(level => level.Wins != 2))).Message);
        DraftRewardLevel With(Reward reward, int weight = 1) => new() { Mode = DraftRewardLevel.Online, Wins = 0, Reward = new Reward(),
            Choices = [new DraftRewardChoice { Weight = weight, Reward = reward }] };
        Assert.Null(DraftRules.LevelProblem(With(new Reward { Coins = 5 })));
        Assert.NotNull(DraftRules.LevelProblem(With(new Reward { FcPoints = 5 })));
        Assert.NotNull(DraftRules.LevelProblem(With(new Reward { Lines = [new RewardLine { CardResourceId = 1, Untradeable = false }] })));
        Assert.NotNull(DraftRules.LevelProblem(With(new Reward { Lines = [new RewardLine { PackId = 304, Untradeable = false, Source = "sbc" }] })));
        Assert.NotNull(DraftRules.LevelProblem(With(new Reward(), weight: 0)));
        Assert.NotNull(DraftRules.LevelProblem(new DraftRewardLevel { Mode = "single", Wins = 0, Reward = new Reward(), Choices = [] }));
    }

    [Fact]
    public void TheEntryIsFifteenThousandAndAHalfWinRateEarnsLessInGuaranteedCoins()
    {
        Assert.Equal(15000, Rules.Entry.Coins);
        // P(k wins) at p = 0.5: 0.5, 0.25, 0.125, 0.0625, 0.0625. The levels' own coins come to 7,250 online (0.48x the entry).
        var chances = new[] { 0.5, 0.25, 0.125, 0.0625, 0.0625 };
        var coinEv = chances.Select((chance, wins) => chance * Rules.RunReward(wins, online: true, 1).Level.Coins).Sum();
        Assert.Equal(7250, coinEv);
    }

    [Fact]
    public void DraftOffersStandardAndScheduledCards()
    {
        using var rt = TestRuntime.Create();
        var draft = rt.Fut.Draft;
        var inPool = draft.PoolFilter();
        var players = rt.Game.Players;

        // The initial schedule adds the 69 TOTW 1–3 cards to standard base cards.
        var totw = rt.Game.Rules.Totw.Weeks.Where(week => week.Week <= 3).SelectMany(week => week.Starters.Concat(week.Subs)).Select(card => card.ResourceId).ToList();
        Assert.Equal(69, totw.Distinct().Count());
        Assert.All(totw, id => Assert.True(inPool(id, players.Find(id)!.Rareflag), $"TOTW card {id}"));
        var laterTotw = rt.Game.Rules.Totw.Week(4)!.Starters.Select(card => card.ResourceId).Except(totw).First();
        Assert.False(inPool(laterTotw, players.Find(laterTotw)!.Rareflag));
        foreach (var excluded in new long[] { 11, 47, 48, 52, 53 })
        {
            var card = players.Entries.First(entry => entry.Value.Rareflag == excluded);
            Assert.False(inPool(card.Key, card.Value.Rareflag), $"rareflag {excluded}");
        }
        var catalog = rt.Game.Live.Current.Catalog;
        foreach (var tier in new[] { "base_icons", "mid_icons", "prime_icons", "prime_icon_moments" })
        {
            var promo = catalog.Promos.Single(p => p.Code == tier);
            var definition = catalog.PromoCards.First(c => c.PromoId == promo.PromoId).PlayerCardDefinitionId;
            var id = catalog.DefinitionsById[definition].NativeResourceId!.Value.Value;
            Assert.Equal(tier == "base_icons", inPool(id, players.Find(id)!.Rareflag));
        }
        Assert.DoesNotContain(players.Entries, entry => entry.Value.Rating == 99 && inPool(entry.Key, entry.Value.Rareflag));

        var offered = new List<JsonObject>();
        for (var seed = 1; seed <= 12; seed++)
        {
            var run = draft.NewRun();
            run["seed"] = seed;
            run["formation"] = "f442";
            Assert.Empty(draft.Elite99PickOrdinals(run));
            offered.AddRange(draft.PlayerChoices(run, 0, captain: true));
            for (var position = 0; position < 18; position++)
            {
                offered.AddRange(draft.PlayerChoices(run, position));
            }
        }
        Assert.All(offered, item => Assert.True(item.Int("rating") < 99 && inPool(item.Int("resourceId"), item.Int("rareflag")), item.ToJsonString()));
        // Scheduled TOTW cards reach the screens.
        Assert.Contains(offered, item => totw.Contains(item.Int("resourceId")));
        Assert.Contains(offered, item => item.Int("rareflag") == 12);
    }
}
