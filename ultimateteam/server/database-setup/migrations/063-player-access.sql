-- Hosted player access, edited live from the operator panel. A player may launch when their verified Discord account
-- holds one of the play roles in the Aurora Discord server (asked through the bot; no play roles = every verified
-- account), unless blocked; during maintenance only bypass roles or listed accounts get in; the player cap applies to
-- everyone else. Keyed by the verified Discord id, not a club, so a player keeps their row when the club is reset. The
-- launch API reads a snapshot of these rows; a panel write updates the row and the snapshot together.
CREATE TABLE IF NOT EXISTS ultimateteam.player_access (
    discord_id text PRIMARY KEY,
    blocked boolean NOT NULL DEFAULT false,
    maintenance_bypass boolean NOT NULL DEFAULT false,
    display_name text,
    note text,
    last_launch_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT player_access_discord_id CHECK (discord_id ~ '^[0-9]{5,32}$'),
    CONSTRAINT player_access_display_name_length CHECK (display_name IS NULL OR length(display_name) <= 64),
    CONSTRAINT player_access_note_length CHECK (note IS NULL OR length(note) <= 300)
);

CREATE TABLE IF NOT EXISTS ultimateteam.server_access (
    only_row boolean PRIMARY KEY DEFAULT true,
    maintenance boolean NOT NULL DEFAULT false,
    maintenance_message text NOT NULL DEFAULT '',
    max_players integer NOT NULL DEFAULT 0,
    discord_guild_id text NOT NULL DEFAULT '',
    play_role_ids text[] NOT NULL DEFAULT '{}',
    bypass_role_ids text[] NOT NULL DEFAULT '{}',
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT server_access_one_row CHECK (only_row),
    CONSTRAINT server_access_message_length CHECK (length(maintenance_message) <= 300),
    CONSTRAINT server_access_max_players CHECK (max_players BETWEEN 0 AND 10000),
    CONSTRAINT server_access_guild CHECK (discord_guild_id = '' OR discord_guild_id ~ '^[0-9]{5,32}$'),
    CONSTRAINT server_access_role_counts CHECK (cardinality(play_role_ids) <= 50 AND cardinality(bypass_role_ids) <= 50)
);

INSERT INTO ultimateteam.server_access(only_row) VALUES(true) ON CONFLICT (only_row) DO NOTHING;

-- Verified Blaze sessions, for the operator panel's logins, recent activity and per-player session list. The remote
-- address is shown to the operator only; identity never comes from it.
CREATE TABLE IF NOT EXISTS ultimateteam.player_sessions (
    id bigserial PRIMARY KEY,
    discord_id text NOT NULL,
    remote text,
    connected_at timestamptz NOT NULL DEFAULT now(),
    disconnected_at timestamptz,
    CONSTRAINT player_sessions_discord_id CHECK (discord_id ~ '^[0-9]{5,32}$')
);
CREATE INDEX IF NOT EXISTS player_sessions_discord_connected ON ultimateteam.player_sessions(discord_id, connected_at DESC);
CREATE INDEX IF NOT EXISTS player_sessions_connected ON ultimateteam.player_sessions(connected_at DESC);

-- Every change the operator panel made to a club, with its outcome, for the panel's per-player edit list.
CREATE TABLE IF NOT EXISTS ultimateteam.panel_edits (
    id bigserial PRIMARY KEY,
    persona_id bigint NOT NULL,
    patch jsonb NOT NULL,
    requested_by text,
    result text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    applied_at timestamptz
);
CREATE INDEX IF NOT EXISTS panel_edits_persona_created ON ultimateteam.panel_edits(persona_id, created_at DESC);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(63);
