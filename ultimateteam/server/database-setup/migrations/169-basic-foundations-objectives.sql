-- Simplify only the imported Squad Foundations conditions. Keep IDs, rewards, live
-- state, starts, baselines and objective:<id> counters (including settled claims).
SELECT set_config('ultimateteam.content_publish', 'on', true);
DO $basic$
DECLARE obj record; base jsonb; new_stat text; new_description text;
BEGIN
    FOR obj IN
        SELECT s.path, s.value #>> '{}' AS stat
        FROM ultimateteam.content_rules s
        JOIN ultimateteam.content_rules g ON g.file=s.file AND g.path->>0=s.path->>0
            AND g.path->>1=s.path->>1 AND g.path->>2='sourceKey' AND jsonb_array_length(g.path)=3
        WHERE s.file='objectives' AND s.path->>0='grindGroups' AND s.path->>2='objectives'
            AND s.path->>4='stat' AND jsonb_array_length(s.path)=5
            AND g.value #>> '{}' IN ('mls_squad_foundation_ii','st_patricks_squad_foundation',
                'efl_squad_foundation_ii','eredivisie_squad_foundation_ii','eredivisie_squad_foundation',
                'efl_squad_foundation','mls_squad_foundation')
            AND EXISTS(SELECT 1 FROM ultimateteam.content_rules r WHERE r.file=s.file
                AND r.path->>0=s.path->>0 AND r.path->>1=s.path->>1 AND r.path->>2=s.path->>2
                AND r.path->>3=s.path->>3 AND r.path->>4='requirements'
                AND r.path->>5 IN ('startingSquad','player','goalType','assistType'))
    LOOP
        base := obj.path - 4;
        new_stat := CASE obj.stat WHEN 'goals' THEN 'scoringMatches' WHEN 'assists' THEN 'goals' ELSE obj.stat END;
        new_description := CASE obj.stat
            WHEN 'goals' THEN 'Score at least one goal in {n} separate Division Rivals or FUT Champions matches.'
            WHEN 'assists' THEN 'Score {n} goals in Division Rivals or FUT Champions.'
            WHEN 'matches' THEN 'Play {n} matches in Division Rivals or FUT Champions.'
            ELSE 'Win {n} matches in Division Rivals or FUT Champions.' END;
        UPDATE ultimateteam.content_rules SET value=to_jsonb(new_stat) WHERE file='objectives' AND path=obj.path;
        UPDATE ultimateteam.content_rules SET value=to_jsonb(new_description) WHERE file='objectives' AND path=base || '["description"]'::jsonb;
        IF obj.stat IN ('goals','assists') THEN
            UPDATE ultimateteam.content_rules SET value='10'::jsonb WHERE file='objectives' AND path=base || '["target"]'::jsonb;
            UPDATE ultimateteam.content_rules SET value=to_jsonb(CASE obj.stat WHEN 'goals' THEN 'Scoring Streak' ELSE 'Goal Getter' END)
                WHERE file='objectives' AND path=base || '["name"]'::jsonb;
        END IF;
        DELETE FROM ultimateteam.content_rules WHERE file='objectives'
            AND path->>0=base->>0 AND path->>1=base->>1 AND path->>2=base->>2 AND path->>3=base->>3
            AND path->>4='requirements' AND path->>5 IN ('startingSquad','player','goalType','assistType');
    END LOOP;
    INSERT INTO ultimateteam.content_rules(file,path,value,ordinal)
        SELECT 'objectives','["statLabels","scoringMatches"]'::jsonb,
            '"Score at least one goal in {n} separate FUT matches."'::jsonb,coalesce(max(ordinal),-1)+1
        FROM ultimateteam.content_rules WHERE file='objectives'
        HAVING EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives')
        ON CONFLICT(file,path) DO NOTHING;
END $basic$;
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(169);
