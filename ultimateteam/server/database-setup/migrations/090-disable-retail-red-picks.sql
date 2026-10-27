-- FUT Champions red picks are earned through Champions rewards, never sold in the store.
UPDATE ultimateteam.store_packs SET enabled=false, always_present=false WHERE pack_id=60;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(90);
