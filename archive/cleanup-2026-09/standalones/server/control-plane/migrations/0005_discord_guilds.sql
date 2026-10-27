CREATE TABLE IF NOT EXISTS discord_guilds (
  guild_id TEXT PRIMARY KEY,
  display_name TEXT NOT NULL,
  role_ids TEXT NOT NULL DEFAULT '[]',
  allow_boosters INTEGER NOT NULL DEFAULT 1 CHECK (allow_boosters IN (0, 1)),
  enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
  created_by_discord_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_discord_guilds_enabled
  ON discord_guilds(enabled, display_name);
