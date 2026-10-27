-- Owned club cosmetics from the Python server (ca228bc, ceb35f0, e2e098f): stadium cards, club badges and match balls,
-- each a catalogue definition so a club can own them (the identity pack, pack 66) and equip them on Match Day.
-- Native conventions (Python server notes):
--   badges   6000000 + crest team id: CardsDLL 0x180381014 turns a badge id into CrestID by that subtraction; one per team
--            of the kit catalogue (every one has crest art); name TeamName_Abbr15_<team>.
--   balls    8120000 + ball id: FIFA 21's FUT ball table (cards_ng_db fcc_balls), value 60/70/90, rare 225-243;
--            name BallName_<ball>.
--   stadiums 6200000 + venue: the Match Day slot 5 card's assetId is the match venue (CardsDLL 0x1803c4000); the pool is
--            every venue with card art and an installed stadium, less the bare pitches and the FUT Stadium tiers (every
--            club has those as its default card); gold rare 83; name StadiumName_<venue>.
-- Ids already in the catalogue (the club defaults 6000057, 6200058, 8120212) keep their definitions.
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;

CREATE TEMP TABLE new_cosmetics ON COMMIT DROP AS
WITH wanted(kind, resource_id, asset_id, card_asset_id, item_type, subtype, category, team, venue, rating, rareflag, name) AS (
    SELECT 'custom', 6000000 + t.native_team_id, t.native_team_id, 39, 'custom', 11, 1, t.native_team_id, NULL::bigint, 83, 1,
           'TeamName_Abbr15_' || t.native_team_id
    FROM (SELECT DISTINCT native_team_id FROM ultimateteam.customisation_definitions
          WHERE customisation_kind = 'kit' AND native_team_id > 0) t
    UNION ALL
    SELECT 'ball', 8120000 + b.ball, b.ball, 37, 'ball', 30, 1, NULL, NULL, b.value, b.rare, 'BallName_' || b.ball
    FROM (VALUES
    (0, 60, 0),
    (1, 60, 0),
    (2, 60, 0),
    (3, 60, 0),
    (4, 60, 0),
    (5, 60, 0),
    (6, 60, 0),
    (7, 60, 0),
    (8, 60, 0),
    (9, 60, 0),
    (10, 60, 0),
    (11, 60, 0),
    (12, 70, 0),
    (13, 70, 0),
    (14, 70, 0),
    (15, 70, 0),
    (16, 70, 0),
    (17, 70, 0),
    (18, 70, 0),
    (19, 70, 0),
    (20, 70, 0),
    (21, 70, 0),
    (22, 70, 0),
    (33, 90, 0),
    (36, 90, 0),
    (38, 90, 0),
    (40, 90, 0),
    (43, 90, 0),
    (44, 90, 0),
    (46, 90, 0),
    (47, 90, 0),
    (51, 90, 0),
    (53, 90, 0),
    (54, 90, 0),
    (56, 90, 0),
    (59, 90, 0),
    (64, 90, 0),
    (65, 90, 0),
    (69, 90, 0),
    (71, 90, 0),
    (74, 90, 0),
    (75, 90, 0),
    (77, 90, 0),
    (81, 90, 0),
    (83, 90, 0),
    (84, 90, 0),
    (86, 90, 0),
    (87, 90, 0),
    (90, 90, 0),
    (91, 90, 0),
    (92, 90, 0),
    (93, 90, 0),
    (97, 90, 0),
    (100, 90, 0),
    (103, 90, 0),
    (107, 90, 0),
    (108, 90, 0),
    (109, 90, 0),
    (110, 90, 0),
    (113, 90, 0),
    (114, 90, 0),
    (115, 90, 0),
    (116, 90, 0),
    (117, 90, 0),
    (118, 90, 0),
    (119, 90, 0),
    (120, 90, 0),
    (122, 90, 0),
    (124, 90, 0),
    (125, 90, 0),
    (126, 90, 0),
    (128, 90, 0),
    (129, 90, 0),
    (130, 90, 0),
    (131, 90, 0),
    (132, 90, 0),
    (133, 90, 0),
    (135, 90, 0),
    (136, 90, 0),
    (137, 90, 0),
    (139, 90, 0),
    (141, 90, 0),
    (144, 90, 0),
    (145, 90, 0),
    (147, 90, 0),
    (148, 90, 0),
    (149, 90, 0),
    (150, 90, 0),
    (151, 90, 0),
    (152, 90, 0),
    (153, 90, 0),
    (157, 90, 0),
    (158, 90, 0),
    (159, 90, 0),
    (160, 90, 0),
    (161, 90, 0),
    (162, 90, 0),
    (163, 90, 0),
    (164, 90, 0),
    (225, 90, 1),
    (230, 90, 1),
    (231, 90, 1),
    (232, 90, 1),
    (233, 90, 1),
    (234, 90, 1),
    (235, 90, 1),
    (236, 90, 1),
    (237, 90, 1),
    (238, 90, 1),
    (239, 90, 1),
    (240, 90, 1),
    (241, 90, 1),
    (242, 90, 1),
    (243, 90, 1)) b(ball, value, rare)
    UNION ALL
    SELECT 'stadium', 6200000 + v.venue, v.venue, 36, 'stadium', 10, 4, NULL, v.venue, 83, 1, 'StadiumName_' || v.venue
    FROM unnest(ARRAY[2, 5, 9, 10, 13, 14, 15, 26, 28, 29, 30, 32, 33, 34, 35, 41, 100, 104, 112, 113, 115, 135, 137, 147, 149, 153, 155, 156, 157, 158, 172, 175, 176, 178, 179, 180, 181, 182, 183, 192, 193, 194, 195, 196, 197, 212, 228, 229, 246, 247, 248, 249, 260, 262, 264, 265, 326, 327, 328, 329, 330, 331, 332, 333, 334, 335, 336, 337, 341, 343, 344, 345, 347, 348, 349, 354, 355]::bigint[]) v(venue))
SELECT w.*, nextval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')) AS definition_id,
       row_number() OVER (ORDER BY w.kind, w.resource_id) AS ordinal
FROM wanted w
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions d
                  WHERE d.game_year = 2021 AND d.family_id = 4 AND d.native_resource_id = w.resource_id);

INSERT INTO ultimateteam.item_definitions(definition_id, family_id, game_year, native_resource_id, native_asset_id,
    native_card_db_id, native_card_asset_id, native_item_type, native_subtype, catalog_order, revision, status, display_name)
SELECT n.definition_id, 4, 2021, n.resource_id, n.asset_id, n.resource_id, n.card_asset_id, n.item_type, n.subtype,
       (SELECT max(catalog_order) FROM ultimateteam.item_definitions) + n.ordinal, 1, 'draft', n.name
FROM new_cosmetics n;
INSERT INTO ultimateteam.customisation_definitions(definition_id, customisation_kind, name, native_team_id, team_id,
    native_kit_type, native_category, rating, native_rareflag)
SELECT n.definition_id, n.kind, n.name, n.team, (SELECT t.team_id FROM ultimateteam.teams t WHERE t.native_team_id = n.team LIMIT 1),
       NULL, n.category, n.rating, n.rareflag
FROM new_cosmetics n;

SET CONSTRAINTS ALL IMMEDIATE;

-- Store pack 66, "Horizzon's Identity Crisis" (maintainer content, as it is): cosmetics the club does not own yet when
-- enough remain, by kind: {"balls": n, "badges": n, "kits": n, "stadiums": n}.
ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS identity_pack jsonb;
INSERT INTO ultimateteam.store_packs(pack_id, kind, enabled, name, bio, price, size, gold_weight, silver_weight, bronze_weight,
    rare_slots, store_group, identity_pack, always_present)
VALUES (66, 'store', true, 'Horizzon''s Identity Crisis',
    'Who are you, really? 2 match balls, 3 club badges, 4 kits and 3 stadiums for My Stadium Match Day.',
    10000, 12, 100, 0, 0, 0, 'gold', '{"balls": 2, "badges": 3, "kits": 4, "stadiums": 3}', true)
ON CONFLICT (pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(53);
