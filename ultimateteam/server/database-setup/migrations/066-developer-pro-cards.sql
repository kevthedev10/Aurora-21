-- The developers' Pro Player cards (owner, 2026-09-29): one exclusive 99 card per developer, handed out only by an
-- operator grant (rules/cards.json proCards; every Pro card is grant-only), one copy on the whole server.
--   Ghoul    99 Allan Saint-Maximin  167988548 (kept)
--   Mustafa  99 Diogo Jota           167996618 (kept)
--   Shadow   99 Anthony Martial      167983460 (kept)
--   Miz      99 Michael Olise        168019987 (new; base 247827, moved from Reading to FC Bayern Munchen, Bundesliga)
--   Horizzon 99 Bruno Fernandes      167984358 (new; base 212198, portrait his FIFA 21 Team of the Year card 201538790)
--   Atlaz    99 Bas Dost             167961228 (new; base 189068, moved from Eintracht Frankfurt to SC Heerenveen, Eredivisie)
-- Each new card is built like migration 056's shells: its base card with the Pro Player card type (rare flag 10), rating 99,
-- every outfield attribute and face stat 99, untradeable, 5-star weak foot and skill moves, and every positive trait plus its
-- base card's AI traits (as 056's cards carry). Ids follow 10 x 2^24 + the base asset id. Portraits are the DDS files in
-- runtime-source/ultimateteam21/custom-playerheads named by these ids.
-- The unused Pro cards go, with every copy clubs own (as migration 050 removed cards): 99 Neymar Jr 167963031, 99 Declan
-- Rice 168006538 and 99 Paul Pogba 167968024.
SET CONSTRAINTS ALL DEFERRED;

CREATE TEMP TABLE dev_pro_cards ON COMMIT DROP AS
SELECT v.resource, v.base_asset, v.name, v.label, v.position, v.foot, v.work_attack, v.work_defense, v.team, v.league, v.portrait,
       b.definition_id AS base_definition,
       (SELECT max(definition_id) FROM ultimateteam.item_definitions) + row_number() OVER (ORDER BY v.file_order) AS pro_definition
FROM (VALUES
    (1, 168019987::bigint, 247827::bigint, 'Olise', '99 Michael Olise Pro Player', 'LM', 'Left', 'Medium', 'Medium',
        (SELECT team_id FROM ultimateteam.teams WHERE native_team_id = 21), (SELECT league_id FROM ultimateteam.leagues WHERE native_league_id = 19),
        NULL::bigint),
    (2, 167984358, 212198, 'Bruno Fernandes', '99 Bruno Fernandes Pro Player', 'CAM', 'Right', 'High', 'High',
        NULL::bigint, NULL::bigint, 201538790),
    (3, 167961228, 189068, 'Dost', '99 Bas Dost Pro Player', 'ST', 'Right', 'Medium', 'Medium',
        (SELECT team_id FROM ultimateteam.teams WHERE native_team_id = 1913), (SELECT league_id FROM ultimateteam.leagues WHERE native_league_id = 10),
        NULL)
) v(file_order, resource, base_asset, name, label, position, foot, work_attack, work_defense, team, league, portrait)
JOIN ultimateteam.item_definitions b ON b.family_id = 1 AND b.game_year = 2021 AND b.native_resource_id = v.base_asset
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions d WHERE d.family_id = 1 AND d.native_resource_id = v.resource);

INSERT INTO ultimateteam.item_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
    'definition_id', s.pro_definition, 'native_resource_id', s.resource, 'native_asset_id', s.base_asset,
    -- Listed after the Pro cards already in the catalogue (the loader orders by catalog_order, then definition id).
    'display_name', s.name, 'catalog_order', (SELECT max(o.catalog_order) FROM ultimateteam.item_definitions o
        WHERE o.family_id = 1 AND o.native_resource_id IN (167996618, 167988548, 167983460))))).*
FROM dev_pro_cards s JOIN ultimateteam.item_definitions d ON d.definition_id = s.base_definition;

INSERT INTO ultimateteam.player_card_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p) || jsonb_build_object(
    'definition_id', s.pro_definition,
    'card_type_id', (SELECT n.card_type_id FROM ultimateteam.player_card_definitions n JOIN ultimateteam.item_definitions nd
                     ON nd.definition_id = n.definition_id WHERE nd.family_id = 1 AND nd.native_resource_id = 167983460),
    'team_id', COALESCE(s.team, p.team_id), 'league_id', COALESCE(s.league, p.league_id),
    'rating', 99, 'position', s.position, 'preferred_foot', s.foot, 'weak_foot', 5, 'skill_moves', 5,
    'work_rate_attack', s.work_attack, 'work_rate_defense', s.work_defense, 'default_untradeable', true,
    'custom_card_label', s.label, 'replacement_resource_ids', NULL,
    'dynamic_image_resource_id', COALESCE(s.portrait, p.dynamic_image_resource_id),
    'face_slot_0', 99, 'face_slot_1', 99, 'face_slot_2', 99, 'face_slot_3', 99, 'face_slot_4', 99, 'face_slot_5', 99,
    'acceleration', 99, 'sprint_speed', 99, 'positioning', 99, 'finishing', 99, 'shot_power', 99, 'long_shots', 99,
    'volleys', 99, 'penalties', 99, 'vision', 99, 'crossing', 99, 'free_kick_accuracy', 99, 'short_passing', 99,
    'long_passing', 99, 'curve', 99, 'agility', 99, 'balance', 99, 'reactions', 99, 'ball_control', 99, 'dribbling', 99,
    'composure', 99, 'interceptions', 99, 'heading_accuracy', 99, 'marking', 99, 'standing_tackle', 99,
    'sliding_tackle', 99, 'jumping', 99, 'stamina', 99, 'strength', 99, 'aggression', 99))).*
FROM dev_pro_cards s JOIN ultimateteam.player_card_definitions p ON p.definition_id = s.base_definition;

-- Traits: every positive FIFA 21 trait (the mask all Pro cards share, 17007562: Power Free Kick, Solid Player, Leadership,
-- Early Crosser, Finesse Shot, Flair, Power Header, Giant Throw-In, Outside Foot Shot, Team Player) plus the base card's AI
-- traits (Long Passer, Long Shot Taker, Speed Dribbler, Playmaker, Chip Shot, Technical Dribbler), and trait2 bit 1, as
-- migration 056 gives the other Pro cards. The source is this change's custom-players.json (SHA-256 below).
CREATE TEMP TABLE dev_pro_traits ON COMMIT DROP AS
SELECT * FROM (VALUES
    (168019987::bigint, 84125642::bigint, 1::bigint),
    (167984358, 17018826, 1),
    (167961228, 17007562, 1)
) v(resource, trait1, trait2);
INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT d.definition_id, t.trait_id, d.native_asset_id, 'd62912acb3a80a07c57d04228d35ef20c1b1194b2a73a388670e81a88dc62561'
FROM dev_pro_traits p
JOIN ultimateteam.item_definitions d ON d.family_id = 1 AND d.native_resource_id = p.resource
JOIN ultimateteam.traits t ON ((CASE t.mask_word WHEN 1 THEN p.trait1 ELSE p.trait2 END) & t.bit_value) <> 0
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.player_card_traits x WHERE x.definition_id = d.definition_id)
ORDER BY d.definition_id, t.trait_id;
SET CONSTRAINTS ALL IMMEDIATE;

-- The unused Pro cards, with every row that refers to one (migration 050's removal): squad slots and their roles, market
-- listings, player pick previews and choices, Draft runs, SBC squads, owned copies, then the catalogue rows.
CREATE TEMP TABLE dev_removed_definitions ON COMMIT DROP AS
SELECT definition_id FROM ultimateteam.item_definitions
WHERE family_id = 1 AND game_year = 2021 AND native_resource_id IN (167963031, 168006538, 167968024);

CREATE TEMP TABLE dev_removed_items ON COMMIT DROP AS
SELECT id, club_id, native_item_id FROM ultimateteam.items WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);

CREATE TEMP TABLE dev_removed_squad_players ON COMMIT DROP AS
SELECT id, squad_id, slot_index FROM ultimateteam.squad_players
WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions) OR item_id IN (SELECT id FROM dev_removed_items);
DELETE FROM ultimateteam.squad_roles WHERE squad_player_id IN (SELECT id FROM dev_removed_squad_players);
DELETE FROM ultimateteam.squad_players WHERE id IN (SELECT id FROM dev_removed_squad_players);
UPDATE ultimateteam.squads q SET wire_representation = jsonb_set(jsonb_set(q.wire_representation::jsonb,
        '{emptySlots}', COALESCE(q.wire_representation::jsonb -> 'emptySlots', '[]'::jsonb) || r.empties),
        '{slotExtras}', COALESCE(q.wire_representation::jsonb -> 'slotExtras', '{}'::jsonb) - r.slots)::json
FROM (SELECT p.squad_id,
          jsonb_agg(COALESCE(q2.wire_representation::jsonb -> 'slotExtras' -> p.slot_index::text, '{}'::jsonb)
              || jsonb_build_object('index', p.slot_index) ORDER BY p.slot_index) AS empties,
          array_agg(p.slot_index::text) AS slots
      FROM dev_removed_squad_players p JOIN ultimateteam.squads q2 ON q2.id = p.squad_id GROUP BY p.squad_id) r
WHERE q.id = r.squad_id AND q.wire_representation IS NOT NULL;

DELETE FROM ultimateteam.market_listings WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);

UPDATE ultimateteam.player_pick_items SET preview_definition_id = NULL
WHERE preview_definition_id IN (SELECT definition_id FROM dev_removed_definitions);
CREATE TEMP TABLE dev_redrawn_picks ON COMMIT DROP AS
SELECT DISTINCT token_item_id FROM ultimateteam.player_pick_choices WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);
UPDATE ultimateteam.player_pick_items SET selected_choice_id = NULL, state = 'unrevealed' WHERE item_id IN (SELECT token_item_id FROM dev_redrawn_picks);
DELETE FROM ultimateteam.player_pick_choices WHERE token_item_id IN (SELECT token_item_id FROM dev_redrawn_picks);

DELETE FROM ultimateteam.draft_runs WHERE id IN (
    SELECT run_id FROM ultimateteam.draft_picks WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions)
    UNION SELECT run_id FROM ultimateteam.draft_choices WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions));

DELETE FROM ultimateteam.sbc_squad_items s USING dev_removed_items r WHERE s.club_id = r.club_id AND s.native_item_id = r.native_item_id;
UPDATE ultimateteam.sbc_squad_counts c SET placed = (SELECT count(*) FROM ultimateteam.sbc_squad_items s
    WHERE s.club_id = c.club_id AND s.challenge_id = c.challenge_id)
WHERE c.club_id IN (SELECT club_id FROM dev_removed_items);

DELETE FROM ultimateteam.items WHERE id IN (SELECT id FROM dev_removed_items);
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.promo_cards DISABLE TRIGGER lfv4_trg_r2_promo_cards;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.item_definitions DISABLE TRIGGER lfv4_trg_r2_item_definitions;
DELETE FROM ultimateteam.promo_cards WHERE player_card_definition_id IN (SELECT definition_id FROM dev_removed_definitions);
DELETE FROM ultimateteam.player_card_traits WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);
DELETE FROM ultimateteam.player_card_definitions WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);
DELETE FROM ultimateteam.item_definitions WHERE definition_id IN (SELECT definition_id FROM dev_removed_definitions);
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.item_definitions ENABLE TRIGGER lfv4_trg_r2_item_definitions;
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.promo_cards ENABLE TRIGGER lfv4_trg_r2_promo_cards;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(66);
