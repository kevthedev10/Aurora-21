-- Shared store allowance for lightning packs; zero means unlimited.
-- A pack uses either a per-club allowance or a community allowance.
-- Replay-safe: an upgrade test re-applies migrations 103 onward over the current schema.
ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS community_limit integer NOT NULL DEFAULT 0 CHECK (community_limit >= 0);
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS community_limit integer NOT NULL DEFAULT 0 CHECK (community_limit >= 0);
ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS store_pack_one_limit;
ALTER TABLE ultimateteam.store_packs ADD CONSTRAINT store_pack_one_limit CHECK (purchase_limit = 0 OR community_limit = 0);
ALTER TABLE ultimateteam.owned_legacy_pack_definitions DROP CONSTRAINT IF EXISTS legacy_pack_one_limit;
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD CONSTRAINT legacy_pack_one_limit CHECK (purchase_limit = 0 OR community_limit = 0);

CREATE TABLE IF NOT EXISTS ultimateteam.community_pack_purchases (
    pack_id bigint PRIMARY KEY,
    purchases bigint NOT NULL DEFAULT 0 CHECK (purchases >= 0)
);
-- Count successful store purchases from this migration onward, including when unlimited.
-- No club FK: deleting a club must never replenish community stock.
INSERT INTO ultimateteam.schema_migrations(version) VALUES(107);
