using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class RatingPriceBand
{
    public required long FromRating { get; set; }
    public required long Maximum { get; set; }
}

public sealed class PriceBoundRules
{
    public required long Minimum { get; set; }
    public required long SpecialMinimum { get; set; }
    public required List<RatingPriceBand> MaximumByRating { get; set; }
    public required long DefaultMaximum { get; set; }
    public required long SpecialMaximumFloor { get; set; }

    /// <summary>The highest maximum a special card can have.</summary>
    public required long SpecialMaximumCap { get; set; }
}

public sealed class BidIncrementBand
{
    public required long Below { get; set; }
    public required long Step { get; set; }
}

public sealed class FairValueBand
{
    public required long BelowRating { get; set; }
    public required long FromRating { get; set; }
    public required double Base { get; set; }
    public required double PerRating { get; set; }
    public required double PerRatingSquared { get; set; }
}

/// <summary>A fair value from a rating up (to the next entry's rating).</summary>
public sealed class FairValueStep
{
    public required long FromRating { get; set; }
    public required long Value { get; set; }
}

/// <summary>
/// An AI listing's reference price: a table by rating (byRating, monotonic; the phase 1 economy) or, when the table is
/// empty, the older formula bands.
/// </summary>
public sealed class FairValueRules : IJsonOnDeserialized
{
    public List<FairValueBand> Bands { get; set; } = new();
    public List<FairValueStep> ByRating { get; set; } = new();
    public required double SpecialMultiplier { get; set; }
    public required double SpecialRareflagDivisor { get; set; }
    public required double JitterMinimum { get; set; }
    public required int JitterSteps { get; set; }

    /// <summary>A base card's value at this rating, before rarity and jitter.</summary>
    public long BaseValue(long rating)
    {
        if (ByRating.Count > 0)
        {
            return (ByRating.LastOrDefault(step => rating >= step.FromRating) ?? ByRating[0]).Value;
        }
        var band = Bands.FirstOrDefault(entry => rating < entry.BelowRating) ?? Bands[^1];
        var steps = rating - band.FromRating;
        return (long)(band.Base + band.PerRating * steps + band.PerRatingSquared * steps * steps);
    }

    public void OnDeserialized()
    {
        if (ByRating.Count == 0 && Bands.Count == 0)
        {
            throw new InvalidDataException("rules/market.json fairValue needs byRating or bands.");
        }
        if (ByRating.Zip(ByRating.Skip(1)).Any(pair => pair.Second.FromRating <= pair.First.FromRating || pair.Second.Value < pair.First.Value))
        {
            throw new InvalidDataException("rules/market.json fairValue.byRating must rise in fromRating and never fall in value.");
        }
    }
}

/// <summary>From <see cref="From"/> on, AI listings carry no card rated above <see cref="MaxRating"/>.</summary>
public sealed class RatingCeiling
{
    public required DateTimeOffset From { get; set; }
    public required long MaxRating { get; set; }
}

public sealed class FactorRange
{
    public required double Minimum { get; set; }
    public required double Maximum { get; set; }
}

public sealed class LongRange
{
    public required long Minimum { get; set; }
    public required long Maximum { get; set; }
}

public sealed class AiListingRules : IJsonOnDeserialized
{
    public required int Target { get; set; }
    public required int MaxTarget { get; set; }
    public required int MaxPerTopUp { get; set; }
    public required FactorRange StartingBidFactor { get; set; }
    public required FactorRange BuyNowFactor { get; set; }
    public required List<long> DurationsSeconds { get; set; }
    public required string SellerNamePrefix { get; set; }
    public required LongRange SellerNumber { get; set; }
    public required long PreviewItemIdBase { get; set; }
    public required long PreviewItemIdSpan { get; set; }

    /// <summary>The only rarities an AI listing offers (base cards).</summary>
    public required HashSet<long> Rareflags { get; set; }

    /// <summary>The AI ceiling by date, earliest first (owner decision D7).</summary>
    public required List<RatingCeiling> MaxRatingSchedule { get; set; }

    /// <summary>The highest rating an AI listing may carry at <paramref name="nowSeconds"/>: the latest entry already in force, else the first.</summary>
    public long MaxRatingAt(long nowSeconds)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(nowSeconds);
        return (MaxRatingSchedule.LastOrDefault(entry => entry.From <= now) ?? MaxRatingSchedule[0]).MaxRating;
    }

    public void OnDeserialized()
    {
        if (Rareflags.Count == 0 || MaxRatingSchedule.Count == 0 || MaxRatingSchedule.Zip(MaxRatingSchedule.Skip(1)).Any(pair => pair.Second.From <= pair.First.From))
        {
            throw new InvalidDataException("rules/market.json aiListings needs rareflags and a maxRatingSchedule in rising from order.");
        }
    }
}

public sealed class ListingRules
{
    public required long DefaultDurationSeconds { get; set; }
    public required long MinimumDurationSeconds { get; set; }
    public required long MaximumDurationSeconds { get; set; }

    /// <summary>FUT's listing durations, shortest first.</summary>
    public required List<long> Durations { get; set; }
    public required long MaxAuctionsAllowed { get; set; }
    /// <summary>A bid leaves at least this long on the clock; 0 never extends a listing.</summary>
    public required long BidExtensionSeconds { get; set; }
    public required long SellerEstablishedDaysBefore { get; set; }
    public required long SellerPublicIdBase { get; set; }
    public required long SellerPublicIdSpan { get; set; }
    public required string DefaultSellerName { get; set; }
    public required int MaxSellerNameLength { get; set; }
    public required int SearchPageSize { get; set; }
    public required int MaxSearchPageSize { get; set; }
    public required long SlowSearchMilliseconds { get; set; }
    /// <summary>How long the hub's whole-market active listing count is reused before it is counted again; 0 always counts.</summary>
    public required long ActiveCountCacheMilliseconds { get; set; }
    public required int WatchlistPageSize { get; set; }
    public required int MaxWatchlistPageSize { get; set; }
}

/// <summary>rules/market.json: listing price bounds, bid steps, AI seller listings and listing limits.</summary>
public sealed class MarketRules
{
    public required int MaintenanceIntervalSeconds { get; set; }
    public required PriceBoundRules PriceBounds { get; set; }
    public required List<BidIncrementBand> BidIncrements { get; set; }
    public required long DefaultBidIncrement { get; set; }
    public required FairValueRules FairValue { get; set; }
    public required AiListingRules AiListings { get; set; }
    public required ListingRules Listings { get; set; }

    /// <summary>The share of a sale's price the seller does not receive: coins deleted from the economy (PLAN 4.1).</summary>
    public required long SellerTaxPercent { get; set; }

    /// <summary>What the seller of a sale at <paramref name="price"/> receives: the price less the tax, rounded in the seller's disfavour.</summary>
    public long SellerProceeds(long price)
    {
        price = Math.Max(0, price);
        return price - (price * SellerTaxPercent + 99) / 100;
    }

    /// <summary>The listing price range of a card with this rating and rarity.</summary>
    public (long Minimum, long Maximum) BoundsFor(long rating, bool special)
    {
        var bounds = PriceBounds;
        var maximum = bounds.MaximumByRating.FirstOrDefault(band => rating >= band.FromRating)?.Maximum ?? bounds.DefaultMaximum;
        return special
            ? (SnapUp(bounds.SpecialMinimum), SnapDown(Math.Min(Math.Max(maximum, bounds.SpecialMaximumFloor), bounds.SpecialMaximumCap)))
            : (SnapUp(bounds.Minimum), SnapDown(maximum));
    }

    public long BidIncrement(long price)
    {
        price = Math.Max(0, price);
        return BidIncrements.FirstOrDefault(band => price < band.Below)?.Step ?? DefaultBidIncrement;
    }

    /// <summary>True when <paramref name="price"/> is a price FIFA's own price picker can produce: a multiple of its band's step.</summary>
    public bool IsOnGrid(long price) => price > 0 && price % BidIncrement(price) == 0;

    /// <summary>The smallest grid price at or above <paramref name="price"/> (a price below 1 becomes the first step).</summary>
    public long SnapUp(long price)
    {
        price = Math.Max(1, price);
        var step = BidIncrement(price);
        return (price + step - 1) / step * step;
    }

    /// <summary>The largest grid price at or below <paramref name="price"/>, never below the first step.</summary>
    public long SnapDown(long price)
    {
        price = Math.Max(1, price);
        var step = BidIncrement(price);
        return Math.Max(BidIncrement(1), price / step * step);
    }

    /// <summary>A card's stable reference price for AI listings, within its bounds.</summary>
    public long FairValueFor(long resourceId, long rating, long rareflag, bool special)
    {
        var rules = FairValue;
        rating = Math.Max(1, rating);
        var value = rules.BaseValue(rating);
        if (special)
        {
            value = (long)(value * (rules.SpecialMultiplier + Math.Min(Math.Max(0, rareflag), 100) / rules.SpecialRareflagDivisor));
        }
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(resourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var seed = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        value = (long)(value * (rules.JitterMinimum + seed % (uint)rules.JitterSteps / 100.0));
        var (low, high) = BoundsFor(rating, special);
        return Math.Clamp(value, low, high);
    }
}
