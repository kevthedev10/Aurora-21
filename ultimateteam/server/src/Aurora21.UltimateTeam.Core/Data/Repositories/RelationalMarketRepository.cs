using System.Text.Json.Nodes;
using Npgsql;
using Aurora21.UltimateTeam.Core.Services.Market;
using Aurora21.UltimateTeam.Core.Data.Records;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using static Aurora21.UltimateTeam.Core.Data.ClubSql;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// The shared transfer market. Listings are server-wide, so these statements are not scoped to one club
/// alone: a settlement pays and moves cards between clubs by id, inside the caller's transaction.
/// </summary>
public static class RelationalMarketRepository
{
    public const long MarketLockKey = 2021_0021_0002;
    private const string ListingFields = """
        l.market_listing_id, l.kind, l.seller_club_id, s.persona_id, l.seller_name, l.seller_item_id, l.resource_id, '{}'::text,
        l.starting_bid, l.buy_now, l.current_bid, l.bidder_club_id, l.offers, l.state, l.created_at, l.end_time, l.sold_price
        """;

    private const string ListingColumns = ListingFields + " FROM ultimateteam.market_listings l LEFT JOIN ultimateteam.clubs s ON s.id = l.seller_club_id";

    // ---- lock order -------------------------------------------------------------
    //
    // Every transaction that changes the market takes, in this order and before any write:
    //   1. a shared market advisory lock for normal writes; exclusive for upkeep/withdrawal,
    //   2. the listing rows it changes (FOR UPDATE),
    //   3. every club row it changes, in ascending id (LockClubsAsync),
    //   4. every item row it changes, in ascending (club_id, id) (LockItemsAsync).
    // Club commands outside the market take their own club row (ClubRepository.LockClubAsync) before its
    // items and never lock a listing, so both orders agree and cannot deadlock each other.

    /// <summary>Serializes market changes across every server process on this database until the transaction ends.</summary>
    public static ValueTask<int> LockMarketAsync(IUnitOfWork work) =>
        work.ExecuteAsync("SELECT pg_advisory_xact_lock(@key)", ("key", MarketLockKey));

    /// <summary>Allows independent writes while excluding global maintenance and withdrawal.</summary>
    public static ValueTask<int> LockMarketSharedAsync(IUnitOfWork work) =>
        work.ExecuteAsync("-- query: market.lock.shared\nSELECT pg_advisory_xact_lock_shared(@key)", ("key", MarketLockKey));

    /// <summary>Lock listing history before the seller club; relisting later updates finished rows.</summary>
    public static ValueTask<int> LockItemListingHistoryAsync(IUnitOfWork work, long itemId) =>
        work.ExecuteAsync("""
            -- query: market.listing-history.lock
            SELECT market_listing_id FROM ultimateteam.market_listings
            WHERE seller_club_id=@club AND seller_item_id=@item ORDER BY market_listing_id FOR UPDATE
            """, ("item", itemId));

    /// <summary>Attempts to become the shared-market maintenance owner without waiting.</summary>
    public static ValueTask<bool> TryLockMarketAsync(IUnitOfWork work) =>
        work.ScalarAsync<bool>("SELECT pg_try_advisory_xact_lock(@key)", ("key", MarketLockKey));

    /// <summary>Locks the named club rows in ascending id order (missing and repeated ids are ignored).</summary>
    public static async ValueTask LockClubsAsync(IUnitOfWork work, IEnumerable<long?> clubIds)
    {
        var ids = clubIds.OfType<long>().Distinct().Order().ToArray();
        if (ids.Length > 0)
        {
            var locked = await work.QueryAsync("-- query: club.lock.many\nSELECT id FROM ultimateteam.clubs WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
                reader => reader.GetInt64(0), ("ids", ids));
            // These rows are now held exclusively for the rest of the transaction; recording them lets a later
            // EnsureRelationalClubLockAsync for the same club skip a second lock statement.
            foreach (var id in locked)
            {
                work.HeldClubLocks[id] = true;
            }
        }
    }

    /// <summary>Locks the named item rows in ascending (club_id, id) order (missing items are ignored).</summary>
    public static async ValueTask LockItemsAsync(IUnitOfWork work, IEnumerable<(long ClubId, long ItemId)> items)
    {
        var keys = items.Distinct().OrderBy(key => key.ClubId).ThenBy(key => key.ItemId).ToArray();
        if (keys.Length > 0)
        {
            await work.ExecuteAsync("""
                SELECT i.id FROM ultimateteam.items i
                JOIN unnest(@clubs, @items) AS k(club_id, id) ON i.club_id = k.club_id AND i.native_item_id = k.id
                ORDER BY i.club_id, i.native_item_id FOR UPDATE OF i
                """, ("clubs", keys.Select(key => key.ClubId).ToArray()), ("items", keys.Select(key => key.ItemId).ToArray()));
        }
    }

    // ---- reads ------------------------------------------------------------------

    /// <summary>A cheap unlocked check that lets readers skip expiry when nothing is due.</summary>
    public static ValueTask<bool> AnyDueMarketListingsAsync(IUnitOfWork work, long now) =>
        work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.market_listings WHERE state = 'active' AND end_time <= @now)", ("now", now));

    /// <summary>Whether any of the named listings is due; empty input is always false.</summary>
    public static async ValueTask<bool> AnyDueMarketListingsAsync(IUnitOfWork work, long now, IEnumerable<long> tradeIds)
    {
        var ids = tradeIds.Distinct().ToArray();
        return ids.Length > 0 && await work.ScalarAsync<bool>("""
            -- query: market.expired-check
            SELECT EXISTS(SELECT 1 FROM ultimateteam.market_listings
                WHERE market_listing_id = ANY(@trades) AND state = 'active' AND end_time <= @now)
            """, ("trades", ids), ("now", now));
    }

    public static async ValueTask<MarketListing?> MarketListingAsync(IUnitOfWork work, long tradeId, bool forUpdate = false) =>
        (await ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.market_listing_id = @trade{(forUpdate ? " FOR UPDATE OF l" : "")}", ("trade", tradeId)))
        .FirstOrDefault();

    /// <summary>Named listings in one query; the caller restores request order and duplicates.</summary>
    public static async ValueTask<Dictionary<long, MarketListing>> MarketListingsAsync(IUnitOfWork work, IEnumerable<long> tradeIds)
    {
        var ids = tradeIds.Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<long, MarketListing>()
            : (await ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.market_listing_id = ANY(@trades)", ("trades", ids)))
                .ToDictionary(listing => listing.TradeId);
    }

    /// <summary>Every active listing, soonest ending first.</summary>
    public static ValueTask<List<MarketListing>> ActiveMarketListingsAsync(IUnitOfWork work) =>
        ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.state = 'active' ORDER BY l.end_time, l.market_listing_id");

    /// <summary>Filters, orders and pages active market rows entirely in PostgreSQL.</summary>
    public static async ValueTask<MarketListingPage> SearchMarketListingsAsync(IUnitOfWork work, MarketListingQuery query)
    {
        var where = new List<string>
        {
            "l.state = 'active'",
            "l.end_time > @now",
            "l.seller_club_id IS DISTINCT FROM @seller",
        };
        var parameters = new List<(string Name, object? Value)>
        {
            ("now", query.Now),
            ("seller", query.ExcludedSellerClubId),
            ("offset", Math.Max(0, query.Offset)),
            ("limit", Math.Max(1, query.Limit)),
        };
        if (query.MatchNone)
        {
            where.Add("false");
        }
        if (query.ItemType is { } itemType)
        {
            // The searched type is the item's own wire type: a player definition, a chemistry style or manager league
            // card (training) or a position modifier (development).
            where.Add(itemType switch
            {
                "player" => "EXISTS (SELECT 1 FROM ultimateteam.player_card_definitions pf WHERE pf.definition_id = l.definition_id)",
                "training" => "EXISTS (SELECT 1 FROM ultimateteam.consumable_definitions cf WHERE cf.definition_id = l.definition_id AND cf.consumable_kind IN ('chemistry_style', 'manager_league'))",
                "development" => "EXISTS (SELECT 1 FROM ultimateteam.consumable_definitions cf WHERE cf.definition_id = l.definition_id AND cf.consumable_kind = 'position_modifier')",
                // A club item: any ("club") or one customisation kind ("club:kit", "club:custom", "club:ball", "club:stadium").
                "club" => "EXISTS (SELECT 1 FROM ultimateteam.customisation_definitions cd WHERE cd.definition_id = l.definition_id)",
                "club:kit" or "club:custom" or "club:ball" or "club:stadium" =>
                    $"EXISTS (SELECT 1 FROM ultimateteam.customisation_definitions cd WHERE cd.definition_id = l.definition_id AND cd.customisation_kind = '{itemType[5..]}')",
                _ => "false",
            });
        }
        Add(query.ResourceId, "l.resource_id = @resource", "resource");
        if (query.AssetOrResourceId is { } wanted)
        {
            where.Add("(l.asset_id = @wanted OR l.resource_id = @wanted)");
            parameters.Add(("wanted", wanted));
        }
        Add(query.NationId, "l.nation_id = @nation", "nation");
        Add(query.LeagueId, "l.league_id = @league", "league");
        Add(query.TeamId, "l.team_id = @team", "team");
        AddStrings(query.Positions, "upper(CASE WHEN COALESCE(h.wire_representation::jsonb->'missing','[]'::jsonb) ? 'preferredPosition' OR COALESCE(h.wire_representation::jsonb->'nulls','[]'::jsonb) ? 'preferredPosition' THEN '' ELSE COALESCE(p.position_override,l.preferred_position) END) = ANY(@positions)", "positions");
        AddStrings(query.ZonePositions, "upper(COALESCE(p.position_override, l.preferred_position)) = ANY(@zone_positions)", "zone_positions");
        Add(query.MinimumRating, "l.rating >= @min_rating", "min_rating");
        Add(query.MaximumRating, "l.rating <= @max_rating", "max_rating");
        Add(query.MinimumBuyNow, "l.buy_now >= @min_buy", "min_buy");
        Add(query.MaximumBuyNow, "l.buy_now <= @max_buy", "max_buy");
        Add(query.MinimumBid, "COALESCE(NULLIF(l.current_bid, 0), l.starting_bid) >= @min_bid", "min_bid");
        Add(query.MaximumBid, "COALESCE(NULLIF(l.current_bid, 0), l.starting_bid) <= @max_bid", "max_bid");
        if (query.SpecialsOnly)
        {
            where.Add("(l.rareflag > 1 OR l.is_special)");
        }
        AddLongs(query.RarityIds, "l.rareflag = ANY(@rarities)", "rarities");

        var rows = await work.QueryAsync($"""
            -- query: market.search.page
            WITH filtered AS (
                SELECT l.market_listing_id, l.end_time, count(*) OVER() AS total
                FROM ultimateteam.market_listings l
                LEFT JOIN ultimateteam.market_player_snapshots p ON p.market_listing_id=l.market_listing_id
                JOIN ultimateteam.market_item_snapshots h ON h.market_listing_id=l.market_listing_id
                WHERE {string.Join(" AND ", where)}
            ), page AS (
                SELECT market_listing_id, end_time, total FROM filtered
                ORDER BY end_time, market_listing_id LIMIT @limit OFFSET @offset
            ), totals AS (
                SELECT COALESCE(max(total), 0) AS total FROM filtered
            )
            SELECT {ListingFields}, totals.total
            FROM totals
            LEFT JOIN page ON true
            LEFT JOIN ultimateteam.market_listings l ON l.market_listing_id = page.market_listing_id
            LEFT JOIN ultimateteam.clubs s ON s.id = l.seller_club_id
            ORDER BY page.end_time, page.market_listing_id
            """, reader => (
                Listing: reader.IsDBNull(0) ? null : ReadListing(reader),
                Total: checked((int)reader.GetInt64(17))), parameters.ToArray());
        return new MarketListingPage(await HydrateAsync(work, rows.Select(row => row.Listing).OfType<MarketListing>().ToList()), rows.FirstOrDefault().Total);

        void Add(long? value, string condition, string name)
        {
            if (value is not { } present)
            {
                return;
            }
            where.Add(condition);
            parameters.Add((name, present));
        }

        void AddStrings(IReadOnlyCollection<string>? values, string condition, string name)
        {
            if (values is not { Count: > 0 })
            {
                return;
            }
            where.Add(condition);
            parameters.Add((name, values.ToArray()));
        }

        void AddLongs(IReadOnlyCollection<long>? values, string condition, string name)
        {
            if (values is not { Count: > 0 })
            {
                return;
            }
            where.Add(condition);
            parameters.Add((name, values.ToArray()));
        }
    }

    /// <summary>Active listings past their end time that no other transaction is settling.</summary>
    public static ValueTask<List<MarketListing>> DueMarketListingsAsync(IUnitOfWork work, long now) =>
        ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.state = 'active' AND l.end_time <= @now ORDER BY l.market_listing_id FOR UPDATE OF l SKIP LOCKED", ("now", now));

    /// <summary>The named due listings that no other transaction is settling.</summary>
    public static ValueTask<List<MarketListing>> DueMarketListingsAsync(IUnitOfWork work, long now, IEnumerable<long> tradeIds)
    {
        var ids = tradeIds.Distinct().ToArray();
        return ids.Length == 0
            ? ValueTask.FromResult(new List<MarketListing>())
            : ListingsAsync(work, $"""
                SELECT {ListingColumns}
                WHERE l.market_listing_id = ANY(@trades) AND l.state = 'active' AND l.end_time <= @now
                ORDER BY l.market_listing_id FOR UPDATE OF l SKIP LOCKED
                """, ("trades", ids), ("now", now));
    }

    /// <summary>
    /// A finished listing stays on its seller's Transfer List only while the card does: an expired or cancelled card that
    /// was sent to the club, quick sold or used in an SBC left a ghost row behind that could not be re-listed, discarded or
    /// moved (live 10-05). Sold listings stay until cleared.
    /// </summary>
    private static string OnTransferList(string alias) => $"""
        NOT {alias}seller_cleared AND ({alias}state IN ('active', 'closed') OR EXISTS (
            SELECT 1 FROM ultimateteam.items t
            WHERE t.club_id = {alias}seller_club_id AND t.native_item_id = {alias}seller_item_id AND t.pile = 'trade'))
        """;

    /// <summary>This club's own listings that it has not cleared from its transfer list.</summary>
    public static ValueTask<List<MarketListing>> OwnMarketListingsAsync(IUnitOfWork work) =>
        ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.kind = 'player' AND l.seller_club_id = @club AND {OnTransferList("l.")} ORDER BY l.market_listing_id");

    /// <summary>IDs only: expiry checks must not render every seller snapshot.</summary>
    public static ValueTask<List<long>> OwnMarketListingIdsAsync(IUnitOfWork work) =>
        work.QueryAsync($"SELECT market_listing_id FROM ultimateteam.market_listings WHERE kind='player' AND seller_club_id=@club AND {OnTransferList("")} ORDER BY market_listing_id", r => r.GetInt64(0));

    /// <summary>
    /// Transfer Targets leaves out a trade this club won once its copy has left Unassigned (sent to the club, listed,
    /// quick sold). The watch row outlives the claim, and showing the trade again let players claim the card a second
    /// time (live 10-05). "purchased" is the pile builds before the purchased-pile fix stored; those cards are unclaimed.
    /// </summary>
    private const string UnclaimedWatch = """
        NOT (l.state = 'closed' AND l.bidder_club_id = w.club_id AND NOT EXISTS (
            SELECT 1 FROM ultimateteam.items i
            WHERE i.club_id = w.club_id AND i.definition_id = l.definition_id AND i.last_sale_price = l.sold_price
                AND i.timestamp = l.end_time AND i.pile IN ('unassigned', 'purchased')))
        """;

    public static async ValueTask<(long Count, long Winning, long Outbid)> WatchPageTotalsAsync(IUnitOfWork work) =>
        (await work.QueryAsync($"""
            -- query: market.watchlist.totals
            SELECT count(*), count(*) FILTER (WHERE l.state='active' AND l.bidder_club_id=@club),
                count(*) FILTER (WHERE l.state='active' AND l.bidder_club_id IS NOT NULL AND l.bidder_club_id<>@club)
            FROM ultimateteam.market_watch w JOIN ultimateteam.market_listings l ON l.market_listing_id=w.market_listing_id
            WHERE w.club_id=@club AND {UnclaimedWatch}
            """, r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single();

    public static async ValueTask<(long Count, long Selling, long Sold)> TransferPageTotalsAsync(IUnitOfWork work) =>
        (await work.QueryAsync($"""
            -- query: market.transfer-list.totals
            WITH owned AS (
                SELECT seller_item_id, state FROM ultimateteam.market_listings
                WHERE kind='player' AND seller_club_id=@club AND {OnTransferList("")}
            )
            SELECT count(*) + (SELECT count(*) FROM ultimateteam.items i
                WHERE i.club_id=@club AND i.pile='trade'
                    AND NOT EXISTS (SELECT 1 FROM owned WHERE seller_item_id=i.native_item_id)),
                count(*) FILTER (WHERE state='active'), count(*) FILTER (WHERE state='closed') FROM owned
            """, r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)))).Single();

    public static ValueTask<List<MarketListing>> WatchedMarketPageAsync(IUnitOfWork work, long offset, long count) =>
        ListingsAsync(work, $"""
            -- query: market.watchlist.page
            SELECT {ListingColumns}
            JOIN ultimateteam.market_watch w ON w.market_listing_id=l.market_listing_id
            WHERE w.club_id=@club AND {UnclaimedWatch} ORDER BY w.watched_at, l.market_listing_id
            LIMIT @limit OFFSET @offset
            """, ("limit", count), ("offset", offset));

    /// <summary>One combined page, with listed cards preceding unlisted trade-pile cards.</summary>
    public static ValueTask<List<(long ListingId, long ItemId)>> TransferPageIdsAsync(IUnitOfWork work, long offset, long count) =>
        work.QueryAsync($"""
            -- query: market.transfer-list.page
            WITH entries AS (
                SELECT 0 AS kind, market_listing_id AS ordering, market_listing_id AS listing_id, 0::bigint AS item_id
                FROM ultimateteam.market_listings
                WHERE kind='player' AND seller_club_id=@club AND {OnTransferList("")}
                UNION ALL
                SELECT 1, i.native_item_id, 0::bigint, i.native_item_id
                FROM ultimateteam.items i WHERE i.club_id=@club AND i.pile='trade'
                AND NOT EXISTS (SELECT 1 FROM ultimateteam.market_listings l
                    WHERE l.kind='player' AND l.seller_club_id=@club AND NOT l.seller_cleared
                    AND l.seller_item_id=i.native_item_id)
            )
            SELECT listing_id, item_id FROM entries ORDER BY kind, ordering LIMIT @limit OFFSET @offset
            """, r => (r.GetInt64(0), r.GetInt64(1)), ("limit", count), ("offset", offset));

    public static async ValueTask<MarketListing?> LiveMarketListingForItemAsync(IUnitOfWork work, long itemId)
    {
        return (await ListingsAsync(work, $"SELECT {ListingColumns} WHERE l.kind = 'player' AND l.seller_club_id = @club AND l.seller_item_id = @item AND l.state = 'active'",
            ("item", itemId))).FirstOrDefault();
    }

    /// <summary>Which of these owned items this club currently has listed, in one round trip.</summary>
    public static ValueTask<List<long>> LiveMarketListedItemsAsync(IUnitOfWork work, long[] itemIds) =>
        itemIds.Length == 0 ? ValueTask.FromResult(new List<long>()) : work.QueryAsync("""
            -- query: market.live-listings-for-items
            SELECT seller_item_id FROM ultimateteam.market_listings
            WHERE kind = 'player' AND seller_club_id = @club AND seller_item_id = ANY(@items) AND state = 'active'
            """, reader => reader.GetInt64(0), ("items", itemIds));

    /// <summary>
    /// The copies this club bought from the named closed trades, by trade. A sale gives the buyer a copy under an id from
    /// the buyer's own band, stamped with the listing's definition, sold price and close time; the listing keeps the
    /// seller's card. Trades whose copy has since changed or gone are left out.
    /// </summary>
    public static async ValueTask<Dictionary<long, (long ItemId, long Owners)>> BoughtMarketCopiesAsync(IUnitOfWork work, IEnumerable<long> tradeIds)
    {
        var ids = tradeIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, (long ItemId, long Owners)>();
        }
        var rows = await work.QueryAsync("""
            -- query: market.bought-copies
            SELECT DISTINCT ON (l.market_listing_id) l.market_listing_id, i.native_item_id, i.owners
            FROM ultimateteam.market_listings l
            JOIN ultimateteam.items i ON i.club_id = l.bidder_club_id AND i.definition_id = l.definition_id
                AND i.last_sale_price = l.sold_price AND i.timestamp = l.end_time
            WHERE l.market_listing_id = ANY(@trades) AND l.state = 'closed' AND l.bidder_club_id = @club
            ORDER BY l.market_listing_id, i.native_item_id
            """, r => (Trade: r.GetInt64(0), ItemId: r.GetInt64(1), Owners: r.GetInt64(2)), ("trades", ids));
        return rows.ToDictionary(row => row.Trade, row => (row.ItemId, row.Owners));
    }

    public static async ValueTask<int> CountActiveMarketListingsAsync(IUnitOfWork work, string? kind = null) =>
        (int)await work.ScalarAsync<long>($"-- query: market.active-count\nSELECT count(*) FROM ultimateteam.market_listings WHERE state = 'active'{(kind is null ? "" : " AND kind = @kind")}", ("kind", kind));

    // ---- writes -----------------------------------------------------------------

    public static async ValueTask<long> InsertMarketListingAsync(IUnitOfWork work, string kind, string sellerName, long? sellerItemId, long resourceId,
        JsonObject item, long rating, long startingBid, long buyNow, long createdAt, long endTime)
    {
        var mapper = work.RelationalItems.Mapper;
        var captured = mapper.Capture(work.ClubId, item);
        var h = captured.Header;
        if (captured.Player is { } listed && listed.LoanGames != 0) throw new MarketException("loan cards cannot be listed");
        if (captured.Player is null && captured.Consumable is null && captured.Customisation is null)
            throw new RuntimeSetupException("Only player cards, consumable stacks and club items can be listed.");
        if (kind == MarketListingKind.Player && sellerItemId != h.NativeItemId.Value)
            throw new RuntimeSetupException("Seller item and snapshot native numbers differ.");
        // These indexed values are immutable catalog metadata. Position changes have a typed snapshot field.
        var representation = captured.Extension?.WireRepresentation is { } r ? FutJson.ParseObject(r.Text)! : new JsonObject();
        var canonical = mapper.Render(captured with { Extension = null });
        var differing = new[] { "resourceId", "assetId", "rating", "nation", "leagueId", "teamid", "rareflag" }
            .Where(key => item.IntKeep(key) != canonical.IntKeep(key))
            .Select(key => $"{key} listed={item.Get(key)?.ToJsonString() ?? "missing"} canonical={canonical.Get(key)?.ToJsonString() ?? "missing"}")
            .ToList();
        if (item.Bool("special") != canonical.Bool("special"))
            differing.Add($"special listed={item.Get("special")?.ToJsonString() ?? "missing"} canonical={canonical.Get("special")?.ToJsonString() ?? "missing"}");
        // A club item's card rating and rarity may come from the pack that dropped it (a stored override); its listing columns
        // come from the catalogue either way.
        if (differing.Count > 0 && captured.Customisation is null)
            throw new RuntimeSetupException("Noncanonical market catalog metadata requires converter audit: " + string.Join("; ", differing) + ".");
        representation["nativeItemId"] = h.NativeItemId.Value;
        if (captured.Player is { } playerSnapshot) representation["duplicateAcknowledged"] = playerSnapshot.DuplicateAcknowledged;
        // A player listing denormalises its card metadata; a consumable stack has none of it and carries its stack in the wire
        // snapshot; a club item keeps its rating and rarity.
        var trade = await work.ScalarAsync<long>(captured.Customisation is not null ? """
            INSERT INTO ultimateteam.market_listings(kind,seller_club_id,seller_name,seller_item_id,definition_id,resource_id,rating,
                starting_bid,buy_now,current_bid,offers,state,created_at,end_time,sold_price,seller_cleared,duration_seconds,
                asset_id,nation_id,league_id,team_id,preferred_position,rareflag,is_special)
            SELECT @kind,@seller,@name,@item_id,d.definition_id,d.native_resource_id,COALESCE(c.rating,0),
                @start,@buy,0,0,'active',@created,@end,0,false,@end-@created,
                d.native_asset_id,NULL,NULL,c.native_team_id,NULL,c.native_rareflag,false
            FROM ultimateteam.item_definitions d JOIN ultimateteam.customisation_definitions c ON c.definition_id=d.definition_id
            WHERE d.definition_id=@definition AND d.native_resource_id=@resource
            RETURNING market_listing_id
            """ : captured.Player is null ? """
            INSERT INTO ultimateteam.market_listings(kind,seller_club_id,seller_name,seller_item_id,definition_id,resource_id,rating,
                starting_bid,buy_now,current_bid,offers,state,created_at,end_time,sold_price,seller_cleared,duration_seconds,
                asset_id,nation_id,league_id,team_id,preferred_position,rareflag,is_special)
            SELECT @kind,@seller,@name,@item_id,d.definition_id,d.native_resource_id,c.rating,
                @start,@buy,0,0,'active',@created,@end,0,false,@end-@created,
                d.native_asset_id,NULL,NULL,NULL,NULL,NULL,false
            FROM ultimateteam.item_definitions d JOIN ultimateteam.consumable_definitions c ON c.definition_id=d.definition_id
            WHERE d.definition_id=@definition AND d.native_resource_id=@resource AND c.rating IS NOT DISTINCT FROM @rating
            RETURNING market_listing_id
            """ : """
            INSERT INTO ultimateteam.market_listings(kind,seller_club_id,seller_name,seller_item_id,definition_id,resource_id,rating,
                starting_bid,buy_now,current_bid,offers,state,created_at,end_time,sold_price,seller_cleared,duration_seconds,
                asset_id,nation_id,league_id,team_id,preferred_position,rareflag,is_special)
            SELECT @kind,@seller,@name,@item_id,d.definition_id,d.native_resource_id,c.rating,
                @start,@buy,0,0,'active',@created,@end,0,false,@end-@created,
                d.native_asset_id,nation_ref.native_nation_id,league_ref.native_league_id,team_ref.native_team_id,c.position,rarity_ref.native_rareflag,t.is_special
            FROM ultimateteam.item_definitions d JOIN ultimateteam.player_card_definitions c ON c.definition_id=d.definition_id
            JOIN ultimateteam.card_types t ON t.card_type_id=c.card_type_id
            JOIN ultimateteam.native_rarities rarity_ref ON rarity_ref.native_rarity_id=t.native_rarity_id
            LEFT JOIN ultimateteam.nations nation_ref ON nation_ref.nation_id=c.nation_id
            LEFT JOIN ultimateteam.leagues league_ref ON league_ref.league_id=c.league_id
            LEFT JOIN ultimateteam.teams team_ref ON team_ref.team_id=c.team_id
            WHERE d.definition_id=@definition AND d.native_resource_id=@resource AND c.rating=@rating
            RETURNING market_listing_id
            """,("kind",kind),("seller",kind==MarketListingKind.Player?work.ClubId:null),("name",sellerName),("item_id",sellerItemId),
            ("definition",h.DefinitionId.Value),("resource",resourceId),("rating",rating),("start",startingBid),("buy",buyNow),("created",createdAt),("end",endTime));
        if (trade == 0) throw new RuntimeSetupException("Listing does not match its catalog definition.");
        await work.ExecuteAsync("""
            INSERT INTO ultimateteam.market_item_snapshots(market_listing_id,definition_id,definition_revision,timestamp,item_state,pile,untradeable,owners,last_sale_price,extension_payload,wire_representation)
            SELECT @trade,definition_id,revision,@timestamp,@state,@pile,@untradeable,@owners,@sale,@payload,@representation
            FROM ultimateteam.item_definitions WHERE definition_id=@definition
            """,("trade",trade),("definition",h.DefinitionId.Value),("timestamp",h.Timestamp),("state",h.ItemState),("pile",h.Pile),
            ("untradeable",h.Untradeable),("owners",h.Owners),("sale",h.LastSalePrice),("payload",JsonText(captured.Extension?.Payload.Text??"{}")),("representation",Json(representation)));
        if (captured.Player is { } p)
        {
            await work.ExecuteAsync("""
                INSERT INTO ultimateteam.market_player_snapshots(market_listing_id,contracts,fitness,injury_type,injury_games,suspension,morale,training,position_override,chemistry_style_definition_id,native_play_style,assists,lifetime_assists,loyalty_bonus,match_stats)
                VALUES(@trade,@contracts,@fitness,@injury,@games,@suspension,@morale,@training,@position,@style,@native_style,@assists,@lifetime,@loyalty,@stats)
                """,("trade",trade),("contracts",p.Contracts),("fitness",p.Fitness),("injury",p.InjuryType),("games",p.InjuryGames),("suspension",p.Suspension),
                ("morale",p.Morale),("training",p.Training),("position",p.PositionOverride),("style",p.StyleDefinitionId?.Value),
                ("native_style",p.NativePlayStyle),("assists",p.Assists),("lifetime",p.LifetimeAssists),("loyalty",p.LoyaltyBonus),("stats",JsonText(PlayerMatchStatsJson.Encode(captured.Stats).GetRawText())));
        }
        return trade;
    }

    /// <summary>Hides this club's finished listings of <paramref name="itemId"/> once the card is listed again.</summary>
    public static ValueTask<int> ClearFinishedMarketListingsAsync(IUnitOfWork work, long itemId) =>
        work.ExecuteAsync("""
            UPDATE ultimateteam.market_listings SET seller_cleared = true
            WHERE seller_club_id = @club AND seller_item_id = @item AND state <> 'active'
            """, ("item", itemId));

    /// <summary>Records a bid; a listing ending before <paramref name="endsNoSooner"/> is pushed back to it (FUT's last-seconds bid extension).</summary>
    public static ValueTask<int> SetMarketBidAsync(IUnitOfWork work, long tradeId, long amount, long bidderClubId, long endsNoSooner = 0) =>
        work.ExecuteAsync("UPDATE ultimateteam.market_listings SET current_bid = @bid, bidder_club_id = @bidder, offers = offers + 1, end_time = GREATEST(end_time, @ends) WHERE market_listing_id = @trade",
            ("bid", amount), ("bidder", bidderClubId), ("ends", endsNoSooner), ("trade", tradeId));

    /// <summary>Closes an active listing as sold. False when it was no longer active.</summary>
    public static async ValueTask<bool> CloseMarketListingAsync(IUnitOfWork work, long tradeId, long price, long buyerClubId, long now) =>
        await work.ExecuteAsync("""
            UPDATE ultimateteam.market_listings SET state = 'closed', current_bid = @price, bidder_club_id = @buyer, sold_price = @price, end_time = @now
            WHERE market_listing_id = @trade AND state = 'active'
            """, ("price", price), ("buyer", buyerClubId), ("now", now), ("trade", tradeId)) == 1;

    /// <summary>
    /// Logs a sale just closed by <see cref="CloseMarketListingAsync"/> to market_trades (migration 179), which keeps it when
    /// the listing or either club is deleted: the operator panel's market log.
    /// </summary>
    public static ValueTask<int> LogMarketTradeAsync(IUnitOfWork work, long tradeId, string sale, long sellerProceeds) =>
        work.ExecuteAsync("""
            -- query: market-trades.log
            INSERT INTO ultimateteam.market_trades(trade_id, sold_at, sale, seller_club_id, seller_name, buyer_club_id, definition_id, resource_id,
                rating, rareflag, price, seller_proceeds, starting_bid, buy_now, listed_at)
            SELECT market_listing_id, end_time, @sale, seller_club_id, seller_name, bidder_club_id, definition_id, resource_id,
                rating, rareflag, sold_price, @proceeds, starting_bid, buy_now, created_at
            FROM ultimateteam.market_listings WHERE market_listing_id = @trade AND state = 'closed' AND bidder_club_id IS NOT NULL
            ON CONFLICT (trade_id) DO NOTHING
            """, ("trade", tradeId), ("sale", sale), ("proceeds", sellerProceeds));

    public static ValueTask<int> EndMarketListingAsync(IUnitOfWork work, long tradeId, string state, long now) =>
        work.ExecuteAsync("""
            UPDATE ultimateteam.market_listings SET state = @state, end_time = @now,
                current_bid = CASE WHEN @state = 'cancelled' THEN 0 ELSE current_bid END,
                bidder_club_id = CASE WHEN @state = 'cancelled' THEN NULL ELSE bidder_club_id END
            WHERE market_listing_id = @trade
            """, ("state", state), ("now", now), ("trade", tradeId));

    /// <summary>Ends several listings with no bid to settle; the caller has locked them (see <see cref="DueMarketListingsAsync(IUnitOfWork, long)"/>).</summary>
    public static ValueTask<int> EndMarketListingsAsync(IUnitOfWork work, IReadOnlyCollection<long> tradeIds, string state, long now) =>
        tradeIds.Count == 0 ? ValueTask.FromResult(0) : work.ExecuteAsync("""
            -- query: market.end.many
            UPDATE ultimateteam.market_listings SET state = @state, end_time = @now,
                current_bid = CASE WHEN @state = 'cancelled' THEN 0 ELSE current_bid END,
                bidder_club_id = CASE WHEN @state = 'cancelled' THEN NULL ELSE bidder_club_id END
            WHERE market_listing_id = ANY(@trades)
            """, ("state", state), ("now", now), ("trades", tradeIds.ToArray()));

    public static ValueTask<int> ClearMarketListingAsync(IUnitOfWork work, long tradeId) =>
        work.ExecuteAsync("UPDATE ultimateteam.market_listings SET seller_cleared = true WHERE market_listing_id = @trade AND seller_club_id = @club", ("trade", tradeId));

    public static async ValueTask<int> ClearSoldMarketListingsAsync(IUnitOfWork work)
    {
        // Bulk UPDATE does not promise row order. Acquire the rows in listing order first.
        await work.ExecuteAsync("""
            -- query: market.clear-sold.lock
            SELECT market_listing_id FROM ultimateteam.market_listings
            WHERE seller_club_id=@club AND state='closed' ORDER BY market_listing_id FOR UPDATE
            """);
        return await work.ExecuteAsync("UPDATE ultimateteam.market_listings SET seller_cleared = true WHERE seller_club_id = @club AND state = 'closed'");
    }

    // ---- watchlist --------------------------------------------------------------

    /// <summary>The trades this club watches, in the order it watched them.</summary>
    public static ValueTask<List<long>> MarketWatchIdsAsync(IUnitOfWork work) =>
        work.QueryAsync("-- query: market.watch.ids\nSELECT market_listing_id FROM ultimateteam.market_watch WHERE club_id = @club ORDER BY watched_at", reader => reader.GetInt64(0));

    /// <summary>
    /// This club's coins and watched trades in one statement. A missing club reads as 0 coins, as the
    /// single-column credits read does.
    /// </summary>
    public static async ValueTask<MarketViewer> MarketViewerAsync(IUnitOfWork work) =>
        (await work.QueryAsync("""
            -- query: market.viewer
            SELECT (SELECT credits FROM ultimateteam.clubs WHERE id = @club),
                ARRAY(SELECT market_listing_id FROM ultimateteam.market_watch WHERE club_id = @club)
            """, reader => new MarketViewer(reader.IsDBNull(0) ? 0 : reader.GetInt64(0), reader.GetFieldValue<long[]>(1).ToHashSet()))).Single();

    /// <summary>This club's watched listings in watch order, loaded without per-trade lookups.</summary>
    public static ValueTask<List<MarketListing>> WatchedMarketListingsAsync(IUnitOfWork work) =>
        ListingsAsync(work, $"""
            SELECT {ListingColumns}
            JOIN ultimateteam.market_watch w ON w.market_listing_id = l.market_listing_id
            WHERE w.club_id = @club ORDER BY w.watched_at
            """);

    /// <summary>All market counts required by the hub, including an exact whole-market active count.</summary>
    public static async ValueTask<MarketCounterSnapshot> MarketCountersAsync(IUnitOfWork work) =>
        await MarketClubCountersAsync(work, await CountActiveMarketListingsAsync(work));

    /// <summary>
    /// This club's hub market counts in one statement, with a whole-market active count the caller supplies —
    /// usually the shared cached one, because counting every active listing grows with the market.
    /// </summary>
    public static async ValueTask<MarketCounterSnapshot> MarketClubCountersAsync(IUnitOfWork work, int activeListings) =>
        (await work.QueryAsync($"""
            -- query: market.counters
            SELECT
                (SELECT count(*) FROM ultimateteam.market_listings
                    WHERE kind = 'player' AND seller_club_id = @club AND {OnTransferList("")})
                    +
                (SELECT count(*) FROM ultimateteam.items i
                    WHERE i.club_id = @club AND i.pile = 'trade'
                    AND NOT EXISTS (
                        SELECT 1 FROM ultimateteam.market_listings l
                        WHERE l.kind = 'player' AND l.seller_club_id = @club
                            AND NOT l.seller_cleared AND l.seller_item_id = i.native_item_id)),
                (SELECT count(*) FROM ultimateteam.market_listings
                    WHERE kind = 'player' AND seller_club_id = @club AND NOT seller_cleared AND state = 'active'),
                (SELECT count(*) FROM ultimateteam.market_listings
                    WHERE kind = 'player' AND seller_club_id = @club AND NOT seller_cleared AND state = 'closed'),
                (SELECT count(*) FROM ultimateteam.market_watch w
                    JOIN ultimateteam.market_listings l ON l.market_listing_id = w.market_listing_id
                    WHERE w.club_id = @club AND {UnclaimedWatch}),
                (SELECT count(*) FROM ultimateteam.market_watch w
                    JOIN ultimateteam.market_listings l ON l.market_listing_id = w.market_listing_id
                    WHERE w.club_id = @club AND l.state = 'active'
                        AND l.bidder_club_id IS NOT NULL AND l.bidder_club_id <> @club),
                (SELECT count(*) FROM ultimateteam.market_watch w
                    JOIN ultimateteam.market_listings l ON l.market_listing_id = w.market_listing_id
                    WHERE w.club_id = @club AND l.state = 'active' AND l.bidder_club_id = @club)
            """, reader => new MarketCounterSnapshot(
                activeListings,
                checked((int)reader.GetInt64(0)),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)),
                checked((int)reader.GetInt64(3)),
                checked((int)reader.GetInt64(4)),
                checked((int)reader.GetInt64(5))))).Single();

    /// <summary>Watches an existing trade for <paramref name="clubId"/> (this club by default); unknown trades are ignored.</summary>
    public static ValueTask<int> WatchMarketListingAsync(IUnitOfWork work, long tradeId, long? clubId = null) =>
        work.ExecuteAsync("""
            INSERT INTO ultimateteam.market_watch(club_id, market_listing_id)
            SELECT @owner, market_listing_id FROM ultimateteam.market_listings WHERE market_listing_id = @trade
            ON CONFLICT DO NOTHING
            """, ("owner", clubId ?? work.ClubId), ("trade", tradeId));

    public static ValueTask<int> UnwatchMarketListingAsync(IUnitOfWork work, long tradeId) =>
        work.ExecuteAsync("DELETE FROM ultimateteam.market_watch WHERE club_id = @club AND market_listing_id = @trade", ("trade", tradeId));

    // ---- club withdrawal ----------------------------------------------------------

    /// <summary>
    /// Takes this club out of the market before it is reset or deleted: bidders on its listings
    /// get their coins back and its listings and bids are cancelled.
    /// </summary>
    public static async ValueTask WithdrawFromMarketAsync(IUnitOfWork work)
    {
        // The market lock order: advisory lock, the listings this club sells or bids on, then every
        // club those listings refund, before any change.
        await LockMarketAsync(work);
        var affected = await work.QueryAsync("""
            SELECT seller_club_id, bidder_club_id FROM ultimateteam.market_listings
            WHERE state = 'active' AND (seller_club_id = @club OR bidder_club_id = @club)
            ORDER BY market_listing_id FOR UPDATE
            """, reader => (Seller: NullableLong(reader, 0), Bidder: NullableLong(reader, 1)));
        await LockClubsAsync(work, affected.SelectMany(row => new[] { row.Seller, row.Bidder }).Append(work.ClubId));
        await work.ExecuteAsync("""
            WITH refunds AS (
                SELECT bidder_club_id, SUM(current_bid) AS amount
                FROM ultimateteam.market_listings
                WHERE state = 'active' AND current_bid > 0 AND bidder_club_id IS NOT NULL AND seller_club_id = @club
                GROUP BY bidder_club_id
            )
            UPDATE ultimateteam.clubs c SET credits = c.credits + refunds.amount
            FROM refunds WHERE c.id = refunds.bidder_club_id
            """);
        await work.ExecuteAsync("""
            UPDATE ultimateteam.market_listings SET state = 'cancelled', current_bid = 0, bidder_club_id = NULL, end_time = @now
            WHERE state = 'active' AND seller_club_id = @club
            """, ("now", work.NowSeconds));
        // An AI or other player's listing loses this club's bid; its escrow goes with the club's coins.
        await work.ExecuteAsync("UPDATE ultimateteam.market_listings SET current_bid = 0, bidder_club_id = NULL WHERE state = 'active' AND bidder_club_id = @club");
    }

    private static async ValueTask<List<MarketListing>> ListingsAsync(IUnitOfWork work, string sql, params (string Name, object? Value)[] parameters) =>
        await HydrateAsync(work, await work.QueryAsync(sql, ReadListing, parameters));

    private static MarketListing ReadListing(NpgsqlDataReader reader) => new(
        TradeId: reader.GetInt64(0),
        Kind: reader.GetString(1),
        SellerClubId: NullableLong(reader, 2),
        SellerPersonaId: NullableLong(reader, 3),
        SellerName: reader.GetString(4),
        SellerItemId: NullableLong(reader, 5),
        ResourceId: reader.GetInt64(6),
        Item: FutJson.ParseObject(reader.GetString(7)) ?? new JsonObject(),
        StartingBid: reader.GetInt64(8),
        BuyNow: reader.GetInt64(9),
        CurrentBid: reader.GetInt64(10),
        BidderClubId: NullableLong(reader, 11),
        Offers: reader.GetInt64(12),
        State: reader.GetString(13),
        CreatedAt: reader.GetInt64(14),
        EndTime: reader.GetInt64(15),
        SoldPrice: reader.GetInt64(16));

    // Published snapshots are immutable, so child queries need no additional mutable listing/club locks.
    private static async ValueTask<List<MarketListing>> HydrateAsync(IUnitOfWork work, List<MarketListing> listings)
    {
        if(listings.Count==0)return listings;
        var trades=listings.Select(l=>l.TradeId).Distinct().ToArray();
        var snapshots=await work.QueryAsync("""
            -- query: market.snapshots
            SELECT h.market_listing_id,h.definition_id,h.timestamp,h.pile,h.item_state,h.untradeable,h.owners,h.last_sale_price,
                h.extension_payload::text,h.wire_representation::text,
                p.contracts,p.fitness,p.injury_type,p.injury_games,p.suspension,0::bigint AS loan_games,p.morale,p.training,
                p.position_override,p.chemistry_style_definition_id,p.native_play_style,p.assists,p.lifetime_assists,p.loyalty_bonus,p.match_stats::text,
                EXISTS(SELECT 1 FROM ultimateteam.customisation_definitions cd WHERE cd.definition_id=h.definition_id)
            FROM ultimateteam.market_item_snapshots h LEFT JOIN ultimateteam.market_player_snapshots p ON p.market_listing_id=h.market_listing_id
            WHERE h.market_listing_id=ANY(@trades)
            """,r=> {
                var rep=r.IsDBNull(9)?new JsonObject():FutJson.ParseObject(r.GetString(9))!;
                if(!JsonValues.TryInt(rep.Get("nativeItemId"),out var native)||native<=0)throw new RuntimeSetupException("Snapshot native item identity requires converter audit.");
                var header=new RelationalItemHeader(default,0,new NativeItemId(native),new DefinitionId(r.GetInt64(1)),1,r.GetInt64(2),
                    r.IsDBNull(3)?null:r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetBoolean(5),r.GetInt64(6),r.GetInt64(7));
                // A listing without a player snapshot is a club item or a consumable stack, whose stack lives in the wire representation.
                var item=r.GetBoolean(25)
                    ? new RelationalItemAggregate(header with { FamilyId=4 },Customisation:new CustomisationItemState(default,null),
                        Extension:new ItemExtensionState(default,1,new CatalogJson(r.IsDBNull(8)?"{}":r.GetString(8)),new CatalogJson(rep.ToJsonString())))
                    : r.IsDBNull(10)
                    ? new RelationalItemAggregate(header,
                        Consumable:new ConsumableItemState(default,Math.Max(1,rep.Int("stackCount")),Math.Max(0,rep.Int("untradeableCount"))),
                        Extension:new ItemExtensionState(default,1,new CatalogJson(r.IsDBNull(8)?"{}":r.GetString(8)),new CatalogJson(rep.ToJsonString())))
                    : new RelationalItemAggregate(header,
                        Player:new PlayerItemState(default,r.GetInt64(10),r.GetInt64(11),r.IsDBNull(12)?null:r.GetString(12),r.GetInt64(13),r.GetInt64(14),
                            r.GetInt64(15),r.GetInt64(16),r.GetInt64(17),r.IsDBNull(18)?null:r.GetString(18),
                            r.IsDBNull(19)?null:new DefinitionId(r.GetInt64(19)),NullableLong(r,20),r.GetInt64(21),r.GetInt64(22),NullableLong(r,23),rep.Bool("duplicateAcknowledged")),
                        Extension:new ItemExtensionState(default,1,new CatalogJson(r.IsDBNull(8)?"{}":r.GetString(8)),new CatalogJson(rep.ToJsonString())),
                        Stats:PlayerMatchStatsJson.Decode(r.GetString(24)));
                return (Trade:r.GetInt64(0),Item:item);
            },("trades",trades));
        var documents=snapshots.ToDictionary(s=>s.Trade,s=>work.RelationalItems.Mapper.Render(s.Item));
        return listings.Select(l=>l with { Item=documents.TryGetValue(l.TradeId,out var item)?item:throw new RuntimeSetupException("Market snapshot is incomplete.") }).ToList();
    }
}
