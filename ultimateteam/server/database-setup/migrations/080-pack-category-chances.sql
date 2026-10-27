-- Optional per-slot promo category chances for banded player packs. Existing packs keep their
-- published in-form chance in special_chance; Base Icons use rules/pack_odds.json by default.
ALTER TABLE ultimateteam.store_packs
    ADD COLUMN category_chances jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD CONSTRAINT store_packs_category_chances_object CHECK (jsonb_typeof(category_chances) = 'object');

INSERT INTO ultimateteam.schema_migrations(version) VALUES(80);
