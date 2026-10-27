-- Pending batch: standard kits use id, matching the kit response template.
-- Remove only the audited no-data omission records; preserve other extensions.
DELETE FROM ultimateteam.item_extensions e
USING ultimateteam.items i,ultimateteam.customisation_definitions c
WHERE e.item_id=i.id AND i.definition_id=c.definition_id AND c.customisation_kind='kit'
 AND e.schema_revision=1 AND e.payload::jsonb='{}'::jsonb
 AND e.wire_representation::jsonb='{"overrides":{},"missing":["itemId"],"nulls":[],"stringNumbers":[]}'::jsonb;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(23);
