-- Preserve exclusive legacy pack eligibility as explicit rarity filters.
-- Numeric rarity and per-player weights no longer take part in special player-pack draws.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.store_packs pack SET allowed_rareflags = ARRAY(
    SELECT DISTINCT flag FROM (
        SELECT key::bigint AS flag FROM jsonb_each(coalesce(pack.special_weights,'{}'::jsonb))
        WHERE key ~ '^[0-9]+$' AND CASE WHEN jsonb_typeof(value)='number' THEN (value #>> '{}')::numeric ELSE 0 END > 0
        UNION ALL SELECT unnest(ARRAY[0,1]::bigint[]) WHERE NOT coalesce(pack.promo_only,false)
    ) flags
    WHERE pack.allowed_rareflags IS NULL OR flag=ANY(pack.allowed_rareflags)
    ORDER BY flag
)
WHERE pack.special_weights_exclusive AND NOT coalesce(pack.allow_unweighted_specials,false);

UPDATE ultimateteam.store_packs SET special_weights='{}'::jsonb, special_weights_exclusive=false
WHERE special_weights_exclusive;
SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(92);
