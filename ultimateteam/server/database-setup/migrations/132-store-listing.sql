-- The store listing (owner, 2026-10-03): the Packs page lists only the packs on the store listing, adds others with "+",
-- and switches listed packs on or off; a pack is sold only when it is listed and switched on (and not grant-only). The
-- tradeability of a store purchase is chosen per listing, not when a pack is made.
--
-- store_listed: on the store listing. store_untradeable: whether a purchase opens untradeable items; null keeps
-- rules/tradeability.json's store switch. enabled stays the effective "sold" flag every reader already uses: listed and
-- switched on. Both columns are nullable, like vanity_pack (123), so a pack edit staged before this still publishes.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS store_listed boolean;
ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS store_untradeable boolean;
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS store_listed boolean;
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS store_untradeable boolean;

-- Today's lineup becomes the listing, switched on, so the store sells exactly what it sold before.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.store_packs SET store_listed = true
WHERE store_listed IS NULL AND kind = 'store' AND coalesce(enabled, true) AND NOT coalesce(admin_only, false);
SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(132);
