-- The operator's switch for starting new matches (panel Maintenance page, MatchGate in the server). On, as before this
-- migration. Off pauses every new online and single-player match: a match already being played still settles its result.
-- Replay-safe: an upgrade test re-applies migrations 103 onward over the current schema.
ALTER TABLE ultimateteam.server_access ADD COLUMN IF NOT EXISTS new_matches boolean NOT NULL DEFAULT true;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(108);
