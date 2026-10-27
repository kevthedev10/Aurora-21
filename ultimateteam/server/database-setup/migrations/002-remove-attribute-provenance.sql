-- Version 2: stat group emission is API policy, not repeated catalog metadata.
-- Execute inside the bootstrap transaction and advisory locks.
SET CONSTRAINTS ALL IMMEDIATE;
DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1 FROM ultimateteam.player_card_definitions
        WHERE attribute_provenance::jsonb IS DISTINCT FROM
            '{"mode":"supplied-only","facePresent":true,"attributesPresent":true}'::jsonb
    ) THEN
        RAISE EXCEPTION 'Cannot remove nonstandard attribute provenance; catalog review required';
    END IF;
END
$migration$;
ALTER TABLE ultimateteam.player_card_definitions DROP COLUMN attribute_provenance;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(2);
