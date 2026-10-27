-- The catalogue row a Player Pick token is stored against (issues.md F-2). The seed declares item family 5 (player_pick)
-- but never added its definition, so granting a token failed ("No catalog definition for owned item"). A token is not a
-- product: FIFA 21 recognises it by the misc card subtype 237 (ItemFactory.PlayerPickSubType), and the resourceId it is
-- served with is a real qualifying player kept per token in player_pick_items.preview_definition_id, the same shape the
-- Python server serves (make_player_pick_item). So the row has no native resource id; item type, subtype and card asset
-- are the values the served token already carries (Resources/Templates/items/player-pick-token.json).
INSERT INTO ultimateteam.item_definitions(family_id, game_year, native_resource_id, native_asset_id, native_card_db_id,
    native_card_asset_id, native_item_type, native_subtype, catalog_order, revision, status, display_name)
SELECT 5, 2021, NULL, NULL, NULL, 34, 'misc', 237,
    (SELECT COALESCE(max(catalog_order), -1) + 1 FROM ultimateteam.item_definitions), 1, 'draft', 'Player Pick'
WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.item_definitions WHERE family_id = 5 AND game_year = 2021 AND native_subtype = 237);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(48);
