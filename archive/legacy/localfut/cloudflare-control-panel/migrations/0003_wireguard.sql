ALTER TABLE devices ADD COLUMN wireguard_public_key TEXT;
ALTER TABLE devices ADD COLUMN wireguard_address TEXT;
ALTER TABLE devices ADD COLUMN wireguard_changed_at INTEGER;

CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_wireguard_key
  ON devices(wireguard_public_key) WHERE wireguard_public_key IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_wireguard_address
  ON devices(wireguard_address) WHERE wireguard_address IS NOT NULL;
