using Aurora21.UltimateTeam.Tests.Api;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class GoldAndBaseIconBucketBalanceTests
{
    // Independent expected profile: native assets identify all ordinary Gold Rare versions;
    // resource IDs identify only the chosen Base Icon version.
    private static readonly HashSet<long> DesirableGold = [183394,216388,231443,225100,228618,188377,
        201535,205923,230767,238794,239053,240130,231677,211300,212188,200949,230666,227928,
        229880,195864,215914,200104,188567,183277,211110,208722,209331,203376,231747,190871,20801,158023];
    private static readonly HashSet<long> DesirableIcons = [17015642,17015649,16967265,17019737,
        17014279,16967259,17014289,17025665,17024768,17015922,17015653,16778325,17024543,
        17033229,17032692,17032573,17024916,17016819,17014281,17024521,16999697,17016741,
        17020144,17014287,17028105,17033369,17015652,17024290];

    [Fact]
    public void FreshPoolsHaveCompleteCoverageAndFivefoldPerCardDifferences()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var snapshot = rt.Game.Live.Snapshot;
        var catalog = snapshot.Catalog;
        foreach (var pool in new[] { "gold_common", "gold_rare", "base_icons" })
        {
            var promo = catalog.Promos.Single(p => p.Code == pool);
            var members = catalog.PromoCards.Where(m => m.PromoId == promo.PromoId).ToArray();
            var eligible = members.Select(m => catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value).ToHashSet();
            var distribution = snapshot.PromoCardSelector.Distribution(pool, eligible);
            Assert.Equal(eligible.Count, distribution.Count);
            Assert.Equal(1, distribution.Values.Sum(), 10);
            Assert.All(distribution.Values, value => Assert.True(value > 0));
            foreach (var member in members)
            {
                var definition = catalog.DefinitionsById[member.PlayerCardDefinitionId];
                var card = catalog.PlayersById[member.PlayerCardDefinitionId];
                var bucket = catalog.PromoBuckets.Single(b => b.Id == member.BucketId);
                var desirable = pool == "gold_rare" ? DesirableGold.Contains(definition.NativeAssetId!.Value)
                    : pool == "base_icons" && DesirableIcons.Contains(definition.NativeResourceId!.Value.Value);
                Assert.True(bucket.Enabled);
                var band = card.Rating <= 79 ? "75_79" : card.Rating <= 81 ? "80_81" : card.Rating <= 83 ? "82_83"
                    : card.Rating <= 85 ? "84_85" : card.Rating <= 87 ? "86_87" : card.Rating <= 89 ? "88_89" : "90_99";
                Assert.Equal(pool == "gold_rare" ? $"rating_{band}" : $"rating_{card.Rating}", bucket.Code);
                Assert.Equal(desirable ? 5m : 1m, member.DrawReduction);
                if (pool == "gold_common") Assert.Equal(2L, card.CardTypeId);
                if (pool == "gold_rare") Assert.Equal(5L, card.CardTypeId);
            }
            // Compare every card to an ordinary peer in its bucket, independent of population.
            foreach (var rating in members.GroupBy(m => m.BucketId))
            {
                var cards = rating.Select(m => catalog.DefinitionsById[m.PlayerCardDefinitionId]).ToArray();
                bool Desirable(long resource) => pool == "base_icons" ? DesirableIcons.Contains(resource)
                    : pool == "gold_rare" && DesirableGold.Contains(cards.Single(d => d.NativeResourceId!.Value.Value == resource).NativeAssetId!.Value);
                var ordinary = cards.FirstOrDefault(d => !Desirable(d.NativeResourceId!.Value.Value));
                if (ordinary is null) continue; // No relative suppression when everyone at a rating is desirable.
                var reference = distribution[ordinary.NativeResourceId!.Value.Value];
                foreach (var card in cards)
                {
                    var resource = card.NativeResourceId!.Value.Value;
                    Assert.InRange(distribution[resource] / reference,
                        (Desirable(resource) ? 0.2 : 1) - 0.0005, (Desirable(resource) ? 0.2 : 1) + 0.0005);
                }
            }
            Assert.All(catalog.PromoBuckets.Where(b => b.PromoId == promo.PromoId && b.Enabled),
                b => Assert.Contains(members, m => m.BucketId == b.Id));
        }
        AssertRatingMasses(rt);
    }

    [Fact]
    public void CommonCurveIsRatingOnlyAndFiltersStillProduceEligibleCards()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var snapshot = rt.Game.Live.Snapshot;
        Dictionary<long,double> Distribution(string pool, Func<long,bool>? filter = null)
        {
            var promo = snapshot.Catalog.Promos.Single(p => p.Code == pool);
            var eligible = snapshot.Catalog.PromoCards.Where(m => m.PromoId == promo.PromoId)
                .Select(m => snapshot.Catalog.DefinitionsById[m.PlayerCardDefinitionId].NativeResourceId!.Value.Value)
                .Where(id => filter is null || filter(id)).ToHashSet();
            return snapshot.PromoCardSelector.Distribution(pool, eligible).ToDictionary();
        }
        var common = Distribution("gold_common");
        var perRating = common.GroupBy(card => snapshot.Players.Find(card.Key)!.Rating)
            .ToDictionary(group => group.Key, group => group.First().Value);
        var factors = new Dictionary<long,double> { [75]=1,[76]=0.8,[77]=0.6,[78]=0.4,[79]=0.25,[80]=0.15,[81]=0.08,[82]=0.04 };
        foreach (var (rating, chance) in perRating) Assert.Equal(factors[rating], chance/perRating[75], 8);
        foreach (var (id,chance) in common) Assert.Equal(perRating[snapshot.Players.Find(id)!.Rating], chance, 10);
        var rating83 = Distribution("gold_rare", id => snapshot.Players.Find(id)!.Rating == 83);
        var regular = rating83.First(card => !DesirableGold.Contains(snapshot.Players.Find(card.Key)!.IntOr("assetId",card.Key,card.Key))).Value;
        Assert.InRange(rating83[228618]/regular,0.1995,0.2005);
        foreach (var pool in new[] { "gold_common","gold_rare","base_icons" })
        {
            var filtered = Distribution(pool,id => snapshot.Players.Find(id)!.Rating >= (pool == "base_icons" ? 88 : 80));
            Assert.NotEmpty(filtered);
            Assert.Equal(1,filtered.Values.Sum(),10);
            var only = filtered.Keys.First();
            Assert.Equal(1,Distribution(pool,id => id == only)[only],10);
            Assert.Empty(Distribution(pool,_ => false));
        }
    }

    private static void AssertRatingMasses(TestRuntime rt, double highRareWeight=0.15, double icon86Weight=28)
    {
        var snapshot = rt.Game.Live.Snapshot;
        Dictionary<long,double> RatingWeights(string pool)
        {
            var promo = snapshot.Catalog.Promos.Single(p => p.Code == pool);
            return snapshot.Catalog.PromoBuckets.Where(b => b.PromoId == promo.PromoId && b.Enabled)
                .ToDictionary(b => long.Parse(b.Code.Split('_')[1]),b => (double)b.DrawWeight);
        }
        var rare = RatingWeights("gold_rare");
        foreach (var (low,high,weight) in new[] { (75,79,58.0),(80,81,29.0),(82,83,9.6),(84,85,2.7),
            (86,87,0.5),(88,89,highRareWeight),(90,99,0.05) })
            Assert.InRange(rare.Where(r => r.Key>=low && r.Key<=high).Sum(r => r.Value),weight-0.00002,weight+0.00002);
        var icons = RatingWeights("base_icons");
        foreach (var (rating,weight) in new[] { (85,35.0),(86,icon86Weight),(87,20.0),(88,10.0),(89,5.0),(90,1.5),(91,0.5) })
            Assert.InRange(icons[rating],weight-0.000002,weight+0.000002);
    }

}
