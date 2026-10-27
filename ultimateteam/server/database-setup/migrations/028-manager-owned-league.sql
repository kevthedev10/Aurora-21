-- Owned manager league is an actual value, copied once from the definition.
SET CONSTRAINTS ALL IMMEDIATE;
UPDATE ultimateteam.manager_items m SET league_override_id=d.league_id FROM ultimateteam.items i JOIN ultimateteam.manager_definitions d USING(definition_id) WHERE i.id=m.item_id AND m.league_override_id IS NULL AND d.league_id IS NOT NULL;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.manager_items RENAME COLUMN league_override_id TO league_id;
ALTER TABLE ultimateteam.manager_items RENAME CONSTRAINT lfv4_fk_manager_items_league_override_id TO lfv4_fk_manager_items_league_id;
CREATE INDEX lfv4_idx_manager_items_league ON ultimateteam.manager_items(league_id);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(28);
