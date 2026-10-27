-- Replace the testing store catalogue with nine authored mixed-item definitions.
-- These remain disabled until missing native consumables have been verified in FIFA 21.
-- Retired definitions stay resolvable for already-owned packs; competition rewards are untouched.
ALTER TABLE ultimateteam.store_packs ADD COLUMN mixed_contents jsonb
    CHECK (mixed_contents IS NULL OR jsonb_typeof(mixed_contents) = 'object');

DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM ultimateteam.store_packs WHERE pack_id BETWEEN 70 AND 78) THEN
        RAISE EXCEPTION 'Mixed store pack ids 70-78 are already in use; review the catalogue before upgrading';
    END IF;
END $$;

UPDATE ultimateteam.store_packs SET kind='reward', enabled=false, always_present=false
WHERE kind='store';

WITH packs(id,name,coins,points,size,tier,rare,pmin,pmax,cmin,cmax,smin,smax,mmin,mmax) AS (VALUES
    (70,'Bronze Pack',400,0,12,'bronze',1,3,5,2,6,2,6,0,3),
    (71,'Premium Bronze Pack',750,0,12,'bronze',3,3,6,2,6,3,6,0,2),
    (72,'Silver Pack',2500,50,12,'silver',1,3,9,1,9,0,3,0,3),
    (73,'Premium Silver Pack',3750,75,12,'silver',3,3,6,3,8,0,3,0,1),
    (74,'Gold Pack',5000,100,12,'gold',1,3,6,5,9,0,3,0,2),
    (75,'Premium Gold Pack',7500,150,12,'gold',3,3,5,5,8,0,4,0,2),
    (76,'Jumbo Premium Bronze Pack',1500,75,24,'bronze',7,5,9,6,11,3,11,1,7),
    (77,'Jumbo Premium Silver Pack',7500,150,24,'silver',7,5,11,6,17,1,6,1,4),
    (78,'Jumbo Premium Gold Pack',15000,300,24,'gold',7,6,12,11,15,0,5,0,2)
)
INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,bio,price,points_price,allowed_currencies,
    size,gold_weight,silver_weight,bronze_weight,rare_slots,special_chance,manager_chance,chem_style_chance,
    store_group,banded_odds,include_general_pool,mixed_contents)
SELECT id,'store',false,name,size || ' ' || tier || ' items, including ' || rare || ' rare.',coins,points,
    CASE WHEN points=0 THEN ARRAY['coins'] ELSE ARRAY['coins','points'] END,
    size,CASE WHEN tier='gold' THEN 100 ELSE 0 END,CASE WHEN tier='silver' THEN 100 ELSE 0 END,
    CASE WHEN tier='bronze' THEN 100 ELSE 0 END,rare,0,0,0,tier,true,true,
    jsonb_build_object('tier',tier,'players',jsonb_build_object('min',pmin,'max',pmax),
        'consumables',jsonb_build_object('min',cmin,'max',cmax),'stadium',jsonb_build_object('min',smin,'max',smax),
        'staff',jsonb_build_object('min',mmin,'max',mmax))
FROM packs;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(95);
