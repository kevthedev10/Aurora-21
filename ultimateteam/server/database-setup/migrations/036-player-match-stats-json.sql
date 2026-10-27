-- Pending batch: compact fixed counter arrays, preserving API slot numbers.
SET CONSTRAINTS ALL IMMEDIATE;
CREATE FUNCTION ultimateteam.lfv4_match_stats_valid(data jsonb) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE scope text; entry jsonb; BEGIN
 IF jsonb_typeof(data) IS DISTINCT FROM 'object' OR data-'current'-'lifetime'<>'{}'::jsonb THEN RETURN false; END IF;
 FOREACH scope IN ARRAY ARRAY['current','lifetime'] LOOP
  IF jsonb_typeof(data->scope) IS DISTINCT FROM 'array' THEN RETURN false; END IF;
  IF jsonb_array_length(data->scope)<>5 THEN RETURN false; END IF;
  FOR entry IN SELECT value FROM jsonb_array_elements(data->scope) LOOP
   IF jsonb_typeof(entry) IS DISTINCT FROM 'number' OR (entry #>> '{}') !~ '^[0-9]+$' THEN RETURN false; END IF;
   IF (entry #>> '{}')::numeric>9223372036854775807 THEN RETURN false; END IF;
  END LOOP;
 END LOOP;
 RETURN true; END $$;
ALTER TABLE ultimateteam.player_items ADD COLUMN match_stats jsonb NOT NULL DEFAULT '{"current":[0,0,0,0,0],"lifetime":[0,0,0,0,0]}'::jsonb;
UPDATE ultimateteam.player_items p SET match_stats=jsonb_build_object('current',(SELECT jsonb_agg(COALESCE(s.value,0) ORDER BY slots.index) FROM generate_series(0,4) slots(index) LEFT JOIN ultimateteam.player_stat_values s ON s.item_id=p.item_id AND s.scope='current' AND s.stat_index=slots.index),'lifetime',(SELECT jsonb_agg(COALESCE(s.value,0) ORDER BY slots.index) FROM generate_series(0,4) slots(index) LEFT JOIN ultimateteam.player_stat_values s ON s.item_id=p.item_id AND s.scope='lifetime' AND s.stat_index=slots.index));
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.player_items ADD CONSTRAINT lfv4_ck_player_items_match_stats CHECK(ultimateteam.lfv4_match_stats_valid(match_stats));
DROP TABLE ultimateteam.player_stat_values;
DROP FUNCTION ultimateteam.lfv4_r1_player_stat_values();
ALTER TABLE ultimateteam.market_player_snapshots ADD COLUMN match_stats jsonb NOT NULL DEFAULT '{"current":[0,0,0,0,0],"lifetime":[0,0,0,0,0]}'::jsonb;
ALTER TABLE ultimateteam.market_player_snapshots DISABLE TRIGGER lfv4_trg_r9_market_player_snapshots;
UPDATE ultimateteam.market_player_snapshots p SET match_stats=jsonb_build_object('current',(SELECT jsonb_agg(COALESCE(s.value,0) ORDER BY slots.index) FROM generate_series(0,4) slots(index) LEFT JOIN ultimateteam.market_player_stat_values s ON s.market_listing_id=p.market_listing_id AND s.scope='current' AND s.stat_index=slots.index),'lifetime',(SELECT jsonb_agg(COALESCE(s.value,0) ORDER BY slots.index) FROM generate_series(0,4) slots(index) LEFT JOIN ultimateteam.market_player_stat_values s ON s.market_listing_id=p.market_listing_id AND s.scope='lifetime' AND s.stat_index=slots.index));
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.market_player_snapshots ENABLE TRIGGER lfv4_trg_r9_market_player_snapshots;
ALTER TABLE ultimateteam.market_player_snapshots ADD CONSTRAINT lfv4_ck_market_player_snapshots_match_stats CHECK(ultimateteam.lfv4_match_stats_valid(match_stats));
DROP TABLE ultimateteam.market_player_stat_values;
DROP FUNCTION ultimateteam.lfv4_r1_market_player_stat_values();
INSERT INTO ultimateteam.schema_migrations(version) VALUES(36);
