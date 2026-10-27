-- Reuse the existing Rulebreakers event and cards. Both teams release together;
-- the old event must not start an hour before the campaign or overlap OTW.
DO $$
DECLARE
    event_row ultimateteam.promo_events%ROWTYPE;
    campaign bigint;
    changeset bigint;
    start_time timestamptz := timestamp '2026-10-12 19:00' AT TIME ZONE 'Europe/London';
    end_time timestamptz := timestamp '2026-10-19 19:00' AT TIME ZONE 'Europe/London';
    state text;
    before_json jsonb;
    after_json jsonb;
BEGIN
    SELECT * INTO event_row FROM ultimateteam.promo_events WHERE code='event_rulebreakers_team_1';
    IF NOT FOUND OR EXISTS (
        SELECT 1 FROM ultimateteam.content_changes c JOIN ultimateteam.content_drops d USING(changeset_id)
        WHERE c.area='promo_event' AND c.target_key->>'code'=event_row.code
    ) THEN RETURN; END IF;
    IF (SELECT count(*) FROM ultimateteam.promos
        WHERE code IN ('rulebreakers_team_1','rulebreakers_team_2') AND status='active')<>2 THEN
        RAISE EXCEPTION 'Rulebreakers campaign requires active Team 1 and Team 2 promo definitions';
    END IF;

    PERFORM set_config('ultimateteam.content_publish','on',true);
    -- Existing named pack odds only include Team 1 in the VPS catalogue. Share
    -- that budget across the two teams, preserving explicitly configured Team 2 odds.
    UPDATE ultimateteam.store_packs SET category_chances=category_chances || jsonb_build_object(
        'rulebreakers_team_1',(category_chances->>'rulebreakers_team_1')::numeric/2,
        'rulebreakers_team_2',(category_chances->>'rulebreakers_team_1')::numeric/2)
    WHERE jsonb_typeof(category_chances->'rulebreakers_team_1')='number'
        AND NOT category_chances ? 'rulebreakers_team_2';
    UPDATE ultimateteam.store_packs SET promo_codes=array_append(promo_codes,'rulebreakers_team_2')
    WHERE promo_codes @> ARRAY['rulebreakers_team_1']::text[]
        AND NOT promo_codes @> ARRAY['rulebreakers_team_2']::text[];
    UPDATE ultimateteam.promo_events SET starts_at=start_time,ends_at=end_time WHERE event_id=event_row.event_id;
    state := CASE WHEN now()>=end_time THEN 'ended' WHEN now()>=start_time THEN 'live' ELSE 'scheduled' END;
    -- The baseline matches the normalized row and PromoTimingArea's UTC format.
    -- Team 2 joins the event only when this drop publishes.
    before_json := jsonb_build_object('name',event_row.name,'enabled',event_row.enabled,
        'starts_at',to_char(start_time AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z',
        'ends_at',to_char(end_time AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0Z',
        'promos',(SELECT coalesce(jsonb_agg(p.code ORDER BY p.code),'[]'::jsonb)
            FROM ultimateteam.promo_event_promos l JOIN ultimateteam.promos p USING(promo_id)
            WHERE l.event_id=event_row.event_id AND l.enabled));
    after_json := before_json || jsonb_build_object('name','Rulebreakers Teams 1 + 2','enabled',true,
        'promos',jsonb_build_array('rulebreakers_team_1','rulebreakers_team_2'));
    INSERT INTO ultimateteam.content_campaigns(name,starts_at,ends_at)
    VALUES('Rulebreakers',start_time,end_time) RETURNING campaign_id INTO campaign;
    INSERT INTO ultimateteam.content_changesets(status,note,published_at)
    VALUES(CASE WHEN state='scheduled' THEN 'draft' ELSE 'published' END,
        'Migration 173: Rulebreakers Teams 1 + 2',CASE WHEN state<>'scheduled' THEN start_time END)
    RETURNING changeset_id INTO changeset;
    INSERT INTO ultimateteam.content_changes(changeset_id,area,target_key,operation,before_value,after_value)
    VALUES(changeset,'promo_event',jsonb_build_object('code',event_row.code),'update',before_json,after_json);
    INSERT INTO ultimateteam.content_drops(campaign_id,name,starts_at,ends_at,status,changeset_id,published_at,expires)
    VALUES(campaign,'Rulebreakers Teams 1 + 2',start_time,end_time,state,changeset,
        CASE WHEN state<>'scheduled' THEN start_time END,
        jsonb_build_array(jsonb_build_object('kind','promo_event','target',event_row.code,'endsAt',end_time,'done',state='ended')));

    IF state<>'scheduled' THEN
        -- Late upgrades adopt current availability or history without replaying it.
        UPDATE ultimateteam.promo_events SET name='Rulebreakers Teams 1 + 2',enabled=state='live'
        WHERE event_id=event_row.event_id;
        UPDATE ultimateteam.promo_event_promos SET enabled=false WHERE event_id=event_row.event_id;
        INSERT INTO ultimateteam.promo_event_promos(event_id,promo_id,enabled)
        SELECT event_row.event_id,promo_id,true FROM ultimateteam.promos
        WHERE code IN ('rulebreakers_team_1','rulebreakers_team_2')
        ON CONFLICT(event_id,promo_id) DO UPDATE SET enabled=true;
    END IF;
    PERFORM set_config('ultimateteam.content_publish','',true);
END $$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(173);
