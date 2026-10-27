-- Maintainer requested that packs display and accept their configured FIFA Points price.
-- Preserve all other game rules and balances; there is still no daily Points grant.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.content_rules SET value='true'::jsonb
WHERE file='club' AND path='["fifaPointsPurchases"]'::jsonb;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(106);
