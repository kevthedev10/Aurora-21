-- Managers have identities, nation, optional league and rarity; no team association.
SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.manager_definitions WHERE NULLIF(trim(common_name),'') IS NOT NULL OR COALESCE(native_team_id,0)<>0) THEN RAISE EXCEPTION 'Unexpected manager common name or team; refusing removal'; END IF;
 IF EXISTS(SELECT 1 FROM ultimateteam.manager_definitions m LEFT JOIN ultimateteam.nations n USING(nation_id) LEFT JOIN ultimateteam.leagues l USING(league_id) WHERE m.native_nation_id IS DISTINCT FROM n.native_nation_id OR COALESCE(m.native_league_id,0) IS DISTINCT FROM COALESCE(l.native_league_id,0)) THEN RAISE EXCEPTION 'Manager dimension mapping mismatch'; END IF;
 IF EXISTS(SELECT 1 FROM ultimateteam.manager_definitions m LEFT JOIN ultimateteam.native_rarities r ON r.native_rareflag=m.native_rareflag WHERE r.native_rarity_id IS NULL) THEN RAISE EXCEPTION 'Missing manager rarity mapping'; END IF;
END $$;
ALTER TABLE ultimateteam.manager_definitions ADD COLUMN rarity_id bigint;
ALTER TABLE ultimateteam.manager_definitions DISABLE TRIGGER lfv4_trg_r2_manager_definitions;
UPDATE ultimateteam.manager_definitions m SET rarity_id=r.native_rarity_id FROM ultimateteam.native_rarities r WHERE r.native_rareflag=m.native_rareflag;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.manager_definitions ENABLE TRIGGER lfv4_trg_r2_manager_definitions;
ALTER TABLE ultimateteam.manager_definitions
 ALTER COLUMN rarity_id SET NOT NULL,
 ADD CONSTRAINT lfv4_fk_manager_definitions_rarity_id FOREIGN KEY(rarity_id) REFERENCES ultimateteam.native_rarities(native_rarity_id),
 DROP COLUMN common_name, DROP COLUMN native_team_id, DROP COLUMN team_id,
 DROP COLUMN native_rareflag, DROP COLUMN native_league_id, DROP COLUMN native_nation_id;
CREATE INDEX lfv4_idx_manager_definitions_rarity ON ultimateteam.manager_definitions(rarity_id);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(25);
