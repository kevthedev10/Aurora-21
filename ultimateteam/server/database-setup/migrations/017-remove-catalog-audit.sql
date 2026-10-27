SET CONSTRAINTS ALL IMMEDIATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM ultimateteam.item_definitions WHERE (metadata_source_id IS NOT NULL) IS DISTINCT FROM (metadata_schema_revision IS NOT NULL)) THEN RAISE EXCEPTION 'Metadata projection mismatch'; END IF;
END $$;
ALTER TABLE ultimateteam.customisation_definitions ADD COLUMN pack_eligible boolean NOT NULL DEFAULT false;
ALTER TABLE ultimateteam.customisation_definitions DISABLE TRIGGER lfv4_trg_r2_customisation_definitions;
UPDATE ultimateteam.customisation_definitions c SET pack_eligible=true FROM ultimateteam.item_definitions d JOIN ultimateteam.catalog_sources s USING(source_id) WHERE c.definition_id=d.definition_id AND c.customisation_kind='kit' AND s.provenance::jsonb->>'input'='data/catalogs/kits.json';
ALTER TABLE ultimateteam.customisation_definitions ENABLE TRIGGER lfv4_trg_r2_customisation_definitions;
DROP VIEW ultimateteam.catalog_compatibility_metadata;
DO $$ DECLARE c record; BEGIN
 FOR c IN SELECT table_name,column_name FROM information_schema.columns WHERE table_schema='ultimateteam' AND table_name<>'catalog_sources' AND column_name IN('source_id','metadata_source_id','mapping_status') LOOP
  EXECUTE format('ALTER TABLE ultimateteam.%I DROP COLUMN %I',c.table_name,c.column_name);
 END LOOP;
END $$;
DROP TABLE ultimateteam.catalog_sources;
DROP FUNCTION ultimateteam.lfv4_r1_catalog_sources();
CREATE VIEW ultimateteam.catalog_compatibility_metadata AS
SELECT d.definition_id,COALESCE(d.metadata_schema_revision,e.schema_revision,1) AS schema_revision,
 COALESCE((e.payload::jsonb),'{}'::jsonb) || CASE WHEN p.definition_id IS NOT NULL THEN jsonb_build_object('compatibilityMetadata',COALESCE((e.payload::jsonb)->'compatibilityMetadata','{}'::jsonb) || COALESCE((SELECT jsonb_object_agg(key,value) FROM jsonb_each(jsonb_build_object('height',p.height_cm,'weight',p.weight_kg,'firstName',COALESCE(p.first_name_override,f.first_name),'lastName',COALESCE(p.last_name_override,f.last_name),'commonName',COALESCE(p.common_name_override,f.common_name,''),'dateOfBirth',COALESCE(p.date_of_birth_override,to_char(f.date_of_birth,'YYYY-MM-DD')),'customCard',p.custom_card_label,'untradeable',p.default_untradeable,'dynamicImageResourceId',p.dynamic_image_resource_id,'replacesResourceIds',p.replacement_resource_ids,'commonname',COALESCE(p.common_name_override,f.common_name,''),'wefutCard',jsonb_strip_nulls(jsonb_build_object('id',d.source_card_id,'url',d.source_card_url,'resourceIdSource',d.resource_id_source,'nameSource',d.name_source)))) WHERE key=ANY(d.source_fields)),'{}'::jsonb),'missingSourceKeys',to_jsonb(d.source_missing_fields)) ELSE '{}'::jsonb END
 || CASE WHEN m.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_build_object('talkRating',m.talk_rating,'formationId',m.formation_id) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL AND d.metadata_schema_revision IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug,'teamId',c.source_team_id,'templateMetadata',c.template_metadata)) ELSE '{}'::jsonb END AS payload
FROM ultimateteam.item_definitions d LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id) LEFT JOIN ultimateteam.footballers f USING(footballer_id) LEFT JOIN ultimateteam.manager_definitions m USING(definition_id) LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id) LEFT JOIN ultimateteam.definition_extensions e USING(definition_id) WHERE d.metadata_schema_revision IS NOT NULL OR e.definition_id IS NOT NULL;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(17);
