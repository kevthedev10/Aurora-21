-- Owned manager league codes resolve to the existing league FK; zero means unassigned.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.manager_items m LEFT JOIN ultimateteam.leagues l ON l.native_league_id=m.native_league_override LEFT JOIN ultimateteam.leagues existing ON existing.league_id=m.league_override_id WHERE COALESCE(m.native_league_override,0)<>0 AND (l.league_id IS NULL OR (m.league_override_id IS NOT NULL AND existing.native_league_id IS DISTINCT FROM m.native_league_override))) THEN RAISE EXCEPTION 'Unresolved or conflicting manager league override'; END IF;
END $$;
UPDATE ultimateteam.manager_items m SET league_override_id=l.league_id FROM ultimateteam.leagues l WHERE m.league_override_id IS NULL AND m.native_league_override<>0 AND l.native_league_id=m.native_league_override;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.manager_items DROP COLUMN native_league_override;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(27);
