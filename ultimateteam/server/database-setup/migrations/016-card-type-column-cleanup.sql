-- Keep the existing table, identity sequence, inbound FKs, labels and row IDs.
-- Recreate trailing columns so SELECT * shows id, name, then metadata.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name='card_types' AND column_name NOT IN('card_type_id','name','code','card_level','is_special','source_id','mapping_status','native_rarity_id'))
 OR EXISTS(SELECT 1 FROM pg_indexes WHERE schemaname='ultimateteam' AND tablename='card_types' AND indexname NOT IN('lfv4_pk_card_types','lfv4_uq_card_types_1','lfv4_idx_026_card_types','lfv4_idx_card_types_native_rarity'))
 OR EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='ultimateteam.card_types'::regclass AND contype <> 'n' AND conname NOT IN('lfv4_pk_card_types','lfv4_uq_card_types_1','lfv4_fk_card_types_source_id','lfv4_fk_card_types_native_rarity','lfv4_ck_card_types_name')) THEN
  RAISE EXCEPTION 'Unexpected card type extensions; refusing column reorder';
 END IF;
END $$;
CREATE TEMP TABLE card_type_column_backup ON COMMIT DROP AS SELECT card_type_id,name,card_level,is_special,source_id,mapping_status,native_rarity_id FROM ultimateteam.card_types;
ALTER TABLE ultimateteam.card_types DROP COLUMN code, DROP COLUMN card_level, DROP COLUMN is_special, DROP COLUMN source_id, DROP COLUMN mapping_status, DROP COLUMN native_rarity_id;
ALTER TABLE ultimateteam.card_types ADD COLUMN card_level text, ADD COLUMN native_rarity_id bigint, ADD COLUMN is_special boolean, ADD COLUMN source_id bigint, ADD COLUMN mapping_status text;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types t SET card_level=b.card_level,native_rarity_id=b.native_rarity_id,is_special=b.is_special,source_id=b.source_id,mapping_status=b.mapping_status FROM card_type_column_backup b WHERE t.card_type_id=b.card_type_id;
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
ALTER TABLE ultimateteam.card_types ALTER COLUMN native_rarity_id SET NOT NULL, ALTER COLUMN is_special SET NOT NULL, ALTER COLUMN source_id SET NOT NULL, ALTER COLUMN mapping_status SET NOT NULL;
ALTER TABLE ultimateteam.card_types ADD CONSTRAINT lfv4_fk_card_types_source_id FOREIGN KEY(source_id) REFERENCES ultimateteam.catalog_sources(source_id) ON UPDATE NO ACTION ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE ultimateteam.card_types ADD CONSTRAINT lfv4_fk_card_types_native_rarity FOREIGN KEY(native_rarity_id) REFERENCES ultimateteam.native_rarities(native_rarity_id);
CREATE INDEX lfv4_idx_026_card_types ON ultimateteam.card_types(source_id);
CREATE INDEX lfv4_idx_card_types_native_rarity ON ultimateteam.card_types(native_rarity_id);
DO $$ BEGIN
 IF EXISTS((SELECT * FROM card_type_column_backup EXCEPT SELECT card_type_id,name,card_level,is_special,source_id,mapping_status,native_rarity_id FROM ultimateteam.card_types) UNION ALL (SELECT card_type_id,name,card_level,is_special,source_id,mapping_status,native_rarity_id FROM ultimateteam.card_types EXCEPT SELECT * FROM card_type_column_backup)) THEN
  RAISE EXCEPTION 'Card type metadata changed during reorder';
 END IF;
END $$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(16);
