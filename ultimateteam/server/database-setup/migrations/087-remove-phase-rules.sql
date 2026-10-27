-- The old pack phase no longer selects cards. Keep the operator's red-copy revision
-- order while moving it to the fixed TOTW 1–3 reward rule.
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal, updated_at)
SELECT file, jsonb_build_array('redRewardRevisions', (path->>2)::integer), value, ordinal, updated_at
FROM ultimateteam.content_rules
WHERE file = 'pack_odds' AND path->>0 = 'phase' AND path->>1 = 'redRevisions'
ON CONFLICT (file, path) DO NOTHING;
DELETE FROM ultimateteam.content_rules
WHERE file = 'pack_odds' AND path->>0 IN ('phase', 'informBands', '_phaseComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(87);
