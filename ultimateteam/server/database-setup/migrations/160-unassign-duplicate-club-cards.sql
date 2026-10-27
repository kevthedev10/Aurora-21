-- A club keeps one copy of a manager or club item (kit, badge, ball, stadium card), as of a player: a second copy is a
-- duplicate and stays in Unassigned. The rule covered players only, so clubs kept extra copies (live 10-05: up to 12 of
-- one manager). Keep one copy of each in the club and move the others to Unassigned, where the club quick-sells or
-- lists them. The copy kept is a squad's manager first, then an equipped one, then the oldest.
CREATE TEMP TABLE duplicate_club_cards ON COMMIT DROP AS
SELECT id, club_id, definition_id,
    row_number() OVER (PARTITION BY club_id, definition_id ORDER BY
        EXISTS (SELECT 1 FROM ultimateteam.squad_managers m WHERE m.item_id = i.id) DESC,
        coalesce(item_state, 'free') <> 'free' DESC,
        CASE WHEN pile = 'club' THEN 0 ELSE 1 END, native_item_id) AS copy
FROM ultimateteam.items i
WHERE family_id IN (2, 4) AND pile IN ('club', 'squad');

-- A squad naming another copy of its manager names the kept one.
UPDATE ultimateteam.squad_managers m SET item_id = kept.id
FROM duplicate_club_cards extra
JOIN duplicate_club_cards kept ON kept.club_id = extra.club_id AND kept.definition_id = extra.definition_id AND kept.copy = 1
WHERE extra.copy > 1 AND m.item_id = extra.id;

-- An equipped copy keeps its slot: the slot needs it in the club.
UPDATE ultimateteam.items i SET pile = 'unassigned'
FROM duplicate_club_cards extra
WHERE extra.copy > 1 AND i.id = extra.id AND coalesce(i.item_state, 'free') = 'free'
    AND NOT EXISTS (SELECT 1 FROM ultimateteam.club_slot_bindings b WHERE b.item_id = i.id);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(160);
