-- Code75 identified by the user as FIFA21 Next-Gen/NXT LVL loan items.
-- Code76 is the externally matched first FGS token batch; 77 is locally identified as batch2.
-- These are label corrections, not proof of native IDs/groups; no card stats/ownership change.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
DO $migration$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.card_types WHERE native_rareflag=75)<>2
       OR (SELECT count(*) FROM ultimateteam.card_types WHERE native_rareflag=77)<>1
       OR EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_rareflag=75 AND name NOT IN ('FGS Swaps 1','Next-Gen'))
       OR EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_rareflag=77 AND name NOT IN ('Shapeshifters Hero','FGS Swaps 2')) THEN
        RAISE EXCEPTION 'Unexpected Next-Gen/FGS categories; refusing name correction';
    END IF;
END $migration$;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types SET name=CASE native_rareflag WHEN 75 THEN 'Next-Gen' WHEN 77 THEN 'FGS Swaps 2' END WHERE native_rareflag IN(75,77);
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(12);
