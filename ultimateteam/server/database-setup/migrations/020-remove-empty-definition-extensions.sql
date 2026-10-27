-- Pending batch: remove the retired catalog JSON fallback without discarding data.
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.definition_extensions) THEN
  RAISE EXCEPTION 'Unexpected definition extension data; refusing deletion';
 END IF;
END $$;
DROP VIEW ultimateteam.catalog_compatibility_metadata;
DROP TABLE ultimateteam.definition_extensions;
DROP FUNCTION ultimateteam.lfv4_r1_definition_extensions();
CREATE VIEW ultimateteam.catalog_compatibility_metadata AS
SELECT d.definition_id,COALESCE(d.metadata_schema_revision,1) AS schema_revision,
 CASE WHEN p.definition_id IS NOT NULL THEN jsonb_build_object('compatibilityMetadata',COALESCE((SELECT jsonb_object_agg(key,value) FROM jsonb_each(jsonb_build_object('height',p.height_cm,'weight',p.weight_kg,'firstName',COALESCE(p.first_name_override,f.first_name),'lastName',COALESCE(p.last_name_override,f.last_name),'commonName',COALESCE(p.common_name_override,f.common_name,''),'dateOfBirth',COALESCE(p.date_of_birth_override,to_char(f.date_of_birth,'YYYY-MM-DD')),'customCard',p.custom_card_label,'untradeable',p.default_untradeable,'dynamicImageResourceId',p.dynamic_image_resource_id,'replacesResourceIds',p.replacement_resource_ids,'commonname',COALESCE(p.common_name_override,f.common_name,''),'wefutCard',jsonb_strip_nulls(jsonb_build_object('id',d.source_card_id,'url',d.source_card_url,'resourceIdSource',d.resource_id_source,'nameSource',d.name_source)))) WHERE key=ANY(d.source_fields)),'{}'::jsonb),'missingSourceKeys',to_jsonb(d.source_missing_fields)) ELSE '{}'::jsonb END
 || CASE WHEN m.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_build_object('talkRating',m.talk_rating,'formationId',m.formation_id) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug)) ELSE '{}'::jsonb END AS payload
FROM ultimateteam.item_definitions d LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id) LEFT JOIN ultimateteam.footballers f USING(footballer_id) LEFT JOIN ultimateteam.manager_definitions m USING(definition_id) LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id) WHERE d.metadata_schema_revision IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(20);
