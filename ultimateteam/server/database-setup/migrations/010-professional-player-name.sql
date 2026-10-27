-- User-approved name for retained PRO cards (2026-09-17).
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
DO $migration$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.card_types WHERE native_rareflag=10)<>1
       OR EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_rareflag=10
                 AND name NOT IN ('FIFA rarity 10','Professional Player')) THEN
        RAISE EXCEPTION 'Unexpected rarity 10 category; refusing name correction';
    END IF;
END $migration$;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types SET name='Professional Player' WHERE native_rareflag=10;
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(10);
