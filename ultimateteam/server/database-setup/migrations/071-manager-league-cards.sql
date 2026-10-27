-- Manager league cards (pack-odds-2026-09/PLAN.md v2 section 3.8; owner 2026-09-29): the FUT consumable applied to a manager
-- to change his league for chemistry. One card per league whose players have base cards (rareflag 0 or 1), whether or not
-- FIFA 21 has managers of its own there: Ligue 1 and the Bundesliga have none in the catalogue, yet a manager moved there
-- links their players and their LEAGUE_* SBCs pay the card. The Icons pseudo-league and the CONMEBOL competitions are not
-- leagues a manager can join.
--   * GENERATED, NOT FIFA DATA: FIFA 21 keeps no consumables table of its own (EA's server sent these cards), and no retail
--     record of one was captured, so a card's resource id is 5,100,000 plus the native league id (rules/cards.json
--     managerLeagueCards.resourceIdBase), a range no FIFA 21 definition uses. The card itself follows CardsDLL: card asset
--     32 (FUT_CONSUMABLE_NAME_MANAGERLEAGUE, counted as managerLeagueModifier), consumable type 5, and its league as the card
--     subtype (native_subtype, consumable_definitions.native_effect_id), which the apply screen shows as LAbbr_<league>.
--   * store_packs.consumable_mix: a random consumables pack's count of each kind, {"manager_league": n,
--     "position_modifier": n, "chemistry_style": n}; NULL keeps the mixed draw of random_consumable_count.
--   * store_packs.manager_league_card_share: the share of a player pack's manager slot that is a manager league card instead
--     of a manager (bronze and silver store packs, deploy/launch/phase1-store.sql); NULL is none.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_consumable_mix;
ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_manager_league_card_share;
ALTER TABLE ultimateteam.store_packs
    ADD COLUMN IF NOT EXISTS consumable_mix jsonb,
    ADD COLUMN IF NOT EXISTS manager_league_card_share numeric,
    ADD CONSTRAINT lfv4_ck_store_packs_consumable_mix CHECK (consumable_mix IS NULL OR jsonb_typeof(consumable_mix) = 'object'),
    ADD CONSTRAINT lfv4_ck_store_packs_manager_league_card_share
        CHECK (manager_league_card_share IS NULL OR (manager_league_card_share >= 0 AND manager_league_card_share <= 1));

-- A definition and its consumable row are inserted by separate statements; the family checks run once both exist.
SET CONSTRAINTS ALL DEFERRED;

-- 066 and 068 inserted definitions above the identity sequence; move it past them first.
SELECT setval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id'),
    GREATEST((SELECT max(definition_id) FROM ultimateteam.item_definitions),
             COALESCE(pg_sequence_last_value(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')::regclass), 0)));

-- Replay-safe: a league that already has its card keeps it.
CREATE TEMP TABLE manager_league_cards ON COMMIT DROP AS
SELECT l.native_league_id AS league, l.name, 5100000 + l.native_league_id AS resource_id,
       nextval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')) AS definition_id,
       row_number() OVER (ORDER BY l.native_league_id) AS ordinal
FROM ultimateteam.leagues l
WHERE l.native_league_id NOT IN (1003, 1014, 2118)
  AND EXISTS (SELECT 1 FROM ultimateteam.player_card_definitions p
              JOIN ultimateteam.card_types t ON t.card_type_id = p.card_type_id
              JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id
              WHERE p.league_id = l.league_id AND r.native_rareflag IN (0, 1))
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.consumable_definitions c
                  WHERE c.consumable_kind = 'manager_league' AND c.native_effect_id = l.native_league_id);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ultimateteam.item_definitions d JOIN manager_league_cards c ON c.resource_id = d.native_resource_id) THEN
        RAISE EXCEPTION 'manager league cards: a generated resource id is already a definition';
    END IF;
END
$$;

INSERT INTO ultimateteam.item_definitions(definition_id, family_id, game_year, native_resource_id, native_asset_id,
    native_card_db_id, native_card_asset_id, native_item_type, native_subtype, catalog_order, revision, status, display_name)
SELECT c.definition_id, 3, 2021, c.resource_id, c.resource_id, c.resource_id, 32, 'training', c.league,
       (SELECT max(catalog_order) FROM ultimateteam.item_definitions) + c.ordinal, 1, 'draft', c.name
FROM manager_league_cards c;
INSERT INTO ultimateteam.consumable_definitions(definition_id, consumable_kind, rating, native_consumable_type, native_effect_id,
    source_position, target_position, goalkeeper_only)
SELECT c.definition_id, 'manager_league', 85, 5, c.league, NULL, NULL, NULL
FROM manager_league_cards c;

SET CONSTRAINTS ALL IMMEDIATE;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(71);
