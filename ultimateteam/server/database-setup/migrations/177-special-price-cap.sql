-- Market price bounds as set live on 2026-10-05/07 (owner): 90+ cards up to 3M, 86-89 up to 750k, specials at least 250k
-- and at most 2M, so a 90+ special such as an icon lists up to 2M. The live cap had been entered as 300,000, which held
-- every special, icons included, at 300k. Rules are read from content_rules, so the bundled file alone would not reach
-- existing databases. Only the old defaults (and the mistyped 300k cap) are replaced: a value an operator chose stays.
UPDATE ultimateteam.content_rules SET value='2000000'::jsonb, updated_at=now()
WHERE file='market' AND path='["priceBounds","specialMaximumCap"]'::jsonb AND value='300000'::jsonb;
UPDATE ultimateteam.content_rules SET value='250000'::jsonb, updated_at=now()
WHERE file='market' AND path='["priceBounds","specialMaximumFloor"]'::jsonb AND value='150000'::jsonb;
UPDATE ultimateteam.content_rules SET value='3000000'::jsonb, updated_at=now()
WHERE file='market' AND path='["priceBounds","maximumByRating",0,"maximum"]'::jsonb AND value='1000000'::jsonb;
UPDATE ultimateteam.content_rules SET value='750000'::jsonb, updated_at=now()
WHERE file='market' AND path='["priceBounds","maximumByRating",1,"maximum"]'::jsonb AND value='250000'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(177);
