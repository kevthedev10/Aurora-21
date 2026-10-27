-- Chemistry styles and position modifiers were built untradeable by their item templates, so every
-- stack ever opened kept untradeable = true and a full untradeable count, and the transfer market
-- refused them. The templates now build them tradeable like retail pack consumables; this lifts the
-- stored restriction from stacks already owned. Manager league cards keep whatever restriction they
-- carry: theirs is a per-grant decision (rules/tradeability.json), not a template default.
UPDATE ultimateteam.items AS i
SET untradeable = false
FROM ultimateteam.consumable_definitions c
WHERE i.definition_id = c.definition_id
    AND c.consumable_kind IN ('chemistry_style', 'position_modifier')
    AND i.untradeable;

UPDATE ultimateteam.consumable_items AS s
SET untradeable_quantity = 0
FROM ultimateteam.items i
JOIN ultimateteam.consumable_definitions c ON c.definition_id = i.definition_id
WHERE s.item_id = i.id
    AND c.consumable_kind IN ('chemistry_style', 'position_modifier')
    AND s.untradeable_quantity > 0;

-- The market ownership and snapshot invariants assumed every listing is a player card: they required
-- a player_items row for the seller item, a player snapshot for every listing and checked the
-- listing's denormalised card metadata against player_card_definitions. A consumable stack lists
-- whole, keeps no player snapshot and carries only its rating, so those checks learn the second
-- family. The bodies follow migration 013's shape with migration 033's market_listing_id naming.
CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r8_listing() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
DECLARE
    v_id bigint;
    v_row ultimateteam.market_listings%ROWTYPE;
BEGIN
        IF TG_OP = 'DELETE' THEN v_id := OLD.market_listing_id; ELSE v_id := NEW.market_listing_id; END IF;
    SELECT * INTO v_row FROM ultimateteam.market_listings WHERE market_listing_id = v_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF v_row.kind = 'player' AND v_row.state = 'active' AND NOT EXISTS (
        SELECT 1
        FROM ultimateteam.items i
        JOIN ultimateteam.player_items pi ON pi.item_id = i.id
        WHERE i.club_id = v_row.seller_club_id
          AND i.native_item_id = v_row.seller_item_id
          AND i.definition_id = v_row.definition_id
        FOR KEY SHARE
    ) AND NOT EXISTS (
        -- A consumable stack lists whole: the seller owns the item with its stack, not a player card row.
        SELECT 1
        FROM ultimateteam.items i
        JOIN ultimateteam.consumable_items ci ON ci.item_id = i.id
        WHERE i.club_id = v_row.seller_club_id
          AND i.native_item_id = v_row.seller_item_id
          AND i.definition_id = v_row.definition_id
        FOR KEY SHARE
    ) THEN
        PERFORM ultimateteam.lfv4_err('active market listing ' || v_row.market_listing_id || ' has no matching owned seller item');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM ultimateteam.market_item_snapshots s WHERE s.market_listing_id = v_row.market_listing_id) THEN
        PERFORM ultimateteam.lfv4_err('market listing ' || v_row.market_listing_id || ' has no item snapshot');
    END IF;
    IF EXISTS (SELECT 1 FROM ultimateteam.player_card_definitions pcd WHERE pcd.definition_id = v_row.definition_id)
        AND NOT EXISTS (SELECT 1 FROM ultimateteam.market_player_snapshots s WHERE s.market_listing_id = v_row.market_listing_id) THEN
        PERFORM ultimateteam.lfv4_err('market listing ' || v_row.market_listing_id || ' has no player snapshot');
    END IF;
    RETURN NULL; END
$$;

CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r8_item_snapshot() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
DECLARE
    v_id bigint;
    v_snap ultimateteam.market_item_snapshots%ROWTYPE;
    v_listing ultimateteam.market_listings%ROWTYPE;
BEGIN
        IF TG_OP = 'DELETE' THEN v_id := OLD.market_listing_id; ELSE v_id := NEW.market_listing_id; END IF;
    SELECT * INTO v_snap FROM ultimateteam.market_item_snapshots WHERE market_listing_id = v_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO v_listing FROM ultimateteam.market_listings WHERE market_listing_id = v_snap.market_listing_id FOR KEY SHARE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM ultimateteam.item_definitions d
        JOIN ultimateteam.player_card_definitions pcd ON pcd.definition_id = d.definition_id
        JOIN ultimateteam.card_types ct ON ct.card_type_id = pcd.card_type_id
        JOIN ultimateteam.native_rarities rarity_ref ON rarity_ref.native_rarity_id=ct.native_rarity_id
        LEFT JOIN ultimateteam.teams team_ref ON team_ref.team_id=pcd.team_id
        LEFT JOIN ultimateteam.leagues league_ref ON league_ref.league_id=pcd.league_id
        LEFT JOIN ultimateteam.nations nation_ref ON nation_ref.nation_id=pcd.nation_id
        WHERE d.definition_id = v_listing.definition_id
          AND v_snap.definition_id = v_listing.definition_id
          AND v_snap.definition_revision = d.revision
          AND v_listing.resource_id IS NOT DISTINCT FROM d.native_resource_id
          AND v_listing.rating = pcd.rating
          AND v_listing.asset_id IS NOT DISTINCT FROM d.native_asset_id
          AND v_listing.nation_id IS NOT DISTINCT FROM nation_ref.native_nation_id
          AND v_listing.league_id IS NOT DISTINCT FROM league_ref.native_league_id
          AND v_listing.team_id IS NOT DISTINCT FROM team_ref.native_team_id
          AND v_listing.preferred_position IS NOT DISTINCT FROM pcd.position
          AND v_listing.rareflag IS NOT DISTINCT FROM rarity_ref.native_rareflag
          AND v_listing.is_special IS NOT DISTINCT FROM ct.is_special
    ) AND NOT EXISTS (
        -- A consumable stack carries no card denormalisation: only its rating and resource agree with the catalog.
        SELECT 1
        FROM ultimateteam.item_definitions d
        JOIN ultimateteam.consumable_definitions cd ON cd.definition_id = d.definition_id
        WHERE d.definition_id = v_listing.definition_id
          AND v_snap.definition_id = v_listing.definition_id
          AND v_snap.definition_revision = d.revision
          AND v_listing.resource_id IS NOT DISTINCT FROM d.native_resource_id
          AND v_listing.rating IS NOT DISTINCT FROM cd.rating
          AND v_listing.asset_id IS NOT DISTINCT FROM d.native_asset_id
          AND v_listing.nation_id IS NULL
          AND v_listing.league_id IS NULL
          AND v_listing.team_id IS NULL
          AND v_listing.preferred_position IS NULL
          AND v_listing.rareflag IS NULL
          AND v_listing.is_special = false
    ) THEN
        PERFORM ultimateteam.lfv4_err('market item snapshot of trade ' || v_snap.market_listing_id || ' does not agree with its listing and definition');
    END IF;
    RETURN NULL; END
$$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(116);
