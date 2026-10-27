ALTER TABLE host_presence ADD COLUMN gateway_online INTEGER NOT NULL DEFAULT 0
  CHECK (gateway_online IN (0, 1));

ALTER TABLE host_presence ADD COLUMN gateway_updated_at INTEGER;
