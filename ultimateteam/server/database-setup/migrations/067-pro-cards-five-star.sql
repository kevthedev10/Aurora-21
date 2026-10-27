-- Every developer's Pro card has 5-star weak foot and skill moves (owner, 2026-09-29), like the ones 066 created: 99
-- Anthony Martial (167983460, 3/4 from the Python server entry 056 copied) and 99 Allan Saint-Maximin (167988548, weak
-- foot 4) move up to 5/5. Martial also gets the card label its custom-players.json entry has always carried ("99 Anthony
-- Martial Pro Player"), which 056 left empty. Traits already match the owner's rule (every good trait plus Set Play
-- Specialist, no Injury Prone, each card's own CPU AI traits kept), so they do not change.
-- Earlier migrations in the same install transaction may leave deferred checks queued; ALTER TABLE needs them run first.
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
UPDATE ultimateteam.player_card_definitions p SET weak_foot = 5, skill_moves = 5,
    custom_card_label = COALESCE(p.custom_card_label, CASE d.native_resource_id WHEN 167983460 THEN '99 Anthony Martial Pro Player' END)
FROM ultimateteam.item_definitions d
WHERE d.definition_id = p.definition_id AND d.family_id = 1 AND d.native_resource_id IN (167983460, 167988548);
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(67);
