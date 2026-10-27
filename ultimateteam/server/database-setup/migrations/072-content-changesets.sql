-- Live content editing (ultimateteam/live-content-editing-plan.md, step 3). Content edits made in the operator panel wait
-- as rows of one open changeset until the operator reviews and publishes them; publishing applies every change in one
-- transaction, validates the resulting content with the start-up loader and swaps it into the running server. Each
-- change keeps the row as it was when edited (before_value), so a publish refuses a row that changed since, and a
-- published changeset can be reverted by publishing its before values.
CREATE TABLE IF NOT EXISTS ultimateteam.content_changesets (
    changeset_id bigserial PRIMARY KEY,
    status text NOT NULL,
    note text,
    created_by text,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz,
    reverts_changeset_id bigint REFERENCES ultimateteam.content_changesets(changeset_id),
    CONSTRAINT content_changesets_status CHECK (status IN ('open', 'published', 'discarded')),
    CONSTRAINT content_changesets_published CHECK ((status = 'published') = (published_at IS NOT NULL)),
    CONSTRAINT content_changesets_note_length CHECK (note IS NULL OR length(note) <= 500),
    CONSTRAINT content_changesets_created_by_length CHECK (created_by IS NULL OR length(created_by) <= 64)
);
-- One open changeset at a time: every panel content edit adds to it.
CREATE UNIQUE INDEX IF NOT EXISTS content_changesets_one_open ON ultimateteam.content_changesets((true)) WHERE status = 'open';
CREATE INDEX IF NOT EXISTS content_changesets_published ON ultimateteam.content_changesets(published_at DESC) WHERE status = 'published';

CREATE TABLE IF NOT EXISTS ultimateteam.content_changes (
    change_id bigserial PRIMARY KEY,
    changeset_id bigint NOT NULL REFERENCES ultimateteam.content_changesets(changeset_id),
    area text NOT NULL,
    target_key jsonb NOT NULL,
    operation text NOT NULL,
    before_value jsonb,
    after_value jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT content_changes_operation CHECK (operation IN ('insert', 'update', 'hide')),
    CONSTRAINT content_changes_area CHECK (area ~ '^[a-z][a-z0-9_:.-]{0,63}$'),
    CONSTRAINT content_changes_key CHECK (jsonb_typeof(target_key) = 'object'),
    CONSTRAINT content_changes_values CHECK (
        (operation = 'insert' AND before_value IS NULL AND after_value IS NOT NULL)
        OR (operation = 'update' AND before_value IS NOT NULL AND after_value IS NOT NULL)
        OR (operation = 'hide' AND before_value IS NOT NULL AND after_value IS NULL)),
    CONSTRAINT content_changes_one_per_row UNIQUE (changeset_id, area, target_key)
);

-- Recipe 2 (schema/003-invariants.sql) kept the catalogue insert-only; migrations changed rows by disabling its
-- triggers. The publish pipeline may now UPDATE catalogue rows inside its own transaction, which sets the
-- transaction-local ultimateteam.content_publish setting; any other UPDATE, and every DELETE, is still refused.
CREATE OR REPLACE FUNCTION ultimateteam.lfv4_r2_catalog() RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog, ultimateteam
AS $$
BEGIN
    IF TG_OP = 'DELETE' OR (TG_OP = 'UPDATE' AND coalesce(current_setting('ultimateteam.content_publish', true), '') <> 'on') THEN
        PERFORM ultimateteam.lfv4_err('catalog table ' || TG_TABLE_NAME || ' is immutable in this artifact version');
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END
$$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(72);
