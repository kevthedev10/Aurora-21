-- Preserve optional player filters formerly supplied by personal pack settings.
-- Existing packs leave both filters unset; this only adds configuration capacity.
ALTER TABLE ultimateteam.store_packs
    ADD COLUMN allowed_team_ids bigint[],
    ADD COLUMN allowed_nations bigint[];

INSERT INTO ultimateteam.schema_migrations(version) VALUES(42);
