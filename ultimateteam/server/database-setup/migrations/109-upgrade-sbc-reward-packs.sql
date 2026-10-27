-- Reward packs for the Bronze, Silver and Gold Upgrade SBCs (sbc.json UPG_*, rules/sbc.json rewardPacks
-- two_rare_<tier>_players): two rare players of one tier, no specials, hidden from the store.
-- The live server already published these definitions through the panel (changeset 4); this keeps every other database
-- in step and leaves an existing definition alone. Replay-safe: an upgrade test re-applies migrations 103 onward.
WITH packs(id,tier,title) AS (VALUES
    (80,'bronze','Bronze'),
    (81,'silver','Silver'),
    (82,'gold','Gold')
)
INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,bio,price,points_price,
    size,gold_weight,silver_weight,bronze_weight,rare_slots,special_chance,manager_chance,chem_style_chance,
    store_group,include_general_pool)
SELECT id,'store',false,'2 Rare ' || title || ' Players Pack','2 rare ' || tier || ' players.',0,0,
    2,CASE WHEN tier='gold' THEN 100 ELSE 0 END,CASE WHEN tier='silver' THEN 100 ELSE 0 END,
    CASE WHEN tier='bronze' THEN 100 ELSE 0 END,2,0,0,0,tier,true
FROM packs
ON CONFLICT(pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(109);
