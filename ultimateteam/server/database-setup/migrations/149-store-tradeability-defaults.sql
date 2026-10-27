-- Resolve old inherited choices once, preserving effective tradeability of existing listings.
SELECT set_config('ultimateteam.content_publish','on',true);
UPDATE ultimateteam.store_packs p SET store_untradeable = coalesce(
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path=jsonb_build_array('sourcePacks','store',p.pack_id::text)),
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path=jsonb_build_array('packs',p.pack_id::text)),
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path='["sources","store"]'::jsonb),false)
WHERE kind='store' AND store_untradeable IS NULL;
UPDATE ultimateteam.token_store_listings p SET untradeable = coalesce(
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path=jsonb_build_array('sourcePacks','store',p.pack_id::text)),
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path=jsonb_build_array('packs',p.pack_id::text)),
    (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules WHERE file='tradeability' AND path='["sources","store"]'::jsonb),false)
WHERE untradeable IS NULL;
ALTER TABLE ultimateteam.store_packs ALTER COLUMN store_untradeable SET DEFAULT false;
ALTER TABLE ultimateteam.token_store_listings ALTER COLUMN untradeable SET DEFAULT true;
SELECT set_config('ultimateteam.content_publish','',true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(149);
