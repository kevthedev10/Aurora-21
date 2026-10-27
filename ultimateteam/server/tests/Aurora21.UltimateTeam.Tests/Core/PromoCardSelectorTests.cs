using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Helpers;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class PromoCardSelectorTests
{
    [Fact]
    public void CardReductionsPreserveBucketMassAndRenormalizeAfterFilters()
    {
        var selector = new PromoCardSelector(
            [new PromoRecord(1,"gold_rare","Gold Rare",null,null,"active",true,"gold_rare",1)],
            [new PromoBucketRecord(10,1,"rating_82_83","82-83",1),new PromoBucketRecord(20,1,"rating_84_85","84-85",3)],
            [new PromoCardRecord(1,1,new DefinitionId(1),10),new PromoCardRecord(2,1,new DefinitionId(2),10,5),
             new PromoCardRecord(3,1,new DefinitionId(3),10,10),new PromoCardRecord(4,1,new DefinitionId(4),20)],
            new Dictionary<DefinitionId,long> { [new(1)]=100,[new(2)]=200,[new(3)]=300,[new(4)]=400 });
        var eligible = new HashSet<long> { 100,200,300,400 };
        var distribution = selector.Distribution("gold_rare",eligible);
        Assert.Equal(0.25,distribution[100]+distribution[200]+distribution[300],10);
        Assert.Equal(0.75,distribution[400],10);
        Assert.Equal(5,distribution[100]/distribution[200],10);
        Assert.Equal(10,distribution[100]/distribution[300],10);
        Assert.Equal(1.0/6,selector.Distribution("gold_rare",new HashSet<long> { 100,200 })[200],10);
        Assert.Equal(1,selector.Distribution("gold_rare",new HashSet<long> { 200 })[200],10);
        Assert.Empty(selector.Distribution("gold_rare",new HashSet<long>()));
        Assert.Equal(200,selector.Pick("gold_rare",new HashSet<long> { 200 },GameRandom.Seeded("single-marked",1)));
        var random = GameRandom.Seeded("card-reductions",1);
        var counts = new Dictionary<long,int>();
        for (var i=0;i<30_000;i++)
        {
            var card = selector.Pick("gold_rare",eligible,random)!.Value;
            counts[card]=counts.GetValueOrDefault(card)+1;
        }
        foreach (var (card,probability) in distribution)
            Assert.InRange(counts[card]/30_000.0,probability-0.01,probability+0.01);
    }

    [Fact]
    public void PromoAndBucketWeightsApplyAtSeparateLevels()
    {
        var selector = new PromoCardSelector(
            [new PromoRecord(1, "first", "First", null, null, "active", true, "icon", 2),
             new PromoRecord(2, "second", "Second", null, null, "active", true, "icon", 1)],
            [new PromoBucketRecord(10, 1, "single", "Single", 1),
             new PromoBucketRecord(11, 1, "pair", "Pair", 9),
             new PromoBucketRecord(12, 2, "all", "All", 1)],
            [new PromoCardRecord(1, 1, new DefinitionId(1), 10),
             new PromoCardRecord(2, 1, new DefinitionId(2), 11),
             new PromoCardRecord(3, 1, new DefinitionId(3), 11),
             new PromoCardRecord(4, 2, new DefinitionId(4), 12)],
            new Dictionary<DefinitionId, long> { [new(1)] = 100, [new(2)] = 200, [new(3)] = 300, [new(4)] = 400 });
        var random = GameRandom.Seeded("promo-weight-test", 1);
        var eligible = new HashSet<long> { 100, 200, 300, 400 };
        var counts = new Dictionary<long, int>();
        for (var draw = 0; draw < 30_000; draw++)
        {
            var card = selector.Pick("icon", eligible, random)!.Value;
            counts[card] = counts.GetValueOrDefault(card) + 1;
        }

        Assert.InRange((double)(counts[100] + counts[200] + counts[300]) / 30_000, 0.65, 0.68);
        Assert.InRange((double)counts[100] / (counts[100] + counts[200] + counts[300]), 0.09, 0.11);
        Assert.InRange((double)counts[200] / (counts[200] + counts[300]), 0.48, 0.52);
        Assert.Equal(100, selector.Pick("icon", new HashSet<long> { 100 }, random));
        Assert.Equal(400, selector.Pick("icon", eligible, random,
            activePromoIds: new HashSet<long> { 2 }));
        Assert.Null(selector.Pick("toty", eligible, random));
        Assert.Equal(400, selector.Pick("second", eligible, random));
        Assert.Null(selector.Pick("second", eligible, random, activePromoIds: new HashSet<long> { 1 }));
        Assert.Equal(new[] { "icon" }, selector.EligibleCategories(eligible));
        Assert.Contains("second", selector.EligibleCategories(eligible, includePromoCodes: true));
        Assert.Equal(1.0, selector.Distribution("second", eligible)[400]);
    }

    [Fact]
    public void ConditionalDistributionAddsRoutesAndRenormalizesAfterFilters()
    {
        var selector = new PromoCardSelector(
            [new PromoRecord(1, "first", "First", null, null, "active", true, "totw", 3),
             new PromoRecord(2, "second", "Second", null, null, "active", true, "totw", 1)],
            [new PromoBucketRecord(10, 1, "pair", "Pair", 1), new PromoBucketRecord(20, 2, "single", "Single", 1)],
            [new PromoCardRecord(1, 1, new DefinitionId(1), 10), new PromoCardRecord(2, 1, new DefinitionId(2), 10),
             new PromoCardRecord(3, 2, new DefinitionId(1), 20)],
            new Dictionary<DefinitionId, long> { [new(1)] = 100, [new(2)] = 200 });
        var probabilities = selector.Distribution("totw", new HashSet<long> { 100, 200 });
        Assert.Equal(0.625, probabilities[100], 10);
        Assert.Equal(0.375, probabilities[200], 10);
        Assert.Equal(1, probabilities.Values.Sum(), 10);
        Assert.Equal(1, selector.Distribution("totw", new HashSet<long> { 100 })[100], 10);
        Assert.Equal(0.5, selector.Distribution("totw", new HashSet<long> { 100, 200 }, activePromoIds: new HashSet<long> { 1 })[100], 10);
    }

    [Fact]
    public void NamedPackProfileCanOverrideBucketWeightsWithoutChangingMembers()
    {
        var selector = new PromoCardSelector(
            [new PromoRecord(1, "gold_rare", "Gold Rare", null, null, "active", true, "gold_rare", 1)],
            [new PromoBucketRecord(10, 1, "rating_75_79", "75–79", 60),
             new PromoBucketRecord(11, 1, "rating_90_99", "90–99", 40)],
            [new PromoCardRecord(1, 1, new DefinitionId(1), 10),
             new PromoCardRecord(2, 1, new DefinitionId(2), 11)],
            new Dictionary<DefinitionId, long> { [new(1)] = 100, [new(2)] = 200 });
        var random = GameRandom.Seeded("gold-rare-profile", 1);
        var eligible = new HashSet<long> { 100, 200 };
        var profile = new Dictionary<string, double> { ["rating_75_79"] = 95, ["rating_90_99"] = 5 };
        var high = Enumerable.Range(0, 10_000).Count(_ => selector.Pick("gold_rare", eligible, random, profile) == 200);
        Assert.InRange(high, 400, 600);
        Assert.Equal(200, selector.Pick("gold_rare", new HashSet<long> { 200 }, random, profile));
    }
}
