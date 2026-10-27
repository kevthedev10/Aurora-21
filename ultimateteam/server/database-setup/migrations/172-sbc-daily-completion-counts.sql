-- Preserve every existing daily completion as one claim in its original reset window.
ALTER TABLE ultimateteam.sbc_daily_completed ADD COLUMN completion_count bigint NOT NULL DEFAULT 1
    CHECK(completion_count > 0);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(172);
