-- Remove unused imported customisation fields; rendering remains owned by club rules.
DROP VIEW ultimateteam.catalog_compatibility_metadata;
ALTER TABLE ultimateteam.customisation_definitions
 DROP COLUMN native_stadium_id,
 DROP COLUMN source_team_id,
 DROP COLUMN template_metadata;
CREATE VIEW ultimateteam.catalog_compatibility_metadata AS
SELECT d.definition_id,COALESCE(d.metadata_schema_revision,e.schema_revision,1) AS schema_revision,
 COALESCE((e.payload::jsonb),'{}'::jsonb) || CASE WHEN p.definition_id IS NOT NULL THEN jsonb_build_object('compatibilityMetadata',COALESCE((e.payload::jsonb)->'compatibilityMetadata','{}'::jsonb) || COALESCE((SELECT jsonb_object_agg(key,value) FROM jsonb_each(jsonb_build_object('height',p.height_cm,'weight',p.weight_kg,'firstName',COALESCE(p.first_name_override,f.first_name),'lastName',COALESCE(p.last_name_override,f.last_name),'commonName',COALESCE(p.common_name_override,f.common_name,''),'dateOfBirth',COALESCE(p.date_of_birth_override,to_char(f.date_of_birth,'YYYY-MM-DD')),'customCard',p.custom_card_label,'untradeable',p.default_untradeable,'dynamicImageResourceId',p.dynamic_image_resource_id,'replacesResourceIds',p.replacement_resource_ids,'commonname',COALESCE(p.common_name_override,f.common_name,''),'wefutCard',jsonb_strip_nulls(jsonb_build_object('id',d.source_card_id,'url',d.source_card_url,'resourceIdSource',d.resource_id_source,'nameSource',d.name_source)))) WHERE key=ANY(d.source_fields)),'{}'::jsonb),'missingSourceKeys',to_jsonb(d.source_missing_fields)) ELSE '{}'::jsonb END
 || CASE WHEN m.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_build_object('talkRating',m.talk_rating,'formationId',m.formation_id) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug)) ELSE '{}'::jsonb END AS payload
FROM ultimateteam.item_definitions d LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id) LEFT JOIN ultimateteam.footballers f USING(footballer_id) LEFT JOIN ultimateteam.manager_definitions m USING(definition_id) LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id) LEFT JOIN ultimateteam.definition_extensions e USING(definition_id) WHERE d.metadata_schema_revision IS NOT NULL OR e.definition_id IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(19);
