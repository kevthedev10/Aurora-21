-- My Stadium becomes FIFA 21's retail layout: FIFA's own slot ids (eVanitySlotSubType) and labels, retail's four
-- sections, several slots per item type (tifo positions, eight prop slots), and the audio items (anthems, goal songs,
-- chants, nicknames). The old slot ids meant other things to FIFA (8 was celebrations, 10 mow patterns, 11 the fog
-- machine, 25 goal pyro), so every club's equipped vanity choices are cleared (owner decision 10-02).
DELETE FROM ultimateteam.club_vanity_slots;

-- The slot list and the changed comments and lists are stored rules values, which start-up never changes. Removing them
-- lets start-up add them back from the bundled rules/club.json (missing keys are filled from the file); the new
-- catalogue entries and resource offsets are new keys and arrive the same way.
DELETE FROM ultimateteam.content_rules
WHERE file = 'club' AND path->>0 IN ('myStadiumSlots', '_myStadiumComment', '_vanityComment', 'unsupportedVanityCategories');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(121);
