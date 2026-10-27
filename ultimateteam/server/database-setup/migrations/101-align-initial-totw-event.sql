-- Give the initial TOTW 1-3 event the same approved dates as the Base Icon season.
-- Only this event's window changes; membership and enabled state stay intact.
SELECT set_config('ultimateteam.content_publish','on',true);
UPDATE ultimateteam.promo_events
SET starts_at=TIMESTAMP '2026-09-30 19:00' AT TIME ZONE 'Europe/Amsterdam',
    ends_at=TIMESTAMP '2027-01-01 19:00' AT TIME ZONE 'Europe/Amsterdam'
WHERE code='initial_totw_1_3';
SELECT set_config('ultimateteam.content_publish','',true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(101);
