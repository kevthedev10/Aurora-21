-- Eredivisie, EFL and MLS Squad Foundations back to the retail FIFA 21 conditions (live 10-08 02:19 and 02:23 UTC by
-- hand): 7+ league players in the starting XI on every objective, 15 league goals, 15 league assists, 15 matches,
-- 10 wins. Migration 169 had simplified them. Groups are found by sourceKey, not by index. A group whose objectives already carry a
-- startingSquad requirement is left alone (live, or an operator's own edit). Keeps ids, rewards, live state and
-- objective:<id> counters; modes stay Rivals + FUT Champions.
SELECT set_config('ultimateteam.content_publish', 'on', true);
DO $sf$
DECLARE g record; gi text; o int; base jsonb; nxt int;
BEGIN
    FOR g IN SELECT * FROM (VALUES (10, 'Eredivisie', 'eredivisie_squad_foundation', 'Assistance Required'),
                                   (14, 'EFL Championship', 'efl_squad_foundation', 'Assistance Required'),
                                   (39, 'MLS', 'mls_squad_foundation', 'Made by MLS')) v(league, lname, src, assist_name)
    LOOP
        SELECT path->>1 INTO gi FROM ultimateteam.content_rules
        WHERE file='objectives' AND path->>0='grindGroups' AND path->>2='sourceKey' AND jsonb_array_length(path)=3
            AND value=to_jsonb(g.src)
        LIMIT 1;
        CONTINUE WHEN gi IS NULL;
        CONTINUE WHEN EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives'
            AND path->>0='grindGroups' AND path->>1=gi AND path->>2='objectives'
            AND path->>4='requirements' AND path->>5='startingSquad');
        CONTINUE WHEN (SELECT count(DISTINCT path->>3) FROM ultimateteam.content_rules WHERE file='objectives'
            AND path->>0='grindGroups' AND path->>1=gi AND path->>2='objectives' AND path->>4='stat') <> 4;
        FOR o IN 0..3 LOOP
            base := jsonb_build_array('grindGroups', gi::int, 'objectives', o);
            SELECT coalesce(max(ordinal), 0)+1 INTO nxt FROM ultimateteam.content_rules WHERE file='objectives';
            UPDATE ultimateteam.content_rules SET value=to_jsonb(CASE o WHEN 0 THEN 'goals' WHEN 1 THEN 'assists'
                    WHEN 2 THEN 'matches' ELSE 'wins' END), updated_at=now()
                WHERE file='objectives' AND path=base || '["stat"]'::jsonb;
            UPDATE ultimateteam.content_rules SET value=to_jsonb(CASE WHEN o=3 THEN 10 ELSE 15 END), updated_at=now()
                WHERE file='objectives' AND path=base || '["target"]'::jsonb;
            UPDATE ultimateteam.content_rules SET value=to_jsonb(CASE o
                    WHEN 0 THEN 'Score {n} goals using '||g.lname||' players in Division Rivals or FUT Champions, with at least 7 '||g.lname||' players in your starting squad.'
                    WHEN 1 THEN 'Assist {n} goals using '||g.lname||' players in Division Rivals or FUT Champions, with at least 7 '||g.lname||' players in your starting squad.'
                    WHEN 2 THEN 'Play {n} matches in Division Rivals or FUT Champions, with at least 7 '||g.lname||' players in your starting squad.'
                    ELSE 'Win {n} matches in Division Rivals or FUT Champions, with at least 7 '||g.lname||' players in your starting squad.' END),
                    updated_at=now()
                WHERE file='objectives' AND path=base || '["description"]'::jsonb;
            IF o = 1 THEN
                UPDATE ultimateteam.content_rules SET value=to_jsonb(g.assist_name), updated_at=now()
                    WHERE file='objectives' AND path=base || '["name"]'::jsonb;
            END IF;
            INSERT INTO ultimateteam.content_rules(file,path,value,ordinal) VALUES
                ('objectives', base || '["requirements","startingSquad","leagueId"]'::jsonb, to_jsonb(g.league), nxt),
                ('objectives', base || '["requirements","startingSquad","minimumCount"]'::jsonb, '7'::jsonb, nxt+1)
                ON CONFLICT(file,path) DO UPDATE SET value=EXCLUDED.value, updated_at=now();
            IF o IN (0, 1) THEN
                INSERT INTO ultimateteam.content_rules(file,path,value,ordinal) VALUES
                    ('objectives', base || '["requirements","player","leagueId"]'::jsonb, to_jsonb(g.league), nxt+2)
                    ON CONFLICT(file,path) DO UPDATE SET value=EXCLUDED.value, updated_at=now();
            END IF;
        END LOOP;
    END LOOP;
END $sf$;
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(178);
