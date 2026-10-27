-- FUT Champions red picks and store pack 60 (Python server 82ba3a3; maintainer decision 2026-09-26).
-- FIFA 21's own rarity table carries rarity 18 as the Team of the Week card recoloured red and marked untradeable (the FUT
-- Champions red pick), but no imported card uses it. As on the Python server, each player's highest-rated Team of the Week
-- card (rarity 3) and Team of the Season card (rarity 11) gets a red copy. GENERATED, NOT FIFA DATA: a copy's resource id
-- is the player's asset id plus revision 20 (from Team of the Week) or 21 (from Team of the Season) times 2^24, revisions
-- the catalogue does not use; its portrait stays on the source card (dynamic_image_resource_id), which EA's CDN serves.
-- Red picks are reward-only: packs and picks reach them only by naming rarity 18 (rules/packs.json rewardOnlyRareflags).
SET CONSTRAINTS ALL IMMEDIATE;

-- A pick token's own name when its pick group names it (pack 60).
ALTER TABLE ultimateteam.player_pick_items ADD COLUMN IF NOT EXISTS label text;
-- A definition and its player row are inserted by separate statements; the family checks run once both exist.
SET CONSTRAINTS ALL DEFERRED;

-- Replay-safe: an installation that already has the FUT Champions card types keeps its red cards as they are.
CREATE TEMP TABLE red_state ON COMMIT DROP AS
SELECT EXISTS (SELECT 1 FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
               WHERE r.native_rareflag = 18) AS installed;

INSERT INTO ultimateteam.native_rarities(native_rareflag, name) VALUES (18, 'FUT Champions') ON CONFLICT (native_rareflag) DO NOTHING;

-- One FUT Champions card type per level of the Team of the Week types, in the same rarity groups.
CREATE TEMP TABLE red_types ON COMMIT DROP AS
SELECT t.card_type_id AS source_type, t.card_level,
       nextval(pg_get_serial_sequence('ultimateteam.card_types', 'card_type_id')) AS red_type
FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
WHERE r.native_rareflag = 3 AND NOT (SELECT installed FROM red_state);
INSERT INTO ultimateteam.card_types
SELECT (jsonb_populate_record(NULL::ultimateteam.card_types, to_jsonb(t) || jsonb_build_object(
    'card_type_id', x.red_type, 'name', 'FUT Champions', 'is_special', true,
    'native_rarity_id', (SELECT native_rarity_id FROM ultimateteam.native_rarities WHERE native_rareflag = 18)))).*
FROM ultimateteam.card_types t JOIN red_types x ON x.source_type = t.card_type_id;
INSERT INTO ultimateteam.card_type_rarity_group_memberships
SELECT (jsonb_populate_record(NULL::ultimateteam.card_type_rarity_group_memberships, to_jsonb(m) || jsonb_build_object(
    'id', nextval(pg_get_serial_sequence('ultimateteam.card_type_rarity_group_memberships', 'id')), 'card_type_id', x.red_type))).*
FROM ultimateteam.card_type_rarity_group_memberships m JOIN red_types x ON x.source_type = m.card_type_id;

-- The source cards: per player (asset id) and rarity, the highest rating, then the highest resource id.
CREATE TEMP TABLE red_cards ON COMMIT DROP AS
SELECT s.*, nextval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')) AS red_definition,
       s.asset + (CASE s.rareflag WHEN 3 THEN 20 ELSE 21 END) * 16777216 AS red_resource,
       (SELECT x.red_type FROM red_types x WHERE x.card_level = s.card_level) AS red_type,
       row_number() OVER (ORDER BY s.rareflag, s.asset) AS ordinal
FROM (SELECT DISTINCT ON (d.native_asset_id, r.native_rareflag)
             d.definition_id AS source_definition, d.native_resource_id AS source_resource, d.native_asset_id AS asset,
             r.native_rareflag AS rareflag, t.card_level
      FROM ultimateteam.item_definitions d
      JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
      JOIN ultimateteam.card_types t ON t.card_type_id = p.card_type_id
      JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
      WHERE d.family_id = 1 AND d.game_year = 2021 AND r.native_rareflag IN (3, 11) AND d.native_asset_id > 0
        AND NOT (SELECT installed FROM red_state)
      ORDER BY d.native_asset_id, r.native_rareflag, p.rating DESC, d.native_resource_id DESC) s;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM red_cards WHERE red_type IS NULL) THEN
        RAISE EXCEPTION 'A red pick source card has a level without a FUT Champions card type';
    END IF;
    IF EXISTS (SELECT 1 FROM red_cards c JOIN ultimateteam.item_definitions d
               ON d.game_year = 2021 AND d.family_id = 1 AND d.native_resource_id = c.red_resource) THEN
        RAISE EXCEPTION 'A red pick resource id is already a catalogue card';
    END IF;
END $$;

INSERT INTO ultimateteam.item_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
    'definition_id', c.red_definition, 'native_resource_id', c.red_resource,
    'catalog_order', (SELECT max(catalog_order) FROM ultimateteam.item_definitions) + c.ordinal))).*
FROM red_cards c JOIN ultimateteam.item_definitions d ON d.definition_id = c.source_definition;
INSERT INTO ultimateteam.player_card_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p) || jsonb_build_object(
    'definition_id', c.red_definition, 'card_type_id', c.red_type, 'default_untradeable', true,
    'dynamic_image_resource_id', COALESCE(p.dynamic_image_resource_id, c.source_resource), 'replacement_resource_ids', NULL))).*
FROM red_cards c JOIN ultimateteam.player_card_definitions p ON p.definition_id = c.source_definition;
INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT c.red_definition, t.trait_id, t.source_native_player_id, t.source_sha256
FROM red_cards c JOIN ultimateteam.player_card_traits t ON t.definition_id = c.source_definition;

-- Store pack 60: one 84+ red pick (names and description are the maintainer's content, as they are).
INSERT INTO ultimateteam.store_packs(pack_id, kind, enabled, name, bio, price, points_price, size, gold_weight, silver_weight,
    bronze_weight, rare_slots, store_group, player_picks, always_present)
VALUES (60, 'store', true, 'FUT Champs Player Pick (1 of 3)',
    'Red picks are back. Choose 1 of 3 FUT Champions players rated 84 or higher - red Team of the Week and Team of the Season cards you cannot pull from any other pack. Untradeable.',
    1750000, 45000, 1, 100, 0, 0, 1, 'special',
    '[{"count": 1, "min_rating": 84, "choice_count": 3, "rareflags": [18], "label": "1 of 3 FUT Champs Player Pick"}]', true)
ON CONFLICT (pack_id) DO NOTHING;

SET CONSTRAINTS ALL IMMEDIATE;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(51);
