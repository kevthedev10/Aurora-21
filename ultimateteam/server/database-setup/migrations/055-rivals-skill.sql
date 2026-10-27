-- Division Rivals skill and weekly record (Python server 17172fc, 6bd9b03): weekly draws and all-time matches beside
-- the existing counters, and the opponent's skill rating snapshotted when a Rivals match is created, which the match
-- end rates the result against (Elo). Also the club record (won, drawn, lost) the club header shows (17172fc).
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS weekly_draws bigint;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS matches_played bigint;

-- The Rivals opponent a club's CreateMatch was paired with, settled (and removed) by its next match end.
CREATE TABLE IF NOT EXISTS ultimateteam.rivals_pending (
    club_id          bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    game_id          bigint NOT NULL,
    opponent_persona bigint NOT NULL,
    opponent_rating  bigint NOT NULL CHECK (opponent_rating >= 0),
    created_at       bigint NOT NULL
);

-- Every settled non-Draft match result, for the club header's record.
CREATE TABLE IF NOT EXISTS ultimateteam.club_records (
    club_id bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    won     bigint NOT NULL DEFAULT 0 CHECK (won >= 0),
    draw    bigint NOT NULL DEFAULT 0 CHECK (draw >= 0),
    loss    bigint NOT NULL DEFAULT 0 CHECK (loss >= 0)
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(55);
