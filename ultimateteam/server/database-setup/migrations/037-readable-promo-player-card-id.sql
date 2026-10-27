-- Clarify that promo memberships reference a player card definition.
ALTER TABLE ultimateteam.promo_cards RENAME COLUMN definition_id TO player_card_definition_id;
ALTER TABLE ultimateteam.promo_cards RENAME CONSTRAINT lfv4_fk_promo_cards_definition_id TO lfv4_fk_promo_cards_player_card_definition_id;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(37);
