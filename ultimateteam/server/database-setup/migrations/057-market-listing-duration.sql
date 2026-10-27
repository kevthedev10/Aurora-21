-- Transfer List "Re-list All" (Python server 5fd85ed) puts every expired card back up for the duration it ran for, but an
-- expiry moves end_time to when it was processed. Listings now store their duration; older rows keep 0, and a re-list takes
-- the longest FUT duration that fits their created and end times (rules/market.json listings.durations).
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.market_listings ADD COLUMN IF NOT EXISTS duration_seconds bigint NOT NULL DEFAULT 0
    CHECK (duration_seconds >= 0);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(57);
