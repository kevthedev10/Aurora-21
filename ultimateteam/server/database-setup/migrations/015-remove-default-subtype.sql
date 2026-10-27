-- Remove unused category-level placeholder; retain item_definitions.native_subtype.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_default_subtype IS NOT NULL) THEN
  RAISE EXCEPTION 'Nonempty default subtype requires review before removal';
 END IF;
END $$;
ALTER TABLE ultimateteam.card_types DROP COLUMN native_default_subtype;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(15);
