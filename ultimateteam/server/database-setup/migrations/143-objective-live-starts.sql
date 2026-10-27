-- Live objectives (owner, 2026-10-04): rules/objectives.json holds every objective group (the catalogue) and a live list of
-- the groups the game shows, each switched on or off. A group's objectives count from when the group first went live: when a
-- publish puts a group live for the first time, each club's current counters are stored as that group's starting point, and
-- the group's progress is the counter minus it (a club without a row starts at 0).
--
-- objective_group_starts: the groups that have been live, and when. objective_baselines: the starting points.
--
-- The groups the rules already hold were live before this, and their progress has always counted from the club's creation:
-- they are marked started here with no starting points, so no club loses progress. Replay-safe.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.objective_group_starts (
    group_id   bigint      NOT NULL,
    started_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT objective_group_starts_pk PRIMARY KEY (group_id),
    CONSTRAINT objective_group_starts_id CHECK (group_id > 0)
);

CREATE TABLE IF NOT EXISTS ultimateteam.objective_baselines (
    club_id   bigint NOT NULL,
    group_id  bigint NOT NULL,
    stat_code text   NOT NULL,
    value     bigint NOT NULL,
    CONSTRAINT objective_baselines_pk PRIMARY KEY (club_id, group_id, stat_code),
    CONSTRAINT objective_baselines_club FOREIGN KEY (club_id) REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    CONSTRAINT objective_baselines_group FOREIGN KEY (group_id) REFERENCES ultimateteam.objective_group_starts(group_id) ON DELETE CASCADE,
    CONSTRAINT objective_baselines_value CHECK (value >= 0)
);

INSERT INTO ultimateteam.objective_group_starts(group_id)
SELECT DISTINCT (value #>> '{}')::bigint FROM ultimateteam.content_rules
WHERE file = 'objectives' AND jsonb_typeof(value) = 'number' AND (
    path = '["welcomeGroup", "id"]'::jsonb
    OR (jsonb_array_length(path) = 3 AND path->>0 IN ('grindGroups', 'gifts') AND path->>2 = 'id'))
ON CONFLICT (group_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(143);
