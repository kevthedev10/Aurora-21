-- Division Rivals rewards as tables (todo.md section 2, owner design 2026-10-03), in the shared reward format of migration 126.
-- rivals_reward_options: each division's six ranks (1 = Rank I), each with 1-4 reward options the club chooses from (FIFA shows
-- at most four). rivals_promotion_rewards: the reward for reaching a division the first time.
--
-- This migration only creates the tables. At start-up the server fills them once (ModeRewardRepository.FillRivalsAsync): a
-- database whose rules still hold rivals_rewards.json divisions/promotion has them converted with the tradeability they were paid
-- with and those rule rows removed; a fresh database gets the bundled rewards/rivals.json.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.rivals_reward_options (
    division  integer NOT NULL,
    rank      integer NOT NULL,
    option    integer NOT NULL,
    reward_id bigint  NOT NULL,
    CONSTRAINT rivals_reward_options_pk PRIMARY KEY (division, rank, option),
    CONSTRAINT rivals_reward_options_reward UNIQUE (reward_id),
    CONSTRAINT rivals_reward_options_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT rivals_reward_options_division CHECK (division BETWEEN 1 AND 10),
    CONSTRAINT rivals_reward_options_rank CHECK (rank BETWEEN 1 AND 6),
    CONSTRAINT rivals_reward_options_option CHECK (option BETWEEN 1 AND 4)
);

CREATE TABLE IF NOT EXISTS ultimateteam.rivals_promotion_rewards (
    division  integer NOT NULL,
    reward_id bigint  NOT NULL,
    CONSTRAINT rivals_promotion_rewards_pk PRIMARY KEY (division),
    CONSTRAINT rivals_promotion_rewards_reward UNIQUE (reward_id),
    CONSTRAINT rivals_promotion_rewards_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT rivals_promotion_rewards_division CHECK (division BETWEEN 1 AND 10)
);

-- Content revisions (migration 094): the panel warns when these tables change outside its loaded revision.
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['rivals_reward_options', 'rivals_promotion_rewards'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS content_revision ON ultimateteam.%I', t);
        EXECUTE format('CREATE TRIGGER content_revision AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON ultimateteam.%I FOR EACH STATEMENT EXECUTE FUNCTION ultimateteam.note_content_revision()', t);
    END LOOP;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(131);
