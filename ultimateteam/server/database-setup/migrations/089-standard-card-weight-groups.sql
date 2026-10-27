-- The remaining ordinary card types start with uniform odds within each tier/rarity.
-- Operators can split these default groups or move members later without changing pack code.
DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.card_types WHERE (card_type_id, name) IN (
        (1, 'Non-Rare Bronze'), (2, 'Non-Rare Gold'), (3, 'Non-Rare Silver'),
        (4, 'Rare Bronze'), (6, 'Rare Silver'))) <> 5 THEN
        RAISE EXCEPTION 'Standard card type ids do not match the expected catalogue';
    END IF;
END $$;

ALTER TABLE ultimateteam.promos DROP CONSTRAINT promos_category;
ALTER TABLE ultimateteam.promos ADD CONSTRAINT promos_category
    CHECK (category IN ('historical_squad', 'event_item_group', 'icon_tier',
                       'competition', 'unsorted', 'standard_pool'));

CREATE TEMP TABLE standard_card_groups(code text, name text, card_type_id bigint, sort_order integer) ON COMMIT DROP;
INSERT INTO standard_card_groups VALUES
    ('bronze_common', 'Bronze Common', 1, 20),
    ('gold_common', 'Gold Common', 2, 30),
    ('silver_common', 'Silver Common', 3, 40),
    ('bronze_rare', 'Bronze Rare', 4, 50),
    ('silver_rare', 'Silver Rare', 6, 60);

INSERT INTO ultimateteam.promos
    (code, name, status, family, category, sort_order, enabled, source, odds_category, draw_weight)
SELECT code, name, 'active', 'Standard Pool', 'standard_pool', sort_order, true,
       'card_type:' || card_type_id, code, 1
FROM standard_card_groups;

INSERT INTO ultimateteam.promo_card_weight_groups(promo_id, code, name, draw_weight)
SELECT promo.promo_id, 'default', 'Default', 1
FROM ultimateteam.promos promo JOIN standard_card_groups seed USING(code);

INSERT INTO ultimateteam.promo_cards(promo_id, player_card_definition_id, weight_group_id)
SELECT promo.promo_id, card.definition_id, grp.id
FROM standard_card_groups seed
JOIN ultimateteam.promos promo USING(code)
JOIN ultimateteam.promo_card_weight_groups grp ON grp.promo_id=promo.promo_id AND grp.code='default'
JOIN ultimateteam.player_card_definitions card ON card.card_type_id=seed.card_type_id;

DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.promo_cards member
        JOIN ultimateteam.promos promo USING(promo_id)
        WHERE promo.category='standard_pool')
       <> (SELECT count(*) FROM ultimateteam.player_card_definitions
           WHERE card_type_id IN (1,2,3,4,6)) THEN
        RAISE EXCEPTION 'Standard groups must cover every ordinary definition exactly once';
    END IF;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(89);
