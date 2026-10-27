-- Unused manager metadata: retain manager item response behavior.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.manager_definitions WHERE COALESCE(talk_rating,0)<>0 OR COALESCE(formation_id,0)<>0) THEN RAISE EXCEPTION 'Unexpected nonzero manager metadata; refusing removal'; END IF;
END $$;
DROP VIEW ultimateteam.catalog_compatibility_metadata;
ALTER TABLE ultimateteam.manager_definitions DROP COLUMN talk_rating, DROP COLUMN formation_id;
CREATE VIEW ultimateteam.catalog_compatibility_metadata AS
SELECT d.definition_id,1::bigint AS schema_revision,
 CASE WHEN p.definition_id IS NOT NULL THEN jsonb_build_object('compatibilityMetadata',jsonb_strip_nulls(jsonb_build_object(
 'height',p.height_cm,'weight',p.weight_kg,
 'firstName',COALESCE(p.first_name_override,f.first_name),
 'lastName',COALESCE(p.last_name_override,f.last_name),
 'commonName',COALESCE(p.common_name_override,f.common_name,''),
 'dateOfBirth',COALESCE(p.date_of_birth_override,to_char(f.date_of_birth,'YYYY-MM-DD')),
 'customCard',p.custom_card_label,'untradeable',p.default_untradeable,
 'dynamicImageResourceId',p.dynamic_image_resource_id,'replacesResourceIds',p.replacement_resource_ids))) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug)) ELSE '{}'::jsonb END AS payload
FROM ultimateteam.item_definitions d
 LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id)
 LEFT JOIN ultimateteam.footballers f USING(footballer_id)
 LEFT JOIN ultimateteam.manager_definitions m USING(definition_id)
 LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id)
WHERE p.definition_id IS NOT NULL OR m.definition_id IS NOT NULL OR c.definition_id IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(26);
