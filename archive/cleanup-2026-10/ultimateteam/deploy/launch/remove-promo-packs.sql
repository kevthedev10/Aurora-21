-- Aurora 21 launch store, part two (owner, 2026-09-29): the six promo packs launch-store.sql kept off sale go too
-- (42 JayFreezy, 43 Ballon d'Or, 44 Galacticos, 45 Icon Vault, 46 Deep Freeze Mega, 47 84+ x50). The objective groups and
-- Season Progress levels that granted them now grant FIFA 21 reward packs (rules/objectives.json: 402 Jumbo Rare Players,
-- 403 Mega, 404 Rare Mega, 405 Ultimate).
--
-- Run once, on the production database, after launch-store.sql and once the server image carrying those rules is live:
-- an older image would still award 42-47. It stops without changing anything while a club holds an unopened copy of
-- one of them, or if the reward packs that replace them are missing.
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ultimateteam.unopened_packs WHERE pack_id BETWEEN 42 AND 47) THEN
        RAISE EXCEPTION 'promo packs: a club holds an unopened pack 42-47; nothing was removed';
    END IF;
    IF (SELECT count(*) FROM ultimateteam.store_packs WHERE pack_id IN (402, 403, 404, 405) AND kind = 'reward') <> 4 THEN
        RAISE EXCEPTION 'promo packs: the reward packs 402-405 that replace them are missing; nothing was removed';
    END IF;
END
$$;

DELETE FROM ultimateteam.store_packs WHERE kind = 'store' AND pack_id BETWEEN 42 AND 47;

COMMIT;

-- What the store holds now: the six standard packs and the Cosmetics Pack.
SELECT pack_id, name, price, points_price, rare_slots, coalesce(store_group, '(by tier)') AS tab, admin_only
FROM ultimateteam.store_packs WHERE kind = 'store' ORDER BY pack_id;
