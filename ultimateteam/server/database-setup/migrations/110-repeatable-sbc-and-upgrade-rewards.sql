-- Retain historical claims and count subsequent completions of repeatable challenges.
ALTER TABLE ultimateteam.sbc_completed
    ADD COLUMN IF NOT EXISTS completion_count bigint NOT NULL DEFAULT 1
    CHECK (completion_count > 0);

-- Upgrade rewards contain only standard rare players. Explicit zero category odds
-- suppress the inherited Icon chance; the rarity filter also excludes all specials.
UPDATE ultimateteam.store_packs
SET allowed_rareflags = ARRAY[1]::bigint[],
    category_chances = '{"icon":0,"totw":0}'::jsonb,
    special_chance = 0, guaranteed_specials = 0,
    banded_odds = true
WHERE pack_id IN (80,81,82);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(110);
