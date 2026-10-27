-- Squad Battles as in FIFA 21 retail (Python server f354894, a4fdc85). A week also counts the squads a refresh skipped
-- (they count toward the week's matches for no points), the Team of the Week matches, and this week's TOTW result (a
-- replay counts only when it beats it). Each club draws four opponents from the other real clubs on the server, frozen
-- when drawn, with the clubs it already faced this week. A pending CreateMatch says what it named: a slot of the set drawn
-- then, or the TOTW. A closed week settles once for every club (rules/sqbt_rewards.json), and each result is claimed once.
-- The old per-cycle "played" keys now hold the slots of replaced sets that already scored.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.squad_battles_weeks ADD COLUMN IF NOT EXISTS skipped integer NOT NULL DEFAULT 0 CHECK (skipped >= 0);
ALTER TABLE ultimateteam.squad_battles_weeks ADD COLUMN IF NOT EXISTS special_games integer NOT NULL DEFAULT 0 CHECK (special_games >= 0);
ALTER TABLE ultimateteam.squad_battles_weeks ADD COLUMN IF NOT EXISTS totw jsonb;

-- A club's current four opponents: the squads as drawn (summary information, the frozen squad and the result once played).
CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_sets (
    club_id         bigint  PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    week_id         bigint  NOT NULL,
    set_index       integer NOT NULL CHECK (set_index >= 0),
    created_at      bigint  NOT NULL,
    next_refresh_at bigint  NOT NULL,
    faced           bigint[] NOT NULL DEFAULT '{}',
    squads          jsonb   NOT NULL
);

ALTER TABLE ultimateteam.squad_battles_pending ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'set';
ALTER TABLE ultimateteam.squad_battles_pending ADD COLUMN IF NOT EXISTS featured_squad_id bigint;
ALTER TABLE ultimateteam.squad_battles_pending ADD COLUMN IF NOT EXISTS set_week bigint;
ALTER TABLE ultimateteam.squad_battles_pending ADD COLUMN IF NOT EXISTS set_index integer;

-- A closed week's standings, written once when it settles.
CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_settlements (
    week_id      bigint  PRIMARY KEY,
    settled_at   bigint  NOT NULL,
    participants bigint  NOT NULL,
    ranked       boolean NOT NULL
);

-- Each club's settled result with a prize, the awards fixed at settlement.
CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_results (
    week_id    bigint  NOT NULL REFERENCES ultimateteam.squad_battles_settlements(week_id) ON DELETE CASCADE,
    club_id    bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    position   integer NOT NULL,
    rank       integer NOT NULL,
    tier_id    text    NOT NULL,
    tier_level integer NOT NULL,
    points     bigint  NOT NULL,
    awards     jsonb   NOT NULL,
    claimed_at bigint,
    PRIMARY KEY (week_id, club_id)
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(60);
