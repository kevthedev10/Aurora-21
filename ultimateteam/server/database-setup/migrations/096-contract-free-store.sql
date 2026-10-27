-- Maintainer policy: no permanent-card contracts; bronze/silver consumable slots
-- become player slots. Keep total size, rare counts and other category ranges.
UPDATE ultimateteam.store_packs
SET mixed_contents = jsonb_set(jsonb_set(mixed_contents, '{players}',
    jsonb_build_object('min', (mixed_contents #>> '{players,min}')::int + (mixed_contents #>> '{consumables,min}')::int,
                      'max', LEAST(size, (mixed_contents #>> '{players,max}')::int + (mixed_contents #>> '{consumables,max}')::int))),
    '{consumables}', '{"min":0,"max":0}'::jsonb)
WHERE pack_id IN (70,71,72,73,76,77);

-- All nine definitions now draw from supported pools; enable the requested lineup.
UPDATE ultimateteam.store_packs SET enabled=true WHERE pack_id BETWEEN 70 AND 78 AND kind='store';

INSERT INTO ultimateteam.schema_migrations(version) VALUES(96);
