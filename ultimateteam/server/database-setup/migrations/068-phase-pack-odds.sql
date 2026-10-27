-- Phase-1 pack odds (pack-odds-2026-09/PLAN.md v2 section 3; owner decisions 2026-09-29). A pack marked banded_odds draws
-- only the phase pool of rules/pack_odds.json (base cards without the UEFA / Libertadores / Sudamericana ones, and the live
-- Team of the Week in-forms), its gold rare slots by rating band. The reward player packs and the 82+-88+ picks become banded;
-- store packs stay as they are (the store is operator data: deploy/launch/phase1-store.sql sets the hosted store).
--   * 540 Premium TOTW becomes 3 in-forms + 9 gold (was 3 cards), 541 Ultimate TOTW 11 in-forms + 1 gold (was 11 cards).
--   * 405 Ultimate Pack is no longer a copy of 404: 3 of its 30 rares are rated 84 or more (rated_rare_slots).
--   * New reward packs 542 TOTW Player (one in-form) and 543 Small Rare Gold Players (6 rares).
--   * Reward packs roll an in-form in 1.1% of rare slots (was 10%), and the elite-rating boost is off.
--   * Red copies of the phase's Team of the Week cards: migration 051 copied each player's highest Team of the Week card, so
--     a live week's red pick handed out a later, higher card. GENERATED, NOT FIFA DATA: a copy of each phase card whose 051
--     copy has another rating, at resource id = asset id + revision 22 x 2^24 (rules/pack_odds.json phase.redRevisions).
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_rated_rare_slots;
ALTER TABLE ultimateteam.store_packs
    -- Draw from the phase pool by rating band (rules/pack_odds.json); NULL or false keeps the unbanded draw.
    ADD COLUMN IF NOT EXISTS banded_odds boolean,
    -- Rare slots with a rating floor, after the guaranteed specials: [{count, min_rating}].
    ADD COLUMN IF NOT EXISTS rated_rare_slots jsonb,
    -- A named gold rare band table of rules/pack_odds.json goldRareBandTables; NULL draws by goldRareBands.
    ADD COLUMN IF NOT EXISTS odds_table text,
    ADD CONSTRAINT lfv4_ck_store_packs_rated_rare_slots CHECK (rated_rare_slots IS NULL OR jsonb_typeof(rated_rare_slots) = 'array');

UPDATE ultimateteam.store_pack_details SET reward_pack_special_chance = 0.011, elite_per_rating = 0;

UPDATE ultimateteam.store_packs SET banded_odds = true
WHERE kind = 'reward' AND (pack_id BETWEEN 100 AND 406 OR pack_id IN (513, 514, 515, 516, 518, 534, 540, 541));

UPDATE ultimateteam.store_packs SET size = 12, rare_slots = 3, guaranteed_specials = 3 WHERE pack_id = 540;
UPDATE ultimateteam.store_packs SET size = 12, rare_slots = 11, guaranteed_specials = 11 WHERE pack_id = 541;
UPDATE ultimateteam.store_packs SET rated_rare_slots = '[{"count": 3, "min_rating": 84}]' WHERE pack_id = 405;

INSERT INTO ultimateteam.store_packs(pack_id, kind, name, size, gold_weight, silver_weight, bronze_weight, rare_slots,
    guaranteed_specials, banded_odds, always_present, display_only)
VALUES
(542, 'reward', 'TOTW Player Pack', 1, 100, NULL, NULL, 1, 1, true, false, false),
(543, 'reward', 'Small Rare Gold Players Pack', 6, 100, NULL, NULL, 6, NULL, true, false, false)
ON CONFLICT (pack_id) DO NOTHING;

-- Red copies of the phase cards (rules/sqbt_totw.json weeks 1-3). A definition and its player row are inserted by separate
-- statements; the family checks run once both exist.
SET CONSTRAINTS ALL DEFERRED;
CREATE TEMP TABLE phase_red_cards ON COMMIT DROP AS
SELECT s.*, (SELECT max(definition_id) FROM ultimateteam.item_definitions) + row_number() OVER (ORDER BY s.asset) AS red_definition,
       s.asset + 22 * 16777216 AS red_resource,
       (SELECT t.card_type_id FROM ultimateteam.card_types t JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
        WHERE r.native_rareflag = 18 AND t.card_level = s.card_level ORDER BY t.card_type_id LIMIT 1) AS red_type,
       row_number() OVER (ORDER BY s.asset) AS ordinal
FROM (SELECT d.definition_id AS source_definition, d.native_resource_id AS source_resource, d.native_asset_id AS asset,
             p.rating, t.card_level
      FROM ultimateteam.item_definitions d
      JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id
      JOIN ultimateteam.card_types t ON t.card_type_id = p.card_type_id
      WHERE d.family_id = 1 AND d.game_year = 2021 AND d.native_asset_id > 0
        AND d.native_resource_id IN (
            50546871, 50561206, 50503439, 50552218, 50331689, 50543846, 50474724, 50553640, 50540478, 67285444, 50531099,
            16966269, 17001587, 17009360, 17030220, 16939496, 16982647, 17012640, 17028508, 16979892, 17006077, 16932887,
            16925581, 67300983, 67338848, 67319529, 67341520, 50548036, 50538165, 50562415, 50376834, 84125165, 67297409,
            50533774, 16975926, 16954849, 16998390, 16971549, 17014351, 16998913, 16994412, 16983111, 17016241, 16997235,
            16966293, 17020379, 16962338, 16979138, 16965593, 16933078, 16997005, 33764421, 16967676, 16999881, 17001064,
            16979158, 16970381, 17018311, 16955725, 16989283, 16980515, 17014455, 16961700, 16985381, 16955138, 16990836,
            16989104, 16989899, 17028477)
        -- Only where migration 051's copy of this player is another card (or missing), and not already made.
        AND NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions red
                        JOIN ultimateteam.player_card_definitions rp ON rp.definition_id = red.definition_id
                        WHERE red.game_year = 2021 AND red.family_id = 1 AND red.native_resource_id = d.native_asset_id + 20 * 16777216
                          AND rp.rating = p.rating)
        AND NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions red
                        WHERE red.game_year = 2021 AND red.family_id = 1 AND red.native_resource_id = d.native_asset_id + 22 * 16777216)
        -- A catalogue without the FUT Champions card types (051) has no red picks to fix.
        AND EXISTS (SELECT 1 FROM ultimateteam.native_rarities WHERE native_rareflag = 18)) s;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM phase_red_cards WHERE red_type IS NULL) THEN
        RAISE EXCEPTION 'A phase red pick source card has a level without a FUT Champions card type';
    END IF;
    IF (SELECT count(DISTINCT asset) FROM phase_red_cards) <> (SELECT count(*) FROM phase_red_cards) THEN
        RAISE EXCEPTION 'Two phase Team of the Week cards of one player need a red copy; one revision cannot hold both';
    END IF;
END $$;

INSERT INTO ultimateteam.item_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
    'definition_id', c.red_definition, 'native_resource_id', c.red_resource,
    'catalog_order', (SELECT max(catalog_order) FROM ultimateteam.item_definitions) + c.ordinal))).*
FROM phase_red_cards c JOIN ultimateteam.item_definitions d ON d.definition_id = c.source_definition;
INSERT INTO ultimateteam.player_card_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p) || jsonb_build_object(
    'definition_id', c.red_definition, 'card_type_id', c.red_type, 'default_untradeable', true,
    'dynamic_image_resource_id', COALESCE(p.dynamic_image_resource_id, c.source_resource), 'replacement_resource_ids', NULL))).*
FROM phase_red_cards c JOIN ultimateteam.player_card_definitions p ON p.definition_id = c.source_definition;
INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT c.red_definition, t.trait_id, t.source_native_player_id, t.source_sha256
FROM phase_red_cards c JOIN ultimateteam.player_card_traits t ON t.definition_id = c.source_definition;

SET CONSTRAINTS ALL IMMEDIATE;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(68);
