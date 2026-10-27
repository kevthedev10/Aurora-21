-- Store packs 48-65 from the Python server (fix/rivals-osdk-pregame, DEFAULT_PACK_SETTINGS; names and descriptions are
-- the maintainer's content, ported as they are by maintainer decision 2026-09-25) and the pack mechanics they use.
-- Not here: pack 60 (FUT Champs red picks) waits for the rarity-18 red pick cards, and pack 66 (identity pack: balls,
-- badges, kits, stadiums) for owned badge, ball and stadium cards (port-python.md C2).
SET CONSTRAINTS ALL IMMEDIATE;

-- Replay-safe (tests re-run later migrations): columns are added once, the checks are re-created.
ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_points_price, DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_currencies, DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_counts, DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_player_picks;
ALTER TABLE ultimateteam.store_packs
    -- FIFA Points price; NULL keeps the coin price converted by rules/packs.json coinsPerPoint.
    ADD COLUMN IF NOT EXISTS points_price bigint,
    -- The currencies a pack is sold for ('coins', 'points'); NULL means both.
    ADD COLUMN IF NOT EXISTS allowed_currencies text[],
    -- Never listed or sold in the store; only an explicit admin grant hands one out.
    ADD COLUMN IF NOT EXISTS admin_only boolean NOT NULL DEFAULT false,
    -- Every rare slot draws a special card of any rarity, and no common or rare card is drawn.
    ADD COLUMN IF NOT EXISTS promo_only boolean,
    -- The pack's special_weights are the complete weights instead of overrides of the defaults.
    ADD COLUMN IF NOT EXISTS special_weights_exclusive boolean NOT NULL DEFAULT false,
    -- Player pick tokens in the pack: [{count, min_rating, choice_count, promo_only?, rareflags?, label?}].
    ADD COLUMN IF NOT EXISTS player_picks jsonb,
    -- Unique players rated bonus_min_rating or more added after a pack's pick tokens.
    ADD COLUMN IF NOT EXISTS bonus_player_count integer,
    ADD COLUMN IF NOT EXISTS bonus_min_rating integer,
    ADD COLUMN IF NOT EXISTS bonus_promo_only boolean,
    -- Distinct random position modifiers and chemistry styles.
    ADD COLUMN IF NOT EXISTS random_consumable_count integer,
    -- Distinct random managers.
    ADD COLUMN IF NOT EXISTS random_manager_count integer,
    ADD CONSTRAINT lfv4_ck_store_packs_points_price CHECK (points_price IS NULL OR points_price >= 0),
    ADD CONSTRAINT lfv4_ck_store_packs_currencies CHECK (allowed_currencies IS NULL
        OR (cardinality(allowed_currencies) > 0 AND allowed_currencies <@ ARRAY['coins','points']::text[])),
    ADD CONSTRAINT lfv4_ck_store_packs_counts CHECK ((bonus_player_count IS NULL OR bonus_player_count >= 0)
        AND (random_consumable_count IS NULL OR random_consumable_count >= 0)
        AND (random_manager_count IS NULL OR random_manager_count >= 0)),
    ADD CONSTRAINT lfv4_ck_store_packs_player_picks CHECK (player_picks IS NULL OR jsonb_typeof(player_picks) = 'array');

-- A token remembers the pick group it came from, so its choices follow the same filters.
ALTER TABLE ultimateteam.player_pick_items
    ADD COLUMN IF NOT EXISTS promo_only boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS rareflags bigint[] NOT NULL DEFAULT '{}';

INSERT INTO ultimateteam.store_packs(pack_id, kind, enabled, name, bio, price, points_price, allowed_currencies, admin_only,
    size, gold_weight, silver_weight, bronze_weight, rare_slots, special_chance, manager_chance, min_rating, store_group,
    player_pick_count, guaranteed_specials, allow_unweighted_specials, promo_only, allowed_rareflags, special_weights,
    special_weights_exclusive, player_picks, bonus_player_count, bonus_min_rating, bonus_promo_only,
    random_consumable_count, random_manager_count, always_present)
VALUES
(48, 'store', true, '12 Random Consumables Pack', 'Contains 12 random FIFA 21 position modifiers and Chemistry Styles.',
    1, NULL, NULL, false, 12, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, 12, NULL, true),
(49, 'store', true, 'The Ghoul Pack', 'shoutout to futties saint maximin that mf lowkey goated, also type "true..." in chat if inde navarette bad asf',
    5000000, 80000, NULL, false, 12, 100, 0, 0, 12, 0.0, 0.0, 91, 'special',
    NULL, 1, false, NULL, NULL, '{"16": 1}', true, NULL, NULL, NULL, NULL, NULL, NULL, true),
(50, 'store', true, 'F2P FIFA Points FTW', 'You can''t buy fifa points so waste your daily points here',
    0, 250, ARRAY['points'], false, 1, 100, 0, 0, 1, NULL, NULL, 91, 'special',
    3, NULL, NULL, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, true),
(51, 'store', true, 'The Investment Pack', 'This shit is like bitcoin in 2008 bro guaranteed banger cards',
    2000000, 50000, NULL, false, 5, 100, 0, 0, 5, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 4, "min_rating": 86, "choice_count": 3}, {"count": 1, "min_rating": 92, "choice_count": 3}]',
    NULL, NULL, NULL, NULL, NULL, true),
(52, 'store', true, 'The Tokyo Pack', 'Now you may ask, why is there two ghoulnotokyo packs? Uh well shut the fuck up you ungrateful bastard.',
    1750000, 35000, NULL, false, 12, 100, 0, 0, 12, 0.25, 0.0, 89, 'special',
    NULL, 0, true, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, true),
(53, 'store', true, 'The Mustafa Pack', 'Gigglegang on top fr',
    3500000, 80000, NULL, false, 75, 100, 0, 0, 75, 0.25, 0.0, 85, 'special',
    NULL, 0, true, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, true),
(54, 'store', true, 'The UnknownX Pack', 'Solid pack for content, gooner nation rise',
    2000000, 50000, NULL, false, 12, 100, 0, 0, 12, 0.25, 0.0, 90, 'special',
    NULL, 0, true, true, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, true),
(55, 'store', true, 'The TorMusi Pack', 'Shoutout tormusi',
    2000000, 50000, NULL, false, 16, 100, 0, 0, 16, 0.0, 0.0, 84, 'special',
    NULL, 1, false, true, NULL, '{"51": 1}', true, NULL, NULL, NULL, NULL, NULL, NULL, true),
(56, 'store', true, 'The JayProds Pack', 'If you''re on jayprods stream seeing this, say "shoutout to the big ghoul" xd',
    5000000, 75000, NULL, false, 4, 100, 0, 0, 4, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 4, "min_rating": 93, "choice_count": 3, "promo_only": true}]',
    NULL, NULL, NULL, NULL, NULL, true),
(57, 'store', true, 'The Dev Pack', 'The pack from the devs, holy moly',
    0, 0, NULL, true, 17, 100, 0, 0, 17, NULL, NULL, NULL, 'admin',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 5, "min_rating": 99, "choice_count": 3, "promo_only": true}]',
    12, 92, true, NULL, NULL, false),
(58, 'store', true, '6 Random Managers Pack', 'Contains 6 random FIFA 21 managers.',
    25000, NULL, ARRAY['coins'], false, 6, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, 6, true),
(59, 'store', true, '96+ Player Pick (1 of 3)', 'Pick 1 of 3 players, every one rated 96 or higher.',
    1500000, 40000, NULL, false, 1, 100, 0, 0, 1, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 1, "min_rating": 96, "choice_count": 3}]',
    NULL, NULL, NULL, NULL, NULL, true),
(61, 'store', true, 'Chinese Road to Glory', 'You met me at a very chinese time in my life',
    2000000, 50000, NULL, false, 12, 100, 0, 0, 12, 0.0, 0.0, 87, 'special',
    NULL, 1, false, true, NULL, '{"11": 1}', true, NULL, NULL, NULL, NULL, NULL, NULL, true),
(62, 'store', true, 'The Sudgey Pack', 'Some call him cole palmer, others call him sludgey, but we all know him as the first streamer to get a 198 on live, gives you some insane picks, shoutout to the sealion',
    5000000, 80000, NULL, false, 8, 100, 0, 0, 8, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 8, "min_rating": 95, "choice_count": 3}]',
    NULL, NULL, NULL, NULL, NULL, true),
(63, 'store', true, 'The No Pack', 'Now you may wonder why THE FUCK is there ANOTHER ghoulnotokyo pack, well istg if you dont shut the fuck up im removing your whitelist and sending you to china, tbf thats quite fun tho so maybe not',
    5000000, 80000, NULL, false, 12, 100, 0, 0, 12, NULL, NULL, NULL, 'special',
    NULL, NULL, NULL, NULL, NULL, NULL, false,
    '[{"count": 10, "min_rating": 92, "choice_count": 3}, {"count": 2, "min_rating": 92, "choice_count": 3}]',
    NULL, NULL, NULL, NULL, NULL, true),
(64, 'store', true, 'The Sudgey''s Dad Pack', 'A pack designed for Sudgey''s dad, easy to open',
    5000000, 80000, NULL, false, 16, 100, 0, 0, 16, 0.0, 0.0, 94, 'special',
    NULL, 16, false, true, ARRAY[12,84]::bigint[], '{"12": 1, "84": 1}', true, NULL, NULL, NULL, NULL, NULL, NULL, true),
(65, 'store', true, 'The Jayprods Road To 198 Pack', 'if bro doesnt get this soon hes gonna end it all, also who else remembers sommer ray? where she at now, used to be bad asf',
    2000000, 50000, NULL, false, 7, 100, 0, 0, 7, 0.0, 0.0, 96, 'special',
    NULL, 7, true, NULL, NULL, '{}', true, NULL, NULL, NULL, NULL, NULL, NULL, true)
ON CONFLICT (pack_id) DO NOTHING;

-- The Mustafa Pack holds 75 cards, the Python server's largest pack (_MAX_PACK_SIZE); rules/packs.json maxPackSize matches.
UPDATE ultimateteam.store_pack_details SET max_pack_size = 75 WHERE max_pack_size < 75;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(49);
