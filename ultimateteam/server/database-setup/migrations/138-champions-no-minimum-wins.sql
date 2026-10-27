-- Leaderboard rewards depend on position and minimum matches, without a wins threshold.
-- Remove the retired setting and refresh bundled documentation on existing databases.
SET CONSTRAINTS ALL IMMEDIATE;

DELETE FROM ultimateteam.content_rules
WHERE file = 'champions_rewards' AND path->>0 IN ('positionsNeedWins', '_readme');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(138);
