-- User-approved label correction (2026-09-17); all 18 token definitions remain.
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
DO $migration$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.card_types WHERE native_rareflag=89)<>1
       OR EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_rareflag=89
                 AND name NOT IN ('FUT Birthday Evolution','Icon Swaps 1')) THEN
        RAISE EXCEPTION 'Unexpected rarity 89 category; refusing name correction';
    END IF;
END $migration$;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types SET name='Icon Swaps 1' WHERE native_rareflag=89;
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(9);
