-- Maintainer-approved initial Icon calendar, expressed as Amsterdam wall-clock times.
-- Existing enabled operator rotations take precedence; do not overwrite their calendar.
INSERT INTO ultimateteam.icon_rotations(code,name,promo_id,enabled,starts_at,ends_at)
SELECT seed.code,seed.name,p.promo_id,true,
       seed.starts_at::timestamp AT TIME ZONE 'Europe/Amsterdam',
       seed.ends_at::timestamp AT TIME ZONE 'Europe/Amsterdam'
FROM (VALUES
    ('initial_base_icons','Base Icons','base_icons','2026-09-30 19:00','2027-01-01 19:00'),
    ('initial_mid_icons','Mid Icons','mid_icons','2027-01-01 19:00','2027-03-01 19:00'),
    ('initial_prime_icons','Prime Icons','prime_icons','2027-03-01 19:00','2027-05-01 19:00'),
    ('initial_icon_moments','Prime Icon Moments','prime_icon_moments','2027-05-01 19:00','2028-01-01 19:00')
) seed(code,name,promo_code,starts_at,ends_at)
JOIN ultimateteam.promos p ON p.code=seed.promo_code
WHERE NOT EXISTS(SELECT 1 FROM ultimateteam.icon_rotations WHERE enabled)
ON CONFLICT(code) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(100);
