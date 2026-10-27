-- Existing Developer/Pro cards receive the same restriction as newly granted cards.
-- Keep ownership, stats and card definitions intact.
UPDATE ultimateteam.items i SET untradeable=true
FROM ultimateteam.player_card_definitions pc
JOIN ultimateteam.card_types t USING(card_type_id)
JOIN ultimateteam.native_rarities r USING(native_rarity_id)
WHERE i.definition_id=pc.definition_id AND NOT i.untradeable
  AND (r.native_rareflag=10 OR EXISTS (
      SELECT 1 FROM ultimateteam.promo_cards c JOIN ultimateteam.promos p USING(promo_id)
      WHERE c.player_card_definition_id=pc.definition_id AND p.code='developer_cards'));

INSERT INTO ultimateteam.schema_migrations(version) VALUES(102);
