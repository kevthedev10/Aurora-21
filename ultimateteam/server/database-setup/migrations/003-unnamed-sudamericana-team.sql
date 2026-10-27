-- Version 3: preserve the sourced native team ID without inventing a club name.
-- Four pinned Sudamericana definitions use 110978 and have a blank source teamName.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.teams, ultimateteam.player_card_definitions IN ACCESS EXCLUSIVE MODE;
INSERT INTO ultimateteam.teams(native_team_id,name,source_id)
SELECT 110978,'',min(d.source_id)
FROM ultimateteam.player_card_definitions p
JOIN ultimateteam.item_definitions d USING(definition_id)
WHERE p.native_team_id=110978
HAVING count(*) > 0
ON CONFLICT(native_team_id) DO NOTHING;
ALTER TABLE ultimateteam.player_card_definitions DISABLE TRIGGER lfv4_trg_r2_player_card_definitions;
UPDATE ultimateteam.player_card_definitions p
SET team_id=t.team_id
FROM ultimateteam.teams t
WHERE p.native_team_id=110978 AND p.team_id IS NULL AND t.native_team_id=110978;
ALTER TABLE ultimateteam.player_card_definitions ENABLE TRIGGER lfv4_trg_r2_player_card_definitions;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(3);
