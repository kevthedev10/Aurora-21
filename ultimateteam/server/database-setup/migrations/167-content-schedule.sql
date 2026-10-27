-- Independent campaign drops; existing immediate changes and player state remain untouched.
ALTER TABLE ultimateteam.content_changesets DROP CONSTRAINT content_changesets_status;
ALTER TABLE ultimateteam.content_changesets ADD CONSTRAINT content_changesets_status CHECK (status IN ('open','draft','published','discarded'));
CREATE SEQUENCE ultimateteam.content_store_ids AS bigint;
CREATE TABLE ultimateteam.content_campaigns (
 campaign_id bigserial PRIMARY KEY, name text NOT NULL CHECK(length(name) BETWEEN 1 AND 120),
 ends_at timestamptz, version bigint NOT NULL DEFAULT 1
);
CREATE TABLE ultimateteam.content_drops (
 drop_id bigserial PRIMARY KEY, campaign_id bigint REFERENCES ultimateteam.content_campaigns,
 name text NOT NULL CHECK(length(name) BETWEEN 1 AND 120), starts_at timestamptz NOT NULL, ends_at timestamptz,
 inherit_end boolean NOT NULL DEFAULT true,
 status text NOT NULL DEFAULT 'draft' CHECK(status IN ('draft','scheduled','live','ended','cancelled','failed')),
 version bigint NOT NULL DEFAULT 1, changeset_id bigint NOT NULL UNIQUE REFERENCES ultimateteam.content_changesets,
 error text, published_at timestamptz, expires jsonb NOT NULL DEFAULT '[]',
 -- End now may close a drop at the same instant it started. Draft APIs still require a positive window.
 CHECK(ends_at IS NULL OR ends_at>=starts_at)
);
CREATE INDEX content_drops_due ON ultimateteam.content_drops(starts_at) WHERE status='scheduled';
CREATE TABLE ultimateteam.content_drop_item_ends (
 change_id bigint PRIMARY KEY REFERENCES ultimateteam.content_changes ON DELETE CASCADE,
 ends_at timestamptz
);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(167);
