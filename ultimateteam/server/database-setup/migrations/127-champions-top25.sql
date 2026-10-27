-- Owner, 2026-10-03: fixed Top 25 rewards and leaderboard. Retain stable tier ids, each band's reward and all settled
-- results. Only the previous six default position ranges are converted; operator-defined ranges are left alone.
-- Empty reward tables are filled from the new seed (or converted from the old four default rule ranks) at startup.
SET CONSTRAINTS ALL IMMEDIATE;

WITH bands(tier, old_low, old_high, new_low, new_high, name) AS (VALUES
    (1, 1, 1, 1, 1, 'Rank 1'),
    (2, 2, 20, 2, 5, 'Rank 2-5'),
    (3, 21, 50, 6, 10, 'Rank 6-10'),
    (17, 51, 100, 11, 15, 'Rank 11-15'),
    (4, 101, 150, 16, 20, 'Rank 16-20'),
    (18, 151, 200, 21, 25, 'Rank 21-25')
), unchanged AS (
    SELECT count(*) = 6 AND (SELECT count(*) FROM ultimateteam.champions_reward_ranks WHERE positions_low IS NOT NULL) = 6 AS matches
    FROM bands b JOIN ultimateteam.champions_reward_ranks c
        ON c.tier_level = b.tier AND c.positions_low = b.old_low AND c.positions_high = b.old_high
)
UPDATE ultimateteam.champions_reward_ranks c
SET name = b.name, positions_low = b.new_low, positions_high = b.new_high
FROM bands b, unchanged u WHERE u.matches AND c.tier_level = b.tier;

UPDATE ultimateteam.content_rules SET value = '25'::jsonb, updated_at = now()
WHERE file = 'seasons' AND path = '["weekendLeague","topX"]'::jsonb;

UPDATE ultimateteam.content_rules SET value = '1'::jsonb, updated_at = now()
WHERE file = 'champions_rewards' AND path IN (
    '["leaderboardBands","referenceParticipants"]'::jsonb,
    '["leaderboardBands","floorParticipants"]'::jsonb
);
DELETE FROM ultimateteam.content_rules
WHERE file = 'champions_rewards' AND path->>0 = 'leaderboardBands' AND path->>1 = 'participants';
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
SELECT 'champions_rewards', '["leaderboardBands","participants"]'::jsonb, '{}'::jsonb, 20000
WHERE EXISTS (SELECT 1 FROM ultimateteam.content_rules WHERE file = 'champions_rewards');

-- Refresh explanatory text from the bundled files on the next load.
DELETE FROM ultimateteam.content_rules WHERE file IN ('champions_rewards', 'seasons') AND path->>0 = '_readme';
DELETE FROM ultimateteam.content_rules WHERE file = 'seasons' AND path = '["_weekendLeagueComment"]'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(127);
