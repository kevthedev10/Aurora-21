using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Helpers;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>Chooses an eligible promo, then a weighted bucket, then a card weighted by its inverse draw reduction.</summary>
public sealed class PromoCardSelector
{
    private sealed record Card(long Resource, double Weight);
    private sealed record Bucket(string Code, double Weight, Card[] Cards);
    private sealed record Promo(long Id, double Weight, Bucket[] Buckets);

    private readonly IReadOnlyDictionary<string, Promo[]> _byCategory;
    private readonly IReadOnlyDictionary<string, Promo[]> _byCode;

    public PromoCardSelector(IReadOnlyList<PromoRecord> promos, IReadOnlyList<PromoBucketRecord> buckets,
        IReadOnlyList<PromoCardRecord> memberships, IReadOnlyDictionary<DefinitionId, long> resourceByDefinition)
    {
        var resourcesByBucket = memberships.GroupBy(member => member.BucketId)
            .ToDictionary(group => group.Key,
                group => group.Select(member => member.DrawReduction is >= 1 and <= 999999
                    ? new Card(resourceByDefinition.GetValueOrDefault(member.PlayerCardDefinitionId), 1.0 / (double)member.DrawReduction)
                    : throw new InvalidDataException("A promo card draw reduction must be between 1 and 999999."))
                    .Where(card => card.Resource > 0).DistinctBy(card => card.Resource).ToArray());
        var bucketsByPromo = buckets.Where(bucket => bucket.Enabled).GroupBy(bucket => bucket.PromoId)
            .ToDictionary(group => group.Key,
                group => group.Select(bucket => new Bucket(bucket.Code, (double)bucket.DrawWeight,
                    resourcesByBucket.GetValueOrDefault(bucket.Id) ?? Array.Empty<Card>())).ToArray());
        _byCategory = promos.Where(promo => promo.Status == "active")
            .GroupBy(promo => promo.OddsCategory)
            .ToDictionary(group => group.Key,
                group => group.Select(promo => new Promo(promo.PromoId, (double)promo.DrawWeight,
                    bucketsByPromo.GetValueOrDefault(promo.PromoId) ?? Array.Empty<Bucket>())).ToArray());
        _byCode = promos.Where(promo => promo.Status == "active").ToDictionary(promo => promo.Code,
            promo => new[] { new Promo(promo.PromoId, (double)promo.DrawWeight,
                bucketsByPromo.GetValueOrDefault(promo.PromoId) ?? Array.Empty<Bucket>()) });
    }

    private bool Sources(string key, out Promo[] source) =>
        _byCategory.TryGetValue(key, out source!) || _byCode.TryGetValue(key, out source!);

    public long? Pick(string category, IReadOnlySet<long> eligible, GameRandom random,
        IReadOnlyDictionary<string, double>? bucketWeights = null, IReadOnlySet<long>? activePromoIds = null)
    {
        if (!Sources(category, out var categoryPromos)) return null;
        var promos = categoryPromos.Where(promo => activePromoIds is null || activePromoIds.Contains(promo.Id))
            .Select(promo => (Promo: promo,
                Buckets: promo.Buckets.Select(bucket => (Bucket: bucket,
                    Cards: bucket.Cards.Where(card => eligible.Contains(card.Resource)).ToArray()))
                    .Where(bucket => bucket.Cards.Length > 0).ToArray()))
            .Where(promo => promo.Buckets.Length > 0).ToArray();
        if (promos.Length == 0) return null;
        var selectedPromo = random.WeightedChoice(promos, promos.Select(promo => promo.Promo.Weight).ToList());
        var selectedBucket = random.WeightedChoice(selectedPromo.Buckets,
            selectedPromo.Buckets.Select(bucket => bucketWeights?.GetValueOrDefault(bucket.Bucket.Code) ?? bucket.Bucket.Weight).ToList());
        return selectedBucket.Cards.All(card => card.Weight == selectedBucket.Cards[0].Weight)
            ? random.Choice(selectedBucket.Cards).Resource
            : random.WeightedChoice(selectedBucket.Cards, selectedBucket.Cards.Select(card => card.Weight).ToList()).Resource;
    }

    public bool Contains(string category, long resourceId, IReadOnlySet<long>? activePromoIds = null) =>
        Sources(category, out var promos) && promos.Any(promo =>
            (activePromoIds is null || activePromoIds.Contains(promo.Id))
            && promo.Buckets.Any(bucket => bucket.Cards.Any(card => card.Resource == resourceId)));

    /// <summary>Conditional card probabilities after the category has been selected; adds overlapping promo routes.</summary>
    public IReadOnlyDictionary<long, double> Distribution(string category, IReadOnlySet<long> eligible,
        IReadOnlyDictionary<string, double>? bucketWeights = null, IReadOnlySet<long>? activePromoIds = null)
    {
        if (!Sources(category, out var source)) return new Dictionary<long, double>();
        var promos = source.Where(p => activePromoIds is null || activePromoIds.Contains(p.Id))
            .Select(p => (Promo: p, Buckets: p.Buckets.Select(b => (Weight: bucketWeights?.GetValueOrDefault(b.Code) ?? b.Weight,
                Cards: b.Cards.Where(card => eligible.Contains(card.Resource)).ToArray())).Where(b => b.Cards.Length > 0 && b.Weight > 0).ToArray()))
            .Where(p => p.Buckets.Length > 0).ToArray();
        var totalPromoWeight = promos.Sum(p => p.Promo.Weight);
        var result = new Dictionary<long, double>();
        foreach (var promo in promos)
        {
            var totalBucketWeight = promo.Buckets.Sum(b => b.Weight);
            foreach (var bucket in promo.Buckets)
            {
                var chance = promo.Promo.Weight / totalPromoWeight * bucket.Weight / totalBucketWeight;
                var cardWeight = bucket.Cards.Sum(card => card.Weight);
                foreach (var card in bucket.Cards) result[card.Resource] = result.GetValueOrDefault(card.Resource) + chance * card.Weight / cardWeight;
            }
        }
        return result;
    }

    public IReadOnlyList<string> EligibleCategories(IReadOnlySet<long> eligible, IReadOnlySet<long>? activePromoIds = null,
        bool includePromoCodes = false) =>
        (includePromoCodes ? _byCategory.Concat(_byCode) : _byCategory).Where(entry => entry.Value.Any(promo =>
            (activePromoIds is null || activePromoIds.Contains(promo.Id))
            && promo.Buckets.Any(bucket => bucket.Cards.Any(card => eligible.Contains(card.Resource)))))
            .Select(entry => entry.Key).Distinct().Order(StringComparer.Ordinal).ToArray();
}
