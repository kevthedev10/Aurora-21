-- One non-player drop table (rules/pack_odds.json nonPlayerDrops). Packs drew chemistry styles, position modifiers, league
-- cards, managers and cosmetics five different ways, and a mixed pack's rare items could only fill its rare slots, which go
-- to players: live 10-04, each rare chemistry style (Hawk, Engine, Anchor, Hunter, Catalyst, Shadow) had 2-10 copies across
-- every club against 350-430 for each common style, and kits and badges (all rare) almost never dropped. Now a pack's rare
-- slots are its rare players, and every other item is drawn from its pool with rare items at rareWeight of a common one's
-- chance. The Match Pack's own meta style table goes; start-up adds nonPlayerDrops from the bundled file.
DELETE FROM ultimateteam.content_rules
WHERE file = 'pack_odds' AND path->>0 = 'matchPack' AND path->>1 = 'chemistryStyles';

-- Refresh the Match Pack's explanatory text from the bundled file on the next load.
DELETE FROM ultimateteam.content_rules WHERE file = 'pack_odds' AND path = '["_matchPackComment"]'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(150);
