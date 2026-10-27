-- My Stadium vanity slots (Python server ca228bc): the tifo, theme, pitch, stadium colour and pyrotechnics items a club
-- has put in each vanity slot. Every club owns the whole vanity catalogue (rules/club.json vanity), so the items are
-- virtual; a row names the chosen one by its native card subtype and asset id, which stay valid if the catalogue grows.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.club_vanity_slots (
    club_id         bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    slot_id         integer NOT NULL CHECK (slot_id > 0),
    native_subtype  integer NOT NULL CHECK (native_subtype > 0),
    native_asset_id bigint  NOT NULL CHECK (native_asset_id > 0),
    PRIMARY KEY (club_id, slot_id)
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(54);
