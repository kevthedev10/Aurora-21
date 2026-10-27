-- Inline cohesive squad compatibility data; preserve existing JSON representation/order.
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.squads
 ADD COLUMN extension_schema_revision bigint NOT NULL DEFAULT 1,
 ADD COLUMN extension_payload json NOT NULL DEFAULT '{}'::json,
 ADD COLUMN wire_representation json;
UPDATE ultimateteam.squads s SET extension_schema_revision=e.schema_revision,
 extension_payload=e.payload,wire_representation=e.wire_representation
 FROM ultimateteam.squad_extensions e WHERE e.squad_id=s.id;
SET CONSTRAINTS ALL IMMEDIATE;
DROP TABLE ultimateteam.squad_extensions;
DROP FUNCTION ultimateteam.lfv4_r1_squad_extensions();
-- Names and native mask identity remain; player-card links are unchanged.
ALTER TABLE ultimateteam.traits DROP COLUMN code, DROP COLUMN mapping_evidence;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(40);
