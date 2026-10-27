-- The FUT Stadium tier cards become ownable definitions (10-03). Migration 053 left them out of the pool because every
-- club then had the shared default card 6200058; Stadium Development upgrades now hand the tier's own card over as an
-- item (objectives pay stadiumTier, PR #59), and owning, storing and equipping that card needs a catalogue definition.
-- Ratings and rare flags follow rules/club.json venues.futStadiumCards: 443 FUT Stadium 64 common,
-- 444 FUT Challenger Stadium 72 rare, 445 FUT Champions Stadium 83 rare. Replay-safe (each row lands only if missing).
SET CONSTRAINTS ALL IMMEDIATE;
SET CONSTRAINTS ALL DEFERRED;

-- Catalogue imports and the card editor can assign explicit definition IDs, leaving the
-- identity sequence behind existing rows. Serialize catalogue inserts while moving it
-- past those IDs; preserve a sequence already ahead (including IDs used by a failed run).
LOCK TABLE ultimateteam.item_definitions IN SHARE ROW EXCLUSIVE MODE;
SELECT setval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id'),
    GREATEST((SELECT max(definition_id) FROM ultimateteam.item_definitions),
             COALESCE(pg_sequence_last_value(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')::regclass), 0)));

CREATE TEMP TABLE tier_cards ON COMMIT DROP AS
WITH wanted(venue, rating, rareflag) AS (
    VALUES (443::bigint, 64::bigint, 0::bigint),
           (444, 72, 1),
           (445, 83, 1))
SELECT w.*, nextval(pg_get_serial_sequence('ultimateteam.item_definitions', 'definition_id')) AS definition_id,
       row_number() OVER (ORDER BY w.venue) AS ordinal
FROM wanted w
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions d
                  WHERE d.game_year = 2021 AND d.family_id = 4 AND d.native_resource_id = 6200000 + w.venue);

INSERT INTO ultimateteam.item_definitions(definition_id, family_id, game_year, native_resource_id, native_asset_id,
    native_card_db_id, native_card_asset_id, native_item_type, native_subtype, catalog_order, revision, status, display_name)
SELECT n.definition_id, 4, 2021, 6200000 + n.venue, n.venue, 6200000 + n.venue, 36, 'stadium', 10,
       (SELECT max(catalog_order) FROM ultimateteam.item_definitions) + n.ordinal, 1, 'draft', 'StadiumName_' || n.venue
FROM tier_cards n;
INSERT INTO ultimateteam.customisation_definitions(definition_id, customisation_kind, name, native_team_id, team_id,
    native_kit_type, native_category, rating, native_rareflag)
SELECT n.definition_id, 'stadium', 'StadiumName_' || n.venue, NULL, NULL, NULL, 4, n.rating, n.rareflag
FROM tier_cards n;

SET CONSTRAINTS ALL IMMEDIATE;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(139);
