-- Each club serves its virtual cosmetics (active stadium, kits, badge, ball) from reserved ids at the top of its own
-- item id band instead of ids every club shared (rules/club.json virtualItemIdSlots): two clubs in one online match
-- drew each other's crest (observed on the Python server, commit e2e098f). The ids are computed, not stored, except
-- the badge item id onboarding remembers, which moves from the shared 5004 to the club's own badge slot (4).
UPDATE ultimateteam.onboarding SET badge_item_id = 900000000000 + club_id * 10000000 - 16 + 4 WHERE badge_item_id = 5004;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(46);
