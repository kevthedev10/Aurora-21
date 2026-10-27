-- The store's My Stadium Pack (pack 83): six My Stadium items the club does not own yet, owned on the spot (VanityService
-- DrawPackAsync): a pitch line paint, a mow pattern, a commentator nickname, a goal song and two tifos. The first tifo is
-- from the basic block (6800000+); the second is a club or player tifo 30% of the time. Items the Stadium Development
-- rewards give (rules/club.json vanity.rewardOnly) are never drawn. Priced like the economy plan's cosmetics pack
-- (pack-odds-2026-09/PLAN.md 3.3: 5,000 coins); My Stadium items are untradeable, so it is a pure coin sink.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS vanity_pack jsonb;
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD COLUMN IF NOT EXISTS vanity_pack jsonb;

INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,bio,price,points_price,
    size,gold_weight,silver_weight,bronze_weight,rare_slots,manager_chance,chem_style_chance,
    store_group,include_general_pool,vanity_pack)
VALUES (83,'store',true,'My Stadium Pack','A pitch line paint, a mow pattern, a commentator nickname, a goal song and two tifos.',
    5000,0,6,0,0,0,0,0,0,'special',false,
    '{"slots": [
        {"subtypes": [506]},
        {"subtypes": [505]},
        {"subtypes": [518]},
        {"subtypes": [517]},
        {"subtypes": [500], "pools": [{"weight": 1, "min": 6800000, "max": 6809999}]},
        {"subtypes": [500], "pools": [{"weight": 70, "min": 6800000, "max": 6809999}, {"weight": 30, "max": 6799999}]}
    ]}'::jsonb)
ON CONFLICT(pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(123);
