using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>Where a transfer market listing is in its life.</summary>
public static class MarketState
{
    public const string Active = "active";
    public const string Closed = "closed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
}

/// <summary>Who is selling a listing: another player's club, or the server's AI sellers.</summary>
public static class MarketListingKind
{
    public const string Player = "player";
    public const string Ai = "ai";
}

/// <summary>How a logged sale (market_trades) completed.</summary>
public static class MarketSale
{
    public const string BuyNow = "buyNow";
    public const string Auction = "auction";
}

/// <summary>One row of the server-wide transfer market.</summary>
public sealed record MarketListing(
    long TradeId,
    string Kind,
    long? SellerClubId,
    long? SellerPersonaId,
    string SellerName,
    long? SellerItemId,
    long ResourceId,
    JsonObject Item,
    long StartingBid,
    long BuyNow,
    long CurrentBid,
    long? BidderClubId,
    long Offers,
    string State,
    long CreatedAt,
    long EndTime,
    long SoldPrice)
{
    public bool IsActive => State == MarketState.Active;
}

/// <summary>Market filters whose semantics map exactly to indexed listing columns.</summary>
public sealed record MarketListingQuery(
    long ExcludedSellerClubId,
    long Now = long.MinValue,
    bool MatchNone = false,
    long? ResourceId = null,
    long? AssetOrResourceId = null,
    long? NationId = null,
    long? LeagueId = null,
    long? TeamId = null,
    IReadOnlyCollection<string>? Positions = null,
    IReadOnlyCollection<string>? ZonePositions = null,
    long? MinimumRating = null,
    long? MaximumRating = null,
    long? MinimumBuyNow = null,
    long? MaximumBuyNow = null,
    long? MinimumBid = null,
    long? MaximumBid = null,
    bool SpecialsOnly = false,
    IReadOnlyCollection<long>? RarityIds = null,
    int Offset = 0,
    int Limit = int.MaxValue,
    /// <summary>The native item type a search is restricted to (player, training, development); null finds every listing.</summary>
    string? ItemType = null);

/// <summary>One SQL-filtered market page and the total number of matching listings.</summary>
public sealed record MarketListingPage(IReadOnlyList<MarketListing> Listings, int Total);

/// <summary>The fixed-cost counts used by the FUT hub.</summary>
public sealed record MarketCounterSnapshot(
    int ActiveListings,
    int TradePileCount,
    int Selling,
    int Sold,
    int WatchCount,
    int Outbid,
    int Winning);


/// <summary>The requesting club's coins and watched trades, which market responses render with their auctions.</summary>
public sealed record MarketViewer(long Credits, IReadOnlySet<long> Watched);
