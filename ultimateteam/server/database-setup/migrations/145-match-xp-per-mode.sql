-- Season XP per completed match moves into each mode's completion reward (owner, 2026-10-04): a completed match pays the
-- same whatever the result, so the objectives win bonus (rules/objectives.json xp.win) is retired, and xp.match becomes
-- seasons matchCompletionRewards.<mode>.xp, set per mode under Modes. Each mode takes the stored xp.match (300 when none
-- is stored); a fresh database gets the bundled value from the rules fill. Replay-safe.
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
SELECT 'seasons', jsonb_build_array('matchCompletionRewards', coins.path->>1, 'xp'),
    coalesce((SELECT value FROM ultimateteam.content_rules WHERE file = 'objectives' AND path = '["xp", "match"]'::jsonb), '300'::jsonb),
    coins.ordinal
FROM ultimateteam.content_rules coins
WHERE coins.file = 'seasons' AND jsonb_array_length(coins.path) = 3 AND coins.path->>0 = 'matchCompletionRewards' AND coins.path->>2 = 'coins'
ON CONFLICT (file, path) DO NOTHING;

DELETE FROM ultimateteam.content_rules WHERE file = 'objectives' AND path IN ('["xp", "match"]'::jsonb, '["xp", "win"]'::jsonb);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(145);
