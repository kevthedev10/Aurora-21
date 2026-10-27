-- Squad Foundations (rarity 87, the maintainer's card type and promo, 2026-10-04). FIFA 21's catalogue already holds its 21
-- objective cards (MLS, League of Ireland, EFL and Eredivisie, November 2020 to March 2021) as rarity 87, which the seed
-- named after wefut's "Objective Player" and migration 076 grouped with every other objective card in Objective Players.
-- The card type and its rarity are now called Squad Foundations, and the 21 cards move to a promo of their own with Objective
-- Players' settings (status, schedule, events, odds category and weight), so their pack availability does not change. Packs,
-- picks and rules that drew from Objective Players draw from both. Rarity 98 keeps the name Objective Player.
SET CONSTRAINTS ALL IMMEDIATE;
SELECT set_config('ultimateteam.content_publish', 'on', true);

-- The 21 cards by native resource id and rating (runtime-source/ultimateteam21/players.json, rareflag 87).
CREATE TEMP TABLE squad_foundations(resource bigint PRIMARY KEY, rating bigint NOT NULL, member_order integer NOT NULL) ON COMMIT DROP;
INSERT INTO squad_foundations VALUES
    (50520590, 86, 1),  -- Victor Wanyama, Impact Montréal (MLS, 2021-03-23)
    (50554529, 84, 2),  -- Auro, Toronto FC
    (50539540, 85, 3),  -- Joseph-Claude Gyau, FC Cincinnati
    (84123366, 86, 4),  -- Aaron Connolly, Brighton (St Patrick's, 2021-03-17)
    (50532126, 85, 5),  -- Jeff Hendrick, Newcastle United
    (50534311, 84, 6),  -- Daryl Horgan, Wycombe
    (50557810, 86, 7),  -- Emiliano Buendía, Norwich (EFL, 2021-03-09)
    (50556142, 84, 8),  -- Rico Henry, Brentford
    (50545639, 85, 9),  -- Jefferson Lerma, AFC Bournemouth
    (67342042, 86, 10), -- Mats Köhlert, Willem II (Eredivisie, 2021-03-02)
    (50571529, 85, 11), -- Danilo, FC Twente
    (50543835, 84, 12), -- Philipp Max, PSV
    (50563512, 84, 13), -- Mitchell van Bergen, SC Heerenveen (Eredivisie, 2020-12-03)
    (50555289, 83, 14), -- Timo Baumgartl, PSV
    (50577752, 82, 15), -- Ryan Gravenberch, Ajax
    (50542746, 82, 16), -- Bradley Dack, Blackburn Rovers (EFL, 2020-11-22)
    (50517070, 84, 17), -- Joshua King, AFC Bournemouth
    (50500862, 82, 18), -- Scott Sinclair, Preston
    (50553771, 82, 19), -- Aaron Long, NY Red Bulls (MLS, 2020-11-04)
    (50568641, 83, 20), -- Nick Lima, SJ Earthquakes
    (50508641, 84, 21); -- Bojan, Impact Montréal

DO $$ BEGIN
    IF (SELECT count(*) FROM squad_foundations s
        JOIN ultimateteam.item_definitions d ON d.family_id = 1 AND d.game_year = 2021 AND d.native_resource_id = s.resource
        JOIN ultimateteam.player_card_definitions p ON p.definition_id = d.definition_id AND p.rating = s.rating
        JOIN ultimateteam.card_types t ON t.card_type_id = p.card_type_id
        JOIN ultimateteam.native_rarities r ON r.native_rarity_id = t.native_rarity_id AND r.native_rareflag = 87) <> 21 THEN
        RAISE EXCEPTION 'The Squad Foundations cards no longer match the catalogue (rarity 87); refusing the promo split';
    END IF;
END $$;

-- The card type and the rarity are named Squad Foundations; cards report the card type's name as their rarityName.
LOCK TABLE ultimateteam.card_types IN ACCESS EXCLUSIVE MODE;
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types t SET name = 'Squad Foundations'
FROM ultimateteam.native_rarities r WHERE r.native_rarity_id = t.native_rarity_id AND r.native_rareflag = 87;
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.native_rarities SET name = 'Squad Foundations' WHERE native_rareflag = 87;

-- The promo takes Objective Players' settings; without that promo it starts active, enabled and with the default odds.
INSERT INTO ultimateteam.promos(code, name, starts_at, ends_at, status, family, category, sort_order, enabled, source, odds_category, draw_weight)
SELECT 'squad_foundations', 'Squad Foundations', o.starts_at, o.ends_at, COALESCE(o.status, 'active'), 'Squad Foundations',
       COALESCE(o.category, 'event_item_group'), COALESCE(o.sort_order + 1, 1011), COALESCE(o.enabled, true),
       'the 21 Squad Foundations cards (rarity 87), split from Objective Players by migration 151',
       COALESCE(o.odds_category, 'other_promo'), COALESCE(o.draw_weight, 1)
FROM (SELECT 1) one LEFT JOIN ultimateteam.promos o ON o.code = 'objective_players'
ON CONFLICT (code) DO NOTHING;
INSERT INTO ultimateteam.promo_buckets(promo_id, code, name, draw_weight, enabled)
SELECT promo_id, 'default', 'Default', 1, true FROM ultimateteam.promos WHERE code = 'squad_foundations'
ON CONFLICT (promo_id, code) DO NOTHING;

INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id, bucket_id, member_order)
SELECT p.promo_id, d.definition_id, b.id, s.member_order
FROM squad_foundations s
JOIN ultimateteam.item_definitions d ON d.family_id = 1 AND d.game_year = 2021 AND d.native_resource_id = s.resource
JOIN ultimateteam.promos p ON p.code = 'squad_foundations'
JOIN ultimateteam.promo_buckets b ON b.promo_id = p.promo_id AND b.code = 'default'
ON CONFLICT (promo_id, player_card_definition_id) DO NOTHING;
DELETE FROM ultimateteam.promo_cards c
USING ultimateteam.promos o, ultimateteam.item_definitions d, squad_foundations s
WHERE o.code = 'objective_players' AND c.promo_id = o.promo_id AND d.definition_id = c.player_card_definition_id
  AND d.family_id = 1 AND d.game_year = 2021 AND d.native_resource_id = s.resource;

-- Every promo event that offers Objective Players offers Squad Foundations too.
INSERT INTO ultimateteam.promo_event_promos(event_id, promo_id, enabled)
SELECT e.event_id, n.promo_id, e.enabled
FROM ultimateteam.promo_event_promos e
JOIN ultimateteam.promos o ON o.promo_id = e.promo_id AND o.code = 'objective_players'
JOIN ultimateteam.promos n ON n.code = 'squad_foundations'
ON CONFLICT (event_id, promo_id) DO NOTHING;

-- A pack, pick or rule that names Objective Players as a source names Squad Foundations as well.
UPDATE ultimateteam.store_packs SET promo_codes = array_append(promo_codes, 'squad_foundations')
WHERE 'objective_players' = ANY(promo_codes) AND NOT 'squad_foundations' = ANY(promo_codes);
UPDATE ultimateteam.owned_legacy_pack_definitions SET promo_codes = array_append(promo_codes, 'squad_foundations')
WHERE 'objective_players' = ANY(promo_codes) AND NOT 'squad_foundations' = ANY(promo_codes);
CREATE FUNCTION pg_temp.with_squad_foundations(value jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
BEGIN
    CASE jsonb_typeof(value)
      WHEN 'array' THEN
        IF value @> '["objective_players"]'::jsonb AND NOT value @> '["squad_foundations"]'::jsonb THEN
            RETURN value || '["squad_foundations"]'::jsonb;
        END IF;
        RETURN coalesce((SELECT jsonb_agg(pg_temp.with_squad_foundations(element) ORDER BY ord)
            FROM jsonb_array_elements(value) WITH ORDINALITY a(element, ord)), '[]'::jsonb);
      WHEN 'object' THEN RETURN coalesce((SELECT jsonb_object_agg(key, pg_temp.with_squad_foundations(element))
            FROM jsonb_each(value) a(key, element)), '{}'::jsonb);
      ELSE RETURN value;
    END CASE;
END $$;
UPDATE ultimateteam.store_packs SET player_picks = pg_temp.with_squad_foundations(player_picks)
WHERE player_picks IS DISTINCT FROM pg_temp.with_squad_foundations(player_picks);
UPDATE ultimateteam.owned_legacy_pack_definitions SET player_picks = pg_temp.with_squad_foundations(player_picks)
WHERE player_picks IS DISTINCT FROM pg_temp.with_squad_foundations(player_picks);
UPDATE ultimateteam.player_pick_items SET draw_config = pg_temp.with_squad_foundations(draw_config)
WHERE draw_config IS DISTINCT FROM pg_temp.with_squad_foundations(draw_config);
UPDATE ultimateteam.content_rules SET value = pg_temp.with_squad_foundations(value)
WHERE value IS DISTINCT FROM pg_temp.with_squad_foundations(value);
DROP FUNCTION pg_temp.with_squad_foundations(jsonb);

SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(151);
