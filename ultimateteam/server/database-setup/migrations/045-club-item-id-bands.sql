-- Every club numbers its FUT item ids inside its own band of 10,000,000, starting at 900000000000 + (club id - 1) *
-- 10,000,000, so two clubs never share an item id. FIFA's item cache is keyed by id alone: in an online match a shared
-- id let the opponent's card overwrite the player's own (observed on the Python server, commit 17172fc).
--
-- Clubs created before this migration all started at 900000000000. Each one is renumbered once into its band by a
-- fixed offset: every id it has issued, (900000000000, next_item_id] (next_item_id holds the last id issued),
-- moves by the same amount wherever the club stores it. Club 1's band starts at 900000000000, so a single-player
-- club keeps its ids. A club already inside its band is left alone, which also makes a replay a no-op.
SET CONSTRAINTS ALL IMMEDIATE;
CREATE TEMP TABLE club_item_id_shift ON COMMIT DROP AS
SELECT id AS club_id, next_item_id AS old_next, (id - 1) * 10000000 AS shift
FROM ultimateteam.clubs
WHERE id > 1 AND next_item_id < 900000000000 + (id - 1) * 10000000;
DO $$ BEGIN
 IF EXISTS (SELECT 1 FROM club_item_id_shift WHERE old_next - 900000000000 > 10000000 - 16) THEN
  RAISE EXCEPTION 'A club has issued more item ids than one band holds below its reserved virtual ids';
 END IF;
END $$;
ALTER TABLE ultimateteam.items DISABLE TRIGGER lfv4_trg_r1_items;
ALTER TABLE ultimateteam.market_listings DISABLE TRIGGER lfv4_trg_r1_market_listings;
UPDATE ultimateteam.items i SET native_item_id = i.native_item_id + s.shift
FROM club_item_id_shift s WHERE i.club_id = s.club_id AND i.native_item_id >= 900000000000 AND i.native_item_id <= s.old_next;
UPDATE ultimateteam.club_player_search p SET native_item_id = p.native_item_id + s.shift
FROM club_item_id_shift s WHERE p.club_id = s.club_id AND p.native_item_id >= 900000000000 AND p.native_item_id <= s.old_next;
UPDATE ultimateteam.market_listings l SET seller_item_id = l.seller_item_id + s.shift
FROM club_item_id_shift s WHERE l.seller_club_id = s.club_id AND l.seller_item_id >= 900000000000 AND l.seller_item_id <= s.old_next;
UPDATE ultimateteam.player_pick_choices c SET reserved_wire_item_id = c.reserved_wire_item_id + s.shift
FROM ultimateteam.items t JOIN club_item_id_shift s ON s.club_id = t.club_id
WHERE t.id = c.token_item_id AND c.reserved_wire_item_id >= 900000000000 AND c.reserved_wire_item_id <= s.old_next;
UPDATE ultimateteam.sbc_squad_items q SET native_item_id = q.native_item_id + s.shift
FROM club_item_id_shift s WHERE q.club_id = s.club_id AND q.native_item_id >= 900000000000 AND q.native_item_id <= s.old_next;
UPDATE ultimateteam.onboarding o SET
    badge_item_id = CASE WHEN o.badge_item_id >= 900000000000 AND o.badge_item_id <= s.old_next THEN o.badge_item_id + s.shift ELSE o.badge_item_id END,
    loan_item_id = CASE WHEN o.loan_item_id >= 900000000000 AND o.loan_item_id <= s.old_next THEN o.loan_item_id + s.shift ELSE o.loan_item_id END
FROM club_item_id_shift s WHERE o.club_id = s.club_id;
UPDATE ultimateteam.onboarding_stale_loans o SET wire_item_id = o.wire_item_id + s.shift
FROM club_item_id_shift s WHERE o.club_id = s.club_id AND o.wire_item_id >= 900000000000 AND o.wire_item_id <= s.old_next;
UPDATE ultimateteam.clubs c SET next_item_id = c.next_item_id + s.shift FROM club_item_id_shift s WHERE c.id = s.club_id;
SET CONSTRAINTS ALL IMMEDIATE;
ALTER TABLE ultimateteam.items ENABLE TRIGGER lfv4_trg_r1_items;
ALTER TABLE ultimateteam.market_listings ENABLE TRIGGER lfv4_trg_r1_market_listings;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(45);
