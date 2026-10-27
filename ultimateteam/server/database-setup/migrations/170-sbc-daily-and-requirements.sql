-- Daily claims are separate from lifetime SBC completion/reward history.
CREATE TABLE ultimateteam.sbc_daily_completed (
    club_id bigint NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    challenge_key text NOT NULL,
    reset_at bigint NOT NULL,
    PRIMARY KEY(club_id,challenge_key,reset_at)
);
SELECT set_config('ultimateteam.content_publish', 'on', true);
INSERT INTO ultimateteam.content_rules(file,path,value,ordinal)
SELECT 'sbc', additions.path, additions.value,
    coalesce((SELECT max(ordinal) FROM ultimateteam.content_rules WHERE file='sbc'),-1) + row_number() OVER ()
FROM (VALUES
('["requirements","LEAGUE_PLAYERS","bound"]'::jsonb,'"min"'::jsonb),
('["requirements","LEAGUE_PLAYERS","eligibilityKey"]'::jsonb,'"LeagueId"'::jsonb),
('["requirements","LEAGUE_PLAYERS","selector"]'::jsonb,'"leagueId"'::jsonb),
('["requirements","LEAGUE_PLAYERS","scope"]'::jsonb,'"AtLeast"'::jsonb),
('["requirements","LEAGUE_PLAYERS","label"]'::jsonb,'"League players ({selection}): {comparison} {value}"'::jsonb),
('["requirements","NATION_PLAYERS","bound"]'::jsonb,'"min"'::jsonb),
('["requirements","NATION_PLAYERS","eligibilityKey"]'::jsonb,'"NationId"'::jsonb),
('["requirements","NATION_PLAYERS","selector"]'::jsonb,'"nationId"'::jsonb),
('["requirements","NATION_PLAYERS","scope"]'::jsonb,'"AtLeast"'::jsonb),
('["requirements","NATION_PLAYERS","label"]'::jsonb,'"Nation players ({selection}): {comparison} {value}"'::jsonb),
('["requirements","CLUB_PLAYERS","bound"]'::jsonb,'"min"'::jsonb),
('["requirements","CLUB_PLAYERS","eligibilityKey"]'::jsonb,'"ClubId"'::jsonb),
('["requirements","CLUB_PLAYERS","selector"]'::jsonb,'"clubId"'::jsonb),
('["requirements","CLUB_PLAYERS","scope"]'::jsonb,'"AtLeast"'::jsonb),
('["requirements","CLUB_PLAYERS","label"]'::jsonb,'"Club players ({selection}): {comparison} {value}"'::jsonb),
('["requirements","QUALITY_PLAYERS","bound"]'::jsonb,'"min"'::jsonb),
('["requirements","QUALITY_PLAYERS","eligibilityKey"]'::jsonb,'"Quality"'::jsonb),
('["requirements","QUALITY_PLAYERS","selector"]'::jsonb,'"quality"'::jsonb),
('["requirements","QUALITY_PLAYERS","scope"]'::jsonb,'"AtLeast"'::jsonb),
('["requirements","QUALITY_PLAYERS","label"]'::jsonb,'"{selection} players: {comparison} {value}"'::jsonb),
('["requirements","QUALITY_PLAYERS","qualities","bronze"]'::jsonb,'1'::jsonb),
('["requirements","QUALITY_PLAYERS","qualities","silver"]'::jsonb,'2'::jsonb),
('["requirements","QUALITY_PLAYERS","qualities","gold"]'::jsonb,'3'::jsonb)
) additions(path,value)
WHERE EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='sbc')
ON CONFLICT(file,path) DO NOTHING;
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(170);
