-- My Stadium items as the game names and uses them (10-03): the audio items' resource ids move to retail's ranges (6871000
-- anthems, 6872000 goal songs, 6873000 nicknames, 6874000 chants), and the catalogue loses the game's empty-slot
-- entries (6800000 No TIFO, 6869000 No Prop) and two tifos without art or name (6800376, 6800380). Stored rules keep
-- their values and lists through start-up, so these rows are removed and start-up adds the bundled ones back.
-- Owned items are kept (club_vanity_items names subtype and asset id, which do not change); an owned item the
-- catalogue no longer holds is simply not shown.
SET CONSTRAINTS ALL IMMEDIATE;

DELETE FROM ultimateteam.content_rules
WHERE file = 'club'
  AND (path->>0 IN ('_vanityComment', 'myStadiumSlots') OR (path->>0 = 'vanity' AND path->>1 IN ('resourceOffsets', 'catalogue', 'rewardOnly')));

-- The big tifo slot (18) takes the game's label FUT_STADIUM_TIFO_LARGE (FUT_STADIUM_BIG_TIFO_GOAL_BANNER has no string).
-- Stadium Development: the trophy objectives give retail's named milestone trophies (Match Fundamentals, Squad Battles,
-- Division Rivals, FUT Champions) instead of a random pitch prop, and the Fog Machine objective (no fog machine name exists
-- in the game's strings) pays goal pyrotechnics. grindGroups is a stored list, reloaded the same way.
DELETE FROM ultimateteam.content_rules WHERE file = 'objectives' AND path->>0 IN ('grindGroups', '_grindGroupsComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(137);
