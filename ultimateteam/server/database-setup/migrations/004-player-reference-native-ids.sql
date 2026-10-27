-- Version 4: dimensions own their native ID mapping; cards keep only foreign keys.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.player_card_definitions IN ACCESS EXCLUSIVE MODE;
DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1 FROM ultimateteam.player_card_definitions p
        LEFT JOIN ultimateteam.teams t ON t.team_id=p.team_id
        LEFT JOIN ultimateteam.leagues l ON l.league_id=p.league_id
        LEFT JOIN ultimateteam.nations n ON n.nation_id=p.nation_id
        WHERE p.native_team_id IS DISTINCT FROM t.native_team_id
           OR p.native_league_id IS DISTINCT FROM l.native_league_id
           OR p.native_nation_id IS DISTINCT FROM n.native_nation_id
    ) THEN
        RAISE EXCEPTION 'Cannot remove inconsistent player native references';
    END IF;
END
$migration$;
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
        IF TG_OP = 'DELETE' THEN v_id := OLD.trade_id; ELSE v_id := NEW.trade_id; END IF;
    SELECT * INTO v_snap FROM ultimateteam.market_item_snapshots WHERE trade_id = v_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO v_listing FROM ultimateteam.market_listings WHERE trade_id = v_snap.trade_id FOR KEY SHARE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM ultimateteam.item_definitions d
        JOIN ultimateteam.player_card_definitions pcd ON pcd.definition_id = d.definition_id
        JOIN ultimateteam.card_types ct ON ct.card_type_id = pcd.card_type_id
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
          AND v_listing.rareflag IS NOT DISTINCT FROM ct.native_rareflag
          AND v_listing.is_special IS NOT DISTINCT FROM ct.is_special
    ) THEN
        PERFORM ultimateteam.lfv4_err('market item snapshot of trade ' || v_snap.trade_id || ' does not agree with its listing and definition');
    END IF;
    RETURN NULL; END
$$;


ALTER TABLE ultimateteam.player_card_definitions
    DROP COLUMN native_team_id,
    DROP COLUMN native_league_id,
    DROP COLUMN native_nation_id;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(4);
