-- Preserve the operator's red rarity list under its reward-specific name.
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal, updated_at)
SELECT file, jsonb_build_array('futChampionsRedRareflags', (path->>1)::integer), value, ordinal, updated_at
FROM ultimateteam.content_rules
WHERE file = 'cards' AND path->>0 = 'liveTotwRedRareflags'
ON CONFLICT (file, path) DO NOTHING;
DELETE FROM ultimateteam.content_rules
WHERE file = 'cards' AND path->>0 IN ('liveTotwRedRareflags', '_liveTotwRedComment');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(88);
