using System.Text.Json.Nodes;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Market operations backed by the relational schema.</summary>
public static class MarketRepository
{
    public static ValueTask<int> LockMarketAsync(this IUnitOfWork work) => RelationalMarketRepository.LockMarketAsync(work);
    public static ValueTask<bool> TryLockMarketAsync(this IUnitOfWork work) => RelationalMarketRepository.TryLockMarketAsync(work);
    public static ValueTask LockClubsAsync(this IUnitOfWork work, IEnumerable<long?> clubIds) => RelationalMarketRepository.LockClubsAsync(work, clubIds);
    public static ValueTask LockItemsAsync(this IUnitOfWork work, IEnumerable<(long ClubId, long ItemId)> items) => RelationalMarketRepository.LockItemsAsync(work, items);
    public static ValueTask<bool> AnyDueMarketListingsAsync(this IUnitOfWork work, long now) => RelationalMarketRepository.AnyDueMarketListingsAsync(work, now);
    public static ValueTask<bool> AnyDueMarketListingsAsync(this IUnitOfWork work, long now, IEnumerable<long> tradeIds) => RelationalMarketRepository.AnyDueMarketListingsAsync(work, now, tradeIds);
    public static ValueTask<MarketListing?> MarketListingAsync(this IUnitOfWork work, long tradeId, bool forUpdate = false) => RelationalMarketRepository.MarketListingAsync(work, tradeId, forUpdate);
    public static ValueTask<Dictionary<long, MarketListing>> MarketListingsAsync(this IUnitOfWork work, IEnumerable<long> tradeIds) => RelationalMarketRepository.MarketListingsAsync(work, tradeIds);
    public static ValueTask<List<MarketListing>> ActiveMarketListingsAsync(this IUnitOfWork work) => RelationalMarketRepository.ActiveMarketListingsAsync(work);
    public static ValueTask<MarketListingPage> SearchMarketListingsAsync(this IUnitOfWork work, MarketListingQuery query) => RelationalMarketRepository.SearchMarketListingsAsync(work, query);
    public static ValueTask<List<MarketListing>> DueMarketListingsAsync(this IUnitOfWork work, long now) => RelationalMarketRepository.DueMarketListingsAsync(work, now);
    public static ValueTask<List<MarketListing>> DueMarketListingsAsync(this IUnitOfWork work, long now, IEnumerable<long> tradeIds) => RelationalMarketRepository.DueMarketListingsAsync(work, now, tradeIds);
    public static ValueTask<List<MarketListing>> OwnMarketListingsAsync(this IUnitOfWork work) => RelationalMarketRepository.OwnMarketListingsAsync(work);
    public static ValueTask<MarketListing?> LiveMarketListingForItemAsync(this IUnitOfWork work, long itemId) => RelationalMarketRepository.LiveMarketListingForItemAsync(work, itemId);
    public static ValueTask<List<long>> LiveMarketListedItemsAsync(this IUnitOfWork work, long[] itemIds) => RelationalMarketRepository.LiveMarketListedItemsAsync(work, itemIds);
    public static ValueTask<int> CountActiveMarketListingsAsync(this IUnitOfWork work, string? kind = null) => RelationalMarketRepository.CountActiveMarketListingsAsync(work, kind);
    public static ValueTask<long> InsertMarketListingAsync(this IUnitOfWork work, string kind, string sellerName, long? sellerItemId, long resourceId,
        JsonObject item, long rating, long startingBid, long buyNow, long createdAt, long endTime) => RelationalMarketRepository.InsertMarketListingAsync(work, kind, sellerName, sellerItemId, resourceId, item, rating, startingBid, buyNow, createdAt, endTime);
    public static ValueTask<int> ClearFinishedMarketListingsAsync(this IUnitOfWork work, long itemId) => RelationalMarketRepository.ClearFinishedMarketListingsAsync(work, itemId);
    public static ValueTask<int> SetMarketBidAsync(this IUnitOfWork work, long tradeId, long amount, long bidderClubId, long endsNoSooner = 0) => RelationalMarketRepository.SetMarketBidAsync(work, tradeId, amount, bidderClubId, endsNoSooner);
    public static ValueTask<bool> CloseMarketListingAsync(this IUnitOfWork work, long tradeId, long price, long buyerClubId, long now) => RelationalMarketRepository.CloseMarketListingAsync(work, tradeId, price, buyerClubId, now);
    public static ValueTask<int> LogMarketTradeAsync(this IUnitOfWork work, long tradeId, string sale, long sellerProceeds) => RelationalMarketRepository.LogMarketTradeAsync(work, tradeId, sale, sellerProceeds);
    public static ValueTask<int> EndMarketListingAsync(this IUnitOfWork work, long tradeId, string state, long now) => RelationalMarketRepository.EndMarketListingAsync(work, tradeId, state, now);
    public static ValueTask<int> EndMarketListingsAsync(this IUnitOfWork work, IReadOnlyCollection<long> tradeIds, string state, long now) => RelationalMarketRepository.EndMarketListingsAsync(work, tradeIds, state, now);
    public static ValueTask<int> ClearMarketListingAsync(this IUnitOfWork work, long tradeId) => RelationalMarketRepository.ClearMarketListingAsync(work, tradeId);
    public static ValueTask<int> ClearSoldMarketListingsAsync(this IUnitOfWork work) => RelationalMarketRepository.ClearSoldMarketListingsAsync(work);
    public static ValueTask<List<long>> MarketWatchIdsAsync(this IUnitOfWork work) => RelationalMarketRepository.MarketWatchIdsAsync(work);
    public static ValueTask<MarketViewer> MarketViewerAsync(this IUnitOfWork work) => RelationalMarketRepository.MarketViewerAsync(work);
    public static ValueTask<List<MarketListing>> WatchedMarketListingsAsync(this IUnitOfWork work) => RelationalMarketRepository.WatchedMarketListingsAsync(work);
    public static ValueTask<MarketCounterSnapshot> MarketCountersAsync(this IUnitOfWork work) => RelationalMarketRepository.MarketCountersAsync(work);
    public static ValueTask<int> WatchMarketListingAsync(this IUnitOfWork work, long tradeId, long? clubId = null) => RelationalMarketRepository.WatchMarketListingAsync(work, tradeId, clubId);
    public static ValueTask<int> UnwatchMarketListingAsync(this IUnitOfWork work, long tradeId) => RelationalMarketRepository.UnwatchMarketListingAsync(work, tradeId);
    public static ValueTask WithdrawFromMarketAsync(this IUnitOfWork work) => RelationalMarketRepository.WithdrawFromMarketAsync(work);
}
