-- Disable automatic AI market filling in existing databases, not just new seeds.
UPDATE ultimateteam.content_rules SET value='0'::jsonb, updated_at=now()
WHERE file='market' AND path='["aiListings","target"]'::jsonb;

-- Remove unbid unsold AI inventory and its cascading snapshots/watch entries.
-- Preserve player listings, completed sale history and auctions with escrowed bids:
-- the ordinary settlement job must still deliver the item or refund the bidder.
DELETE FROM ultimateteam.market_listings
WHERE kind='ai' AND state IN ('active','expired','cancelled')
    AND bidder_club_id IS NULL AND current_bid=0;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(111);
