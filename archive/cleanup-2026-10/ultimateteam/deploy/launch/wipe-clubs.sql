-- Aurora 21 launch (owner, 2026-09-29): every club starts over, so each player creates theirs through FIFA's own
-- first-run screens (ultimateteam/onboarding.md). Run once, on the production database, with the server and
-- redirector stopped and after the predeploy dump (README.md in this folder).
--
-- Every table that holds club data references ultimateteam.clubs (checked on the VPS 2026-09-29), so the cascade
-- clears clubs, items, squads, packs, progress, competitions and the shared market (its listings refill on the next
-- market maintenance). Player access, the maintenance switch, sessions, panel edits, the store and the catalogue do
-- not reference clubs and are kept. The server recreates its own configured club when it starts.
BEGIN;
TRUNCATE ultimateteam.clubs CASCADE;
COMMIT;

SELECT (SELECT count(*) FROM ultimateteam.clubs) AS clubs, (SELECT count(*) FROM ultimateteam.items) AS items,
       (SELECT count(*) FROM ultimateteam.player_access) AS player_access_rows_kept;
