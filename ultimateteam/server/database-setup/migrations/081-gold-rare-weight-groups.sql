-- Seed Gold Rare as an always-available card pool with editable weight groups.
-- The ranges populate the initial member lists; later edits can move individual cards.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM ultimateteam.card_types WHERE card_type_id = 5 AND name = 'Rare Gold') THEN
        RAISE EXCEPTION 'Expected card type 5 to be Rare Gold';
    END IF;
END $$;

INSERT INTO ultimateteam.promos
    (code, name, status, family, category, sort_order, enabled, source, odds_category, draw_weight)
VALUES ('gold_rare', 'Gold Rare', 'active', 'Standard Pool', 'event_item_group', 10, true,
        'card_type:5; initial groups from 2026-09-29 goldRareBands', 'gold_rare', 1);

INSERT INTO ultimateteam.promo_card_weight_groups (promo_id, code, name, draw_weight)
SELECT promo.promo_id, band.code, band.name, band.weight
FROM ultimateteam.promos promo
CROSS JOIN (VALUES
    ('rating_75_79', '75–79', 60.4::numeric),
    ('rating_80_81', '80–81', 28::numeric),
    ('rating_82_83', '82–83', 8.7::numeric),
    ('rating_84_85', '84–85', 2.4::numeric),
    ('rating_86_87', '86–87', 0.35::numeric),
    ('rating_88_89', '88–89', 0.11::numeric),
    ('rating_90_99', '90–99', 0.04::numeric)
) AS band(code, name, weight)
WHERE promo.code = 'gold_rare';

INSERT INTO ultimateteam.promo_cards (promo_id, player_card_definition_id, weight_group_id)
SELECT promo.promo_id, card.definition_id, grp.id
FROM ultimateteam.promos promo
JOIN ultimateteam.player_card_definitions card ON card.card_type_id = 5
JOIN ultimateteam.promo_card_weight_groups grp ON grp.promo_id = promo.promo_id
    AND grp.code = CASE
        WHEN card.rating BETWEEN 75 AND 79 THEN 'rating_75_79'
        WHEN card.rating BETWEEN 80 AND 81 THEN 'rating_80_81'
        WHEN card.rating BETWEEN 82 AND 83 THEN 'rating_82_83'
        WHEN card.rating BETWEEN 84 AND 85 THEN 'rating_84_85'
        WHEN card.rating BETWEEN 86 AND 87 THEN 'rating_86_87'
        WHEN card.rating BETWEEN 88 AND 89 THEN 'rating_88_89'
        WHEN card.rating BETWEEN 90 AND 99 THEN 'rating_90_99'
    END
WHERE promo.code = 'gold_rare';

DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.promo_cards membership
        JOIN ultimateteam.promos promo USING (promo_id)
        WHERE promo.code = 'gold_rare')
       <> (SELECT count(*) FROM ultimateteam.player_card_definitions WHERE card_type_id = 5) THEN
        RAISE EXCEPTION 'Gold Rare groups must cover every Rare Gold definition exactly once';
    END IF;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(81);
