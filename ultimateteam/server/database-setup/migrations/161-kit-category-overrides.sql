-- Kits drawn in mixed packs before PR #87 were saved with category 9 (the definition's native subtype) instead of
-- 2 + kitType, and the item extension kept 9 as an override. FIFA labels a kit by category (2 Home, 3 Away, 5 Third);
-- any other value shows a blank "KIT" card with no shirt (live 10-05: 24 kits in 11 clubs). Drop those overrides so
-- the kits render from their definitions again.
DELETE FROM ultimateteam.item_extensions e
USING ultimateteam.items i, ultimateteam.customisation_definitions c
WHERE e.item_id = i.id AND i.definition_id = c.definition_id AND c.customisation_kind = 'kit'
    AND e.payload::jsonb = '{}'::jsonb
    AND e.wire_representation::jsonb = '{"overrides":{"category":9},"missing":[],"nulls":[],"stringNumbers":[]}'::jsonb;

-- A kit override that also holds other fields keeps them and loses only its category.
UPDATE ultimateteam.item_extensions e
SET wire_representation = jsonb_set(e.wire_representation::jsonb, '{overrides}', (e.wire_representation::jsonb -> 'overrides') - 'category')::json
FROM ultimateteam.items i, ultimateteam.customisation_definitions c
WHERE e.item_id = i.id AND i.definition_id = c.definition_id AND c.customisation_kind = 'kit'
    AND e.wire_representation::jsonb -> 'overrides' ? 'category';

INSERT INTO ultimateteam.schema_migrations(version) VALUES(161);
