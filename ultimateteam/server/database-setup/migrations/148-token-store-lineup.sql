-- Independent offers let one pack appear in both stores with separate price and tradeability.
-- Keep removed offers as unlisted reservations, including after a definition is deleted:
-- a client holding an old offer id must never buy another pack under that id.
CREATE TABLE IF NOT EXISTS ultimateteam.token_store_listings (
    pack_id bigint PRIMARY KEY,
    offer_id integer NOT NULL UNIQUE CHECK (offer_id BETWEEN 1 AND 32767),
    price bigint NOT NULL CHECK (price > 0),
    enabled boolean NOT NULL,
    listed boolean NOT NULL DEFAULT true,
    untradeable boolean
);
-- Preserve existing token prices as separate offers. Coin prices remain on regular listings.
INSERT INTO ultimateteam.token_store_listings(pack_id,offer_id,price,enabled,untradeable)
SELECT pack_id, (SELECT greatest(coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
    coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
    coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0))) + row_number() OVER (ORDER BY pack_id),
    points_price, coalesce(enabled,true), store_untradeable
FROM ultimateteam.store_packs WHERE points_price > 0 AND NOT coalesce(admin_only,false)
    AND (store_section='tokens' OR coalesce(store_listed,false))
ON CONFLICT(pack_id) DO NOTHING;
SELECT set_config('ultimateteam.content_publish','on',true);
UPDATE ultimateteam.store_packs SET store_listed=false,enabled=false
WHERE store_section='tokens' AND pack_id IN (SELECT pack_id FROM ultimateteam.token_store_listings);
UPDATE ultimateteam.store_packs SET store_section=NULL,points_price=0;
-- Token-only legacy listings must not become free coin offers.
UPDATE ultimateteam.store_packs SET enabled=false WHERE coalesce(price,0)=0 AND NOT always_present;
SELECT set_config('ultimateteam.content_publish','',true);
DROP TRIGGER IF EXISTS content_revision ON ultimateteam.token_store_listings;
CREATE TRIGGER content_revision AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE
    ON ultimateteam.token_store_listings FOR EACH STATEMENT EXECUTE FUNCTION ultimateteam.note_content_revision();
INSERT INTO ultimateteam.schema_migrations(version) VALUES(148);
