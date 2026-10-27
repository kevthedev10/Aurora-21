-- The weekly Squad Battles competition (Python server 2f46d8d; rules/squad_battles.json competition).
SET CONSTRAINTS ALL IMMEDIATE;

-- One row per club and week it played: the points, games and results, the opponents already scored ("cycle:slot"),
-- and whether the week's prize was claimed.
CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_weeks (
    club_id       bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    week_id       bigint  NOT NULL,
    points        bigint  NOT NULL DEFAULT 0 CHECK (points >= 0),
    games         integer NOT NULL DEFAULT 0 CHECK (games >= 0),
    wins          integer NOT NULL DEFAULT 0 CHECK (wins >= 0),
    draws         integer NOT NULL DEFAULT 0 CHECK (draws >= 0),
    losses        integer NOT NULL DEFAULT 0 CHECK (losses >= 0),
    played        text[]  NOT NULL DEFAULT '{}',
    prize_claimed boolean NOT NULL DEFAULT false,
    PRIMARY KEY (club_id, week_id)
);
CREATE INDEX IF NOT EXISTS lfv4_ix_squad_battles_weeks_board ON ultimateteam.squad_battles_weeks (week_id, points DESC);

-- The Squad Battles match a club's CreateMatch named, settled by its match end.
CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_pending (
    club_id    bigint  PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    slot       integer NOT NULL,
    difficulty integer NOT NULL,
    cycle      bigint  NOT NULL,
    week_id    bigint  NOT NULL,
    created_at bigint  NOT NULL
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(52);
