-- Relational imports are deferred; retain legacy public.club_imports.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.club_imports) THEN
  RAISE EXCEPTION 'Unexpected relational import history; refusing deletion';
 END IF;
END $$;
DROP TABLE ultimateteam.club_imports;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(18);
