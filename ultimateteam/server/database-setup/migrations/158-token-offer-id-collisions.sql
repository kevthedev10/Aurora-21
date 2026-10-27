-- Pack definitions and Token Store offers share the client-visible id namespace.
-- Imported reward packs (migration 154's 75+ pack) must retain their definition, operator edits and grants.
-- Move only conflicting offers; preserve their pack, price and availability.
WITH collisions AS (
    SELECT t.pack_id, row_number() OVER (ORDER BY t.offer_id) AS offset_id
    FROM ultimateteam.token_store_listings t
    WHERE EXISTS (SELECT 1 FROM ultimateteam.store_packs p WHERE p.pack_id=t.offer_id)
       OR EXISTS (SELECT 1 FROM ultimateteam.owned_legacy_pack_definitions p WHERE p.pack_id=t.offer_id)
), reserved AS (
    SELECT greatest(
        coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
        coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
        coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0)) AS last_id
)
UPDATE ultimateteam.token_store_listings t
SET offer_id=reserved.last_id+collisions.offset_id
FROM collisions, reserved WHERE t.pack_id=collisions.pack_id;
-- The existing offer_id check fails atomically if the namespace is exhausted.
INSERT INTO ultimateteam.schema_migrations(version) VALUES(158);
