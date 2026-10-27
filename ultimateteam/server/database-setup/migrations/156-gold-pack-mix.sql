-- Gold pack mixes (owner, 10-04). Live previews of 400 packs each: Premium Gold averaged 4.1 players in 12 (players 3-5,
-- consumables 3-5) and Jumbo Premium Gold 11.2 in 24, against 9-10 in 12 for silver packs. Premium Gold now holds 8-9
-- players, 2-3 consumables and at most one club item and one manager; Jumbo Premium Gold 10-15 players (13.2 on average),
-- 5-8 consumables, 2-4 club items and up to two managers. The Gold Pack (74) stays as it is.
SELECT set_config('ultimateteam.content_publish', 'on', true);

UPDATE ultimateteam.store_packs SET mixed_contents = v.contents
FROM (VALUES
    (75, '{"tier": "gold", "players": {"min": 8, "max": 9}, "consumables": {"min": 2, "max": 3}, "stadium": {"min": 0, "max": 1}, "staff": {"min": 0, "max": 1}}'::jsonb),
    (78, '{"tier": "gold", "players": {"min": 10, "max": 15}, "consumables": {"min": 5, "max": 8}, "stadium": {"min": 2, "max": 4}, "staff": {"min": 0, "max": 2}}'::jsonb)
) AS v(pack_id, contents)
WHERE store_packs.pack_id = v.pack_id AND store_packs.kind = 'store' AND store_packs.mixed_contents IS NOT NULL;

SELECT set_config('ultimateteam.content_publish', '', true);

-- A mixed pack now blends each slot kind's pools; refresh the drop table's explanatory text from the bundled file.
DELETE FROM ultimateteam.content_rules WHERE file = 'pack_odds' AND path = '["_nonPlayerDropsComment"]'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(156);
