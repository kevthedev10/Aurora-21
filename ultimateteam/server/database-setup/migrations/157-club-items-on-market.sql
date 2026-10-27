-- Club items on the transfer market (owner, 10-04): kits, badges, balls and stadium cards from tradeable packs open
-- tradeable and can be listed, searched and bought. The market ownership and snapshot invariants (migration 116) knew
-- player cards and consumable stacks; they learn the customisation family. A club item's listing carries its catalogue
-- rating, team and rarity and no player snapshot. The bodies are migration 116's with the third family added.
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
    ) AND NOT EXISTS (
        -- A club item (kit, badge, ball or stadium card) lists as its own customisation item.
        SELECT 1
        FROM ultimateteam.items i
        JOIN ultimateteam.customisation_items cu ON cu.item_id = i.id
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
    ) AND NOT EXISTS (
        -- A club item keeps its catalogue rating, team and rarity.
        SELECT 1
        FROM ultimateteam.item_definitions d
        JOIN ultimateteam.customisation_definitions cu ON cu.definition_id = d.definition_id
        WHERE d.definition_id = v_listing.definition_id
          AND v_snap.definition_id = v_listing.definition_id
          AND v_snap.definition_revision = d.revision
          AND v_listing.resource_id IS NOT DISTINCT FROM d.native_resource_id
          AND v_listing.rating = COALESCE(cu.rating, 0)
          AND v_listing.asset_id IS NOT DISTINCT FROM d.native_asset_id
          AND v_listing.nation_id IS NULL
          AND v_listing.league_id IS NULL
          AND v_listing.team_id IS NOT DISTINCT FROM cu.native_team_id
          AND v_listing.preferred_position IS NULL
          AND v_listing.rareflag IS NOT DISTINCT FROM cu.native_rareflag
          AND v_listing.is_special = false
    ) THEN
        PERFORM ultimateteam.lfv4_err('market item snapshot of trade ' || v_snap.market_listing_id || ' does not agree with its listing and definition');
    END IF;
    RETURN NULL; END
$$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(157);
