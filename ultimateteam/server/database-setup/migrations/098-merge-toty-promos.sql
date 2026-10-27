-- Consolidate the maintainer's TOTY collections without deleting player definitions.
-- Existing default buckets combine; curated buckets retain their weights and enabled state.
-- Flush deferred FK events before changing the two deletion guards on upgrade.
SET CONSTRAINTS ALL IMMEDIATE;
SELECT set_config('ultimateteam.content_publish', 'on', true);
CREATE TEMP TABLE toty_merges ON COMMIT DROP AS
SELECT source.promo_id AS source_id, target.promo_id AS target_id
FROM (VALUES ('toty_12th_man', 'team_of_the_year'),
             ('toty_honourable_mentions_objective_sbc', 'toty_honourable_mentions_pack_squad')) codes(source_code,target_code)
JOIN ultimateteam.promos source ON source.code=codes.source_code
JOIN ultimateteam.promos target ON target.code=codes.target_code;

UPDATE ultimateteam.promos SET code='toty_honourable_mentions', name='TOTY Honourable Mentions'
WHERE code='toty_honourable_mentions_pack_squad';

CREATE TEMP TABLE toty_bucket_moves ON COMMIT DROP AS
SELECT b.id AS source_bucket, m.source_id, m.target_id,
       CASE WHEN b.code='default' AND EXISTS (
           SELECT 1 FROM ultimateteam.promo_buckets t WHERE t.promo_id=m.target_id AND t.code='default' AND t.enabled=b.enabled)
         THEN 'default' ELSE 'merged_' || b.id::text END AS target_code
FROM toty_merges m JOIN ultimateteam.promo_buckets b ON b.promo_id=m.source_id;

INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight,enabled)
SELECT m.target_id,m.target_code,b.name,b.draw_weight,b.enabled
FROM toty_bucket_moves m JOIN ultimateteam.promo_buckets b ON b.id=m.source_bucket
ON CONFLICT(promo_id,code) DO NOTHING;

INSERT INTO ultimateteam.promo_cards(promo_id,player_card_definition_id,bucket_id,member_order)
SELECT m.target_id,c.player_card_definition_id,t.id,c.member_order
FROM toty_bucket_moves m JOIN ultimateteam.promo_cards c ON c.bucket_id=m.source_bucket
JOIN ultimateteam.promo_buckets t ON t.promo_id=m.target_id AND t.code=m.target_code
ON CONFLICT(promo_id,player_card_definition_id) DO NOTHING;
DELETE FROM ultimateteam.promo_cards WHERE promo_id IN (SELECT source_id FROM toty_merges);

INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id,enabled)
SELECT e.event_id,m.target_id,e.enabled FROM ultimateteam.promo_event_promos e JOIN toty_merges m ON m.source_id=e.promo_id
ON CONFLICT(event_id,promo_id) DO UPDATE SET enabled=ultimateteam.promo_event_promos.enabled OR excluded.enabled;
DELETE FROM ultimateteam.promo_event_promos WHERE promo_id IN (SELECT source_id FROM toty_merges);
UPDATE ultimateteam.icon_rotations r SET promo_id=m.target_id FROM toty_merges m WHERE r.promo_id=m.source_id;

-- Only this migration may retire these obsolete identities; restore both guards afterwards.
ALTER TABLE ultimateteam.promo_buckets DISABLE TRIGGER promo_buckets_publish_guard;
DELETE FROM ultimateteam.promo_buckets WHERE promo_id IN (SELECT source_id FROM toty_merges);
ALTER TABLE ultimateteam.promo_buckets ENABLE TRIGGER promo_buckets_publish_guard;
ALTER TABLE ultimateteam.promos DISABLE TRIGGER lfv4_trg_r2_promos;
DELETE FROM ultimateteam.promos WHERE promo_id IN (SELECT source_id FROM toty_merges);
ALTER TABLE ultimateteam.promos ENABLE TRIGGER lfv4_trg_r2_promos;

-- Keep configured sources and already-owned picks pointing at the merged collection.
CREATE FUNCTION pg_temp.toty_codes(value jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
BEGIN
    CASE jsonb_typeof(value)
      WHEN 'string' THEN RETURN to_jsonb(CASE value #>> '{}'
        WHEN 'toty_12th_man' THEN 'team_of_the_year'
        WHEN 'toty_honourable_mentions_pack_squad' THEN 'toty_honourable_mentions'
        WHEN 'toty_honourable_mentions_objective_sbc' THEN 'toty_honourable_mentions'
        ELSE value #>> '{}' END);
      WHEN 'array' THEN RETURN coalesce((SELECT jsonb_agg(pg_temp.toty_codes(element) ORDER BY ord)
        FROM jsonb_array_elements(value) WITH ORDINALITY a(element,ord)), '[]'::jsonb);
      WHEN 'object' THEN RETURN coalesce((SELECT jsonb_object_agg(key,pg_temp.toty_codes(element))
        FROM jsonb_each(value) a(key,element)), '{}'::jsonb);
      ELSE RETURN value;
    END CASE;
END $$;
UPDATE ultimateteam.store_packs SET promo_codes=ARRAY(SELECT DISTINCT pg_temp.toty_codes(to_jsonb(c)) #>> '{}' FROM unnest(promo_codes) c)
WHERE promo_codes && ARRAY['toty_12th_man','toty_honourable_mentions_pack_squad','toty_honourable_mentions_objective_sbc'];
UPDATE ultimateteam.owned_legacy_pack_definitions SET promo_codes=ARRAY(SELECT DISTINCT pg_temp.toty_codes(to_jsonb(c)) #>> '{}' FROM unnest(promo_codes) c)
WHERE promo_codes && ARRAY['toty_12th_man','toty_honourable_mentions_pack_squad','toty_honourable_mentions_objective_sbc'];
UPDATE ultimateteam.store_packs SET player_picks=pg_temp.toty_codes(player_picks) WHERE player_picks IS DISTINCT FROM pg_temp.toty_codes(player_picks);
UPDATE ultimateteam.owned_legacy_pack_definitions SET player_picks=pg_temp.toty_codes(player_picks) WHERE player_picks IS DISTINCT FROM pg_temp.toty_codes(player_picks);
UPDATE ultimateteam.player_pick_items SET draw_config=pg_temp.toty_codes(draw_config) WHERE draw_config IS DISTINCT FROM pg_temp.toty_codes(draw_config);
UPDATE ultimateteam.content_rules SET value=pg_temp.toty_codes(value) WHERE value IS DISTINCT FROM pg_temp.toty_codes(value);
DROP FUNCTION pg_temp.toty_codes(jsonb);
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(98);
