-- Prepared only: owned cards use their catalog attributes.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.player_attribute_overrides) THEN RAISE EXCEPTION 'Owned player attribute overrides contain data; refusing removal'; END IF;
END $$;
DROP TABLE ultimateteam.player_attribute_overrides;
DROP FUNCTION ultimateteam.lfv4_r1_player_attribute_overrides();
CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r10_draft() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
DECLARE
    v_id bigint;
    v_row record;
    v_kind text;
    v_def_id bigint;
    v_footballer_id bigint;
BEGIN
    IF TG_OP = 'DELETE' THEN v_id := OLD.id; ELSE v_id := NEW.id; END IF;
    IF TG_TABLE_NAME = 'draft_picks' THEN
        SELECT * INTO v_row FROM ultimateteam.draft_picks WHERE id = v_id;
        IF NOT FOUND THEN RETURN NULL; END IF;
        v_kind := v_row.pick_kind; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;
        IF v_kind NOT IN ('player', 'manager') THEN PERFORM ultimateteam.lfv4_err('invalid Draft kind'); END IF;
        IF v_kind = 'player' AND (v_footballer_id IS NULL OR NOT EXISTS (
            SELECT 1 FROM ultimateteam.player_card_definitions d
            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id
        )) THEN
            PERFORM ultimateteam.lfv4_err('draft pick ' || v_id || ' player kind requires a matching player card definition');
        END IF;
        IF v_kind = 'manager' AND (v_footballer_id IS NOT NULL OR NOT EXISTS (
            SELECT 1 FROM ultimateteam.manager_definitions d WHERE d.definition_id = v_def_id
        )) THEN
            PERFORM ultimateteam.lfv4_err('draft pick ' || v_id || ' manager kind requires a manager definition and null footballer');
        END IF;
    ELSIF TG_TABLE_NAME = 'draft_choices' THEN
        SELECT * INTO v_row FROM ultimateteam.draft_choices WHERE id = v_id;
        IF NOT FOUND THEN RETURN NULL; END IF;
        v_kind := v_row.choice_kind; v_def_id := v_row.definition_id; v_footballer_id := v_row.footballer_id;
        IF v_kind NOT IN ('player', 'manager') THEN PERFORM ultimateteam.lfv4_err('invalid Draft kind'); END IF;
        IF v_kind = 'player' AND (v_footballer_id IS NULL OR NOT EXISTS (
            SELECT 1 FROM ultimateteam.player_card_definitions d
            WHERE d.definition_id = v_def_id AND d.footballer_id = v_footballer_id
        )) THEN
            PERFORM ultimateteam.lfv4_err('draft choice ' || v_id || ' player kind requires a matching player card definition');
        END IF;
        IF v_kind = 'manager' AND (v_footballer_id IS NOT NULL OR NOT EXISTS (
            SELECT 1 FROM ultimateteam.manager_definitions d WHERE d.definition_id = v_def_id
        )) THEN
            PERFORM ultimateteam.lfv4_err('draft choice ' || v_id || ' manager kind requires a manager definition and null footballer');
        END IF;
    END IF;
    RETURN NULL; END
$$;


DROP FUNCTION ultimateteam.lfv4_attr_code_allowed(text);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(34);
