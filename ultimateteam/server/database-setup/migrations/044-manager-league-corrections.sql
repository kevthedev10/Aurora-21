-- Ten manager leagues corrected from FIFA 21's own card database, as the Python server derived them (commit bdfe5fe:
-- managercards.carddbid - 1000000 = manager.managerid -> manager.teamid -> leagueteamlinks, FUT leagues only). The
-- maintainer chose these over migration 029's CSV assignments on 2026-09-25; the 19 managers with no club in FIFA's
-- data keep 029's leagues. Owned cards still on the old default follow; a league a player changed is kept.
SET CONSTRAINTS ALL IMMEDIATE;
CREATE TEMP TABLE manager_league_correction(native_resource_id bigint PRIMARY KEY, old_native_league bigint NOT NULL,
    new_native_league bigint NOT NULL) ON COMMIT DROP;
INSERT INTO manager_league_correction VALUES
(1007512, 353, 1003), -- Hernán Crespo
(1010974, 353, 1014), -- Mauricio Pellegrino
(1143063, 353, 1014), -- Juan Manuel Azconzábal
(1249253, 353, 1014), -- Diego Dabove
(1249775, 308, 54),   -- José Gomes
(1249929, 353, 1014), -- Lucas Pusineri
(1249944, 353, 1014), -- Luis Zubeldía
(1250470, 353, 1003), -- Sebastián Beccacece
(1252696, 319, 330),  -- Dušan Uhrin Jr.
(1255029, 353, 1014); -- Israel Damonte
CREATE TEMP TABLE manager_league_change ON COMMIT DROP AS
SELECT d.definition_id, m.league_id AS current_league_id, old.league_id AS old_league_id, new.league_id AS new_league_id
FROM manager_league_correction x
JOIN ultimateteam.item_definitions d ON d.native_resource_id = x.native_resource_id AND d.family_id = 2
JOIN ultimateteam.manager_definitions m ON m.definition_id = d.definition_id
JOIN ultimateteam.leagues old ON old.native_league_id = x.old_native_league
JOIN ultimateteam.leagues new ON new.native_league_id = x.new_native_league;
DO $$ BEGIN
 IF (SELECT count(*) FROM manager_league_change) <> 10 THEN
  RAISE EXCEPTION 'Manager league correction must resolve exactly 10 managers and leagues';
 END IF;
 -- Replaying the migration finds them corrected already; anything else is an unexpected catalogue.
 IF EXISTS (SELECT 1 FROM manager_league_change WHERE current_league_id NOT IN (old_league_id, new_league_id)) THEN
  RAISE EXCEPTION 'A corrected manager is on neither its previous nor its corrected league';
 END IF;
END $$;
ALTER TABLE ultimateteam.manager_definitions DISABLE TRIGGER lfv4_trg_r2_manager_definitions;
UPDATE ultimateteam.manager_definitions m SET league_id = c.new_league_id
FROM manager_league_change c WHERE m.definition_id = c.definition_id AND m.league_id = c.old_league_id;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.manager_definitions ENABLE TRIGGER lfv4_trg_r2_manager_definitions;
UPDATE ultimateteam.manager_items mi SET league_id = c.new_league_id
FROM ultimateteam.items i JOIN manager_league_change c ON c.definition_id = i.definition_id
WHERE mi.item_id = i.id AND mi.league_id = c.old_league_id;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(44);
