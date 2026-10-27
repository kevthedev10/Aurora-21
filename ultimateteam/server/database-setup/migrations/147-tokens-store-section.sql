-- Store placement is independent of pack artwork. Existing packs retain their normal category.
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS store_section text
    CHECK (store_section IN ('standard', 'tokens'));
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS store_section text
    CHECK (store_section IN ('standard', 'tokens'));
INSERT INTO ultimateteam.schema_migrations(version) VALUES(147);
