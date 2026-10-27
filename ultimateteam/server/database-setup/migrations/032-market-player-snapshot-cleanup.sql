-- Pending batch: tradable market cards cannot be loans.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.market_player_snapshots WHERE loan_games<>0) THEN RAISE EXCEPTION 'Market snapshots contain loan cards; refusing column removal'; END IF;
END $$;
ALTER TABLE ultimateteam.market_player_snapshots DROP COLUMN loan_games;
ALTER TABLE ultimateteam.market_player_snapshots RENAME COLUMN style_definition_id TO chemistry_style_definition_id;
ALTER TABLE ultimateteam.market_player_snapshots RENAME CONSTRAINT lfv4_fk_market_player_snapshots_style_definition_id TO lfv4_fk_market_player_snapshots_chemistry_style_definition_id;
CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r8_player_snapshot() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
DECLARE
    v_id bigint;
    v_snap ultimateteam.market_player_snapshots%ROWTYPE;
BEGIN
        IF TG_OP = 'DELETE' THEN v_id := OLD.trade_id; ELSE v_id := NEW.trade_id; END IF;
    SELECT * INTO v_snap FROM ultimateteam.market_player_snapshots WHERE trade_id = v_id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF NOT EXISTS (SELECT 1 FROM ultimateteam.market_item_snapshots s WHERE s.trade_id = v_snap.trade_id FOR KEY SHARE) THEN
        PERFORM ultimateteam.lfv4_err('market player snapshot of trade ' || v_snap.trade_id || ' has no item snapshot parent');
    END IF;
    IF v_snap.chemistry_style_definition_id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM ultimateteam.consumable_definitions cd
        WHERE cd.definition_id = v_snap.chemistry_style_definition_id AND cd.consumable_kind = 'chemistry_style'
    ) THEN
        PERFORM ultimateteam.lfv4_err('market player snapshot of trade ' || v_snap.trade_id || ' style definition is not a chemistry_style consumable');
    END IF;
    RETURN NULL; END
$$;


INSERT INTO ultimateteam.schema_migrations(version) VALUES(32);
