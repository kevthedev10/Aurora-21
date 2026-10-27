-- Version 1: catalog metadata only. Run inside the bootstrap transaction/advisory locks.
-- Flush deferred seed checks before additive DDL; the encompassing transaction remains atomic.
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.footballers ADD COLUMN date_of_birth date, ADD COLUMN date_of_birth_source text;
ALTER TABLE ultimateteam.item_definitions
 ADD COLUMN metadata_source_id bigint REFERENCES ultimateteam.catalog_sources(source_id),
 ADD COLUMN metadata_schema_revision bigint,
 ADD COLUMN source_fields text[],
 ADD COLUMN source_missing_fields text[],
 ADD COLUMN source_card_id bigint,
 ADD COLUMN source_card_url text,
 ADD COLUMN resource_id_source text,
 ADD COLUMN name_source text;
ALTER TABLE ultimateteam.player_card_definitions
 ADD COLUMN height_cm bigint,
 ADD COLUMN weight_kg bigint,
 ADD COLUMN first_name_override text,
 ADD COLUMN last_name_override text,
 ADD COLUMN common_name_override text,
 ADD COLUMN date_of_birth_override text,
 ADD COLUMN custom_card_label text,
 ADD COLUMN default_untradeable boolean,
 ADD COLUMN dynamic_image_resource_id bigint,
 ADD COLUMN replacement_resource_ids bigint[];
ALTER TABLE ultimateteam.manager_definitions
 ADD COLUMN talk_rating bigint,
 ADD COLUMN formation_id bigint;
ALTER TABLE ultimateteam.customisation_definitions
 ADD COLUMN source_type text,
 ADD COLUMN slug text,
 ADD COLUMN source_team_id bigint,
 ADD COLUMN template_metadata jsonb;
-- Catalogs remain immutable to runtime writes. Only this locked migration changes them.
ALTER TABLE ultimateteam.footballers DISABLE TRIGGER lfv4_trg_r2_footballers;
ALTER TABLE ultimateteam.item_definitions DISABLE TRIGGER lfv4_trg_r2_item_definitions;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.manager_definitions DISABLE TRIGGER lfv4_trg_r2_manager_definitions;
ALTER TABLE ultimateteam.customisation_definitions DISABLE TRIGGER lfv4_trg_r2_customisation_definitions;
ALTER TABLE ultimateteam.definition_extensions DISABLE TRIGGER lfv4_trg_r2_definition_extensions;
DO $$ BEGIN
 IF EXISTS (SELECT p.footballer_id FROM ultimateteam.player_card_definitions p JOIN ultimateteam.definition_extensions e USING(definition_id) WHERE (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth' IS NOT NULL AND (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth' NOT IN ('1970-01-01','0000-00-00','') GROUP BY p.footballer_id HAVING count(DISTINCT (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth') > 1) THEN RAISE EXCEPTION 'Conflicting footballer dates of birth; refusing metadata upgrade'; END IF;
END $$;
UPDATE ultimateteam.footballers f SET date_of_birth=q.dob FROM (SELECT p.footballer_id,min(((e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth')::date) AS dob FROM ultimateteam.player_card_definitions p JOIN ultimateteam.definition_extensions e USING(definition_id) WHERE (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth' NOT IN ('1970-01-01','0000-00-00','') GROUP BY p.footballer_id) q WHERE f.footballer_id=q.footballer_id;
-- User-supplied corrections, 2026-09-17; pinned native identity mappings remain unresolved.
UPDATE ultimateteam.footballers SET date_of_birth_source='catalog' WHERE date_of_birth IS NOT NULL;
UPDATE ultimateteam.footballers SET date_of_birth=DATE '1994-09-08',date_of_birth_source='user-supplied:2026-09-17' WHERE footballer_id=6060 AND native_footballer_id=219670;
UPDATE ultimateteam.footballers SET date_of_birth=DATE '2000-01-08',date_of_birth_source='user-supplied:2026-09-17' WHERE footballer_id=12657 AND native_footballer_id=245061;
UPDATE ultimateteam.item_definitions d SET metadata_source_id=e.source_id,metadata_schema_revision=e.schema_revision,source_fields=ARRAY(SELECT jsonb_object_keys(COALESCE((e.payload::jsonb)->'compatibilityMetadata','{}'::jsonb))),source_missing_fields=ARRAY(SELECT jsonb_array_elements_text(COALESCE((e.payload::jsonb)->'missingSourceKeys','[]'::jsonb))),source_card_id=((e.payload::jsonb)->'compatibilityMetadata'->'wefutCard'->>'id')::bigint,source_card_url=(e.payload::jsonb)->'compatibilityMetadata'->'wefutCard'->>'url',resource_id_source=(e.payload::jsonb)->'compatibilityMetadata'->'wefutCard'->>'resourceIdSource',name_source=(e.payload::jsonb)->'compatibilityMetadata'->'wefutCard'->>'nameSource' FROM ultimateteam.definition_extensions e WHERE d.definition_id=e.definition_id;
UPDATE ultimateteam.player_card_definitions p SET height_cm=((e.payload::jsonb)->'compatibilityMetadata'->>'height')::bigint,
 weight_kg=((e.payload::jsonb)->'compatibilityMetadata'->>'weight')::bigint,
 first_name_override=CASE WHEN ((e.payload::jsonb)->'compatibilityMetadata'->>'firstName')::text IS DISTINCT FROM f.first_name THEN ((e.payload::jsonb)->'compatibilityMetadata'->>'firstName')::text END,
 last_name_override=CASE WHEN ((e.payload::jsonb)->'compatibilityMetadata'->>'lastName')::text IS DISTINCT FROM f.last_name THEN ((e.payload::jsonb)->'compatibilityMetadata'->>'lastName')::text END,
 common_name_override=CASE WHEN COALESCE((e.payload::jsonb)->'compatibilityMetadata'->>'commonName',(e.payload::jsonb)->'compatibilityMetadata'->>'commonname') IS DISTINCT FROM COALESCE(f.common_name,'') THEN COALESCE((e.payload::jsonb)->'compatibilityMetadata'->>'commonName',(e.payload::jsonb)->'compatibilityMetadata'->>'commonname') END,
 date_of_birth_override=CASE WHEN (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth' IS DISTINCT FROM to_char(f.date_of_birth,'YYYY-MM-DD') THEN (e.payload::jsonb)->'compatibilityMetadata'->>'dateOfBirth' END,
 custom_card_label=((e.payload::jsonb)->'compatibilityMetadata'->>'customCard')::text,
 default_untradeable=((e.payload::jsonb)->'compatibilityMetadata'->>'untradeable')::boolean,
 dynamic_image_resource_id=((e.payload::jsonb)->'compatibilityMetadata'->>'dynamicImageResourceId')::bigint,
 replacement_resource_ids=CASE WHEN (e.payload::jsonb)->'compatibilityMetadata' ? 'replacesResourceIds' THEN ARRAY(SELECT jsonb_array_elements_text((e.payload::jsonb)->'compatibilityMetadata'->'replacesResourceIds'))::bigint[] END FROM ultimateteam.definition_extensions e,ultimateteam.footballers f WHERE p.definition_id=e.definition_id AND f.footballer_id=p.footballer_id;
-- Approved source corrections: canonical custom name key and footballer names/dates.
UPDATE ultimateteam.item_definitions d SET source_fields=ARRAY(SELECT CASE WHEN field='commonname' THEN 'commonName' ELSE field END FROM unnest(d.source_fields) AS field) WHERE d.metadata_source_id=2;
UPDATE ultimateteam.footballers SET first_name='Gonzalo',last_name='Piovi' WHERE footballer_id=6060 AND native_footballer_id=219670;
UPDATE ultimateteam.footballers SET first_name='Julián',last_name='López' WHERE footballer_id=12657 AND native_footballer_id=245061;
UPDATE ultimateteam.player_card_definitions SET first_name_override=NULL,last_name_override=NULL,date_of_birth_override=NULL WHERE definition_id IN (5713,16783,19140);
UPDATE ultimateteam.player_card_definitions SET last_name_override=NULL WHERE definition_id=22403 AND footballer_id=1948;
UPDATE ultimateteam.manager_definitions p SET talk_rating=((e.payload::jsonb)->>'talkRating')::bigint,
 formation_id=((e.payload::jsonb)->>'formationId')::bigint FROM ultimateteam.definition_extensions e WHERE p.definition_id=e.definition_id;
UPDATE ultimateteam.customisation_definitions p SET source_type=((e.payload::jsonb)->>'type')::text,
 slug=((e.payload::jsonb)->>'slug')::text,
 source_team_id=((e.payload::jsonb)->>'teamId')::bigint,
 template_metadata=(e.payload::jsonb)->'templateMetadata' FROM ultimateteam.definition_extensions e WHERE p.definition_id=e.definition_id;
CREATE VIEW ultimateteam.catalog_compatibility_metadata AS
SELECT d.definition_id,COALESCE(d.metadata_schema_revision,e.schema_revision,1) AS schema_revision,
 COALESCE((e.payload::jsonb),'{}'::jsonb) || CASE WHEN p.definition_id IS NOT NULL THEN jsonb_build_object('compatibilityMetadata',COALESCE((e.payload::jsonb)->'compatibilityMetadata','{}'::jsonb) || COALESCE((SELECT jsonb_object_agg(key,value) FROM jsonb_each(jsonb_build_object('height',p.height_cm,'weight',p.weight_kg,'firstName',COALESCE(p.first_name_override,f.first_name),'lastName',COALESCE(p.last_name_override,f.last_name),'commonName',COALESCE(p.common_name_override,f.common_name,''),'dateOfBirth',COALESCE(p.date_of_birth_override,to_char(f.date_of_birth,'YYYY-MM-DD')),'customCard',p.custom_card_label,'untradeable',p.default_untradeable,'dynamicImageResourceId',p.dynamic_image_resource_id,'replacesResourceIds',p.replacement_resource_ids,'commonname',COALESCE(p.common_name_override,f.common_name,''),'wefutCard',jsonb_strip_nulls(jsonb_build_object('id',d.source_card_id,'url',d.source_card_url,'resourceIdSource',d.resource_id_source,'nameSource',d.name_source)))) WHERE key=ANY(d.source_fields)),'{}'::jsonb),'missingSourceKeys',to_jsonb(d.source_missing_fields)) ELSE '{}'::jsonb END
 || CASE WHEN m.definition_id IS NOT NULL AND d.metadata_source_id IS NOT NULL THEN jsonb_build_object('talkRating',m.talk_rating,'formationId',m.formation_id) ELSE '{}'::jsonb END
 || CASE WHEN c.definition_id IS NOT NULL AND d.metadata_source_id IS NOT NULL THEN jsonb_strip_nulls(jsonb_build_object('type',c.source_type,'slug',c.slug,'teamId',c.source_team_id,'templateMetadata',c.template_metadata)) ELSE '{}'::jsonb END AS payload,
 COALESCE(d.metadata_source_id,e.source_id,d.source_id) AS source_id
FROM ultimateteam.item_definitions d LEFT JOIN ultimateteam.player_card_definitions p USING(definition_id) LEFT JOIN ultimateteam.footballers f USING(footballer_id) LEFT JOIN ultimateteam.manager_definitions m USING(definition_id) LEFT JOIN ultimateteam.customisation_definitions c USING(definition_id) LEFT JOIN ultimateteam.definition_extensions e USING(definition_id) WHERE d.metadata_source_id IS NOT NULL OR e.definition_id IS NOT NULL;
UPDATE ultimateteam.definition_extensions SET payload=CASE WHEN (payload::jsonb) ? 'compatibilityMetadata' THEN ((payload::jsonb)-'compatibilityMetadata'-'missingSourceKeys') || CASE WHEN (((payload::jsonb)->'compatibilityMetadata') - ARRAY['height','weight','firstName','lastName','commonName','dateOfBirth','customCard','untradeable','dynamicImageResourceId','replacesResourceIds','commonname','wefutCard']) <> '{}'::jsonb THEN jsonb_build_object('compatibilityMetadata',((payload::jsonb)->'compatibilityMetadata') - ARRAY['height','weight','firstName','lastName','commonName','dateOfBirth','customCard','untradeable','dynamicImageResourceId','replacesResourceIds','commonname','wefutCard']) ELSE '{}'::jsonb END ELSE (payload::jsonb) - ARRAY['talkRating','formationId','type','slug','teamId','templateMetadata'] END;
DELETE FROM ultimateteam.definition_extensions WHERE (payload::jsonb)='{}'::jsonb;
ALTER TABLE ultimateteam.footballers ENABLE TRIGGER lfv4_trg_r2_footballers;
ALTER TABLE ultimateteam.item_definitions ENABLE TRIGGER lfv4_trg_r2_item_definitions;
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
ALTER TABLE ultimateteam.manager_definitions ENABLE TRIGGER lfv4_trg_r2_manager_definitions;
ALTER TABLE ultimateteam.customisation_definitions ENABLE TRIGGER lfv4_trg_r2_customisation_definitions;
ALTER TABLE ultimateteam.definition_extensions ENABLE TRIGGER lfv4_trg_r2_definition_extensions;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(1);
