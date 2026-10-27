-- Optional lifetime store purchase limit per pack and club; zero means unlimited.
-- Owned/gift/reward pack openings never consume this allowance.
-- Replay-safe: an upgrade test re-applies migrations 103 onward over the current schema.
ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS purchase_limit integer NOT NULL DEFAULT 0 CHECK (purchase_limit >= 0);
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS purchase_limit integer NOT NULL DEFAULT 0 CHECK (purchase_limit >= 0);

CREATE TABLE IF NOT EXISTS ultimateteam.club_pack_purchases (
    club_id bigint NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    pack_id bigint NOT NULL,
    purchases bigint NOT NULL DEFAULT 0 CHECK (purchases >= 0),
    PRIMARY KEY(club_id, pack_id)
);
-- Counts start with this feature; old purchases cannot be reliably reconstructed.
INSERT INTO ultimateteam.schema_migrations(version) VALUES(105);
