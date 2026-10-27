-- Adopt the existing October launch content into Schedule without recreating cards,
-- changing pack odds, or touching any club/completion/reward records.
-- Historical releases remain historical; future releases use the normal scheduler.
DO $$
DECLARE
    campaign bigint;
    changeset bigint;
    release record;
    item record;
    event_row ultimateteam.promo_events%ROWTYPE;
    start_time timestamptz := timestamp '2026-10-05 19:00' AT TIME ZONE 'Europe/London';
    end_time timestamptz := timestamp '2026-10-12 19:00' AT TIME ZONE 'Europe/London';
    release_time timestamptz;
    state text;
    before_json jsonb;
    after_json jsonb;
    expiries jsonb;
BEGIN
    SELECT * INTO event_row FROM ultimateteam.promo_events WHERE code='event_ones_to_watch';
    -- Fresh installs and databases where the operator already migrated this event
    -- must not receive a second release or a campaign with no underlying content.
    IF NOT FOUND OR EXISTS (
        SELECT 1 FROM ultimateteam.content_changes c JOIN ultimateteam.content_drops d USING(changeset_id)
        WHERE c.area='promo_event' AND c.target_key->>'code'=event_row.code
    ) THEN RETURN; END IF;

    PERFORM set_config('ultimateteam.content_publish','on',true);
    INSERT INTO ultimateteam.content_campaigns(name,starts_at,ends_at)
    VALUES('Ones to Watch',start_time,end_time) RETURNING campaign_id INTO campaign;

    CREATE TEMP TABLE otw_campaign_items(day integer, name text, kind text, target text, enabled boolean) ON COMMIT DROP;
    INSERT INTO otw_campaign_items VALUES(5,'OTW launch + Squad Foundations 1','promo_event',event_row.code,event_row.enabled);
    INSERT INTO otw_campaign_items
    SELECT 5,'OTW launch + Squad Foundations 1','sbc',s.set_key,coalesce(l.enabled,false)
    FROM ultimateteam.sbc_sets s LEFT JOIN ultimateteam.sbc_live l USING(set_key)
    WHERE s.set_key=CASE WHEN EXISTS(SELECT 1 FROM ultimateteam.sbc_sets WHERE set_key='OTW_OR_TOTW_UPGRADE_II')
        THEN 'OTW_OR_TOTW_UPGRADE_II' ELSE 'OTW_OR_TOTW_UPGRADE' END;
    INSERT INTO otw_campaign_items
    SELECT CASE s.set_key WHEN 'FLASHBACK_AXEL_WITSEL' THEN 6 ELSE 7 END,
        CASE s.set_key WHEN 'FLASHBACK_AXEL_WITSEL' THEN 'Flashback Witsel' ELSE 'POTM 1' END,
        'sbc',s.set_key,coalesce(l.enabled,false)
    FROM ultimateteam.sbc_sets s LEFT JOIN ultimateteam.sbc_live l USING(set_key)
    WHERE s.set_key IN ('FLASHBACK_AXEL_WITSEL','POTM_1');
    INSERT INTO otw_campaign_items
    SELECT CASE WHEN source_key.value #>> '{}' IN ('eredivisie_squad_foundation','efl_squad_foundation','mls_squad_foundation') THEN 5 ELSE 8 END,
        CASE WHEN source_key.value #>> '{}' IN ('eredivisie_squad_foundation','efl_squad_foundation','mls_squad_foundation') THEN 'OTW launch + Squad Foundations 1' ELSE 'Squad Foundations 2' END,
        'objective',id.value::text,coalesce(live_enabled.value='true'::jsonb,false)
    FROM ultimateteam.content_rules id
    JOIN ultimateteam.content_rules source_key ON source_key.file=id.file
        AND source_key.path=jsonb_build_array('grindGroups',(id.path->>1)::integer,'sourceKey')
    LEFT JOIN ultimateteam.content_rules live_id ON live_id.file='objectives' AND live_id.path->>0='live'
        AND live_id.path->>2='group' AND live_id.value=id.value
    LEFT JOIN ultimateteam.content_rules live_enabled ON live_enabled.file='objectives'
        AND live_enabled.path=jsonb_build_array('live',(live_id.path->>1)::integer,'enabled')
    WHERE id.file='objectives' AND id.path->>0='grindGroups' AND id.path->>2='id'
        AND source_key.value #>> '{}' IN ('mls_squad_foundation_ii','st_patricks_squad_foundation',
            'efl_squad_foundation_ii','eredivisie_squad_foundation_ii',
            'eredivisie_squad_foundation','efl_squad_foundation','mls_squad_foundation');

    UPDATE ultimateteam.sbc_sets s SET definition=definition || '{"repeatable":true,"repeatableDaily":true}'::jsonb,updated_at=now()
    WHERE s.set_key IN (SELECT target FROM otw_campaign_items WHERE day=5 AND kind='sbc');
    -- The requested window is 19:00 London, not the legacy event's 18:00 / 18:59 window.
    UPDATE ultimateteam.promo_events SET starts_at=start_time,ends_at=end_time WHERE event_id=event_row.event_id;

    FOR release IN SELECT DISTINCT day,name FROM otw_campaign_items ORDER BY day LOOP
        release_time := (make_date(2026,10,release.day)+time '19:00') AT TIME ZONE 'Europe/London';
        state := CASE WHEN now()>=end_time THEN 'ended'
            WHEN release_time<=now() AND NOT EXISTS(SELECT 1 FROM otw_campaign_items WHERE day=release.day AND NOT enabled) THEN 'live'
            ELSE 'scheduled' END;
        INSERT INTO ultimateteam.content_changesets(status,note,published_at)
        VALUES(CASE WHEN state IN ('live','ended') THEN 'published' ELSE 'draft' END,
            'Migration 171: adopt existing OTW content',CASE WHEN state IN ('live','ended') THEN release_time END)
        RETURNING changeset_id INTO changeset;
        expiries := '[]'::jsonb;
        FOR item IN SELECT * FROM otw_campaign_items WHERE day=release.day LOOP
            IF item.kind='promo_event' THEN
                before_json := jsonb_build_object('name',event_row.name,'enabled',event_row.enabled,
                    -- Match PromoTimingArea's round-trip UTC strings. A scheduled
                    -- adoption starts from the normalized row already stored above.
                    'starts_at',to_char((CASE WHEN state='scheduled' THEN start_time ELSE event_row.starts_at END) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z',
                    'ends_at',to_char((CASE WHEN state='scheduled' THEN end_time ELSE event_row.ends_at END) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z',
                    'promos',(SELECT coalesce(jsonb_agg(p.code ORDER BY p.code),'[]'::jsonb)
                        FROM ultimateteam.promo_event_promos l JOIN ultimateteam.promos p USING(promo_id)
                        WHERE l.event_id=event_row.event_id AND l.enabled));
                after_json := before_json || jsonb_build_object('enabled',true,
                    'starts_at',to_char(start_time AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z',
                    'ends_at',to_char(end_time AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z');
                INSERT INTO ultimateteam.content_changes(changeset_id,area,target_key,operation,before_value,after_value)
                VALUES(changeset,'promo_event',jsonb_build_object('code',item.target),'update',before_json,after_json);
            ELSE
                INSERT INTO ultimateteam.content_changes(changeset_id,area,target_key,operation,before_value,after_value)
                VALUES(changeset,'content_window',jsonb_build_object('kind',item.kind,'target',item.target),
                    'update',jsonb_build_object('enabled',item.enabled),'{"enabled":true}');
                -- Avoid exposing an already-enabled item ahead of its release, and
                -- close expired content without replaying historical releases.
                IF release_time>now() OR end_time<=now() THEN
                    IF item.kind='sbc' THEN
                        UPDATE ultimateteam.sbc_live SET enabled=false,updated_at=now() WHERE set_key=item.target AND enabled;
                    ELSE
                        UPDATE ultimateteam.content_rules r SET value='false'::jsonb,updated_at=now()
                        FROM ultimateteam.content_rules id WHERE id.file='objectives' AND id.path->>0='live'
                            AND id.path->>2='group' AND id.value=to_jsonb(item.target::bigint)
                            AND r.file='objectives' AND r.path=jsonb_build_array('live',(id.path->>1)::integer,'enabled')
                            AND r.value='true'::jsonb;
                    END IF;
                END IF;
            END IF;
            expiries := expiries || jsonb_build_array(jsonb_build_object('kind',item.kind,'target',item.target,'endsAt',end_time,'done',state='ended'));
        END LOOP;
        INSERT INTO ultimateteam.content_drops(campaign_id,name,starts_at,ends_at,status,changeset_id,published_at,expires)
        VALUES(campaign,release.name,release_time,end_time,state,changeset,
            CASE WHEN state IN ('live','ended') THEN release_time END,expiries);
    END LOOP;
    PERFORM set_config('ultimateteam.content_publish','',true);
END $$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(171);
