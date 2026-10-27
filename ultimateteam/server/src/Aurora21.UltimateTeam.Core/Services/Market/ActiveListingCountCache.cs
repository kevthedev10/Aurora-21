using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;

namespace Aurora21.UltimateTeam.Core.Services.Market;

/// <summary>
/// The whole market's active listing count that every hub load shows, shared by every club this process
/// serves. Counting all active listings on each hub load grows with the market, so the count is reused for
/// <c>listings.activeCountCacheMilliseconds</c>; the displayed total can lag the market by up to that long.
/// Expiry follows elapsed process time, not the game clock, so a fixed test clock does not freeze it.
/// </summary>
public sealed class ActiveListingCountCache
{
    private sealed record Entry(int Count, long ExpiresAtTicks);

    private Entry? _entry;

    /// <summary>The hub's market counts: the shared active count, counted again once it has expired, and this club's live counts.</summary>
    public async ValueTask<MarketCounterSnapshot> CountersAsync(IUnitOfWork uow, long lifetimeMilliseconds)
    {
        var now = Environment.TickCount64;
        var entry = Volatile.Read(ref _entry);
        int active;
        // A lifetime of 0 always counts, including when an earlier caller left a longer-lived entry.
        if (lifetimeMilliseconds > 0 && entry is not null && now < entry.ExpiresAtTicks)
        {
            active = entry.Count;
        }
        else
        {
            // Concurrent misses may each count once; the last writer's value is kept, and all are current.
            active = await RelationalMarketRepository.CountActiveMarketListingsAsync(uow);
            if (lifetimeMilliseconds > 0)
            {
                Volatile.Write(ref _entry, new Entry(active, now + lifetimeMilliseconds));
            }
        }
        return await RelationalMarketRepository.MarketClubCountersAsync(uow, active);
    }
}
