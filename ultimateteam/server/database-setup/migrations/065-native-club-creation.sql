-- FIFA 21's own first-run club creation (ultimateteam/onboarding.md): the country the player picked on the country screen
-- (one of the nine nations the game offers; ONBOARDING_COUNTRY_<n>_SELECTED) and the club name and abbreviation they
-- typed (CreateUser, POST /user). NULL for every club made before this, and for clubs the server seeds itself.
ALTER TABLE ultimateteam.onboarding
    ADD COLUMN IF NOT EXISTS nation_id bigint,
    ADD COLUMN IF NOT EXISTS club_name text,
    ADD COLUMN IF NOT EXISTS club_abbr text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'onboarding_club_name_length'
                   AND conrelid = 'ultimateteam.onboarding'::regclass) THEN
        ALTER TABLE ultimateteam.onboarding ADD CONSTRAINT onboarding_club_name_length
            CHECK (club_name IS NULL OR length(club_name) BETWEEN 1 AND 30);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'onboarding_club_abbr_length'
                   AND conrelid = 'ultimateteam.onboarding'::regclass) THEN
        ALTER TABLE ultimateteam.onboarding ADD CONSTRAINT onboarding_club_abbr_length
            CHECK (club_abbr IS NULL OR length(club_abbr) BETWEEN 1 AND 5);
    END IF;
END
$$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(65);
