-- Initial Aurora access policy requested for the maintenance period:
-- Founder / Co-Founder and Developers may play and bypass maintenance.
-- Boosters and Whitelisters remain named UI choices, but grant no access by default.
-- Apply once on upgrade as well as a fresh install. Later panel edits persist.
-- Preserve the Discord server id, message, player cap and per-account restrictions.
ALTER TABLE ultimateteam.server_access ALTER COLUMN maintenance SET DEFAULT true;
ALTER TABLE ultimateteam.server_access ALTER COLUMN play_role_ids SET DEFAULT
    ARRAY['1531102023108395169','1546887158424670238','1546861326037549056']::text[];
ALTER TABLE ultimateteam.server_access ALTER COLUMN bypass_role_ids SET DEFAULT
    ARRAY['1531102023108395169','1546887158424670238','1546861326037549056']::text[];

UPDATE ultimateteam.server_access
SET maintenance=true,
    play_role_ids=ARRAY['1531102023108395169','1546887158424670238','1546861326037549056']::text[],
    bypass_role_ids=ARRAY['1531102023108395169','1546887158424670238','1546861326037549056']::text[],
    updated_at=now()
WHERE only_row;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(103);
