-- SBCs and opened packs no longer earn Season XP (owner, 2026-10-04): Season XP comes from completed matches (each mode's
-- completion reward, migration 145) and claimed objectives. They still count for objectives (the sbcs and packs stats).
-- rules/objectives.json xp (sbc, pack) and its comment are removed. Replay-safe.
DELETE FROM ultimateteam.content_rules WHERE file = 'objectives' AND path->>0 IN ('xp', '_xpComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(146);
