-- Owner, 2026-10-03: both leaderboard tabs default to Top 25. Preserve custom sizes and all reward/settlement data.
UPDATE ultimateteam.content_rules SET value='25'::jsonb, updated_at=now()
WHERE file='squad_battles' AND path='["competition","boardSize"]'::jsonb AND value='200'::jsonb;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(129);
