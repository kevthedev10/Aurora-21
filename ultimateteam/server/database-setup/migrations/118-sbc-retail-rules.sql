-- The retail SBC rules (fix/sbc-locked-slots) reach existing databases, not just new seeds. Start-up filling only adds
-- missing object keys (the new CLUBS rule and _formationComment arrive that way); it never changes a stored value or
-- list item, so these three changes need a migration. Only the old shipped values are replaced, so an operator's own
-- edit is kept.

-- 1. Chemistry is shown and checked: the server now computes it from the saved squad (SbcService.Validation), so it is
--    no longer an ignored requirement. A list left empty is stored as one [] row (073's contract), at the item's ordinal.
--    The statement sees the table as it was before the DELETE, so "left empty" means no item other than CHEMISTRY.
WITH chemistry AS (
    DELETE FROM ultimateteam.content_rules
    WHERE file = 'sbc' AND jsonb_array_length(path) = 2 AND path->>0 = 'ignoredRequirements' AND value = '"CHEMISTRY"'::jsonb
    RETURNING ordinal
)
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
SELECT 'sbc', '["ignoredRequirements"]'::jsonb, '[]'::jsonb, min(ordinal) FROM chemistry
HAVING count(*) > 0
   AND NOT EXISTS (SELECT 1 FROM ultimateteam.content_rules
                   WHERE file = 'sbc' AND path->>0 = 'ignoredRequirements' AND value <> '"CHEMISTRY"'::jsonb)
ON CONFLICT (file, path) DO NOTHING;

UPDATE ultimateteam.content_rules SET value = to_jsonb('Requirement rule types dropped from every challenge: neither sent to the client nor checked on submit. CHEMISTRY is sent and checked: the server computes the squad''s chemistry (chemistry.json) from the slots, formation and manager FIFA saves, plus the challenge''s locked-slot cards, and never trusts the chemistry FIFA reports.'::text),
    updated_at = now()
WHERE file = 'sbc' AND path = '["_ignoredRequirementsComment"]'::jsonb AND jsonb_typeof(value) = 'string'
    AND strpos(value #>> '{}', 'CHEMISTRY is dropped because the server cannot check it') > 0;

-- 2. FND_ADVANCED (Gold Rush, Rising Standard, Marquee Squad, Elite Standard) has no retail FIFA 21 source: hide it.
--    Appended after the last hiddenSets item; later rows of the file move down one ordinal to make room.
DO $$
DECLARE last_item record;
BEGIN
    IF EXISTS (SELECT 1 FROM ultimateteam.content_rules
               WHERE file = 'sbc' AND path->>0 = 'hiddenSets' AND value = '"FND_ADVANCED"'::jsonb) THEN
        RETURN;
    END IF;
    SELECT (path->>1)::integer AS position, ordinal INTO last_item FROM ultimateteam.content_rules
    WHERE file = 'sbc' AND jsonb_array_length(path) = 2 AND path->>0 = 'hiddenSets'
    ORDER BY ordinal DESC LIMIT 1;
    IF last_item IS NULL THEN
        RETURN;
    END IF;
    UPDATE ultimateteam.content_rules SET ordinal = ordinal + 1 WHERE file = 'sbc' AND ordinal > last_item.ordinal;
    INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
    VALUES ('sbc', jsonb_build_array('hiddenSets', last_item.position + 1), '"FND_ADVANCED"'::jsonb, last_item.ordinal + 1);
END $$;

-- 3. A challenge with no client-visible rule names its player count instead of always claiming eleven
--    (First Exchange takes one player).
UPDATE ultimateteam.content_rules SET value = '"Players in Squad: {count}"'::jsonb, updated_at = now()
WHERE file = 'sbc' AND path = '["text","noRequirements"]'::jsonb AND value = '"Submit an 11-player squad."'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(118);
