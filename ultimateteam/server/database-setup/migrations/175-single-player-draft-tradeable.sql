-- Future single-player Draft end-of-run bundles are tradeable. Preserve every
-- reward's contents, counts, coins and choice weights, and all already-owned packs.
-- Match-completion packs and online Draft reward settings are unchanged.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.reward_lines
SET untradeable=false
WHERE untradeable AND reward_id IN (
    SELECT reward_id FROM ultimateteam.draft_reward_levels WHERE mode='single'
    UNION
    SELECT reward_id FROM ultimateteam.draft_reward_choices WHERE mode='single'
);
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(175);
