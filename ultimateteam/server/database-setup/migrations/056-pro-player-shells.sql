-- Three more Pro Player shells from the Python server, the maintainer's custom cards, like the Neymar Jr, Diogo Jota and
-- Allan Saint-Maximin shells already in the catalogue (custom-players.json): 99 Declan Rice (168006538, base card 234378) and
-- 99 Anthony Martial (167983460, base card 211300) as Aqua Pro shells (ab6b0eb, synced from the live VPS; rules/cards.json
-- aquaProCards), and the grant-only developer card 99 Paul Pogba (167968024, base card 195864; 8d674f7), whose portrait is
-- his 88 Team of the Week card (134413592) and which only an explicit grant hands out (rules/cards.json grantOnlyResourceIds).
-- Each is its base gold card with the existing shells' card type (Pro Player, rare flag 10), rating 99, every outfield
-- attribute and face stat 99, the Python entry's position, foot, weak foot, skill moves and work rates, untradeable.
SET CONSTRAINTS ALL DEFERRED;

CREATE TEMP TABLE pro_shells ON COMMIT DROP AS
SELECT v.resource, v.base_asset, v.name, v.position, v.foot, v.weak_foot, v.skill_moves, v.work_attack, v.work_defense, v.portrait,
       b.definition_id AS base_definition,
       (SELECT max(definition_id) FROM ultimateteam.item_definitions) + row_number() OVER (ORDER BY v.file_order) AS shell_definition
FROM (VALUES
    (1, 168006538::bigint, 234378::bigint, 'Rice', 'CDM', 'Right', 3::bigint, 2::bigint, 'Low', 'High', NULL::bigint),
    (2, 167983460, 211300, 'Martial', 'LM', 'Right', 3, 4, 'Medium', 'Medium', NULL),
    (3, 167968024, 195864, 'Pogba', 'CM', 'Right', 5, 5, 'High', 'Medium', 134413592)
) v(file_order, resource, base_asset, name, position, foot, weak_foot, skill_moves, work_attack, work_defense, portrait)
JOIN ultimateteam.item_definitions b ON b.family_id = 1 AND b.native_resource_id = v.base_asset
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions d WHERE d.family_id = 1 AND d.native_resource_id = v.resource);

INSERT INTO ultimateteam.item_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.item_definitions, to_jsonb(d) || jsonb_build_object(
    'definition_id', s.shell_definition, 'native_resource_id', s.resource, 'native_asset_id', s.base_asset,
    -- The catalogue lists custom cards in custom-players.json order: these follow the existing shells (the loader orders by
    -- catalog_order, then definition id, and these ids are the newest).
    'display_name', s.name, 'catalog_order', (SELECT max(o.catalog_order) FROM ultimateteam.item_definitions o
        WHERE o.family_id = 1 AND o.native_resource_id IN (167963031, 167996618, 167988548))))).*
FROM pro_shells s JOIN ultimateteam.item_definitions d ON d.definition_id = s.base_definition;

INSERT INTO ultimateteam.player_card_definitions
SELECT (jsonb_populate_record(NULL::ultimateteam.player_card_definitions, to_jsonb(p) || jsonb_build_object(
    'definition_id', s.shell_definition,
    -- Any existing shell's card type (they share one): migration 066 removes Neymar Jr, and a re-run must still find it.
    'card_type_id', (SELECT min(n.card_type_id) FROM ultimateteam.player_card_definitions n JOIN ultimateteam.item_definitions nd
                     ON nd.definition_id = n.definition_id WHERE nd.family_id = 1 AND nd.native_resource_id IN (167963031, 167996618, 167988548)),
    'rating', 99, 'position', s.position, 'preferred_foot', s.foot, 'weak_foot', s.weak_foot, 'skill_moves', s.skill_moves,
    'work_rate_attack', s.work_attack, 'work_rate_defense', s.work_defense, 'default_untradeable', true,
    'replacement_resource_ids', NULL, 'dynamic_image_resource_id', COALESCE(s.portrait, p.dynamic_image_resource_id),
    'face_slot_0', 99, 'face_slot_1', 99, 'face_slot_2', 99, 'face_slot_3', 99, 'face_slot_4', 99, 'face_slot_5', 99,
    'acceleration', 99, 'sprint_speed', 99, 'positioning', 99, 'finishing', 99, 'shot_power', 99, 'long_shots', 99,
    'volleys', 99, 'penalties', 99, 'vision', 99, 'crossing', 99, 'free_kick_accuracy', 99, 'short_passing', 99,
    'long_passing', 99, 'curve', 99, 'agility', 99, 'balance', 99, 'reactions', 99, 'ball_control', 99, 'dribbling', 99,
    'composure', 99, 'interceptions', 99, 'heading_accuracy', 99, 'marking', 99, 'standing_tackle', 99,
    'sliding_tackle', 99, 'jumping', 99, 'stamina', 99, 'strength', 99, 'aggression', 99))).*
FROM pro_shells s JOIN ultimateteam.player_card_definitions p ON p.definition_id = s.base_definition;

INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT s.shell_definition, t.trait_id, t.source_native_player_id, t.source_sha256
FROM pro_shells s JOIN ultimateteam.player_card_traits t ON t.definition_id = s.base_definition;

-- The six Pro cards carry every positive FIFA 21 trait (Power Free-Kick, Solid Player, Leadership, Early Crosser, Finesse
-- Shot, Flair, Power Header, Giant Throw-in, Outside Foot Shot, Team Player, Set Play Specialist; no Injury Prone), each
-- keeping its own CPU-AI bits (Python server 87de315, checked there card by card against FUTWIZ and FUTBIN). The masks are
-- that server's custom-players.json (git show 56145ed:.../custom-players.json SHA-256
-- 056332a6bafba932a55fc473f8272edae590c47f8d9328b0cac753f6be5ae017); every bit is a trait of ultimateteam.traits.
CREATE TEMP TABLE pro_traits ON COMMIT DROP AS
SELECT * FROM (VALUES
    (167963031::bigint, 84120522::bigint, 1::bigint),
    (167968024, 84127690, 1),
    (167983460, 117677002, 1),
    (167988548, 84122570, 1),
    (167996618, 17011658, 1),
    (168006538, 17007562, 1)
) v(resource, trait1, trait2);
DELETE FROM ultimateteam.player_card_traits t
USING ultimateteam.item_definitions d, pro_traits p
WHERE t.definition_id = d.definition_id AND d.family_id = 1 AND d.native_resource_id = p.resource;
INSERT INTO ultimateteam.player_card_traits(definition_id, trait_id, source_native_player_id, source_sha256)
SELECT d.definition_id, t.trait_id, d.native_asset_id, '056332a6bafba932a55fc473f8272edae590c47f8d9328b0cac753f6be5ae017'
FROM pro_traits p
JOIN ultimateteam.item_definitions d ON d.family_id = 1 AND d.native_resource_id = p.resource
JOIN ultimateteam.traits t ON ((CASE t.mask_word WHEN 1 THEN p.trait1 ELSE p.trait2 END) & t.bit_value) <> 0
ORDER BY d.definition_id, t.trait_id;

SET CONSTRAINTS ALL IMMEDIATE;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(56);
