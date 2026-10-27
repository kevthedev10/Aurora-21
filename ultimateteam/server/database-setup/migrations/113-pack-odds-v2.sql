-- Pack odds v2 (2026-10-02; model, evidence and results: deploy/launch/pack-odds-v2/README.md). Applied with the deploy,
-- so the odds the README promises arrive with the code that gives a pack's rares to its players first.
--  * Gold Rare groups: a visible top (86+ per rare slot 0.5% -> 0.7%) with a slightly fatter 82-85 middle.
--  * Gold store packs draw live TOTW in-forms and Icons in their rare player slots (store packs had no in-forms).
--  * Silver rares quick-sell at most 148 (was 90, below silver commons' 98-111), still under the 150 market floor.
-- Operator edits made since are respected: a group, pack or rule changed away from its seeded value is left alone.
SELECT set_config('ultimateteam.content_publish', 'on', true);

UPDATE ultimateteam.promo_buckets bucket SET draw_weight = v.weight
FROM ultimateteam.promos promo, (VALUES
    ('rating_75_79', 60.4, 58::numeric),
    ('rating_80_81', 28, 29),
    ('rating_82_83', 8.7, 9.6),
    ('rating_84_85', 2.4, 2.7),
    ('rating_86_87', 0.35, 0.5),
    ('rating_88_89', 0.11, 0.15),
    ('rating_90_99', 0.04, 0.05)
) AS v(code, seeded, weight)
WHERE promo.code = 'gold_rare' AND bucket.promo_id = promo.promo_id AND bucket.code = v.code
    AND bucket.draw_weight = v.seeded;

-- Mixed gold store packs only (migration 095): Gold, Premium Gold, Jumbo Premium Gold. A per-pack chance replaces the
-- global rules/pack_odds.json iconChance for that pack, so icon is always set alongside totw.
UPDATE ultimateteam.store_packs SET category_chances = v.chances
FROM (VALUES
    (74, '{"totw": 0.006, "icon": 0.0005}'::jsonb),
    (75, '{"totw": 0.008, "icon": 0.0008}'::jsonb),
    (78, '{"totw": 0.008, "icon": 0.0008}'::jsonb)
) AS v(pack_id, chances)
WHERE store_packs.pack_id = v.pack_id AND store_packs.kind = 'store' AND store_packs.mixed_contents IS NOT NULL
    AND coalesce(store_packs.category_chances, '{}'::jsonb) = '{}'::jsonb;

UPDATE ultimateteam.content_rules SET value = '148'::jsonb, updated_at = now()
WHERE file = 'cards' AND path = '["quickSell","tiers","silver","rareMaximum"]'::jsonb AND value = '90'::jsonb;

SELECT set_config('ultimateteam.content_publish', '', true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(113);
