-- The earlier Taison and Promes UEL RTTF definitions duplicate the later cards.
-- Maintainer confirmed the surviving native resource IDs on 2026-09-29. Keep owned items,
-- squads, picks, drafts, market listings and promo groupings by moving their references.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TEMP TABLE rttf_replacements(old_definition bigint PRIMARY KEY, new_definition bigint NOT NULL,
    old_resource bigint NOT NULL, new_resource bigint NOT NULL) ON COMMIT DROP;
INSERT INTO rttf_replacements VALUES
    (20381, 21475, 33743235, 67297667),
    (20520, 21561, 33763240, 67317672);

DO $$ BEGIN
    -- An already-upgraded database can replay this migration if its ledger was restored to an older version.
    -- The old definitions may be gone, but any still present must retain their verified identities.
    IF (SELECT count(*) FROM rttf_replacements m
        JOIN ultimateteam.item_definitions new_d ON new_d.definition_id=m.new_definition AND new_d.native_resource_id=m.new_resource
        JOIN ultimateteam.player_card_definitions new_p ON new_p.definition_id=m.new_definition) <> 2
       OR (SELECT count(*) FROM rttf_replacements m JOIN ultimateteam.item_definitions old_d
           ON old_d.definition_id=m.old_definition) <>
          (SELECT count(*) FROM rttf_replacements m
           JOIN ultimateteam.item_definitions old_d ON old_d.definition_id=m.old_definition AND old_d.native_resource_id=m.old_resource
           JOIN ultimateteam.player_card_definitions old_p ON old_p.definition_id=m.old_definition
           JOIN ultimateteam.player_card_definitions new_p ON new_p.definition_id=m.new_definition
           WHERE old_p.footballer_id=new_p.footballer_id AND old_p.rating=new_p.rating AND old_p.position=new_p.position
             AND old_p.team_id IS NOT DISTINCT FROM new_p.team_id) THEN
        RAISE EXCEPTION 'RTTF duplicate identities changed; refusing automatic replacement';
    END IF;
END $$;

SET CONSTRAINTS ALL DEFERRED;
ALTER TABLE ultimateteam.items DISABLE TRIGGER lfv4_trg_r1_items;
ALTER TABLE ultimateteam.market_listings DISABLE TRIGGER lfv4_trg_r1_market_listings;
UPDATE ultimateteam.items i SET definition_id=m.new_definition
FROM rttf_replacements m WHERE i.definition_id=m.old_definition;
UPDATE ultimateteam.squad_players s SET definition_id=m.new_definition
FROM rttf_replacements m WHERE s.definition_id=m.old_definition;
UPDATE ultimateteam.player_pick_items p SET preview_definition_id=m.new_definition
FROM rttf_replacements m WHERE p.preview_definition_id=m.old_definition;
UPDATE ultimateteam.player_pick_choices p SET definition_id=m.new_definition
FROM rttf_replacements m WHERE p.definition_id=m.old_definition;
UPDATE ultimateteam.draft_picks p SET definition_id=m.new_definition
FROM rttf_replacements m WHERE p.definition_id=m.old_definition;
UPDATE ultimateteam.draft_choices c SET definition_id=m.new_definition
FROM rttf_replacements m WHERE c.definition_id=m.old_definition;

-- A promo may already contain the surviving definition. Keep that association and its override.
DELETE FROM ultimateteam.promo_cards old_p USING rttf_replacements m
WHERE old_p.player_card_definition_id=m.old_definition AND EXISTS (
    SELECT 1 FROM ultimateteam.promo_cards new_p WHERE new_p.promo_id=old_p.promo_id
    AND new_p.player_card_definition_id=m.new_definition);
UPDATE ultimateteam.promo_cards p SET player_card_definition_id=m.new_definition
FROM rttf_replacements m WHERE p.player_card_definition_id=m.old_definition;

UPDATE ultimateteam.market_listings l SET definition_id=m.new_definition,
    resource_id=m.new_resource, rareflag=r.native_rareflag
FROM rttf_replacements m
JOIN ultimateteam.player_card_definitions p ON p.definition_id=m.new_definition
JOIN ultimateteam.card_types t ON t.card_type_id=p.card_type_id
JOIN ultimateteam.native_rarities r ON r.native_rarity_id=t.native_rarity_id
WHERE l.definition_id=m.old_definition;
UPDATE ultimateteam.market_item_snapshots s SET definition_id=m.new_definition,
    definition_revision=d.revision
FROM rttf_replacements m JOIN ultimateteam.item_definitions d ON d.definition_id=m.new_definition
WHERE s.definition_id=m.old_definition;

-- The club search projection does not refresh on item definition changes.
UPDATE ultimateteam.club_player_search s SET resource_id=m.new_resource,
    native_rareflag=r.native_rareflag
FROM rttf_replacements m JOIN ultimateteam.player_card_definitions p ON p.definition_id=m.new_definition
JOIN ultimateteam.card_types t ON t.card_type_id=p.card_type_id
JOIN ultimateteam.native_rarities r ON r.native_rarity_id=t.native_rarity_id
JOIN ultimateteam.items i ON i.definition_id=m.new_definition
WHERE i.id=s.item_id AND s.resource_id=m.old_resource;

SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.items ENABLE TRIGGER lfv4_trg_r1_items;
ALTER TABLE ultimateteam.market_listings ENABLE TRIGGER lfv4_trg_r1_market_listings;

SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.item_definitions DISABLE TRIGGER lfv4_trg_r2_item_definitions;
DELETE FROM ultimateteam.player_card_traits WHERE definition_id IN (SELECT old_definition FROM rttf_replacements);
DELETE FROM ultimateteam.player_card_definitions WHERE definition_id IN (SELECT old_definition FROM rttf_replacements);
DELETE FROM ultimateteam.item_definitions WHERE definition_id IN (SELECT old_definition FROM rttf_replacements);
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.item_definitions ENABLE TRIGGER lfv4_trg_r2_item_definitions;

SET CONSTRAINTS ALL IMMEDIATE;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(75);
