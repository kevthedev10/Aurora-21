-- Remove unused player links; retain typed integer/text values and tactic parent FK.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.squad_tactic_instructions WHERE squad_player_id IS NOT NULL) THEN
  RAISE EXCEPTION 'Player-linked tactic instructions exist; refusing to discard their references';
 END IF;
END $$;
DROP TRIGGER lfv4_trg_r5_squad_tactic_instructions ON ultimateteam.squad_tactic_instructions;
ALTER TABLE ultimateteam.squad_tactic_instructions DROP COLUMN squad_player_id;
-- Dropping the column also removes its FK and lfv4_idx_052_squad_tactic_instructions.
-- Preserve the shared validator for squad players, managers and roles.
CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r5_squad() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
DECLARE
    v_id bigint;
    v_row record;
    v_squad ultimateteam.squads%ROWTYPE;
    v_squad_id bigint;
    v_ref_kind text;
    v_item_id bigint;
    v_def_id bigint;
    v_footballer_id bigint;
    v_squad_player_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN v_id := OLD.id; ELSE v_id := NEW.id; END IF;
    IF TG_TABLE_NAME = 'squad_players' THEN
        SELECT * INTO v_row FROM ultimateteam.squad_players WHERE id = v_id;
        IF NOT FOUND THEN RETURN NULL; END IF;
        v_squad_id := v_row.squad_id; v_ref_kind := v_row.reference_kind;
        v_item_id := v_row.item_id; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;
    ELSIF TG_TABLE_NAME = 'squad_managers' THEN
        SELECT * INTO v_row FROM ultimateteam.squad_managers WHERE id = v_id;
        IF NOT FOUND THEN RETURN NULL; END IF;
        v_squad_id := v_row.squad_id; v_ref_kind := v_row.reference_kind;
        v_item_id := v_row.item_id; v_def_id := v_row.definition_id;
    ELSIF TG_TABLE_NAME = 'squad_roles' THEN
        SELECT * INTO v_row FROM ultimateteam.squad_roles WHERE id = v_id;
        IF NOT FOUND THEN RETURN NULL; END IF;
        v_squad_id := v_row.squad_id; v_squad_player_id := v_row.squad_player_id;
    END IF;
    IF TG_TABLE_NAME IN ('squad_players', 'squad_managers') THEN
        SELECT * INTO v_squad FROM ultimateteam.squads WHERE id = v_squad_id FOR KEY SHARE;
        IF NOT FOUND THEN
            PERFORM ultimateteam.lfv4_err('squad ' || v_squad_id || ' does not exist');
        END IF;
    END IF;
    IF TG_TABLE_NAME = 'squad_players' THEN
        IF NOT EXISTS (
            SELECT 1 FROM ultimateteam.player_card_definitions d
            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id
        ) THEN
            PERFORM ultimateteam.lfv4_err('squad player ' || v_id || ' definition does not declare footballer ' || v_footballer_id::text);
        END IF;
        IF v_ref_kind = 'owned' AND EXISTS (
            SELECT 1 FROM ultimateteam.player_items pi WHERE pi.item_id = v_item_id
        ) AND NOT EXISTS (
            SELECT 1
            FROM ultimateteam.player_items pi
            JOIN ultimateteam.items i ON i.id = pi.item_id
            WHERE pi.item_id = v_item_id
              AND i.club_id = v_squad.club_id
              AND i.definition_id = v_def_id
            FOR KEY SHARE
        ) THEN
            PERFORM ultimateteam.lfv4_err('squad player ' || v_id || ' owned item does not match squad club and definition');
        END IF;
    ELSIF TG_TABLE_NAME = 'squad_managers' THEN
        IF v_ref_kind = 'owned' AND EXISTS (
            SELECT 1 FROM ultimateteam.manager_items mi WHERE mi.item_id = v_item_id
        ) AND NOT EXISTS (
            SELECT 1
            FROM ultimateteam.manager_items mi
            JOIN ultimateteam.items i ON i.id = mi.item_id
            WHERE mi.item_id = v_item_id
              AND i.club_id = v_squad.club_id
              AND i.definition_id = v_def_id
            FOR KEY SHARE
        ) THEN
            PERFORM ultimateteam.lfv4_err('squad manager ' || v_id || ' owned item does not match squad club and definition');
        END IF;
    ELSIF TG_TABLE_NAME = 'squad_roles' THEN
        IF NOT EXISTS (
            SELECT 1 FROM ultimateteam.squad_players sp
            WHERE sp.id = v_squad_player_id AND sp.squad_id = v_squad_id
        ) THEN
            PERFORM ultimateteam.lfv4_err('squad role ' || v_id || ' references a squad player outside its squad');
        END IF;
    END IF;
    RETURN NULL; END
$$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(39);
