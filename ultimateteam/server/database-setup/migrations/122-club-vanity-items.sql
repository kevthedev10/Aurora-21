-- My Stadium items are owned, not given: a club used to own the whole vanity catalogue as virtual items. Now it owns the
-- rules/club.json vanity starterItems plus the items it is granted (the Stadium Development objectives' packs, as
-- retail's milestones paid them). A row names an owned item by its native card subtype and asset id, which stay valid
-- if the catalogue grows. Nothing is copied from the old model: the owner reset every club's stadium items (10-02).
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.club_vanity_items (
    club_id         bigint      NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    native_subtype  integer     NOT NULL CHECK (native_subtype > 0),
    native_asset_id bigint      NOT NULL CHECK (native_asset_id > 0),
    granted_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (club_id, native_subtype, native_asset_id)
);

-- The Stadium Development milestone groups (rules/objectives.json grindGroups 20-22) join the stored list. A list is a
-- stored rules value start-up never changes, so its rows are removed and start-up adds the bundled list back.
DELETE FROM ultimateteam.content_rules WHERE file = 'objectives' AND path->>0 IN ('grindGroups', '_grindGroupsComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(122);
