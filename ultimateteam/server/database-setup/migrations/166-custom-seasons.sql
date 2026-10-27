-- Custom seasons (owner, 2026-10-07): rules/objectives.json may schedule named seasons, each with its own Season Pass. A
-- club's Season XP and Season Pass claims (scopes level and level_option) belong to one season: objectives.season_id names
-- it. When the season in force changes, the club's next save moves its progress in the season it leaves to
-- objective_season_history, and takes up its progress in the new season from there (none yet = 0 XP, nothing claimed), so a
-- season that returns (the default season between two custom ones) never pays a level twice. Objective and group claims and
-- the stat counters are not per season.
--
-- Every club so far played the default season, whose id is the published campaign id (1 unless changed). Replay-safe.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'ultimateteam' AND table_name = 'objectives' AND column_name = 'season_id') THEN
        ALTER TABLE ultimateteam.objectives ADD COLUMN season_id bigint NOT NULL DEFAULT 1;
        UPDATE ultimateteam.objectives SET season_id = COALESCE((SELECT (value #>> '{}')::bigint FROM ultimateteam.content_rules
            WHERE file = 'objectives' AND path = '["campaign", "id"]'::jsonb AND jsonb_typeof(value) = 'number'), 1);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS ultimateteam.objective_season_history (
    club_id     bigint      NOT NULL,
    season_id   bigint      NOT NULL,
    user_xp     bigint      NOT NULL,
    -- [[scope, claim_id], ...] of scopes level and level_option, in the claims' order.
    claims      jsonb       NOT NULL DEFAULT '[]',
    archived_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT objective_season_history_pk PRIMARY KEY (club_id, season_id),
    CONSTRAINT objective_season_history_club FOREIGN KEY (club_id) REFERENCES ultimateteam.objectives(club_id) ON DELETE CASCADE,
    CONSTRAINT objective_season_history_claims CHECK (jsonb_typeof(claims) = 'array')
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(166);
