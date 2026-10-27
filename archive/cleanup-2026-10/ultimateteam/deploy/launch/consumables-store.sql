-- The Consumables Pack and manager league cards on a store that already ran phase1-store.sql (production, 2026-09-29 14:08
-- BST): store pack 9 (3,000 coins: 4 manager league cards, 4 position modifiers, 4 chemistry styles, never Basic), and half
-- of a bronze or silver pack's manager hits a manager league card. phase1-store.sql does the same on a fresh launch store.
--
-- Run once, on the production database, after the predeploy dump and after the image with migration 071 is live. It is not
-- a migration: the store is operator data. It stops without changing anything unless it finds the phase-1 store without pack 9.
BEGIN;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'ultimateteam' AND table_name = 'store_packs'
                   AND column_name = 'consumable_mix') THEN
        RAISE EXCEPTION 'consumables store: the database has no manager league cards yet; start the image with migration 071 first';
    END IF;
    IF (SELECT count(*) FROM ultimateteam.store_packs WHERE kind = 'store' AND pack_id IN (1, 2, 4, 5) AND banded_odds) <> 4
        OR NOT EXISTS (SELECT 1 FROM ultimateteam.store_packs WHERE pack_id = 8 AND price = 25000) THEN
        RAISE EXCEPTION 'consumables store: this is not the phase-1 store (phase1-store.sql has not run)';
    END IF;
    IF EXISTS (SELECT 1 FROM ultimateteam.store_packs WHERE pack_id = 9) THEN
        RAISE EXCEPTION 'consumables store: pack 9 already exists';
    END IF;
END
$$;

UPDATE ultimateteam.store_packs SET manager_league_card_share = 0.5,
    bio = 'A bronze pack containing 12 bronze items and a chance of a Manager, Manager League card or Chemistry Style.'
WHERE pack_id = 1;
UPDATE ultimateteam.store_packs SET manager_league_card_share = 0.5,
    bio = 'A premium bronze pack containing 12 bronze items with 1 guaranteed rare player and a chance of a Manager, Manager League card or Chemistry Style.'
WHERE pack_id = 4;
UPDATE ultimateteam.store_packs SET manager_league_card_share = 0.5,
    bio = 'A silver pack containing 12 silver items and a chance of a Manager, Manager League card or Chemistry Style.'
WHERE pack_id = 2;
UPDATE ultimateteam.store_packs SET manager_league_card_share = 0.5,
    bio = 'A premium silver pack containing 12 silver items with 1 guaranteed rare player and a chance of a Manager, Manager League card or Chemistry Style.'
WHERE pack_id = 5;

-- The manager league cards are generated definitions (migration 071), not FIFA data.
INSERT INTO ultimateteam.store_packs(pack_id, kind, enabled, name, bio, price, allowed_currencies, size, random_consumable_count,
    consumable_mix, store_group, always_present, display_only)
VALUES (9, 'store', true, 'Consumables Pack', '12 consumables: manager league cards, position modifiers and chemistry styles.', 3000,
    ARRAY['coins'], 12, 12, '{"manager_league": 4, "position_modifier": 4, "chemistry_style": 4}', 'special', false, false);

COMMIT;

SELECT pack_id, name, price, manager_league_card_share, consumable_mix
FROM ultimateteam.store_packs WHERE kind = 'store' AND NOT admin_only ORDER BY pack_id;
