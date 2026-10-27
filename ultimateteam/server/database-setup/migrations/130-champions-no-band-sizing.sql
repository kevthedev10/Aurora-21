-- Owner, 2026-10-03: FUT Champions band sizing is removed. The six Top bands' places follow seasons.json weekendLeague.topX
-- (the Top players number) and never change with the number of players, so champions_rewards.json leaderboardBands
-- (reference, minimum and per-weekend field sizes) goes. Squad Battles keeps its own leaderboardBands. The readme is
-- refreshed from the bundled file at start-up.
SET CONSTRAINTS ALL IMMEDIATE;

DELETE FROM ultimateteam.content_rules WHERE file = 'champions_rewards' AND path->>0 IN ('leaderboardBands', '_readme');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(130);
