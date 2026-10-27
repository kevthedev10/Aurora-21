-- FUT Champions rewards from the live Team of the Week, and the Weekend League's lost-connection claims (Python server
-- 764ef6e, c3992c5). A pack or player pick can name a resource pool: live_totw (the current week's TOTW cards in
-- rules/sqbt_totw.json) or live_totw_red (the red FUT Champions copies of those players); a pool too small for the pack
-- or pick is ignored. The Premium (540) and Ultimate (541) TOTW packs draw from the live TOTW; the red picks become
-- 550 (1 of 5), new 552 (1 of 4), new 553 (1 of 3) and 551 (1 of 2, rated 84 or lower), all from red copies of the live
-- TOTW. A level Weekend League end after a lost connection is held as a claim until the opponent has had time to report.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS resource_pool text;
ALTER TABLE ultimateteam.player_pick_items ADD COLUMN IF NOT EXISTS resource_pool text;

UPDATE ultimateteam.store_packs
SET resource_pool = 'live_totw', allow_unweighted_specials = true, special_weights = '{}', special_weights_exclusive = false
WHERE pack_id IN (540, 541);

UPDATE ultimateteam.store_packs
SET name = 'FUT Champions Player Pick (1 of 5)',
    player_picks = '[{"count": 1, "min_rating": 0, "choice_count": 5, "rareflags": [18], "resource_pool": "live_totw_red", "label": "1 of 5 FUT Champions Players"}]'
WHERE pack_id = 550;

UPDATE ultimateteam.store_packs
SET player_picks = '[{"count": 1, "min_rating": 0, "max_rating": 84, "choice_count": 2, "rareflags": [18], "resource_pool": "live_totw_red", "label": "1 of 2 FUT Champions Players"}]'
WHERE pack_id = 551;

INSERT INTO ultimateteam.store_packs(pack_id, kind, name, size, gold_weight, silver_weight, bronze_weight, rare_slots,
    guaranteed_specials, special_chance, allow_unweighted_specials, special_weights, special_weights_exclusive, player_picks,
    always_present, display_only)
VALUES
(552, 'reward', 'FUT Champions Player Pick (1 of 4)', 1, 100, NULL, NULL, 1, NULL, NULL, NULL, NULL, false,
    '[{"count": 1, "min_rating": 0, "choice_count": 4, "rareflags": [18], "resource_pool": "live_totw_red", "label": "1 of 4 FUT Champions Players"}]', false, false),
(553, 'reward', 'FUT Champions Player Pick (1 of 3)', 1, 100, NULL, NULL, 1, NULL, NULL, NULL, NULL, false,
    '[{"count": 1, "min_rating": 0, "choice_count": 3, "rareflags": [18], "resource_pool": "live_totw_red", "label": "1 of 3 FUT Champions Players"}]', false, false)
ON CONFLICT (pack_id) DO NOTHING;

-- A level Weekend League end after a lost connection (matchStatusFlags 8): a win once the opponent has sent no result.
CREATE TABLE IF NOT EXISTS ultimateteam.champion_connection_claims (
    club_id    bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    game_id    text   NOT NULL,
    created_at bigint NOT NULL,
    match_at   bigint NOT NULL
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(61);
