CREATE TABLE IF NOT EXISTS host_presence (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  players_json TEXT NOT NULL,
  reported_at INTEGER NOT NULL
);
