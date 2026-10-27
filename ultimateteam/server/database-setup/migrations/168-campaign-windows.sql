-- Preserve existing campaigns until their date window is explicitly chosen.
ALTER TABLE ultimateteam.content_campaigns ADD COLUMN starts_at timestamptz;
ALTER TABLE ultimateteam.content_campaigns ADD CONSTRAINT content_campaigns_window
 CHECK(starts_at IS NULL OR (ends_at IS NOT NULL AND ends_at > starts_at));
INSERT INTO ultimateteam.schema_migrations(version) VALUES(168);
