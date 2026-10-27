-- Card types keep their own human-readable label alongside the category FK.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
ALTER TABLE ultimateteam.card_types ADD COLUMN name text;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types t SET name=CASE WHEN r.native_rareflag IN(0,1) THEN r.name || ' ' || initcap(t.card_level) ELSE r.name END FROM ultimateteam.native_rarities r WHERE r.native_rarity_id=t.native_rarity_id;
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
ALTER TABLE ultimateteam.card_types ALTER COLUMN name SET NOT NULL;
ALTER TABLE ultimateteam.card_types ADD CONSTRAINT lfv4_ck_card_types_name CHECK(length(trim(name))>0);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(14);
