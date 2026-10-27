-- One pack per reward (owner, 2026-10-03): the players-only reward packs 300 Gold Pack, 301 Premium Gold Pack and
-- 303 Jumbo Premium Gold Pack are the same packs as 304 Gold Players Pack, 305 Premium Gold Players Pack and
-- 319 Jumbo Premium Gold Players Pack in everything but their names, and their names clash with the store's mixed
-- Gold (74), Premium Gold (75) and Jumbo Premium Gold (78) packs. The owner chose "Players" names for the reward
-- versions, so each one folds into its Players twin: every reward, rule, owned unopened pack and pick that names it
-- moves to the twin, and the row goes. 200 Silver Pack (players-only, no twin) becomes Silver Players Pack.
--
-- A pair whose rows differ in more than the name (an operator edit) is left as it is, with a notice.
SET CONSTRAINTS ALL IMMEDIATE;
SELECT set_config('ultimateteam.content_publish', 'on', true);

DO $$
DECLARE
    pair integer[];
    old_row jsonb;
    new_row jsonb;
BEGIN
    FOREACH pair SLICE 1 IN ARRAY ARRAY[[300, 304], [301, 305], [303, 319]] LOOP
        SELECT to_jsonb(p) - 'pack_id' - 'name' INTO old_row FROM ultimateteam.store_packs p WHERE p.pack_id = pair[1];
        SELECT to_jsonb(p) - 'pack_id' - 'name' INTO new_row FROM ultimateteam.store_packs p WHERE p.pack_id = pair[2];
        IF old_row IS NULL OR new_row IS NULL THEN
            CONTINUE;
        END IF;
        IF old_row IS DISTINCT FROM new_row THEN
            RAISE NOTICE 'pack % differs from % in more than its name; both are kept', pair[1], pair[2];
            CONTINUE;
        END IF;

        UPDATE ultimateteam.reward_lines SET pack_id = pair[2] WHERE pack_id = pair[1];
        UPDATE ultimateteam.unopened_packs SET pack_id = pair[2] WHERE pack_id = pair[1];
        UPDATE ultimateteam.player_pick_items SET source_pack_id = pair[2] WHERE source_pack_id = pair[1];

        -- Rule leaves that are pack ids (OperatorPanel.PackDelete.cs PackReferences): the nearest property name is a
        -- pack-id key (a scalar, or a list element below it), or the parent is a map whose values are pack ids.
        UPDATE ultimateteam.content_rules r SET value = to_jsonb(pair[2]), updated_at = now()
        WHERE r.value = to_jsonb(pair[1])
          AND ((SELECT e #>> '{}' FROM jsonb_array_elements(r.path) WITH ORDINALITY AS t(e, n)
                WHERE jsonb_typeof(e) = 'string' ORDER BY n DESC LIMIT 1)
                   IN ('packId', 'pack', 'packs', 'hardPacks', 'neverPacks', 'rewardPackId', 'alwaysPresentPackIds',
                       'packIds', 'defaultRewardPack', 'matchPackId', 'sourcePackId', 'sourcePackIds')
               OR (jsonb_array_length(r.path) >= 2
                   AND r.path ->> (jsonb_array_length(r.path) - 2) IN ('rewardPacks', 'legacyRewardPacks', 'playerPickPacks')));

        -- Purchase counters go with the pack, as a panel pack delete does: a freed id must start from zero.
        DELETE FROM ultimateteam.club_pack_purchases WHERE pack_id = pair[1];
        DELETE FROM ultimateteam.community_pack_purchases WHERE pack_id = pair[1];
        DELETE FROM ultimateteam.store_packs WHERE pack_id = pair[1];
    END LOOP;
END $$;

UPDATE ultimateteam.store_packs SET name = 'Silver Players Pack' WHERE pack_id = 200 AND name = 'Silver Pack';

SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(134);
