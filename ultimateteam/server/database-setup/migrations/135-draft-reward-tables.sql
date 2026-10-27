-- FUT Draft run rewards as tables (todo.md section 2, owner design 2026-10-03), in the shared reward format of migration 126.
-- draft_reward_levels: per mode (online, single) and wins (0 up to draft.json winsToComplete) the reward every run ending
-- there gets. draft_reward_choices: the level's bundles; one is drawn by weight from the run's seed when the run ends.
-- Difficulty no longer changes the reward.
--
-- The owner replaced the old draft.json runRewards with FIFA 21's published bundles, so nothing is converted: those rule
-- rows are removed here and, at start-up, the server fills the tables once from the bundled rewards/draft.json
-- (ModeRewardRepository.FillDraftAsync).
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.draft_reward_levels (
    mode      text    NOT NULL,
    wins      integer NOT NULL,
    reward_id bigint  NOT NULL,
    CONSTRAINT draft_reward_levels_pk PRIMARY KEY (mode, wins),
    CONSTRAINT draft_reward_levels_reward UNIQUE (reward_id),
    CONSTRAINT draft_reward_levels_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT draft_reward_levels_mode CHECK (mode IN ('online', 'single')),
    CONSTRAINT draft_reward_levels_wins CHECK (wins BETWEEN 0 AND 10)
);

CREATE TABLE IF NOT EXISTS ultimateteam.draft_reward_choices (
    mode      text    NOT NULL,
    wins      integer NOT NULL,
    choice    integer NOT NULL,
    weight    integer NOT NULL,
    reward_id bigint  NOT NULL,
    CONSTRAINT draft_reward_choices_pk PRIMARY KEY (mode, wins, choice),
    CONSTRAINT draft_reward_choices_level FOREIGN KEY (mode, wins) REFERENCES ultimateteam.draft_reward_levels(mode, wins) ON DELETE CASCADE,
    CONSTRAINT draft_reward_choices_reward UNIQUE (reward_id),
    CONSTRAINT draft_reward_choices_reward_fk FOREIGN KEY (reward_id) REFERENCES ultimateteam.rewards(reward_id),
    CONSTRAINT draft_reward_choices_choice CHECK (choice BETWEEN 1 AND 50),
    CONSTRAINT draft_reward_choices_weight CHECK (weight BETWEEN 1 AND 1000)
);

-- Content revisions (migration 094): the panel warns when these tables change outside its loaded revision.
DO $$ DECLARE t text; BEGIN
    FOREACH t IN ARRAY ARRAY['draft_reward_levels', 'draft_reward_choices'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS content_revision ON ultimateteam.%I', t);
        EXECUTE format('CREATE TRIGGER content_revision AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON ultimateteam.%I FOR EACH STATEMENT EXECUTE FUNCTION ultimateteam.note_content_revision()', t);
    END LOOP;
END $$;

DELETE FROM ultimateteam.content_rules WHERE file = 'draft' AND path ->> 0 IN ('runRewards', '_runRewardsComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(135);
