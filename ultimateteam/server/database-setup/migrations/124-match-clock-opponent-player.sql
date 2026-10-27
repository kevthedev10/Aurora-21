-- The paired opponent's player id beside its persona in match_clock, so the match end of a player whose opponent left
-- (Alt+F4, crash or a dropped connection: the leaver's own client never reports) settles the leaver's loss in the
-- leaver's club at once (MatchService.SettleLeaverAsync). A persona alone cannot open the leaver's club.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.match_clock ADD COLUMN IF NOT EXISTS opponent_player text;

-- The reward gate's comment now states who gets what when an opponent leaves: startup re-adds it from the bundled rules.
DELETE FROM ultimateteam.content_rules WHERE file = 'seasons' AND path = '["_rewardEligibilityComment"]'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(124);
