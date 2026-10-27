-- Give the first scheduled TOTW promos editable rating groups. These are explicit
-- card memberships; changing a player's rating later does not move it automatically.
-- The legacy inform draw still uses pack_odds.informBands until its cutover.
CREATE TEMP TABLE totw_rating_bands (
    code text, name text, min_rating bigint, max_rating bigint,
    draw_weight numeric(12,6), sort_order integer
) ON COMMIT DROP;
INSERT INTO totw_rating_bands VALUES
    ('rating_0_64', '0–64', 0, 64, 1, 1),
    ('rating_65_74', '65–74', 65, 74, 1, 2),
    ('rating_75_79', '75–79', 75, 79, 15, 3),
    ('rating_80_81', '80–81', 80, 81, 27, 4),
    ('rating_82_83', '82–83', 82, 83, 30, 5),
    ('rating_84_85', '84–85', 84, 85, 15, 6),
    ('rating_86_87', '86–87', 86, 87, 9, 7),
    ('rating_88_89', '88–89', 88, 89, 3, 8),
    ('rating_90_99', '90–99', 90, 99, 1, 9);

CREATE TEMP TABLE totw_populated_bands ON COMMIT DROP AS
SELECT promo.promo_id, band.code, band.name, band.min_rating, band.max_rating,
       band.draw_weight, row_number() OVER (PARTITION BY promo.promo_id ORDER BY band.sort_order) AS position
FROM ultimateteam.promos promo
JOIN totw_rating_bands band ON true
WHERE promo.code IN ('totw_1', 'totw_2', 'totw_3')
  AND EXISTS (
      SELECT 1 FROM ultimateteam.promo_cards member
      JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
      WHERE member.promo_id=promo.promo_id AND card.rating BETWEEN band.min_rating AND band.max_rating
  );

DO $$ BEGIN
    IF (SELECT count(DISTINCT promo_id) FROM totw_populated_bands) <> 3 THEN
        RAISE EXCEPTION 'TOTW 1–3 must each have at least one rated card';
    END IF;
    IF EXISTS (
        SELECT 1 FROM ultimateteam.promo_cards member
        JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id
        JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
        WHERE promo.code IN ('totw_1', 'totw_2', 'totw_3')
          AND NOT EXISTS (SELECT 1 FROM totw_rating_bands band WHERE card.rating BETWEEN band.min_rating AND band.max_rating)
    ) THEN RAISE EXCEPTION 'A TOTW 1–3 card is outside the seeded rating groups'; END IF;
END $$;

-- Reuse each promo's original default row because catalogue group rows cannot be deleted.
SELECT set_config('ultimateteam.content_publish', 'on', true);
UPDATE ultimateteam.promo_card_weight_groups grp
SET code=band.code, name=band.name, draw_weight=band.draw_weight
FROM totw_populated_bands band
WHERE grp.promo_id=band.promo_id AND grp.code='default' AND band.position=1;

INSERT INTO ultimateteam.promo_card_weight_groups(promo_id, code, name, draw_weight)
SELECT promo_id, code, name, draw_weight FROM totw_populated_bands WHERE position>1;

UPDATE ultimateteam.promo_cards member
SET weight_group_id=grp.id
FROM ultimateteam.promo_card_weight_groups grp
JOIN totw_populated_bands band ON band.promo_id=grp.promo_id AND band.code=grp.code
JOIN ultimateteam.player_card_definitions card ON card.rating BETWEEN band.min_rating AND band.max_rating
WHERE member.promo_id=grp.promo_id AND member.player_card_definition_id=card.definition_id;
SELECT set_config('ultimateteam.content_publish', '', true);

DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM ultimateteam.promo_cards member
        JOIN ultimateteam.promos promo ON promo.promo_id=member.promo_id
        JOIN ultimateteam.player_card_definitions card ON card.definition_id=member.player_card_definition_id
        JOIN ultimateteam.promo_card_weight_groups grp ON grp.id=member.weight_group_id
        JOIN totw_populated_bands band ON band.promo_id=grp.promo_id AND band.code=grp.code
        WHERE promo.code IN ('totw_1', 'totw_2', 'totw_3')
          AND card.rating NOT BETWEEN band.min_rating AND band.max_rating
    ) THEN RAISE EXCEPTION 'TOTW 1–3 card group does not match seeded rating'; END IF;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(83);
