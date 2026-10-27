-- FIFA's SBC squad save carries the squad's formation and manager next to its players (CardsDLL save builder
-- 0x2fbe40: chemistry, rating, formation, manager[{id,dream}], players[{index,itemData{id,dream}}]). The server kept
-- only the item ids, so it could not compute the squad's chemistry and had to trust the client's. The saved squad now
-- keeps the formation and manager item id; sbc_squad_items.slot_index becomes the squad slot the player sits in
-- (it was a running count).
ALTER TABLE ultimateteam.sbc_squad_counts
    ADD COLUMN formation text,
    ADD COLUMN manager_item_id bigint;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(117);
