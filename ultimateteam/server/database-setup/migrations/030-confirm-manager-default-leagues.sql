-- User-confirmed remaining defaults, 2026-09-17. Vitor Campelos -> MBS Pro League.
-- Romuald Peyras: user-chosen Premier League default; owned cards are unchanged.
SET CONSTRAINTS ALL IMMEDIATE;
CREATE TEMP TABLE confirmed_manager_leagues(native_resource_id bigint PRIMARY KEY,league_name text NOT NULL) ON COMMIT DROP;
INSERT INTO confirmed_manager_leagues VALUES
(1250425,'Premier League'),
(1017880,'Hellas Liga'),
(1248976,'MBS Pro League'),
(1007000,'Süper Lig'),
(1250434,'Liga I'),
(1249326,'A-League'),
(1135713,'LIGA BBVA MX'),
(1249528,'1A Pro League'),
(1249978,'RSL'),
(1250147,'Allsvenskan'),
(1249805,'LaLiga Santander'),
(1250326,'Allsvenskan'),
(1241188,'LaLiga Santander'),
(1164378,'Serie A TIM'),
(1044888,'A-League'),
(1250664,'MBS Pro League'),
(1256057,'Ö. Bundesliga');
DO $$ BEGIN
 IF (SELECT count(*) FROM confirmed_manager_leagues x JOIN ultimateteam.item_definitions d ON d.native_resource_id=x.native_resource_id AND d.family_id=2 JOIN ultimateteam.manager_definitions m USING(definition_id) JOIN ultimateteam.leagues l ON l.name=x.league_name)<>17 THEN RAISE EXCEPTION 'Confirmed league import must resolve exactly 17 managers and leagues'; END IF;
END $$;
ALTER TABLE ultimateteam.manager_definitions DISABLE TRIGGER lfv4_trg_r2_manager_definitions;
UPDATE ultimateteam.manager_definitions m SET league_id=l.league_id FROM ultimateteam.item_definitions d JOIN confirmed_manager_leagues x ON x.native_resource_id=d.native_resource_id JOIN ultimateteam.leagues l ON l.name=x.league_name WHERE m.definition_id=d.definition_id AND d.family_id=2;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.manager_definitions ENABLE TRIGGER lfv4_trg_r2_manager_definitions;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(30);
