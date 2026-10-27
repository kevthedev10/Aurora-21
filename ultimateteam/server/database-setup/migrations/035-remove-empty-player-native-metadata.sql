-- Pending batch: trait masks derive from linked traits, version defaults in API.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.player_card_definitions WHERE native_trait_1 IS NOT NULL OR native_trait_2 IS NOT NULL OR native_version IS NOT NULL) THEN RAISE EXCEPTION 'Legacy player trait/version columns contain values; refusing removal'; END IF;
END $$;
ALTER TABLE ultimateteam.player_card_definitions DROP COLUMN native_trait_1,DROP COLUMN native_trait_2,DROP COLUMN native_version;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(35);
