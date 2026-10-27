-- Reward-only packs of the retail competition rewards (Python server f354894, 21bf125, 56145ed; never in the store):
-- 302 Jumbo Gold, 303 Jumbo Premium Gold, 315 Small Prime Gold Players, 319 Jumbo Premium Gold Players (FIFA's own 309,
-- but this server's 309 is Prime Mixed Players), 518 Rare Electrum Players, 534 Mixed Players, 540 Premium TOTW (3
-- in-forms), 541 Ultimate TOTW (11), and the untradeable red FUT Champions picks 550 (1 of 3) and 551 (1 of 2, rated 84
-- or lower). A player pick can have a rating ceiling (player_pick_items.max_rating).
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.player_pick_items ADD COLUMN IF NOT EXISTS max_rating integer NOT NULL DEFAULT 99;

INSERT INTO ultimateteam.store_packs(pack_id, kind, name, size, gold_weight, silver_weight, bronze_weight, rare_slots,
    guaranteed_specials, special_chance, allow_unweighted_specials, special_weights, special_weights_exclusive, player_picks,
    always_present, display_only)
VALUES
(302, 'reward', 'Jumbo Gold Pack', 24, 100, NULL, NULL, 3, NULL, NULL, NULL, NULL, false, NULL, false, false),
(303, 'reward', 'Jumbo Premium Gold Pack', 24, 100, NULL, NULL, 7, NULL, NULL, NULL, NULL, false, NULL, false, false),
(315, 'reward', 'Small Prime Gold Players Pack', 6, 100, NULL, NULL, 3, NULL, NULL, NULL, NULL, false, NULL, false, false),
(319, 'reward', 'Jumbo Premium Gold Players Pack', 24, 100, NULL, NULL, 7, NULL, NULL, NULL, NULL, false, NULL, false, false),
(518, 'reward', 'Rare Electrum Players Pack', 12, 50, 50, NULL, 12, NULL, NULL, NULL, NULL, false, NULL, false, false),
(534, 'reward', 'Mixed Players Pack', 12, 34, 33, 33, 1, NULL, NULL, NULL, NULL, false, NULL, false, false),
(540, 'reward', 'Premium TOTW Pack', 3, 100, NULL, NULL, 3, 3, 0, false, '{"3": 1}', true, NULL, false, false),
(541, 'reward', 'Ultimate TOTW Pack', 11, 100, NULL, NULL, 11, 11, 0, false, '{"3": 1}', true, NULL, false, false),
(550, 'reward', 'FUT Champions Player Pick', 1, 100, NULL, NULL, 1, NULL, NULL, NULL, NULL, false,
    '[{"count": 1, "min_rating": 0, "choice_count": 3, "rareflags": [18], "label": "1 of 3 FUT Champions Players"}]', false, false),
(551, 'reward', 'FUT Champions Player Pick (max 84)', 1, 100, NULL, NULL, 1, NULL, NULL, NULL, NULL, false,
    '[{"count": 1, "min_rating": 0, "max_rating": 84, "choice_count": 2, "rareflags": [18], "label": "1 of 2 FUT Champions Players"}]', false, false)
ON CONFLICT (pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(59);
