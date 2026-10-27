-- Schema migration history is the sole installed-version record.
DROP TABLE ultimateteam.setup_artifact_versions;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(38);
