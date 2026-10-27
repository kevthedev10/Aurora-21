-- Develop already published SBC migrations 117/118; the pack branch used the same numbers.
-- Repair missing SBC changes on databases from that branch without replaying pack edits.
ALTER TABLE ultimateteam.sbc_squad_counts ADD COLUMN IF NOT EXISTS formation text,
    ADD COLUMN IF NOT EXISTS manager_item_id bigint;
-- The retail SBC rules (fix/sbc-locked-slots) reach existing databases, not just new seeds. Start-up filling only adds
-- missing object keys (the new CLUBS rule and _formationComment arrive that way); it never changes a stored value or
-- list item, so these three changes need a migration. Only the old shipped values are replaced, so an operator's own
-- edit is kept.

-- 1. Chemistry is shown and checked: the server now computes it from the saved squad (SbcService.Validation), so it is
--    no longer an ignored requirement. A list left empty is stored as one [] row (073's contract), at the item's ordinal.
--    The statement sees the table as it was before the DELETE, so "left empty" means no item other than CHEMISTRY.
WITH chemistry AS (
    DELETE FROM ultimateteam.content_rules
    WHERE file = 'sbc' AND jsonb_array_length(path) = 2 AND path->>0 = 'ignoredRequirements' AND value = '"CHEMISTRY"'::jsonb
    RETURNING ordinal
)
INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
SELECT 'sbc', '["ignoredRequirements"]'::jsonb, '[]'::jsonb, min(ordinal) FROM chemistry
HAVING count(*) > 0
   AND NOT EXISTS (SELECT 1 FROM ultimateteam.content_rules
                   WHERE file = 'sbc' AND path->>0 = 'ignoredRequirements' AND value <> '"CHEMISTRY"'::jsonb)
ON CONFLICT (file, path) DO NOTHING;

UPDATE ultimateteam.content_rules SET value = to_jsonb('Requirement rule types dropped from every challenge: neither sent to the client nor checked on submit. CHEMISTRY is sent and checked: the server computes the squad''s chemistry (chemistry.json) from the slots, formation and manager FIFA saves, plus the challenge''s locked-slot cards, and never trusts the chemistry FIFA reports.'::text),
    updated_at = now()
WHERE file = 'sbc' AND path = '["_ignoredRequirementsComment"]'::jsonb AND jsonb_typeof(value) = 'string'
    AND strpos(value #>> '{}', 'CHEMISTRY is dropped because the server cannot check it') > 0;

-- 2. FND_ADVANCED (Gold Rush, Rising Standard, Marquee Squad, Elite Standard) has no retail FIFA 21 source: hide it.
--    Appended after the last hiddenSets item; later rows of the file move down one ordinal to make room.
DO $$
DECLARE last_item record;
BEGIN
    IF EXISTS (SELECT 1 FROM ultimateteam.content_rules
               WHERE file = 'sbc' AND path->>0 = 'hiddenSets' AND value = '"FND_ADVANCED"'::jsonb) THEN
        RETURN;
    END IF;
    SELECT (path->>1)::integer AS position, ordinal INTO last_item FROM ultimateteam.content_rules
    WHERE file = 'sbc' AND jsonb_array_length(path) = 2 AND path->>0 = 'hiddenSets'
    ORDER BY ordinal DESC LIMIT 1;
    IF last_item IS NULL THEN
        RETURN;
    END IF;
    UPDATE ultimateteam.content_rules SET ordinal = ordinal + 1 WHERE file = 'sbc' AND ordinal > last_item.ordinal;
    INSERT INTO ultimateteam.content_rules(file, path, value, ordinal)
    VALUES ('sbc', jsonb_build_array('hiddenSets', last_item.position + 1), '"FND_ADVANCED"'::jsonb, last_item.ordinal + 1);
END $$;

-- 3. A challenge with no client-visible rule names its player count instead of always claiming eleven
--    (First Exchange takes one player).
UPDATE ultimateteam.content_rules SET value = '"Players in Squad: {count}"'::jsonb, updated_at = now()
WHERE file = 'sbc' AND path = '["text","noRequirements"]'::jsonb AND value = '"Submit an 11-player squad."'::jsonb;



-- Old pack columns identify databases that still need the pack migration.
-- Already-upgraded branch databases retain their published odds and operator edits.
DO $merge$ BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam'
        AND table_name='store_packs' AND column_name='special_chance') THEN
        EXECUTE $pack$
-- Packs and pack odds v3 (docs/pack-odds-v3.md, agreed 2026-10-02).
--  * One kind of pack: the former reward-only rows become ordinary packs outside the store lineup. The store sells what the
--    lineup switches on (Grant only, admin_only, still keeps a pack out of it); rewards name packs by id as before.
--  * Every pack draws from its own complete odds table (category_chances): no hidden special_chance, no 10% store or 1.1%
--    reward fallback, no global Icon fallback, no banded/unbanded switch and no named Gold Rare band table.
--  * The agreed odds per rare slot (per choice for picks); "upgraded" is the row for specials rated above a silver or
--    bronze slot whose base card is in that tier. Packs with their own draw rules (540-542 fixed TOTW pool, 544 Match
--    Pack, 550-553 red picks, 560-563 Icon Swaps tokens) keep an empty table.
SELECT set_config('ultimateteam.content_publish', 'on', true);

-- Any other pack keeps the odds it drew with: an empty table becomes what the defaults gave it (Icon in gold slots, the
-- rest of its special chance as TOTW, picks 3% TOTW); an owned definition kept by migration 097 likewise gets the reward
-- fallback as TOTW. Specials-only and guaranteed-special packs keep weighing their eligible categories equally, which an
-- empty table still means for forced slots. The old columns are only there before this migration has run.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'ultimateteam' AND table_name = 'store_packs'
               AND column_name = 'special_chance') THEN
        UPDATE ultimateteam.store_packs SET category_chances = CASE
                WHEN coalesce(player_pick_count, 0) > 0 OR jsonb_array_length(coalesce(player_picks, '[]'::jsonb)) > 0
                    THEN '{"totw": 0.03, "icon": 0.0005}'::jsonb
                ELSE jsonb_build_object(
                    'totw', coalesce(special_chance, CASE WHEN kind = 'reward'
                        THEN (SELECT reward_pack_special_chance FROM ultimateteam.store_pack_details LIMIT 1) ELSE 0.10 END),
                    'icon', CASE WHEN coalesce(gold_weight, 0) > 0 THEN 0.0005 ELSE 0 END)
            END
        WHERE kind IN ('store', 'reward') AND coalesce(category_chances, '{}'::jsonb) = '{}'::jsonb
            AND NOT coalesce(promo_only, false) AND coalesce(guaranteed_specials, 0) = 0
            AND resource_pool IS NULL AND swap_token_rareflag IS NULL AND identity_pack IS NULL
            AND NOT coalesce(all_consumables, false) AND NOT coalesce(all_kits, false) AND NOT coalesce(kit_stadium_pack, false)
            AND coalesce(set_stadium_id, 0) = 0 AND coalesce(random_consumable_count, 0) = 0 AND coalesce(random_manager_count, 0) = 0
            AND pack_id NOT IN (544, 550, 551, 552, 553);

        UPDATE ultimateteam.owned_legacy_pack_definitions SET category_chances = jsonb_build_object(
                'totw', coalesce(special_chance, (SELECT reward_pack_special_chance FROM ultimateteam.store_pack_details LIMIT 1)),
                'icon', CASE WHEN coalesce(gold_weight, 0) > 0 THEN 0.0005 ELSE 0 END)
        WHERE coalesce(category_chances, '{}'::jsonb) = '{}'::jsonb AND NOT coalesce(promo_only, false)
            AND coalesce(guaranteed_specials, 0) = 0 AND coalesce(player_pick_count, 0) = 0
            AND jsonb_array_length(coalesce(player_picks, '[]'::jsonb)) = 0;
    END IF;
END $$;

UPDATE ultimateteam.store_packs SET category_chances = v.chances
FROM (VALUES
    -- Store bronze and silver: TOTW 0.2% / 0.3% / 0.4%, a small upgraded-specials chance, no Icons (gold slots only).
    (70, '{"totw": 0.002, "icon": 0, "upgraded": 0.0002}'::jsonb),
    (72, '{"totw": 0.002, "icon": 0, "upgraded": 0.0002}'::jsonb),
    (71, '{"totw": 0.003, "icon": 0, "upgraded": 0.0003}'::jsonb),
    (73, '{"totw": 0.003, "icon": 0, "upgraded": 0.0003}'::jsonb),
    (76, '{"totw": 0.004, "icon": 0, "upgraded": 0.0003}'::jsonb),
    (77, '{"totw": 0.004, "icon": 0, "upgraded": 0.0003}'::jsonb),
    -- 2 Rare … Players packs: base rares only (rare flag 1).
    (80, '{"totw": 0, "icon": 0, "upgraded": 0}'::jsonb),
    (81, '{"totw": 0, "icon": 0, "upgraded": 0}'::jsonb),
    (82, '{"totw": 0, "icon": 0}'::jsonb),
    -- Reward packs named like a store pack draw like it.
    (100, '{"totw": 0.002, "icon": 0, "upgraded": 0.0002}'::jsonb),
    (200, '{"totw": 0.002, "icon": 0, "upgraded": 0.0002}'::jsonb),
    (201, '{"totw": 0.003, "icon": 0, "upgraded": 0.0003}'::jsonb),
    -- Standard gold level (store Gold Pack 74).
    (300, '{"totw": 0.006, "icon": 0.0005}'::jsonb),
    (302, '{"totw": 0.006, "icon": 0.0005}'::jsonb),
    (304, '{"totw": 0.006, "icon": 0.0005}'::jsonb),
    (306, '{"totw": 0.006, "icon": 0.0005, "upgraded": 0.0002}'::jsonb),
    (534, '{"totw": 0.006, "icon": 0.0005, "upgraded": 0.0002}'::jsonb),
    -- Premium level (store Premium Gold 75 and Jumbo Premium Gold 78).
    (301, '{"totw": 0.008, "icon": 0.0008}'::jsonb),
    (303, '{"totw": 0.008, "icon": 0.0008}'::jsonb),
    (305, '{"totw": 0.008, "icon": 0.0008}'::jsonb),
    (319, '{"totw": 0.008, "icon": 0.0008}'::jsonb),
    -- Prime / Rare level.
    (308, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (315, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (543, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (400, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (401, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (402, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (403, '{"totw": 0.01, "icon": 0.001}'::jsonb),
    (307, '{"totw": 0.01, "icon": 0.001, "upgraded": 0.0004}'::jsonb),
    (518, '{"totw": 0.01, "icon": 0.001, "upgraded": 0.0004}'::jsonb),
    (309, '{"totw": 0.01, "icon": 0.001, "upgraded": 0.0004}'::jsonb),
    (406, '{"totw": 0.01, "icon": 0.001, "upgraded": 0.0004}'::jsonb),
    -- Ultimate level.
    (404, '{"totw": 0.012, "icon": 0.0012}'::jsonb),
    (405, '{"totw": 0.012, "icon": 0.0012}'::jsonb),
    -- Player picks, per choice: better picks offer better choices.
    (513, '{"totw": 0.03, "icon": 0.0005}'::jsonb),
    (514, '{"totw": 0.04, "icon": 0.001}'::jsonb),
    (515, '{"totw": 0.05, "icon": 0.0015}'::jsonb),
    (516, '{"totw": 0.06, "icon": 0.002}'::jsonb)
) AS v(pack_id, chances)
WHERE store_packs.pack_id = v.pack_id AND store_packs.kind IN ('store', 'reward');

-- One kind of pack. The former reward rows stay out of the store.
UPDATE ultimateteam.store_packs SET kind = 'store', enabled = false WHERE kind = 'reward';
UPDATE ultimateteam.owned_legacy_pack_definitions SET kind = 'store', enabled = false WHERE kind = 'reward';
ALTER TABLE ultimateteam.store_packs DROP CONSTRAINT IF EXISTS lfv4_ck_store_packs_kind,
    ADD CONSTRAINT lfv4_ck_store_packs_kind CHECK (kind IN ('store', 'template', 'display'));

ALTER TABLE ultimateteam.store_packs DROP COLUMN IF EXISTS special_chance, DROP COLUMN IF EXISTS banded_odds, DROP COLUMN IF EXISTS odds_table;
ALTER TABLE ultimateteam.owned_legacy_pack_definitions DROP COLUMN IF EXISTS special_chance, DROP COLUMN IF EXISTS banded_odds,
    DROP COLUMN IF EXISTS odds_table;
ALTER TABLE ultimateteam.store_pack_details DROP COLUMN IF EXISTS reward_pack_special_chance;

-- The named Gold Rare band tables (the unused "volume" profile) leave the active pack odds rules.
DELETE FROM ultimateteam.content_rules WHERE file = 'pack_odds' AND path ->> 0 = 'goldRareBandTables';

SELECT set_config('ultimateteam.content_publish', '', true);


$pack$;
    END IF;
END $merge$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(119);
