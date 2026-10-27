-- Squad Battles reward tiers as a table (todo.md section 2, owner design 2026-10-03), in the shared reward format of migration 126.
-- Six Top slots (kind ranked) take their places from squad_battles.json competition.boardSize, as FUT Champions does with its
-- Top size: Rank 1, then the other places split over five slots; there is no band sizing. The twelve points tiers (Elite 1 to
-- Bronze 3) keep their percentile bands and minimum points. tier_id is stable: settled results name it.
--
-- This migration creates the table. At start-up the server fills it once (ModeRewardRepository.FillSquadBattlesAsync): a
-- database whose rules still hold sqbt_rewards.json tiers has them converted (tradeability as paid, six Top slots placed by the
-- Top size) and those rule rows removed; a fresh database gets the bundled rewards/squad_battles.json.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.squad_battles_reward_tiers (
    tier_id       text    NOT NULL,
    kind          text    NOT NULL,
    rank_start    integer NOT NULL DEFAULT 0,
    rank_end      integer NOT NULL DEFAULT 0,
    tier_level    integer NOT NULL DEFAULT 0,
    percent_start integer NOT NULL DEFAULT 0,
    percent_end   integer NOT NULL DEFAULT 0,
    min_points    bigint  NOT NULL DEFAULT 0,
    reward_id     bigint  NOT NULL,
    CONSTRAINT squad_battles_reward_tiers_pk PRIMARY KEY (tier_id),
    CONSTRAINT squad_battles_reward_tiers_reward UNIQUE (reward_id),
    CONSTRAINT squad_battles_reward_tiers_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT squad_battles_reward_tiers_id CHECK (tier_id ~ '^[a-z][a-z0-9_]{0,31}$'),
    CONSTRAINT squad_battles_reward_tiers_kind CHECK (kind IN ('ranked', 'points'))
);

DO $$ BEGIN
    EXECUTE 'DROP TRIGGER IF EXISTS content_revision ON ultimateteam.squad_battles_reward_tiers';
    EXECUTE 'CREATE TRIGGER content_revision AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON ultimateteam.squad_battles_reward_tiers FOR EACH STATEMENT EXECUTE FUNCTION ultimateteam.note_content_revision()';
END $$;

-- Six Top slots share the board's places, so the Top size is at least six.
UPDATE ultimateteam.content_rules SET value = '6'::jsonb, updated_at = now()
WHERE file = 'squad_battles' AND path = '["competition","boardSize"]'::jsonb AND (value #>> '{}')::integer < 6;

-- The readme is refreshed from the bundled file at start-up.
DELETE FROM ultimateteam.content_rules WHERE file = 'sqbt_rewards' AND path->>0 = '_readme';

INSERT INTO ultimateteam.schema_migrations(version) VALUES(133);
