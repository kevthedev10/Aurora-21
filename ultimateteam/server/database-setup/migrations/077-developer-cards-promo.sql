-- Group every current Pro Player card, including the three added by migration 066.
-- Pro cards remain grant-only; promo membership does not make them pack eligible.
INSERT INTO ultimateteam.promos
    (code, name, starts_at, ends_at, status, family, category, sort_order, enabled, source)
SELECT 'developer_cards', 'Developer Cards', NULL, NULL, 'active',
       'Developer Cards', 'event_item_group', 2000, true, 'card_type:professional_player'
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.promos WHERE code = 'developer_cards');

INSERT INTO ultimateteam.promo_cards (promo_id, player_card_definition_id)
SELECT promo.promo_id, card.definition_id
FROM ultimateteam.promos promo
JOIN ultimateteam.player_card_definitions card ON true
JOIN ultimateteam.card_types type ON type.card_type_id = card.card_type_id
JOIN ultimateteam.native_rarities rarity ON rarity.native_rarity_id = type.native_rarity_id
WHERE promo.code = 'developer_cards' AND rarity.native_rareflag = 10
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.promo_cards membership
                  WHERE membership.promo_id = promo.promo_id AND membership.player_card_definition_id = card.definition_id);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(77);
