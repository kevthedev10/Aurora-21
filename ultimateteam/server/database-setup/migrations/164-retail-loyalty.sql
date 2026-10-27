-- Retail loyalty and club stats (owner 10-07: "packed = loyalty, bought = no loyalty, bought + 10 games played = loyalty";
-- a bought card's counted stats belong to its previous owners). Every player card was built with loyaltyBonus 1 and a
-- market buy copied the seller's card whole, so each bought card was loyal at once and showed the seller's games, goals,
-- cards and assists as its club stats. Buys now start the club stats at zero, keep lifetime stats and drop the bonus;
-- the tenth club game sets the bonus again (MatchService).
--
-- Cards already bought: the sale snapshot holds the seller's club stats at the sale, and the buyer's copy has added only
-- its own games since, so take the snapshot's numbers off (lifetime stats keep them). The latest sale into the card's
-- current club is the one that counts. Then a bought card (two or more owners) is loyal only after ten of its own games.
-- Packed and reward cards (one owner) keep their bonus.
WITH bought AS (
    SELECT DISTINCT ON (i.id) i.id AS item_id, s.match_stats -> 'current' AS seller, s.assists AS seller_assists
    FROM ultimateteam.market_listings l
    JOIN ultimateteam.market_player_snapshots s ON s.market_listing_id = l.market_listing_id
    JOIN ultimateteam.items i ON i.club_id = l.bidder_club_id AND i.definition_id = l.definition_id
        AND i.last_sale_price = l.sold_price AND i.timestamp = l.end_time
    WHERE l.state = 'closed' AND l.bidder_club_id IS NOT NULL
    ORDER BY i.id, l.end_time DESC
)
UPDATE ultimateteam.player_items p SET
    match_stats = jsonb_set(p.match_stats, '{current}', (
        SELECT jsonb_agg(GREATEST(0, e.value::bigint - COALESCE((b.seller ->> (e.ord - 1)::int)::bigint, 0)) ORDER BY e.ord)
        FROM jsonb_array_elements_text(p.match_stats -> 'current') WITH ORDINALITY e(value, ord))),
    assists = GREATEST(0, p.assists - COALESCE(b.seller_assists, 0))
FROM bought b
WHERE p.item_id = b.item_id;

UPDATE ultimateteam.player_items p SET loyalty_bonus = CASE WHEN (p.match_stats -> 'current' ->> 0)::bigint >= 10 THEN 1 ELSE 0 END
FROM ultimateteam.items i
WHERE i.id = p.item_id AND i.owners >= 2;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(164);
