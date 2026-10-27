-- TOTW stays independent of campaigns. Both sides of today's transition use
-- the same instant so there is no gap or overlap between TOTW 1–3 and TOTW 4.
SELECT set_config('ultimateteam.content_publish','on',true);
UPDATE ultimateteam.promo_events
SET ends_at=timestamp '2026-10-07 19:00' AT TIME ZONE 'Europe/London'
WHERE code='initial_totw_1_3'
    AND starts_at < (timestamp '2026-10-07 19:00' AT TIME ZONE 'Europe/London');
UPDATE ultimateteam.promo_events
SET starts_at=timestamp '2026-10-07 19:00' AT TIME ZONE 'Europe/London'
WHERE code='event_totw_4'
    AND ends_at > (timestamp '2026-10-07 19:00' AT TIME ZONE 'Europe/London');
SELECT set_config('ultimateteam.content_publish','',true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(174);
