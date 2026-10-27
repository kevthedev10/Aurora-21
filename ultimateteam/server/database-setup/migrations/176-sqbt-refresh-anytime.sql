-- Squad Battles opponents refresh whenever the player wants (FIFA 21 retail; PR #166 set rules/squad_battles.json
-- sets.refreshSeconds to 0). Rules are read from content_rules, so the bundled file alone left existing databases on
-- the old 6-hour timer (live 10-07). Only the old default is replaced: a value an operator chose stays.
UPDATE ultimateteam.content_rules SET value='0'::jsonb, updated_at=now()
WHERE file='squad_battles' AND path='["sets","refreshSeconds"]'::jsonb AND value='21600'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(176);
