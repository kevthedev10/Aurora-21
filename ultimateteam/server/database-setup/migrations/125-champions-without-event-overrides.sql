-- FUT Champions rewards no longer change per Weekend League (owner, 2026-10-03: reward changes are published, not
-- scheduled). champions_rewards.json eventOverrides held one entry, the test weekend 2961's Rank 1 pack; settled results
-- keep the reward they stored, so removing it changes no payout already made.
--
-- rivals_rewards.json gains weeklyScore.weekendLeagueCounts (whether Weekend League matches add to the Rivals Weekly
-- Score); startup adds that key, and both files' _readme, from the bundled rules.
SET CONSTRAINTS ALL IMMEDIATE;

DELETE FROM ultimateteam.content_rules WHERE file = 'champions_rewards' AND path->>0 = 'eventOverrides';
DELETE FROM ultimateteam.content_rules WHERE file IN ('champions_rewards', 'rivals_rewards') AND path->>0 = '_readme';

INSERT INTO ultimateteam.schema_migrations(version) VALUES(125);
