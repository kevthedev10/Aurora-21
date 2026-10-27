-- Pending batch: database naming only; FIFA API still uses tradeId.
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.market_listings RENAME COLUMN trade_id TO market_listing_id;
ALTER TABLE ultimateteam.market_item_snapshots RENAME COLUMN trade_id TO market_listing_id;
ALTER TABLE ultimateteam.market_player_snapshots RENAME COLUMN trade_id TO market_listing_id;
ALTER TABLE ultimateteam.market_player_stat_values RENAME COLUMN trade_id TO market_listing_id;
ALTER TABLE ultimateteam.market_watch RENAME COLUMN trade_id TO market_listing_id;
ALTER TABLE ultimateteam.market_item_snapshots RENAME CONSTRAINT lfv4_fk_market_item_snapshots_trade_id TO lfv4_fk_market_item_snapshots_market_listing_id;
ALTER TABLE ultimateteam.market_player_snapshots RENAME CONSTRAINT lfv4_fk_market_player_snapshots_trade_id TO lfv4_fk_market_player_snapshots_market_listing_id;
ALTER TABLE ultimateteam.market_player_stat_values RENAME CONSTRAINT lfv4_fk_market_player_stat_values_trade_id TO lfv4_fk_market_player_stat_values_market_listing_id;
ALTER TABLE ultimateteam.market_watch RENAME CONSTRAINT lfv4_fk_market_watch_trade_id TO lfv4_fk_market_watch_market_listing_id;
-- PostgreSQL updates keys/indexes, but PL/pgSQL source needs the new identifiers.
DO $rename$ DECLARE entry record; sequence_name text; BEGIN
 FOR entry IN SELECT pg_get_functiondef(p.oid) AS definition
 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='ultimateteam' AND p.prokind='f' AND p.prosrc ~ '\mtrade_id\M'
 LOOP
  EXECUTE regexp_replace(entry.definition,'\mtrade_id\M','market_listing_id','g');
 END LOOP;
 sequence_name:=pg_get_serial_sequence('ultimateteam.market_listings','market_listing_id');
 EXECUTE format('ALTER SEQUENCE %s RENAME TO market_listings_market_listing_id_seq',sequence_name);
END $rename$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(33);
