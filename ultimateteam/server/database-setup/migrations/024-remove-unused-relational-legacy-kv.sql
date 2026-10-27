-- Pending batch: relational imports are deferred; keep public legacy storage.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.legacy_kv) THEN
  RAISE EXCEPTION 'Unexpected relational legacy values; refusing deletion';
 END IF;
END $$;
DROP TABLE ultimateteam.legacy_kv;
DROP FUNCTION ultimateteam.lfv4_r1_legacy_kv();
INSERT INTO ultimateteam.schema_migrations(version) VALUES(24);
