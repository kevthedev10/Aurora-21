-- Removes three custom cards that never existed in FIFA 21: neither FUTWIZ nor FUTBIN lists them (Python server 0ea58e4,
-- which removed eight such cards from custom-players.json; these three are the ones this catalogue carried):
--   50522519 Neymar Jr, 50527512 and 83886121 (custom-players.json).
-- Maintainer decision 2026-09-26: remove them, including copies clubs own. Every row that refers to one goes with it:
-- squad slots (with their captain and kick-taker roles), market listings, player pick previews and choices (a pick
-- that offered one is drawn again), Draft runs that picked or were offered one, SBC squads, promo lists and traits.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TEMP TABLE removed_definitions ON COMMIT DROP AS
SELECT definition_id FROM ultimateteam.item_definitions
WHERE family_id = 1 AND game_year = 2021 AND native_resource_id IN (50522519, 50527512, 83886121);

CREATE TEMP TABLE removed_items ON COMMIT DROP AS
SELECT id, club_id, native_item_id FROM ultimateteam.items WHERE definition_id IN (SELECT definition_id FROM removed_definitions);

-- Squads: the slots holding one (an owned copy or a concept card), and the roles (captain, kick takers) of those slots.
CREATE TEMP TABLE removed_squad_players ON COMMIT DROP AS
SELECT id, squad_id, slot_index FROM ultimateteam.squad_players
WHERE definition_id IN (SELECT definition_id FROM removed_definitions) OR item_id IN (SELECT id FROM removed_items);
DELETE FROM ultimateteam.squad_roles WHERE squad_player_id IN (SELECT id FROM removed_squad_players);
DELETE FROM ultimateteam.squad_players WHERE id IN (SELECT id FROM removed_squad_players);
-- Each emptied slot becomes an empty slot of its squad, as a save of the squad without that card records it: the slot's
-- extra fields and its index, no item (RelationalSquadCompatibilityStore's shape: emptySlots, slotExtras, slotOrder).
UPDATE ultimateteam.squads q SET wire_representation = jsonb_set(jsonb_set(q.wire_representation::jsonb,
        '{emptySlots}', COALESCE(q.wire_representation::jsonb -> 'emptySlots', '[]'::jsonb) || r.empties),
        '{slotExtras}', COALESCE(q.wire_representation::jsonb -> 'slotExtras', '{}'::jsonb) - r.slots)::json
FROM (SELECT p.squad_id,
          jsonb_agg(COALESCE(q2.wire_representation::jsonb -> 'slotExtras' -> p.slot_index::text, '{}'::jsonb)
              || jsonb_build_object('index', p.slot_index) ORDER BY p.slot_index) AS empties,
          array_agg(p.slot_index::text) AS slots
      FROM removed_squad_players p JOIN ultimateteam.squads q2 ON q2.id = p.squad_id GROUP BY p.squad_id) r
WHERE q.id = r.squad_id AND q.wire_representation IS NOT NULL;

-- Market: listings of one (their snapshots and watch entries cascade).
DELETE FROM ultimateteam.market_listings WHERE definition_id IN (SELECT definition_id FROM removed_definitions);

-- Player picks: no preview shows one, and a pick that offered one is drawn again.
UPDATE ultimateteam.player_pick_items SET preview_definition_id = NULL
WHERE preview_definition_id IN (SELECT definition_id FROM removed_definitions);
CREATE TEMP TABLE redrawn_picks ON COMMIT DROP AS
SELECT DISTINCT token_item_id FROM ultimateteam.player_pick_choices WHERE definition_id IN (SELECT definition_id FROM removed_definitions);
UPDATE ultimateteam.player_pick_items SET selected_choice_id = NULL, state = 'unrevealed' WHERE item_id IN (SELECT token_item_id FROM redrawn_picks);
DELETE FROM ultimateteam.player_pick_choices WHERE token_item_id IN (SELECT token_item_id FROM redrawn_picks);

-- Draft: a run that picked or was offered one ends (its picks and offers cascade).
DELETE FROM ultimateteam.draft_runs WHERE id IN (
    SELECT run_id FROM ultimateteam.draft_picks WHERE definition_id IN (SELECT definition_id FROM removed_definitions)
    UNION SELECT run_id FROM ultimateteam.draft_choices WHERE definition_id IN (SELECT definition_id FROM removed_definitions));

-- SBC squads name owned copies by native item id; the saved count follows.
DELETE FROM ultimateteam.sbc_squad_items s USING removed_items r WHERE s.club_id = r.club_id AND s.native_item_id = r.native_item_id;
UPDATE ultimateteam.sbc_squad_counts c SET placed = (SELECT count(*) FROM ultimateteam.sbc_squad_items s
    WHERE s.club_id = c.club_id AND s.challenge_id = c.challenge_id)
WHERE c.club_id IN (SELECT club_id FROM removed_items);

-- Owned copies (their player state, stats, extensions and search rows cascade).
DELETE FROM ultimateteam.items WHERE id IN (SELECT id FROM removed_items);
SET CONSTRAINTS ALL IMMEDIATE;

-- The catalogue rows. The catalogue is immutable in normal operation; this removal is the one exception.
ALTER TABLE ultimateteam.promo_cards DISABLE TRIGGER lfv4_trg_r2_promo_cards;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.item_definitions DISABLE TRIGGER lfv4_trg_r2_item_definitions;
DELETE FROM ultimateteam.promo_cards WHERE player_card_definition_id IN (SELECT definition_id FROM removed_definitions);
DELETE FROM ultimateteam.player_card_traits WHERE definition_id IN (SELECT definition_id FROM removed_definitions);
DELETE FROM ultimateteam.player_card_definitions WHERE definition_id IN (SELECT definition_id FROM removed_definitions);
DELETE FROM ultimateteam.item_definitions WHERE definition_id IN (SELECT definition_id FROM removed_definitions);
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.item_definitions ENABLE TRIGGER lfv4_trg_r2_item_definitions;
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.promo_cards ENABLE TRIGGER lfv4_trg_r2_promo_cards;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(50);
