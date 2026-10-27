-- Aurora 21 launch store (owner, 2026-09-29): the store sells only the six standard packs of the other Aurora servers
-- (Aurora 16's src/fut/pack-catalog.js and FIFA 15 Local FUT's pack_settings.json: 400 / 750 / 2,500 / 3,750 /
-- 5,000 / 7,500 coins, 12 items) and a Cosmetics Pack with the insides of Horizzon's Identity Crisis (pack 66). Every
-- other store pack is removed; the objective rewards that still grant packs 42-47 keep those rows, off sale.
--
-- Run once, on the production database, after the predeploy dump and after exporting a copy of the old packs
-- (see README.md in this folder). It is not a migration: the store is operator data (the panel edits it live), and a
-- local or test install keeps the bundled store. Pack odds (special, manager and chemistry-style chances) stay as
-- the bundled Bronze/Silver/Gold packs have them until the owner provides new ones.
BEGIN;

-- The premium packs start as copies of their tier's standard pack, so every odds column matches it.
CREATE TEMP TABLE launch_premium ON COMMIT DROP AS
    SELECT * FROM ultimateteam.store_packs WHERE pack_id IN (1, 2, 3) AND kind = 'store';
DO $$
BEGIN
    IF (SELECT count(*) FROM launch_premium) <> 3 THEN
        RAISE EXCEPTION 'launch store: the Bronze, Silver and Gold store packs (1, 2, 3) are not all present';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM ultimateteam.store_packs WHERE pack_id = 66 AND identity_pack IS NOT NULL) THEN
        RAISE EXCEPTION 'launch store: pack 66 (the identity pack the Cosmetics Pack copies) is missing';
    END IF;
    IF EXISTS (SELECT 1 FROM ultimateteam.clubs WHERE persona_id <> 1000000002) THEN
        RAISE NOTICE 'launch store: player clubs still exist; unopened packs of removed store packs will no longer open';
    END IF;
END
$$;

-- The Cosmetics Pack copies pack 66's contents before 66 goes.
CREATE TEMP TABLE launch_cosmetics ON COMMIT DROP AS
    SELECT * FROM ultimateteam.store_packs WHERE pack_id = 66;

-- Objective group awards grant packs 42-47 (rules/objectives.json): kept, off sale, until those awards change.
UPDATE ultimateteam.store_packs SET admin_only = true
WHERE kind = 'store' AND pack_id BETWEEN 42 AND 47;

-- Everything else sold in the store goes.
DELETE FROM ultimateteam.store_packs
WHERE kind = 'store' AND pack_id NOT IN (1, 2, 3) AND pack_id NOT BETWEEN 42 AND 47;

-- The standard packs: Bronze, Silver and Gold keep their ids; the premium ones take 4, 5 and 6.
UPDATE ultimateteam.store_packs SET price = 400, points_price = 8, rare_slots = 0, admin_only = false, enabled = true,
    bio = 'A bronze pack containing 12 items and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 1;
UPDATE ultimateteam.store_packs SET price = 2500, points_price = 50, rare_slots = 0, admin_only = false, enabled = true,
    bio = 'A silver pack containing 12 items and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 2;
UPDATE ultimateteam.store_packs SET price = 5000, points_price = 100, rare_slots = 1, admin_only = false, enabled = true,
    bio = 'A gold pack containing 12 items with 1 guaranteed rare and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 3;

UPDATE launch_premium SET pack_id = pack_id + 3, name = 'Premium ' || name, admin_only = false, enabled = true, always_present = false;
UPDATE launch_premium SET price = 750, points_price = 15, rare_slots = 1,
    bio = 'A premium bronze pack containing 12 items with 1 guaranteed rare and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 4;
UPDATE launch_premium SET price = 3750, points_price = 75, rare_slots = 1,
    bio = 'A premium silver pack containing 12 items with 1 guaranteed rare and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 5;
UPDATE launch_premium SET price = 7500, points_price = 150, rare_slots = 3,
    bio = 'A premium gold pack containing 12 items with 3 guaranteed rares and a chance of a Manager or Chemistry Style.'
WHERE pack_id = 6;
INSERT INTO ultimateteam.store_packs SELECT * FROM launch_premium;

-- The Cosmetics Pack (the Promo tab): Horizzon's Identity Crisis's insides under a store name and price.
UPDATE launch_cosmetics SET pack_id = 7, name = 'Cosmetics Pack', price = 5000, points_price = 100, store_group = 'special',
    admin_only = false, enabled = true,
    bio = '2 match balls, 3 club badges, 4 kits and 3 stadiums for My Stadium Match Day.';
INSERT INTO ultimateteam.store_packs SELECT * FROM launch_cosmetics;

COMMIT;

-- What the store sells now (the objective packs 42-47 are admin_only and never listed).
SELECT pack_id, name, price, points_price, rare_slots, coalesce(store_group, '(by tier)') AS tab, admin_only
FROM ultimateteam.store_packs WHERE kind = 'store' ORDER BY pack_id;
