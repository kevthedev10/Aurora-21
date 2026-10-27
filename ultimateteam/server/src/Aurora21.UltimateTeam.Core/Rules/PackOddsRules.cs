using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class RatingBand
{
    public required long Min { get; set; }
    public required long Max { get; set; }
    public required double Weight { get; set; }
}

/// <summary>Player pick odds: the TOTW chance per choice that the panel fills into a new pick (picks carry their own odds table).</summary>
public sealed class PickOddsRules
{
    public required double InformChance { get; set; }
}

/// <summary>
/// How packs draw everything that is not a player (rules/pack_odds.json nonPlayerDrops): a pool by its weight, then an item,
/// a rare one at <see cref="RareWeight"/> of a common one's chance.
/// </summary>
public sealed class NonPlayerDropRules : IJsonOnDeserialized
{
    public static readonly string[] Pools = ["chemistry_style", "position_modifier", "manager_league", "kit", "ball", "badge", "stadium", "manager"];

    public required double RareWeight { get; set; }
    public required Dictionary<string, double> PoolWeights { get; set; }

    /// <summary>A pool's weight; a pool the rules leave out drops at weight 1.</summary>
    public double PoolWeight(string pool) => PoolWeights.TryGetValue(pool, out var weight) ? weight : 1.0;

    public void OnDeserialized()
    {
        if (!double.IsFinite(RareWeight) || RareWeight < 0 || PoolWeights.Any(entry => !Pools.Contains(entry.Key) || !double.IsFinite(entry.Value) || entry.Value < 0))
            throw new InvalidDataException($"nonPlayerDrops needs a rareWeight of 0 or more and poolWeights of 0 or more for {string.Join(", ", Pools)}");
    }
}

/// <summary>rules/pack_odds.json: pack categories, reward-copy revisions, and initial Gold Rare group profiles.</summary>
public sealed class PackOddsRules : IJsonOnDeserialized
{
    /// <summary>
    /// The odds-table row for upgraded specials: specials rated above a silver or bronze slot's tier whose base card is in
    /// that tier (pack odds v3). It is not a promo category.
    /// </summary>
    public const string UpgradedCategory = "upgraded";

    public required List<long> RedRewardRevisions { get; set; }
    /// <summary>The Icon chance per rare slot that the panel fills into a new pack (packs carry their own odds table).</summary>
    public required double IconChance { get; set; }
    /// <summary>The initial Gold Rare group ranges and weights, for reference and odds checks.</summary>
    public required List<RatingBand> GoldRareBands { get; set; }

    public required PickOddsRules Picks { get; set; }
    public required NonPlayerDropRules NonPlayerDrops { get; set; }

    public void OnDeserialized()
    {
        if (IconChance is < 0 or > 1 || Picks.InformChance is < 0 or > 1)
            throw new InvalidDataException("iconChance and picks.informChance must be between 0 and 1");
    }

    /// <summary>The index of the band holding a rating, or -1.</summary>
    public static int BandOf(IReadOnlyList<RatingBand> bands, long rating)
    {
        for (var index = 0; index < bands.Count; index++)
        {
            if (rating >= bands[index].Min && rating <= bands[index].Max)
            {
                return index;
            }
        }
        return -1;
    }
}
