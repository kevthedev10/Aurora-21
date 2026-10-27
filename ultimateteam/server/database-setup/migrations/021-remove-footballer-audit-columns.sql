-- Pending batch: retain real identities and birthdays, remove audit labels.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.footballers WHERE native_footballer_id IS NOT NULL GROUP BY native_footballer_id HAVING count(*)>1) THEN
  RAISE EXCEPTION 'Duplicate native footballer IDs; refusing index replacement';
 END IF;
END $$;
DROP INDEX ultimateteam.lfv4_idx_004_footballers;
ALTER TABLE ultimateteam.footballers DROP COLUMN identity_status, DROP COLUMN date_of_birth_source;
CREATE UNIQUE INDEX lfv4_idx_004_footballers ON ultimateteam.footballers(native_footballer_id) WHERE native_footballer_id IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(21);
