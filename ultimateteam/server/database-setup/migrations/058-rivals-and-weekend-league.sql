-- Division Rivals and the FUT Champions Weekend League as in FIFA 21 retail (Python server 21bf125, 56145ed).
-- rivals_progress gains the retail record: the rules version (older records are cleared on first read, so every club
-- places again), placement, the week's fixed division, the best division entered (promotion coins are paid once each),
-- the Weekly Score and its counted matches, FUT Champions Points, the last five results and the last week's result.
-- Weekly rewards are settled once for everyone after a week closes (rivals_week_settlements, rivals_week_results) and
-- claimed per club; the Weekend League keeps each club's record per event, its entries, registration, the pending
-- CreateMatch and its settled rewards. An owned pack can be untradeable (a Rivals reward option): its items open
-- untradeable.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS rating_version bigint;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS placed boolean NOT NULL DEFAULT false;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS placement_played bigint NOT NULL DEFAULT 0;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS week_division bigint NOT NULL DEFAULT 10;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS best_division bigint NOT NULL DEFAULT 11;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS weekly_score bigint NOT NULL DEFAULT 0;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS weekly_counted bigint NOT NULL DEFAULT 0;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS fc_points bigint NOT NULL DEFAULT 0;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS form text NOT NULL DEFAULT '';
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS last_delta bigint NOT NULL DEFAULT 0;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS last_week_id bigint;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS last_week_division bigint;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS last_week_score bigint;
ALTER TABLE ultimateteam.rivals_progress ADD COLUMN IF NOT EXISTS last_week_games bigint;

-- A closed week's thresholds per division, written once when the week settles.
CREATE TABLE IF NOT EXISTS ultimateteam.rivals_week_settlements (
    week_id    bigint PRIMARY KEY,
    settled_at bigint NOT NULL,
    award_time bigint NOT NULL,
    divisions  jsonb  NOT NULL
);

-- Each ranked club's result for a settled week, with the reward options resolved at settlement.
CREATE TABLE IF NOT EXISTS ultimateteam.rivals_week_results (
    week_id    bigint  NOT NULL REFERENCES ultimateteam.rivals_week_settlements(week_id) ON DELETE CASCADE,
    club_id    bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    division   integer NOT NULL,
    rank       integer NOT NULL,
    score      bigint  NOT NULL,
    options    jsonb   NOT NULL,
    claimed_at bigint,
    PRIMARY KEY (week_id, club_id)
);

-- A club's record in one Weekend League.
CREATE TABLE IF NOT EXISTS ultimateteam.weekend_league_records (
    club_id  bigint NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    event_id bigint NOT NULL,
    games    bigint NOT NULL DEFAULT 0,
    wins     bigint NOT NULL DEFAULT 0,
    draws    bigint NOT NULL DEFAULT 0,
    losses   bigint NOT NULL DEFAULT 0,
    form     bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (club_id, event_id)
);

-- Weekend Leagues a club redeemed FUT Champions Points for.
CREATE TABLE IF NOT EXISTS ultimateteam.weekend_league_entries (
    club_id     bigint NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    event_id    bigint NOT NULL,
    redeemed_at bigint NOT NULL,
    points      bigint NOT NULL,
    PRIMARY KEY (club_id, event_id)
);

-- The country a club registered for FUT Champions.
CREATE TABLE IF NOT EXISTS ultimateteam.champion_registrations (
    club_id bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    country text   NOT NULL,
    region  text   NOT NULL
);

-- The Weekend League CreateMatch (it names championId) that the next match end settles.
CREATE TABLE IF NOT EXISTS ultimateteam.champion_pending (
    club_id    bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    league_id  bigint NOT NULL,
    created_at bigint NOT NULL
);

-- A Weekend League's final standings, written once at its rewards time.
CREATE TABLE IF NOT EXISTS ultimateteam.champion_settlements (
    event_id     bigint PRIMARY KEY,
    settled_at   bigint NOT NULL,
    participants bigint NOT NULL
);

CREATE TABLE IF NOT EXISTS ultimateteam.champion_results (
    event_id   bigint  NOT NULL REFERENCES ultimateteam.champion_settlements(event_id) ON DELETE CASCADE,
    club_id    bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    position   bigint  NOT NULL,
    tier_level integer NOT NULL,
    rank_name  text    NOT NULL,
    wins       bigint  NOT NULL,
    games      bigint  NOT NULL,
    reward     jsonb   NOT NULL,
    claimed_at bigint,
    PRIMARY KEY (event_id, club_id)
);

ALTER TABLE ultimateteam.unopened_packs ADD COLUMN IF NOT EXISTS untradeable boolean NOT NULL DEFAULT false;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(58);
