using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Web;

namespace Aurora21.UltimateTeam.Core.Services.Market;

/// <summary>
/// A market action the rules refuse. Most replies are still 200 with the reason. A refused bid or Buy Now
/// carries FIFA's own error instead (<see cref="Status"/>, <see cref="WireReason"/>): FIFA reads any 200 offer
/// reply as a win, so a lost Buy Now race used to open the assign screen with nothing to assign.
/// </summary>
public sealed class MarketException : Exception
{
    public MarketException(string message, int status = 200, string? wireReason = null) : base(message)
    {
        Status = status;
        WireReason = wireReason ?? message;
    }

    /// <summary>The HTTP status an offer reply uses; 200 keeps the old refusal body.</summary>
    public int Status { get; }

    /// <summary>The retail text FIFA's offer handler (CardsDLL 0x180344110) matches in a 461 body.</summary>
    public string WireReason { get; }

    /// <summary>Sold, expired or withdrawn: 461 "Permission Denied" = CARDS_CB_ERR_TRADE_CLOSED.</summary>
    public static MarketException TradeClosed(string message = "trade is no longer active") =>
        new(message, 461, "Permission Denied");

    /// <summary>A bid the trade cannot take now: 461 = CARDS_CB_ERR_TRADE_INACTIVE.</summary>
    public static MarketException BidRefused(string message) =>
        new(message, 461, "You are not allowed to bid on this trade");

    /// <summary>470 = CARDS_CB_ERR_NOT_ENOUGH_CREDITS.</summary>
    public static MarketException NotEnoughCoins() => new("not enough coins", 470, "Not enough credit");

    /// <summary>478 = CARDS_CB_ERR_NO_TRADE_EXISTS.</summary>
    public static MarketException NoTrade() => new("invalid trade ID", 478, "NO_TRADE_EXISTS");
}

/// <summary>What the FUT hub shows about the market for one club.</summary>
public sealed record MarketCounters(int ActiveListings, int TradePileCount, int Selling, int Sold, int WatchCount, int Outbid, int Winning);

/// <summary>One attempt to own and maintain the shared market.</summary>
public sealed record MarketMaintenanceRun(bool Acquired, int Expired, int Seeded);

/// <summary>
/// The transfer market every player on the server shares. Listings live in PostgreSQL; each card
/// and balance stays in its owner's club until a sale moves it. Bids are held in escrow: a bid
/// takes the bidder's coins at once and a higher bid refunds them. A sale pays the seller, moves
/// the card and closes the listing in one transaction.
/// </summary>
public sealed class SharedMarketService
{
    /// <summary>The refusal FIFA shows when a market transaction waited longer than the lock timeout.</summary>
    public const string BusyReason = "the transfer market is busy, try again";

    private readonly FutServices _fut;
    private readonly ILogger _log;

    public SharedMarketService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Market");
    }

    internal GameContext Game => _fut.Game;

    private MarketRules Rules => _fut.Game.Rules.Market;

    private long Now => Game.NowSeconds;

    // ---- reads ----------------------------------------------------------------

    /// <summary>Listing price bounds for each requested definition (or definition 0).</summary>
    public JsonArray PriceLimits(QueryValues query)
    {
        var key = new[] { "defId", "defIds", "assetId", "maskedDefId" }.FirstOrDefault(query.ContainsKey);
        var ids = key is null ? new List<long>() : query.Ids(key);
        if (ids.Count == 0)
        {
            ids.Add(0);
        }
        return new JsonArray(ids.Select(resourceId =>
        {
            var (minimum, maximum) = Game.Cards.MarketPriceBounds(resourceId);
            // FIFA's list panel switches to the server's range only when both bounds are above 255 (ListItem.InitOptionValues);
            // the values are logged so a client crash on that panel can be matched to the exact range it was given.
            _log.LogInformation("Market price limits defId={DefId} min={Minimum} max={Maximum}", resourceId, minimum, maximum);
            return (JsonNode)TemplateLibrary.Core.Object("market/price-limit", new TemplateArgs
            {
                ["resourceId"] = resourceId,
                ["minimum"] = minimum,
                ["maximum"] = maximum,
            });
        }).ToArray());
    }

    /// <summary>Other sellers' active listings matching the search, soonest ending first (retail order).</summary>
    public async ValueTask<JsonObject> SearchAsync(QueryValues query, CancellationToken cancellationToken = default)
    {
        var (template, args) = await SearchBodyAsync(query, stream: false, cancellationToken);
        return TemplateLibrary.Core.Object(template, args);
    }

    /// <summary>The search template and its values, for the route that sends the body unchanged.</summary>
    public ValueTask<(string Template, TemplateArgs Args)> SearchBodyAsync(QueryValues query, CancellationToken cancellationToken = default) =>
        SearchBodyAsync(query, stream: true, cancellationToken);

    /// <param name="stream">
    /// True writes the auctions straight into the response, embedding each item document without the
    /// copy a node array needs; false builds them as nodes for callers that read the result back.
    /// </param>
    private async ValueTask<(string Template, TemplateArgs Args)> SearchBodyAsync(QueryValues query, bool stream,
        CancellationToken cancellationToken)
    {
        return await Game.ReadAsync(async uow =>
        {
            var timer = Stopwatch.StartNew();
            var result = await SearchListingsAsync(uow, query);
            timer.Stop();
            _log.LogDebug(
                "Market search matches={Matches} returned={Returned} queryMs={Query}",
                result.Total, result.Listings.Count, timer.ElapsedMilliseconds);
            if (timer.ElapsedMilliseconds >= Rules.Listings.SlowSearchMilliseconds)
            {
                _log.LogWarning(
                    "Slow market search matches={Matches} returned={Returned} queryMs={Query}",
                    result.Total, result.Listings.Count, timer.ElapsedMilliseconds);
            }
            var viewer = await uow.MarketViewerAsync();
            var args = new TemplateArgs
            {
                ["credits"] = viewer.Credits,
                ["total"] = result.Total,
                ["currencies"] = Currencies(viewer.Credits),
            };
            SetAuctions(args, "auctionInfo", AuctionArgs(result.Listings, viewer.Watched), stream);
            return ("market/search", args);
        }, cancellationToken);
    }

    /// <summary>
    /// The transfer list: this club's listings, then trade-pile cards that are not listed. "Send to
    /// Transfer List" only moves a card to the trade pile, so without the second part it vanished.
    /// </summary>
    public ValueTask<JsonObject> TradePileAsync(CancellationToken cancellationToken = default) => TradePileAsync(null, cancellationToken);

    public async ValueTask<JsonObject> TradePileAsync(QueryValues? query, CancellationToken cancellationToken = default)
    {
        var (template, args) = await TradePileBodyAsync(query, stream: false, cancellationToken);
        return TemplateLibrary.Core.Object(template, args);
    }

    /// <summary>The transfer-list template and its values, for the route that sends the body unchanged.</summary>
    public ValueTask<(string Template, TemplateArgs Args)> TradePileBodyAsync(QueryValues? query, CancellationToken cancellationToken = default) =>
        TradePileBodyAsync(query, stream: true, cancellationToken);

    private async ValueTask<(string Template, TemplateArgs Args)> TradePileBodyAsync(QueryValues? query, bool stream,
        CancellationToken cancellationToken)
    {
        return await ReadExpiringAsync(RelationalMarketRepository.OwnMarketListingIdsAsync, async uow =>
        {
            var offset = query is null ? 0 : Math.Max(0, QueryInt(query, 0, "offset", "start"));
            var limit = query is null ? long.MaxValue : Math.Max(0, QueryInt(query, long.MaxValue, "count", "num", "pageSize"));
                var totals = await RelationalMarketRepository.TransferPageTotalsAsync(uow);
                var page = await RelationalMarketRepository.TransferPageIdsAsync(uow, offset, limit);
                var byId = await uow.MarketListingsAsync(page.Where(p => p.ListingId > 0).Select(p => p.ListingId));
                var viewer = await uow.MarketViewerAsync();
                var pageAuctions = AuctionArgs(page.Where(p => p.ListingId > 0).Select(p => byId[p.ListingId]), viewer.Watched);
                var unlisted = await uow.GetItemsAsync(page.Where(p => p.ItemId > 0).Select(p => p.ItemId).ToArray());
                foreach (var item in unlisted)
                    pageAuctions.Add(UnlistedAuctionArgs(item));
                var balance = viewer.Credits;
                var args = new TemplateArgs
                {
                    ["credits"] = balance, ["count"] = totals.Count,
                    ["maxAuctionsAllowed"] = Rules.Listings.MaxAuctionsAllowed,
                    ["selling"] = totals.Selling, ["sold"] = totals.Sold, ["currencies"] = Currencies(balance),
                };
                // Cards on the list whose card the club already owns, so FIFA offers no Send to Club for them: PUT /item would
                // keep the card in Unassigned and the next pack would be refused (471). An unsold card back from its listing
                // is still the club's own item, so it is read by that id.
                var ownListed = page.Where(p => p.ListingId > 0).Select(p => byId[p.ListingId].SellerItemId ?? 0).Where(id => id > 0).ToArray();
                var cards = unlisted.Concat(ownListed.Length == 0 ? Array.Empty<JsonObject>() : await uow.GetItemsAsync(ownListed)).ToList();
                args["duplicates"] = await ClubInventoryService.DuplicateListAsync(uow, cards);
                SetAuctions(args, "auctionInfo", pageAuctions, stream);
                return ("market/tradepile", args);
        }, cancellationToken);
    }

    /// <summary>The watched trades still on the server, paged by <c>offset</c>/<c>count</c>.</summary>
    public async ValueTask<JsonObject> WatchlistAsync(QueryValues? query = null, CancellationToken cancellationToken = default)
    {
        var (template, args) = await WatchlistBodyAsync(query, stream: false, cancellationToken);
        return TemplateLibrary.Core.Object(template, args);
    }

    /// <summary>The watchlist template and its values, for the route that sends the body unchanged.</summary>
    public ValueTask<(string Template, TemplateArgs Args)> WatchlistBodyAsync(QueryValues? query, CancellationToken cancellationToken = default) =>
        WatchlistBodyAsync(query, stream: true, cancellationToken);

    private async ValueTask<(string Template, TemplateArgs Args)> WatchlistBodyAsync(QueryValues? query, bool stream,
        CancellationToken cancellationToken)
    {
        return await ReadExpiringAsync(uow => uow.MarketWatchIdsAsync(), async uow =>
        {
            var offset = query is null ? 0 : Math.Max(0, QueryInt(query, 0, "offset", "start"));
            var count = query is null
                ? Rules.Listings.WatchlistPageSize
                : Math.Clamp(QueryInt(query, Rules.Listings.WatchlistPageSize, "count"), 1, Rules.Listings.MaxWatchlistPageSize);
            var totals = await RelationalMarketRepository.WatchPageTotalsAsync(uow);
            var listings = await RelationalMarketRepository.WatchedMarketPageAsync(uow, offset, count);
            var watched = listings.Select(listing => listing.TradeId).ToHashSet();
            var credits = await uow.CreditsAsync();
            var args = new TemplateArgs
            {
                ["credits"] = credits,
                ["count"] = totals.Count,
                ["offset"] = offset,
                ["winning"] = totals.Winning,
                ["outbid"] = totals.Outbid,
                ["currencies"] = Currencies(credits),
            };
            var bought = await RelationalMarketRepository.BoughtMarketCopiesAsync(uow,
                listings.Where(listing => listing.State == "closed" && listing.BidderClubId == uow.ClubId).Select(listing => listing.TradeId));
            args["duplicates"] = await WonDuplicatesAsync(uow, bought);
            SetAuctions(args, "auctionInfo", AuctionArgs(listings, watched, bought), stream);
            return ("market/watchlist", args);
        }, cancellationToken);
    }

    /// <summary>The current state of the named trades (unknown ids are left out).</summary>
    public async ValueTask<JsonObject> StatusAsync(IEnumerable<long> tradeIds, CancellationToken cancellationToken = default)
    {
        var (template, args) = await StatusBodyAsync(tradeIds, stream: false, cancellationToken);
        return TemplateLibrary.Core.Object(template, args);
    }

    /// <summary>The trade-status template and its values, for the route that sends the body unchanged.</summary>
    public ValueTask<(string Template, TemplateArgs Args)> StatusBodyAsync(IEnumerable<long> tradeIds, CancellationToken cancellationToken = default) =>
        StatusBodyAsync(tradeIds, stream: true, cancellationToken);

    private async ValueTask<(string Template, TemplateArgs Args)> StatusBodyAsync(IEnumerable<long> tradeIds, bool stream,
        CancellationToken cancellationToken)
    {
        var requested = tradeIds.ToList();
        return await ReadExpiringAsync(_ => ValueTask.FromResult(requested), async uow =>
        {
            var viewer = await uow.MarketViewerAsync();
            var byId = await uow.MarketListingsAsync(requested);
            var listings = requested.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var bought = await RelationalMarketRepository.BoughtMarketCopiesAsync(uow,
                listings.Where(listing => listing.State == "closed" && listing.BidderClubId == uow.ClubId).Select(listing => listing.TradeId));
            var args = new TemplateArgs
            {
                ["credits"] = viewer.Credits,
                ["currencies"] = Currencies(viewer.Credits),
            };
            args["duplicates"] = await WonDuplicatesAsync(uow, bought);
            SetAuctions(args, "auctionInfo", AuctionArgs(listings, viewer.Watched, bought), stream);
            return ("market/status", args);
        }, cancellationToken);
    }

    /// <summary>
    /// The won copies still waiting in Unassigned whose card the club already owns, as the duplicateItemIdList FIFA reads
    /// with a Buy Now, a bid and the watchlist. Without it Transfer Targets offered Send to Club for a duplicate; PUT /item
    /// kept the card in Unassigned and the next pack was refused (471) before the store showed it as a duplicate.
    /// </summary>
    private static async ValueTask<JsonArray> WonDuplicatesAsync(IUnitOfWork uow, IReadOnlyDictionary<long, (long ItemId, long Owners)> bought)
    {
        if (bought.Count == 0) return new JsonArray();
        var waiting = (await uow.GetItemsAsync(bought.Values.Select(copy => copy.ItemId).Distinct().ToArray()))
            .Where(item => item.Text("pile") == "unassigned").ToList();
        return await ClubInventoryService.DuplicateListAsync(uow, waiting);
    }

    public async ValueTask<MarketCounters> CountersAsync(CancellationToken cancellationToken = default)
    {
        return await Game.ReadAsync(async uow =>
        {
            var counts = await Game.ActiveListingCount.CountersAsync(uow, Rules.Listings.ActiveCountCacheMilliseconds);
            return new MarketCounters(
                counts.ActiveListings,
                counts.TradePileCount,
                counts.Selling,
                counts.Sold,
                counts.WatchCount,
                counts.Outbid,
                counts.Winning);
        }, cancellationToken);
    }

    public void LogRejected(string reason) =>
        _log.LogWarning("Market action rejected persona={Persona} reason={Reason}", Game.Player.PersonaId, reason);

    /// <summary>A refused action: no auctions, the club's coins and the reason.</summary>
    public async ValueTask<JsonObject> RejectedAsync(string reason, CancellationToken cancellationToken = default)
    {
        return await Game.ReadAsync(async uow =>
        {
            LogRejected(reason);
            var credits = await uow.CreditsAsync();
            return TemplateLibrary.Core.Object("market/rejected", new TemplateArgs
            {
                ["credits"] = credits,
                ["currencies"] = Currencies(credits),
                ["reason"] = reason,
            });
        }, cancellationToken);
    }

    // ---- listing ----------------------------------------------------------------

    /// <summary>Lists an owned, tradeable player that is in no squad. Listing it again returns the live listing.</summary>
    public async ValueTask<JsonObject> ListAsync(long itemId, long startingBid, long buyNow, long duration,
        CancellationToken cancellationToken = default)
    {
        var listing = await WriteAsync("list", async uow =>
        {
            // Relisting updates history: acquire those listing rows before the seller club.
            await RelationalMarketRepository.LockItemListingHistoryAsync(uow, itemId);
            // The seller club protects listed-card guards and serializes listings of the same item.
            await uow.LockClubsAsync(new long?[] { Game.ClubId });
            var owned = await uow.ItemOfAsync(Game.ClubId, itemId, forUpdate: true);
            var type = owned?.Text("itemType", "player") ?? "player";
            if (owned is null || type is not ("player" or "training" or "development") && !ItemFactory.IsClubItem(owned))
            {
                throw new MarketException("player item was not found");
            }
            // A kit, badge, ball or stadium the club plays with stays until another takes its slot.
            if (ItemFactory.IsClubItem(owned) && await uow.ScalarAsync<bool>("""
                    SELECT EXISTS(SELECT 1 FROM ultimateteam.club_slot_bindings b JOIN ultimateteam.items i ON i.id=b.item_id
                    WHERE b.club_id=@club AND i.native_item_id=@native)
                    """, ("native", itemId)))
            {
                throw new MarketException("this club item is in use");
            }
            if (type == "player")
            {
                // Loans and squads are player-only restrictions; a consumable stack lists whole.
                if (owned.IntKeep("loans") != 0 || owned.Bool("expiredLoan"))
                {
                    throw new MarketException("loan cards cannot be listed");
                }
                // Only the active squad holds a card back: FIFA offers listing for a card in a saved squad, and refusing it
                // left a listing that never happened (live 10-05). The listing takes the card out of those squads.
                var squads = (await uow.ListSquadsAsync()).Where(squad => SquadDocuments.ItemIds(squad).Contains(itemId)).ToList();
                if (owned.Text("pile") == "squad" || squads.Any(squad => squad.Bool("active")))
                {
                    throw new MarketException("remove this card from your squad first");
                }
                if (squads.Count > 0)
                {
                    await uow.RelationalItems.Squads.ReleaseItemsAsync(uow, new[] { itemId });
                }
            }
            if (owned.Bool("untradeable"))
            {
                throw new MarketException("this card is untradeable");
            }
            if (await uow.LiveMarketListingForItemAsync(itemId) is { } existing)
            {
                return existing;
            }
            var resourceId = owned.Int("resourceId", owned.Int("assetId"));
            var (low, high) = Game.Cards.MarketPriceBounds(resourceId);
            // FIFA's price picker only produces grid prices; refuse anything else rather than change the player's price.
            if ((startingBid > 0 && !Rules.IsOnGrid(startingBid)) || (buyNow > 0 && !Rules.IsOnGrid(buyNow)))
            {
                throw new MarketException("price is not a valid listing price");
            }
            var start = Math.Clamp(startingBid > 0 ? startingBid : low, low, high);
            var buy = Math.Max(start, Math.Min(high, buyNow > 0 ? buyNow : start));
            var limits = Rules.Listings;
            var seconds = Math.Clamp(duration > 0 ? duration : limits.DefaultDurationSeconds, limits.MinimumDurationSeconds, limits.MaximumDurationSeconds);
            var item = owned.Copy();
            item["pile"] = "trade";
            item["itemState"] = "forSale";
            await uow.SaveItemAsync(item);
            await uow.ClearFinishedMarketListingsAsync(itemId);
            var now = Now;
            var tradeId = await uow.InsertMarketListingAsync(MarketListingKind.Player, await SellerNameAsync(uow), itemId, resourceId, item,
                item.Int("rating"), start, buy, now, now + seconds);
            return await uow.MarketListingAsync(tradeId) ?? throw new InvalidOperationException($"Listing {tradeId} was not persisted");
        }, cancellationToken);
        _log.LogInformation("Market listing trade={Trade} item={Item} start={Start} buyNow={BuyNow}", listing.TradeId, itemId, listing.StartingBid, listing.BuyNow);
        return await Game.ReadAsync(async uow =>
        {
            var viewer = await uow.MarketViewerAsync();
            return TemplateLibrary.Core.Object("market/listed", new TemplateArgs
            {
                ["tradeId"] = listing.TradeId,
                ["auctionInfo"] = new JsonArray(Auction(listing, viewer.Watched)),
                ["credits"] = viewer.Credits,
                ["currencies"] = Currencies(viewer.Credits),
            });
        }, cancellationToken);
    }

    /// <summary>
    /// Transfer List "Re-list All" (PUT auctionhouse/relist, empty body; Python server 5fd85ed): every expired card on this
    /// club's transfer list goes back up at its own starting bid, Buy Now price and duration. FIFA's reply parser
    /// (FutRelistAllServerResponse, 0x1802ef060) reads only dynamicObjectivesUpdates and then fetches the Transfer List
    /// again; the reply lists the new trades. A new listing retires the card's expired entry.
    /// </summary>
    public async ValueTask<JsonObject> RelistAllAsync(CancellationToken cancellationToken = default)
    {
        await ExpireDueAsync(cancellationToken);
        var expired = await Game.ReadAsync(uow => uow.QueryAsync("""
            SELECT market_listing_id, seller_item_id, starting_bid, buy_now, duration_seconds, end_time - created_at
            FROM ultimateteam.market_listings
            WHERE seller_club_id = @club AND kind = @kind AND state = 'expired' AND NOT seller_cleared AND seller_item_id IS NOT NULL
                -- only cards still on the Transfer List: one sent back to the club stays there
                AND EXISTS (SELECT 1 FROM ultimateteam.items t
                    WHERE t.club_id = seller_club_id AND t.native_item_id = seller_item_id AND t.pile = 'trade')
            ORDER BY market_listing_id
            """, r => (Trade: r.GetInt64(0), Item: r.GetInt64(1), Start: r.GetInt64(2), BuyNow: r.GetInt64(3), Stored: r.GetInt64(4), Ran: r.GetInt64(5)),
            ("kind", MarketListingKind.Player)), cancellationToken);
        var relisted = new JsonArray();
        foreach (var row in expired)
        {
            try
            {
                var listed = await ListAsync(row.Item, row.Start, row.BuyNow, RelistDuration(row.Stored, row.Ran), cancellationToken);
                relisted.Add(new JsonObject { ["id"] = listed.Int("tradeId") });
            }
            catch (MarketException error)
            {
                _log.LogWarning("Re-list skipped trade {Trade}: {Reason}", row.Trade, error.Message);
            }
        }
        _log.LogInformation("Market re-list all relisted={Count} of {Expired}", relisted.Count, expired.Count);
        return new JsonObject { ["tradeIdList"] = relisted };
    }

    /// <summary>
    /// The duration a listing ran for. A listing from before migration 057 stored none, and expiry moved its end time to
    /// when it was processed, so it takes the longest FUT duration that fits (a minute's slack for processing).
    /// </summary>
    private long RelistDuration(long stored, long ran)
    {
        const long Slack = 60;
        var durations = Rules.Listings.Durations;
        return stored > 0 ? stored : durations.Where(duration => duration <= ran + Slack).DefaultIfEmpty(durations[0]).Max();
    }

    /// <summary>Takes a listing off this club's transfer list; an unsold card goes back to the club.</summary>
    public async ValueTask<JsonObject> ClearTradeAsync(long tradeId, CancellationToken cancellationToken = default)
    {
        await WriteAsync("clear-trade", async uow =>
        {
            if (await uow.MarketListingAsync(tradeId, forUpdate: true) is not { } listing || listing.SellerClubId != Game.ClubId)
            {
                return;
            }
            await uow.LockClubsAsync(new long?[] { Game.ClubId });
            if (listing.SellerItemId is { } sellerItem)
            {
                await uow.LockItemsAsync(new[] { (Game.ClubId, sellerItem) });
            }
            if (listing.IsActive && listing.BidderClubId is not null)
            {
                throw new MarketException("an auction with bids cannot be cancelled");
            }
            if (listing.State is MarketState.Active or MarketState.Expired)
            {
                if (listing.IsActive)
                {
                    await uow.EndMarketListingAsync(tradeId, MarketState.Cancelled, Now);
                }
                if (await uow.GetItemAsync(listing.SellerItemId ?? 0) is { } item)
                {
                    item["pile"] = "club";
                    item["itemState"] = "free";
                    await uow.SaveItemAsync(item);
                }
            }
            await uow.ClearMarketListingAsync(tradeId);
        }, cancellationToken);
        return await TradePileAsync(cancellationToken);
    }

    public async ValueTask<JsonObject> ClearSoldAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync("clear-sold", async uow => { await uow.ClearSoldMarketListingsAsync(); }, cancellationToken);
        return await TradePileAsync(cancellationToken);
    }

    // ---- watching, bidding and buying ------------------------------------------------

    public Task WatchAsync(long tradeId, CancellationToken cancellationToken = default) =>
        Game.RunAsync("market-watch", async uow =>
        {
            await RelationalMarketRepository.LockMarketSharedAsync(uow);
            // Deferred watch FKs check club then listing at commit. Lock the listing first,
            // so a purchase cannot hold it while waiting for this transaction's club FK lock.
            await uow.ExecuteAsync("-- query: market.watch.listing-lock\nSELECT market_listing_id FROM ultimateteam.market_listings WHERE market_listing_id=@trade FOR KEY SHARE", ("trade", tradeId));
            await uow.WatchMarketListingAsync(tradeId);
        }, cancellationToken);

    public Task UnwatchAsync(long tradeId, CancellationToken cancellationToken = default) =>
        Game.RunAsync("market-unwatch", async uow =>
        {
            await RelationalMarketRepository.LockMarketSharedAsync(uow);
            await uow.UnwatchMarketListingAsync(tradeId);
        }, cancellationToken);

    /// <summary>A bid or offer: at or above Buy Now it buys, otherwise it bids.</summary>
    public async ValueTask OfferAsync(long tradeId, long amount, CancellationToken cancellationToken = default)
    {
        var listing = await Game.ReadAsync(uow => uow.MarketListingAsync(tradeId), cancellationToken);
        if (listing is null)
        {
            throw MarketException.NoTrade();
        }
        if (listing.BuyNow > 0 && amount >= listing.BuyNow)
        {
            await BuyNowAsync(tradeId, amount, cancellationToken);
        }
        else
        {
            await PlaceBidAsync(tradeId, amount, cancellationToken);
        }
    }

    public async ValueTask PlaceBidAsync(long tradeId, long amount, CancellationToken cancellationToken = default)
    {
        var buyNow = await WriteAsync("bid", async uow =>
        {
            if (await uow.MarketListingAsync(tradeId, forUpdate: true) is not { IsActive: true } listing || listing.EndTime <= Now)
            {
                throw MarketException.TradeClosed();
            }
            if (listing.SellerClubId == Game.ClubId)
            {
                throw MarketException.BidRefused("you cannot bid on your own listing");
            }
            if (listing.BuyNow > 0 && amount >= listing.BuyNow)
            {
                return true;
            }
            var current = listing.CurrentBid;
            var minimum = current <= 0 ? listing.StartingBid : current + Rules.BidIncrement(current);
            if (amount < minimum)
            {
                throw MarketException.BidRefused("bid is below the minimum");
            }
            // Bids land on the grid too; the listing's own price is always accepted (older AI rows may be off-grid).
            if (amount != minimum && !Rules.IsOnGrid(amount))
            {
                throw MarketException.BidRefused("bid is not a valid bid amount");
            }
            // The bidder and the outbid bidder being refunded, in ascending id order.
            await uow.LockClubsAsync(new[] { Game.ClubId, listing.BidderClubId });
            var credits = await uow.CreditsOfAsync(Game.ClubId);
            if (listing.BidderClubId == Game.ClubId)
            {
                if (amount <= current)
                {
                    return false;
                }
                if (credits < amount - current)
                {
                    throw MarketException.NotEnoughCoins();
                }
                await uow.AddCreditsToAsync(Game.ClubId, -(amount - current));
            }
            else
            {
                if (credits < amount)
                {
                    throw MarketException.NotEnoughCoins();
                }
                await uow.AddCreditsToAsync(Game.ClubId, -amount);
                if (listing.BidderClubId is { } previous && current > 0)
                {
                    await uow.AddCreditsToAsync(previous, current);
                }
            }
            // As on retail FUT, a bid in the last seconds resets the clock so others can answer it.
            await uow.SetMarketBidAsync(tradeId, amount, Game.ClubId, Now + Rules.Listings.BidExtensionSeconds);
            await uow.WatchMarketListingAsync(tradeId);
            return false;
        }, cancellationToken);
        if (buyNow)
        {
            await BuyNowAsync(tradeId, amount, cancellationToken);
        }
    }

    public async ValueTask BuyNowAsync(long tradeId, long offered = 0, CancellationToken cancellationToken = default)
    {
        await WriteAsync("buy-now", async uow =>
        {
            if (await uow.MarketListingAsync(tradeId, forUpdate: true) is not { } listing)
            {
                throw MarketException.NoTrade();
            }
            if (!listing.IsActive || listing.EndTime <= Now)
            {
                throw MarketException.TradeClosed();
            }
            var price = listing.BuyNow;
            if (price <= 0 || (offered > 0 && offered < price))
            {
                throw MarketException.BidRefused("offer is below Buy Now");
            }
            // Buyer, seller and any refunded bidder in ascending id order, then the seller's card.
            await LockSettlementAsync(uow, new[] { listing }, Game.ClubId);
            var escrowed = listing.BidderClubId == Game.ClubId && listing.CurrentBid > 0;
            if (escrowed)
            {
                // The buyer's own bid is already held: only the difference is taken.
                var difference = price - listing.CurrentBid;
                if (await uow.CreditsOfAsync(Game.ClubId) < difference)
                {
                    throw MarketException.NotEnoughCoins();
                }
                await uow.AddCreditsToAsync(Game.ClubId, -difference);
            }
            else if (listing.BidderClubId is { } previous && listing.CurrentBid > 0)
            {
                await uow.AddCreditsToAsync(previous, listing.CurrentBid);
            }
            await SettleAsync(uow, listing, Game.ClubId, price, escrowed, MarketSale.BuyNow);
        }, cancellationToken);
        _log.LogInformation("Market buy now trade={Trade} buyer={Buyer}", tradeId, Game.Player.PersonaId);
    }

    // ---- settlement and upkeep ----------------------------------------------------

    /// <summary>
    /// Sells a listing to <paramref name="buyerClubId"/> at <paramref name="price"/>: charges the buyer
    /// (unless the coins are already held), gives them a copy of the card, pays the seller, removes
    /// the seller's card, closes the listing and logs the sale (market_trades). Every check runs before the first write.
    /// </summary>
    private async ValueTask SettleAsync(IUnitOfWork uow, MarketListing listing, long buyerClubId, long price, bool escrowed, string sale)
    {
        if (!listing.IsActive)
        {
            throw MarketException.TradeClosed();
        }
        if (listing.SellerClubId == buyerClubId)
        {
            throw MarketException.BidRefused("you cannot buy your own listing");
        }
        price = Math.Max(0, price);
        if (!escrowed && await uow.CreditsOfAsync(buyerClubId) < price)
        {
            throw MarketException.NotEnoughCoins();
        }
        if (listing.SellerClubId is { } sellerClub && await uow.ItemOfAsync(sellerClub, listing.SellerItemId ?? 0, forUpdate: true) is null)
        {
            throw MarketException.TradeClosed("seller card is no longer available");
        }
        if (!escrowed)
        {
            await uow.AddCreditsToAsync(buyerClubId, -price);
        }
        // The copy keeps the trade's market id, which the buyer's screens already show. Its timestamp is the listing's
        // close time, which is how the won trade finds the copy again.
        var now = Now;
        var bought = listing.Item.Copy();
        bought["id"] = ClubRepository.MarketItemIdOf(listing.TradeId);
        bought["timestamp"] = now;
        bought["pile"] = "unassigned";
        bought["itemState"] = "free";
        bought["lastSalePrice"] = price;
        // A bought card has had at least two owners, so it is never loyal on arrival (ChemistryModel.Loyal).
        bought["owners"] = Math.Max(2, bought.Int("owners") + 1);
        bought["untradeable"] = false;
        StartAtNewClub(bought);
        if (ItemFactory.IsClubItem(bought))
        {
            // A club item card carries its own id twice; its card templates start untradeable.
            if (bought.ContainsKey("itemId")) bought["itemId"] = bought.IntKeep("id");
            ItemFactory.MakeTradeable(bought);
        }
        else
        {
            bought["duplicate"] = false;
            bought["isDuplicate"] = false;
        }
        await uow.SaveItemOfAsync(buyerClubId, bought.IntKeep("id"), bought);
        var proceeds = listing.SellerClubId is null ? 0 : Rules.SellerProceeds(price);
        if (listing.SellerClubId is { } seller)
        {
            // The seller receives the price less the transfer tax (rules/market.json sellerTaxPercent); the tax is deleted.
            await uow.AddCreditsToAsync(seller, proceeds);
            await uow.DeleteItemOfAsync(seller, listing.SellerItemId ?? 0);
        }
        if (!await uow.CloseMarketListingAsync(listing.TradeId, price, buyerClubId, now))
        {
            throw MarketException.TradeClosed();
        }
        await uow.LogMarketTradeAsync(listing.TradeId, sale, proceeds);
        await uow.WatchMarketListingAsync(listing.TradeId, buyerClubId);
    }

    /// <summary>
    /// Ends listings past their time: the highest bidder buys, an unsold card returns to its
    /// seller's transfer list, and a sale that can no longer complete refunds the bidder.
    /// </summary>
    public async ValueTask<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        if (!await Game.ReadAsync(uow => uow.AnyDueMarketListingsAsync(now), cancellationToken))
        {
            return 0;
        }
        return await MaintainAsync("expire", uow => ExpireDueLockedAsync(uow, now), cancellationToken);
    }

    /// <summary>
    /// A market view that never shows a listing past its end time as live. Its trades are checked and, when none is
    /// due, the view is read in the same transaction; only when some are due are just those rows expired (without
    /// global upkeep) and the view read again. The common case is one read transaction instead of three.
    /// </summary>
    private async ValueTask<T> ReadExpiringAsync<T>(Func<IUnitOfWork, ValueTask<List<long>>> tradeIds,
        Func<IUnitOfWork, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var now = Now;
        var (due, result) = await Game.ReadAsync(async uow =>
        {
            var ids = (await tradeIds(uow)).Distinct().ToArray();
            return await uow.AnyDueMarketListingsAsync(now, ids) ? (ids, default(T)) : ((long[]?)null, await read(uow));
        }, cancellationToken);
        if (due is null)
        {
            return result!;
        }
        await MaintainAsync("expire-selected", uow => ExpireDueLockedAsync(uow, now, due), cancellationToken);
        return await Game.ReadAsync(read, cancellationToken);
    }

    /// <summary>Expires due rows while the caller owns the market lock and transaction.</summary>
    private async ValueTask<int> ExpireDueLockedAsync(IUnitOfWork uow, long now, IReadOnlyCollection<long>? tradeIds = null)
    {
        var due = tradeIds is null ? await uow.DueMarketListingsAsync(now) : await uow.DueMarketListingsAsync(now, tradeIds);
        await LockSettlementAsync(uow, due, buyerClubId: null);
        // Built inside the action, so a retried attempt starts from nothing.
        var unresolved = new List<(MarketListing Listing, string Reason)>();
        // Listings without a bid only change state, so they end together in one statement after the loop.
        var unsold = new List<long>();
        foreach (var listing in due)
        {
            if (listing.BidderClubId is { } bidder && listing.CurrentBid > 0)
            {
                try
                {
                    await uow.SavepointAsync(() => SettleAsync(uow, listing, bidder, listing.CurrentBid, escrowed: true, MarketSale.Auction));
                }
                catch (MarketException error)
                {
                    unresolved.Add((listing, error.Message));
                }
                continue;
            }
            if (listing.Kind == MarketListingKind.Player && listing.SellerClubId is { } seller
                && await uow.ItemOfAsync(seller, listing.SellerItemId ?? 0, forUpdate: true) is { } item)
            {
                item["pile"] = "trade";
                item["itemState"] = "free";
                await uow.SaveItemOfAsync(seller, item.IntKeep("id"), item);
            }
            unsold.Add(listing.TradeId);
        }
        await uow.EndMarketListingsAsync(unsold, MarketState.Expired, now);
        foreach (var (listing, reason) in unresolved)
        {
            await uow.AddCreditsToAsync(listing.BidderClubId!.Value, listing.CurrentBid);
            await uow.EndMarketListingAsync(listing.TradeId, MarketState.Cancelled, now);
            _log.LogError("Cancelled unresolvable expired trade {Trade}: {Reason}", listing.TradeId, reason);
        }
        return due.Count;
    }

    /// <summary>Tops the AI listings up towards the target with catalogue cards at prices around their fair value.</summary>
    public async ValueTask<int> SeedAiListingsAsync(CancellationToken cancellationToken = default)
    {
        var ai = Rules.AiListings;
        var target = Math.Clamp(Game.Config.Switches.MarketTarget ?? ai.Target, 0, ai.MaxTarget);
        if (target <= 0 || await Game.ReadAsync(uow => uow.CountActiveMarketListingsAsync(MarketListingKind.Ai), cancellationToken) >= target)
        {
            return 0;
        }
        return await MaintainAsync("seed", uow => SeedAiListingsLockedAsync(uow, target), cancellationToken);
    }

    /// <summary>Adds AI rows while the caller owns the market lock and transaction.</summary>
    private async ValueTask<int> SeedAiListingsLockedAsync(IUnitOfWork uow, int target)
    {
        var ai = Rules.AiListings;
        var missing = Math.Min(target - await uow.CountActiveMarketListingsAsync(MarketListingKind.Ai), ai.MaxPerTopUp);
        if (missing <= 0)
        {
            return 0;
        }
        var tradeable = AiListable(Game.NowSeconds);
        if (tradeable.Count == 0)
        {
            return 0;
        }
        var random = GameRandom.Shared;
        var now = Now;
        var seeded = 0;
        foreach (var resourceId in random.Sample(tradeable, Math.Min(missing, tradeable.Count)))
        {
            var meta = Game.Players.Find(resourceId)!;
            var value = Rules.FairValueFor(resourceId, meta.Rating, meta.Rareflag, meta.IsSpecial);
            var (low, high) = Game.Cards.MarketPriceBounds(resourceId);
            // Prices snap UP to FIFA's price grid, so every AI buy-in stays at or above the unrounded value (quick sell stays below it).
            var starting = Math.Clamp(Rules.SnapUp((long)(value * Uniform(random, ai.StartingBidFactor))), low, high);
            var buyNow = Math.Max(starting, Math.Min(high, Rules.SnapUp((long)(value * Uniform(random, ai.BuyNowFactor)))));
            var preview = _fut.Items.BuildPlayer(resourceId, ai.PreviewItemIdBase + random.NextInt64(1, ai.PreviewItemIdSpan + 1), "market");
            var seller = ai.SellerNamePrefix + random.NextInt64(ai.SellerNumber.Minimum, ai.SellerNumber.Maximum + 1).ToString(CultureInfo.InvariantCulture);
            await uow.InsertMarketListingAsync(MarketListingKind.Ai, seller, null, resourceId, preview, preview.Int("rating"),
                starting, buyNow, now, now + random.Choice(ai.DurationsSeconds));
            seeded++;
        }
        return seeded;
    }

    /// <summary>
    /// The cards the AI Market Maker may list now (rules/market.json aiListings): tradeable base cards of the listed rarities
    /// only, never a special, grant-only, Pro, custom or reward-only card, rated at most the ceiling in force.
    /// </summary>
    public List<long> AiListable(long nowSeconds)
    {
        var ai = Rules.AiListings;
        var ceiling = ai.MaxRatingAt(nowSeconds);
        var cards = Game.Cards;
        return Game.Players.Entries
            .Where(entry => entry.Value.Rating > 0 && entry.Value.Rating <= ceiling && ai.Rareflags.Contains(entry.Value.Rareflag)
                && !entry.Value.IsSpecial && !entry.Value.Bool("untradeable") && entry.Key < PlayerCatalog.ResourceRevisionSize
                && !cards.IsGrantOnly(entry.Key) && !cards.IsAquaPro(entry.Key) && cards.RarityAllowed(entry.Value.Rareflag, null))
            .Select(entry => entry.Key)
            .ToList();
    }

    /// <summary>
    /// Runs global upkeep only when this process wins the non-blocking database advisory lock.
    /// The lock covers both expiry settlement and AI top-up in one transaction.
    /// </summary>
    internal async Task<MarketMaintenanceRun> RunMaintenanceOnceAsync(CancellationToken cancellationToken = default)
    {
        return await Game.RunRetryableAsync("market-maintenance", async uow =>
        {
            if (!await uow.TryLockMarketAsync())
            {
                return new MarketMaintenanceRun(false, 0, 0);
            }
            var expired = await ExpireDueLockedAsync(uow, Now);
            var ai = Rules.AiListings;
            var target = Math.Clamp(Game.Config.Switches.MarketTarget ?? ai.Target, 0, ai.MaxTarget);
            var seeded = target > 0 ? await SeedAiListingsLockedAsync(uow, target) : 0;
            return new MarketMaintenanceRun(true, expired, seeded);
        }, cancellationToken);
    }

    // ---- rendering ------------------------------------------------------------------

    private List<TemplateArgs> AuctionArgs(IEnumerable<MarketListing> listings, IReadOnlySet<long> watched,
        IReadOnlyDictionary<long, (long ItemId, long Owners)>? bought = null) =>
        listings.Select(listing => AuctionArgs(listing, watched, bought)).ToList();

    /// <summary>
    /// Supplies an auction array as nodes, or — when streaming — writes each auction later from these
    /// arguments. Both forms take arguments built now, so the clock and the item documents are read at
    /// the same point either way.
    /// </summary>
    private static void SetAuctions(TemplateArgs args, string name, List<TemplateArgs> auctions, bool stream)
    {
        if (!stream)
        {
            args[name] = new JsonArray(auctions.Select(auction => (JsonNode?)TemplateLibrary.Core.Object("market/auction", auction)).ToArray());
            return;
        }
        args.Defer(name, writer =>
        {
            writer.WriteStartArray();
            foreach (var auction in auctions)
            {
                TemplateLibrary.Core.Write(writer, "market/auction", auction);
            }
            writer.WriteEndArray();
        });
    }

    private JsonObject Auction(MarketListing listing, IReadOnlySet<long> watched) =>
        TemplateLibrary.Core.Object("market/auction", AuctionArgs(listing, watched));

    private TemplateArgs AuctionArgs(MarketListing listing, IReadOnlySet<long> watched,
        IReadOnlyDictionary<long, (long ItemId, long Owners)>? bought = null)
    {
        var item = MarketItem(listing.Item, listing.ResourceId);
        // Other clubs see the trade's market id, which the buyer's copy keeps, so a buy does not change the id FIFA is
        // showing; the seller sees their own card's id.
        if (listing.SellerClubId != Game.ClubId)
        {
            item["id"] = ClubRepository.MarketItemIdOf(listing.TradeId);
            if (item.ContainsKey("itemId")) item["itemId"] = item.IntKeep("id");
        }
        // A won trade names the buyer's own copy: the squad-screen buy moves and squads the card by the id in the buy
        // response. Copies from before market ids carry a band id.
        if (bought is not null && bought.TryGetValue(listing.TradeId, out var copy))
        {
            item["id"] = copy.ItemId;
            item["owners"] = copy.Owners;
            item["lastSalePrice"] = listing.SoldPrice;
            StartAtNewClub(item);
        }
        var isWatched = watched.Contains(listing.TradeId);
        return new TemplateArgs
        {
            ["tradeId"] = listing.TradeId,
            ["itemData"] = item,
            ["tradeState"] = listing.State,
            ["buyNowPrice"] = listing.BuyNow,
            ["currentBid"] = listing.CurrentBid,
            ["startingBid"] = listing.StartingBid,
            ["offers"] = listing.Offers,
            ["watched"] = isWatched,
            ["bidState"] = BidState(listing, isWatched),
            ["expires"] = Math.Max(listing.State == MarketState.Closed ? -1 : 0, listing.EndTime - Now),
            ["sellerEstablished"] = listing.CreatedAt - Rules.Listings.SellerEstablishedDaysBefore * 86400,
            ["sellerId"] = SellerPublicId(listing.SellerPersonaId),
            ["sellerName"] = listing.SellerName,
            ["rating"] = item.Int("rating"),
        };
    }

    /// <summary>A trade-pile card with no listing, in FIFA's inactive trade state.</summary>
    private TemplateArgs UnlistedAuctionArgs(JsonObject owned) =>
        new()
        {
            ["tradeId"] = 0,
            ["itemData"] = MarketItem(owned, owned.Int("resourceId", owned.Int("assetId"))),
            ["tradeState"] = "inactive",
            ["buyNowPrice"] = 0,
            ["currentBid"] = 0,
            ["startingBid"] = 0,
            ["offers"] = 0,
            ["watched"] = false,
            ["bidState"] = "none",
            ["expires"] = 0,
            ["sellerEstablished"] = 0,
            ["sellerId"] = 0,
            ["sellerName"] = string.Empty,
            ["rating"] = owned.Int("rating"),
        };

    private JsonObject MarketItem(JsonObject stored, long resourceId)
    {
        // A consumable stack keeps its own shape and quick-sell value, and a club item its own card (a kit's category picks
        // its Home/Away/Third art; as a player card FIFA showed a blank "KIT"); only players take the native player conversion.
        var item = stored.Text("itemType", "player") is "training" or "development"
            ? (JsonObject)stored.DeepClone()
            : ItemFactory.IsClubItem(stored) ? _fut.Native.PackItem(stored)
            : _fut.Native.PlayerItem(stored);
        var (low, high) = Game.Cards.MarketPriceBounds(resourceId);
        item["marketDataMinPrice"] = low;
        item["marketDataMaxPrice"] = high;
        return item;
    }

    /// <summary>
    /// Retail loyalty: a transfer starts a player's time at the buyer's club. His club stats (statsList and assists) start
    /// at zero and the seller's loyalty bonus does not travel, so he is loyal only after ten games for the new club
    /// (MatchService.RecordItemStatsAsync); lifetime stats travel with the card. Before this every bought card kept the
    /// pack's loyaltyBonus 1 and counted as loyal at once.
    /// </summary>
    private void StartAtNewClub(JsonObject item)
    {
        if (item.Text("itemType", "player") != "player")
        {
            return;
        }
        item["statsList"] = Game.Cards.StatRows(null);
        item["assists"] = 0;
        item["loyaltyBonus"] = 0;
    }

    /// <summary>The viewer's relation to a trade: their winning bid, their Buy Now, a watched trade someone else leads, or none.</summary>
    private string BidState(MarketListing listing, bool watched)
    {
        if (listing.BidderClubId == Game.ClubId)
        {
            if (listing.State == MarketState.Closed && listing.BuyNow > 0 && listing.SoldPrice == listing.BuyNow)
            {
                return "buyNow";
            }
            return listing.State is MarketState.Active or MarketState.Closed ? "highest" : "none";
        }
        return watched && listing.CurrentBid > 0 ? "outbid" : "none";
    }

    /// <summary>A stable public id for a seller that does not reveal their persona; AI sellers share the base id.</summary>
    private long SellerPublicId(long? personaId)
    {
        var limits = Rules.Listings;
        if (personaId is not { } persona)
        {
            return limits.SellerPublicIdBase;
        }
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(persona.ToString(CultureInfo.InvariantCulture)));
        return limits.SellerPublicIdBase + System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest) % limits.SellerPublicIdSpan;
    }

    private async ValueTask<string> SellerNameAsync(IUnitOfWork uow)
    {
        var stored = await uow.DisplayNameAsync();
        var name = !string.IsNullOrWhiteSpace(stored) ? stored : Game.Config.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Rules.Listings.DefaultSellerName;
        }
        return name.Length > Rules.Listings.MaxSellerNameLength ? name[..Rules.Listings.MaxSellerNameLength] : name;
    }

    private static JsonArray Currencies(long credits) =>
        (JsonArray)TemplateLibrary.Core.Object("market/currencies", new TemplateArgs { ["credits"] = credits }).Arr("currencies")!.DeepClone();

    private static double Uniform(GameRandom random, FactorRange range) =>
        range.Minimum + (range.Maximum - range.Minimum) * random.NextDouble();

    // ---- search filters ---------------------------------------------------------------

    /// <summary>
    /// The item type a market search asks for. Club items are searched as their own type or, as FIFA's club screens send
    /// them, type clubInfo with a category: "club:kit", "club:custom" (badges), "club:ball", "club:stadium", or "club" for any.
    /// </summary>
    private static string SearchType(QueryValues query)
    {
        var type = QueryText(query, "type").ToLowerInvariant();
        var category = QueryText(query, "cat", "category").ToLowerInvariant();
        string? Kind(string name) => name switch
        {
            "kit" or "kits" or "9" => "kit",
            "badge" or "badges" or "custom" or "11" => "custom",
            "ball" or "balls" or "30" => "ball",
            "stadium" or "stadiums" or "10" => "stadium",
            _ => null,
        };
        if (type is "clubinfo" or "clubitem" or "clubitems") return Kind(category) is { } kind ? "club:" + kind : "club";
        return Kind(type) is { } own ? "club:" + own : type;
    }

    private MarketListingQuery SqlFilters(QueryValues query)
    {
        var type = SearchType(query);
        if (type.StartsWith("club", StringComparison.Ordinal))
        {
            // Club item market searches were never captured from FIFA 21; the request shows what to support.
            _log.LogInformation("MARKET CLUB ITEMS query={Query}", string.Join("&", query.Entries.Select(entry => $"{entry.Key}={string.Join(",", entry.Value)}")));
        }
        var definition = QueryInt(query, 0, "definitionId", "definitionid");
        var wanted = QueryInt(query, 0, "maskedDefId", "defId", "defid", "assetId", "assetid", "resourceId");
        var nation = QueryInt(query, 0, IdFilters[0].Keys);
        var league = QueryInt(query, 0, IdFilters[1].Keys);
        var team = QueryInt(query, 0, IdFilters[2].Keys);
        var position = QueryText(query, "pos", "position", "preferredPosition").ToUpperInvariant();
        var positions = position.Length == 0
            ? null
            : position.Split('-', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var zone = QueryText(query, "zone");
        HashSet<string>? zonePositions = null;
        if (zone.Length > 0)
        {
            zonePositions = Game.Rules.Club.PositionZones.Values
                .SelectMany(group => group)
                .Where(member => Game.Rules.Club.PositionInZone(member, zone))
                .ToHashSet(StringComparer.Ordinal);
        }
        var level = QueryText(query, "lev", "level").ToLowerInvariant();
        long? minimumRating = level switch { "silver" => 65, "gold" => 75, _ => null };
        long? maximumRating = level switch { "bronze" => 64, "silver" => 74, _ => null };
        var minimumBuy = QueryInt(query, 0, "minb", "minBuy", "minBuyNowPrice");
        var maximumBuy = QueryInt(query, 0, "maxb", "maxBuy", "maxBuyNowPrice");
        var minimumBid = QueryInt(query, 0, "micr", "minBid");
        var maximumBid = QueryInt(query, 0, "macr", "maxBid");
        var special = QueryText(query, "rare", "rarity").ToLowerInvariant() is "sp" or "true" or "1" or "special";
        var rarityKey = query.ContainsKey("rarityIds") ? "rarityIds" : "rarityids";
        var rarityIds = query.Get(rarityKey)
            .SelectMany(value => value.Split(','))
            .Select(part => long.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? (long?)id : null)
            .OfType<long>()
            .ToHashSet();
        var offset = Math.Clamp(QueryInt(query, 0, "start", "offset"), 0, int.MaxValue);
        var limit = Math.Clamp(QueryInt(query, Rules.Listings.SearchPageSize, "num", "count"), 1, Rules.Listings.MaxSearchPageSize);
        return new MarketListingQuery(
            Game.ClubId,
            Now: Now,
            MatchNone: (position.Length > 0 && positions is { Count: 0 })
                || (zone.Length > 0 && zonePositions is { Count: 0 }),
            ItemType: type.Length > 0 ? type : null,
            ResourceId: definition == 0 ? null : definition,
            AssetOrResourceId: wanted == 0 ? null : wanted,
            NationId: nation == 0 ? null : nation,
            LeagueId: league == 0 ? null : league,
            TeamId: team == 0 ? null : team,
            Positions: positions,
            ZonePositions: zonePositions,
            MinimumRating: minimumRating,
            MaximumRating: maximumRating,
            MinimumBuyNow: minimumBuy == 0 ? null : minimumBuy,
            MaximumBuyNow: maximumBuy == 0 ? null : maximumBuy,
            MinimumBid: minimumBid == 0 ? null : minimumBid,
            MaximumBid: maximumBid == 0 ? null : maximumBid,
            SpecialsOnly: special,
            RarityIds: rarityIds,
            Offset: checked((int)offset),
            Limit: checked((int)limit));
    }

    /// <summary>Test seam for comparing the SQL implementation with the retained in-memory oracle.</summary>
    internal async ValueTask<MarketListingPage> SearchListingsAsync(IUnitOfWork uow, QueryValues query) =>
        await uow.SearchMarketListingsAsync(SqlFilters(query));

    /// <summary>The pre-Step-3 predicate retained as a parity oracle.</summary>
    internal bool MatchesInMemory(MarketListing listing, QueryValues query) => Matches(listing, query);

    private bool Matches(MarketListing listing, QueryValues query)
    {
        var item = listing.Item;
        // A listing's type is its own item's type: player searches find players, training and development searches find consumables.
        var type = SearchType(query);
        var itemType = item.Text("itemType", "player").ToLowerInvariant();
        if (type.Length > 0 && (type == "club" ? !ItemFactory.IsClubItem(item) : type != (ItemFactory.IsClubItem(item) ? "club:" + itemType : itemType)))
        {
            return false;
        }
        // Compare Price searches one exact card version.
        var definition = QueryInt(query, 0, "definitionId", "definitionid");
        if (definition != 0 && definition != listing.ResourceId)
        {
            return false;
        }
        var wanted = QueryInt(query, 0, "maskedDefId", "defId", "defid", "assetId", "assetid", "resourceId");
        if (wanted != 0 && wanted != item.Int("assetId") && wanted != listing.ResourceId)
        {
            return false;
        }
        foreach (var (keys, field) in IdFilters)
        {
            var value = QueryInt(query, 0, keys);
            if (value != 0 && item.Int(field) != value)
            {
                return false;
            }
        }
        var preferred = item.Text("preferredPosition").ToUpperInvariant();
        // Grouped positions arrive as e.g. "LM-LW".
        var position = QueryText(query, "pos", "position", "preferredPosition").ToUpperInvariant();
        if (position.Length > 0 && !position.Split('-', StringSplitOptions.RemoveEmptyEntries).Contains(preferred))
        {
            return false;
        }
        var zone = QueryText(query, "zone");
        if (zone.Length > 0 && !Game.Rules.Club.PositionInZone(preferred, zone))
        {
            return false;
        }
        var rating = item.Int("rating");
        var level = QueryText(query, "lev", "level").ToLowerInvariant();
        if ((level == "bronze" && rating > 64) || (level == "silver" && rating is < 65 or > 74) || (level == "gold" && rating < 75))
        {
            return false;
        }
        var rareflag = item.Int("rareflag");
        // FIFA sends rare=SP for the Special quality filter.
        if (QueryText(query, "rare", "rarity").ToLowerInvariant() is "sp" or "true" or "1" or "special"
            && !(rareflag > 1 || item.Bool("special")))
        {
            return false;
        }
        var rarityKey = query.ContainsKey("rarityIds") ? "rarityIds" : "rarityids";
        var rarityIds = query.Get(rarityKey)
            .SelectMany(value => value.Split(','))
            .Select(part => long.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? (long?)id : null)
            .OfType<long>()
            .ToHashSet();
        if (rarityIds.Count > 0 && !rarityIds.Contains(rareflag))
        {
            return false;
        }
        var minBuy = QueryInt(query, 0, "minb", "minBuy", "minBuyNowPrice");
        var maxBuy = QueryInt(query, 0, "maxb", "maxBuy", "maxBuyNowPrice");
        if ((minBuy != 0 && listing.BuyNow < minBuy) || (maxBuy != 0 && listing.BuyNow > maxBuy))
        {
            return false;
        }
        // Bid filters apply to the price a new bid must beat.
        var bidPrice = listing.CurrentBid != 0 ? listing.CurrentBid : listing.StartingBid;
        var minBid = QueryInt(query, 0, "micr", "minBid");
        var maxBid = QueryInt(query, 0, "macr", "maxBid");
        return (minBid == 0 || bidPrice >= minBid) && (maxBid == 0 || bidPrice <= maxBid);
    }

    private static readonly (string[] Keys, string Field)[] IdFilters =
    {
        (new[] { "nation", "nat", "nationId", "nationid" }, "nation"),
        (new[] { "league", "leag", "leagueId", "leagueid" }, "leagueId"),
        (new[] { "club", "team", "clubId", "clubid", "teamId", "teamid" }, "teamid"),
    };

    /// <summary>The first value of the first key that parses as an integer.</summary>
    private static long QueryInt(QueryValues query, long fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.First(key) is { } value && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return fallback;
    }

    private static string QueryText(QueryValues query, params string[] keys) =>
        keys.Select(query.First).FirstOrDefault(value => value is not null) ?? string.Empty;

    // ---- transactions ------------------------------------------------------------------

    /// <summary>
    /// Locks the clubs a set of settlements or refunds changes (the buyer, sellers and bidders) in ascending
    /// id order, then the sellers' cards. Callers have already locked the listings.
    /// </summary>
    private static async ValueTask LockSettlementAsync(IUnitOfWork uow, IReadOnlyCollection<MarketListing> listings, long? buyerClubId)
    {
        await uow.LockClubsAsync(listings.SelectMany(listing => new[] { listing.SellerClubId, listing.BidderClubId }).Append(buyerClubId));
        await uow.LockItemsAsync(listings
            .Where(listing => listing.SellerClubId is not null && listing.SellerItemId is not null)
            .Select(listing => (listing.SellerClubId!.Value, listing.SellerItemId!.Value)));
    }

    private Task WriteAsync(string operation, Func<IUnitOfWork, ValueTask> action, CancellationToken cancellationToken = default) =>
        WriteAsync(operation, async uow =>
        {
            await action(uow);
            return true;
        }, cancellationToken);

    /// <summary>
    /// One market transaction: the market advisory lock, then the action, which locks listings, clubs and
    /// items in the order documented in MarketRepository before writing. A deadlock or serialization
    /// failure repeats the whole action; waiting past the lock timeout becomes the busy refusal. The
    /// relational advisory lock is shared for ordinary writes and exclusive for maintenance. Listing
    /// and ordered club/item row locks serialize conflicts across processes.
    /// </summary>
    private async Task<T> WriteAsync<T>(string operation, Func<IUnitOfWork, ValueTask<T>> action,
        CancellationToken cancellationToken = default, bool exclusive = false)
    {
        try
        {
            return await Game.RunRetryableAsync("market-" + operation, async uow =>
            {
                if (exclusive)
                    await uow.LockMarketAsync();
                else
                    await RelationalMarketRepository.LockMarketSharedAsync(uow);
                return await action(uow);
            }, cancellationToken);
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.LockNotAvailable)
        {
            _log.LogWarning("MARKET_BUSY operation={Operation}", operation);
            throw new MarketException(BusyReason, 461, "Auction state is invalid for bidding");
        }
    }

    /// <summary>Best-effort upkeep: a busy market skips it and the next job or targeted refresh retries.</summary>
    private async ValueTask<int> MaintainAsync(string operation, Func<IUnitOfWork, ValueTask<int>> action,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await WriteAsync(operation, action, cancellationToken, exclusive: true);
        }
        catch (MarketException error) when (error.Message == BusyReason)
        {
            // Actions still refuse listings past their end time, so a skipped expiry cannot be bid on.
            return 0;
        }
    }
}
