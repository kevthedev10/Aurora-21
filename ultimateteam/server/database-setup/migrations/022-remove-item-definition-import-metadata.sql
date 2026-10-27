-- Pending batch: API compatibility format is fixed; catalog values are typed.
DROP VIEW ultimateteam.catalog_compatibility_metadata;
ALTER TABLE ultimateteam.item_definitions
 DROP COLUMN metadata_schema_revision, DROP COLUMN source_fields,
 DROP COLUMN source_missing_fields, DROP COLUMN source_card_id,
 DROP COLUMN source_card_url, DROP COLUMN resource_id_source, DROP COLUMN name_source;
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
 || CASE WHEN m.definition_id IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('talkRating',m.talk_rating,'formationId',m.formation_id)) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug)) ELSE '{}'::jsonb END AS payload
FROM ultimateteam.item_definitions d
 LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id)
 LEFT JOIN ultimateteam.footballers f USING(footballer_id)
 LEFT JOIN ultimateteam.manager_definitions m USING(definition_id)
 LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id)
WHERE p.definition_id IS NOT NULL OR m.definition_id IS NOT NULL OR c.definition_id IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(22);
