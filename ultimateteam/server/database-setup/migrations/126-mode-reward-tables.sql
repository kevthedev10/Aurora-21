-- Mode rewards as tables (todo.md section 2, owner design 2026-10-03). Every mode will share one reward format: coins, FUT
-- Champions Points, XP and lines, each a pack/pick or a player card with a count and its own untradeable choice, which is what
-- the club receives. A mode's own tier table says who earns which reward; FUT Champions is the first.
--
-- This migration only creates the tables. At start-up the server fills them once (ModeRewardRepository.FillAsync): a database
-- whose rules still hold champions_rewards.json ranks has them converted with the tradeability they were paid with and those rule
-- rows removed; a fresh database gets the bundled rewards/champions.json.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.rewards (
    reward_id bigint GENERATED ALWAYS AS IDENTITY,
    coins     bigint NOT NULL DEFAULT 0,
    fc_points bigint NOT NULL DEFAULT 0,
    xp        bigint NOT NULL DEFAULT 0,
    CONSTRAINT rewards_pk PRIMARY KEY (reward_id),
    CONSTRAINT rewards_amounts CHECK (coins >= 0 AND fc_points >= 0 AND xp >= 0)
);

CREATE TABLE IF NOT EXISTS ultimateteam.reward_lines (
    reward_id        bigint  NOT NULL,
    position         integer NOT NULL,
    -- A pack or pick; a reward line keeps its pack from being deleted.
    pack_id          bigint,
    -- A player card by native resource id.
    card_resource_id bigint,
    count            integer NOT NULL,
    untradeable      boolean NOT NULL,
    -- The grant source (rules/tradeability.json sources); null is the mode's own.
    source           text,
    CONSTRAINT reward_lines_pk PRIMARY KEY (reward_id, position),
    CONSTRAINT reward_lines_reward FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id) ON DELETE CASCADE,
    CONSTRAINT reward_lines_pack FOREIGN KEY (pack_id) REFERENCES ultimateteam.store_packs(pack_id),
    CONSTRAINT reward_lines_one_item CHECK ((pack_id IS NULL) <> (card_resource_id IS NULL)),
    CONSTRAINT reward_lines_position CHECK (position >= 0),
    CONSTRAINT reward_lines_count CHECK (count BETWEEN 1 AND 100),
    CONSTRAINT reward_lines_source CHECK (source IS NULL OR source ~ '^[a-z][a-z_]{0,63}$')
);
CREATE INDEX IF NOT EXISTS reward_lines_pack ON ultimateteam.reward_lines(pack_id) WHERE pack_id IS NOT NULL;

-- FUT Champions Weekend League reward ranks: a rank by wins [low, high] or a Top 200 band by position [low, high]. tier_level is
-- the rank's stable id: settled results name it.
CREATE TABLE IF NOT EXISTS ultimateteam.champions_reward_ranks (
    tier_level     integer NOT NULL,
    name           text    NOT NULL,
    wins_low       integer,
    wins_high      integer,
    positions_low  integer,
    positions_high integer,
    reward_id      bigint  NOT NULL,
    CONSTRAINT champions_reward_ranks_pk PRIMARY KEY (tier_level),
    CONSTRAINT champions_reward_ranks_reward UNIQUE (reward_id),
    CONSTRAINT champions_reward_ranks_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT champions_reward_ranks_tier CHECK (tier_level > 0),
    CONSTRAINT champions_reward_ranks_name CHECK (length(name) BETWEEN 1 AND 64),
    CONSTRAINT champions_reward_ranks_range CHECK (
        (wins_low IS NOT NULL AND wins_high IS NOT NULL AND positions_low IS NULL AND positions_high IS NULL
            AND wins_low >= 0 AND wins_low <= wins_high)
        OR (positions_low IS NOT NULL AND positions_high IS NOT NULL AND wins_low IS NULL AND wins_high IS NULL
            AND positions_low >= 1 AND positions_low <= positions_high))
);

-- Content revisions (migration 094): the panel warns when these tables change outside its loaded revision.
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['rewards', 'reward_lines', 'champions_reward_ranks'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS content_revision ON ultimateteam.%I', t);
        EXECUTE format('CREATE TRIGGER content_revision AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON ultimateteam.%I FOR EACH STATEMENT EXECUTE FUNCTION ultimateteam.note_content_revision()', t);
    END LOOP;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(126);
