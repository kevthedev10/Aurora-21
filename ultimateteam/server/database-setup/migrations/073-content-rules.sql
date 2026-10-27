-- Game rules in PostgreSQL (ultimateteam/live-content-editing-plan.md, step 6). Every individual rule of the bundled
-- runtime-source/ultimateteam21/rules/*.json files (packs.json excepted: packs have their own tables since 041) is one
-- row, and the server reads its rules from these rows only. The server fills a file's rows from the bundled file when
-- it has none (a fresh database, or the first start of this version); from then on rules change through reviewed
-- content changesets (the operator panel's Rules page and its "Import from files"), and a new key a code update adds
-- to a file is added at start-up. The rows rebuild each file exactly: path is the JSON location of one value, its
-- strings object keys and its numbers list positions; value is that scalar, or [] / {} for an empty list or object;
-- ordinal keeps the file's order.
CREATE TABLE IF NOT EXISTS ultimateteam.content_rules (
    file text NOT NULL,
    path jsonb NOT NULL,
    value jsonb NOT NULL,
    ordinal integer NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT content_rules_pk PRIMARY KEY (file, path),
    CONSTRAINT content_rules_file CHECK (file ~ '^[a-z][a-z0-9_]{0,63}$'),
    CONSTRAINT content_rules_path CHECK (jsonb_typeof(path) = 'array' AND jsonb_array_length(path) > 0),
    CONSTRAINT content_rules_value CHECK (jsonb_typeof(value) NOT IN ('array', 'object') OR value IN ('[]'::jsonb, '{}'::jsonb))
);
CREATE INDEX IF NOT EXISTS content_rules_file_order ON ultimateteam.content_rules(file, ordinal);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(73);
