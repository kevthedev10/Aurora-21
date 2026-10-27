-- Replace result/stat/difficulty payouts with per-mode completion rewards. Startup fills the new
-- settings from the reviewed bundled seasons seed. Keep clubs, settled rewards, unopened packs,
-- all unrelated rules. Already-granted tradeability and rolled contents stay intact.
SET CONSTRAINTS ALL IMMEDIATE;
DELETE FROM ultimateteam.content_rules WHERE file = 'seasons' AND (
    path->>0 IN ('matchRewards', 'matchCoinBonuses', '_matchRewardsComment', '_matchCoinBonusesComment',
                 '_rewardEligibilityComment', '_matchPackComment', 'matchPack')
    OR (path->>0 = 'rewardEligibility' AND path->>1 = 'difficulty')
);
DELETE FROM ultimateteam.content_rules WHERE file = 'pack_odds' AND path->>0 IN ('matchPack', '_matchPackComment');
-- Keep the definition for already-owned pre-rolled packs, but retire it from sale and new panel selections.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.store_packs SET enabled=false, store_listed=false, admin_only=true WHERE pack_id=544;
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(141);
