-- The icon slot is a category. Its first active group is base_icons; later schedules
-- may select Mid, Prime or Moments groups without changing pack configuration.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.promos SET odds_category = 'icon' WHERE odds_category = 'base_icon';
SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(82);
