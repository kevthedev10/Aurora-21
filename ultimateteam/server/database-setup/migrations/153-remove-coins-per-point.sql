-- Remove coins per FIFA Point (maintainer, 2026-10-04). It converted a store pack's coin price into a FIFA Points
-- (Aurora Tokens) price for packs without their own points_price. Since the Token Store lineup (migration 148) a coin listing
-- sells for coins only and a token listing for its own price, so nothing read the value; the Settings page that edited it
-- is gone too. Its CHECK constraint goes with the column.
ALTER TABLE ultimateteam.store_pack_details DROP COLUMN IF EXISTS coins_per_point;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(153);
