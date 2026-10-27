-- The rating-based discard model replaced fixed-value rules without an upgrade
-- for databases already seeded by 073. Startup fills missing keys from the
-- reviewed rules/cards.json after migrations; remove only obsolete definitions.
-- Existing multiplier rules and unrelated operator settings remain untouched.
-- Fixed-value inForm arrays cannot be merged by index with multiplier arrays.
DELETE FROM ultimateteam.content_rules
WHERE file='cards' AND path->>0='quickSell' AND path->>1='inForm'
    AND EXISTS (
        SELECT 1 FROM ultimateteam.content_rules legacy
        WHERE legacy.file='cards' AND legacy.path->>0='quickSell'
            AND legacy.path->>1='inForm' AND legacy.path->>3='value'
    );

DELETE FROM ultimateteam.content_rules
WHERE file='cards' AND path->>0='quickSell' AND (
    path->>1 IN ('common','rare','specialDefault','specialByRareflag','inFormRareflags')
    OR (path->>1='tiers' AND path->>3 IN ('common','rare'))
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(112);
