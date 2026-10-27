-- Pack 542 is the authored one-player TOTW reward from migration 068. Keep its
-- live TOTW source independent of general promo schedules, like packs 540/541.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.store_packs
SET resource_pool = 'live_totw'
WHERE pack_id = 542 AND kind = 'reward' AND resource_pool IS DISTINCT FROM 'live_totw';
SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(84);
