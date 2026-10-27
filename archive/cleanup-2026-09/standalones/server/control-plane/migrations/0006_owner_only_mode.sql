CREATE TABLE IF NOT EXISTS control_settings (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  updated_by_discord_id TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);

INSERT OR IGNORE INTO control_settings(key, value, updated_by_discord_id, updated_at)
VALUES('owner_only_mode', '0', 'system', unixepoch());
