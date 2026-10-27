-- The authored reward pools contain TOTW weeks 1–3 even when the ordinary promo
-- schedule or phase changes. Rename stored references, including unrevealed picks.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.store_packs
SET resource_pool = 'totw_1_3'
WHERE resource_pool = 'live_totw';
UPDATE ultimateteam.store_packs pack
SET player_picks = (
    SELECT jsonb_agg(
        CASE WHEN entry.choice->>'resource_pool' = 'live_totw_red'
             THEN jsonb_set(entry.choice, '{resource_pool}', '"totw_1_3_red"'::jsonb)
             ELSE entry.choice END ORDER BY entry.ordinal)
    FROM jsonb_array_elements(pack.player_picks) WITH ORDINALITY AS entry(choice, ordinal)
)
WHERE pack.player_picks @> '[{"resource_pool":"live_totw_red"}]'::jsonb;
SELECT set_config('ultimateteam.content_publish', '', true);

UPDATE ultimateteam.player_pick_items
SET resource_pool = 'totw_1_3_red'
WHERE resource_pool = 'live_totw_red';

INSERT INTO ultimateteam.schema_migrations(version) VALUES(85);
