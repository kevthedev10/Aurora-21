-- Leaver penalties (owner, 2026-10-09): a player with rewardEligibility.opponentLeft.queueCooldown.earlyLeaves early leaves
-- (match_leaves) within its window commits an offence. Offences escalate along queueCooldown.ladderSeconds (1 hour, then
-- 1 day); the offence after the ladder is a ban (until NULL) that lasts until an operator lifts it (an appeal on Discord).
-- No online search while a penalty runs. A lifted penalty does not count toward the next offence.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.leaver_penalties (
    id bigserial PRIMARY KEY,
    player text NOT NULL,
    offence integer NOT NULL,
    match_key text NOT NULL,
    started_at bigint NOT NULL,
    until bigint,
    lifted_at bigint,
    CONSTRAINT leaver_penalties_one_per_leave UNIQUE (player, match_key)
);
CREATE INDEX IF NOT EXISTS leaver_penalties_player_time ON ultimateteam.leaver_penalties(player, started_at DESC);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(180);
