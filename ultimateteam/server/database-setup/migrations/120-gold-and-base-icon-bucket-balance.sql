-- The old pack branch applied this profile as migration 118. Preserve its subsequent
-- operator changes, including cleared desirability marks, when joining develop.
DO $merge$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='ultimateteam'
        AND table_name='promo_cards' AND column_name='draw_reduction') THEN
        EXECUTE $balance$
-- Aurora balance profile, not recovered EA probabilities. Research, identities and limitations:
-- docs/gold-and-base-icon-bucket-balance.md (2026-10-02).
-- Gold Common: rating only. Other pools retain their buckets; desirable cards get
-- a per-membership draw_reduction (1 = ordinary, 5 = five times harder within a bucket).
-- The selector chooses a bucket first, then weights eligible cards by 1/draw_reduction.
-- Keep Gold Common's old bucket (disabled) for history; do not change membership, pack odds,
-- promo availability, other Icon tiers or any club state.
SELECT set_config('ultimateteam.content_publish','on',true);
ALTER TABLE ultimateteam.promo_cards ADD COLUMN IF NOT EXISTS draw_reduction numeric(12,6) NOT NULL DEFAULT 1
    CHECK (draw_reduction BETWEEN 1 AND 999999);

CREATE TEMP TABLE balance_desirable_gold(asset bigint PRIMARY KEY, name text) ON COMMIT DROP;
INSERT INTO balance_desirable_gold VALUES
    (183394,'Moussa Sissoko'), (216388,'Allan Saint-Maximin'), (231443,'Ousmane Dembele'),
    (225100,'Joe Gomez'), (228618,'Ferland Mendy'), (188377,'Kyle Walker'),
    (201535,'Raphael Varane'), (205923,'Alphonso Davies'), (230767,'Renato Sanches'),
    (238794,'Vinicius Jr'), (239053,'Federico Valverde'), (240130,'Eder Militao'),
    (231677,'Marcus Rashford'), (211300,'Anthony Martial'), (212188,'Timo Werner'),
    (200949,'Lucas Moura'), (230666,'Gabriel Jesus'), (227928,'Nelson Semedo'),
    (229880,'Aaron Wan-Bissaka'), (195864,'Paul Pogba'), (215914,'N Golo Kante'),
    (200104,'Son Heung-min'), (188567,'Pierre-Emerick Aubameyang'), (183277,'Eden Hazard'),
    (211110,'Paulo Dybala'), (208722,'Sadio Mane'), (209331,'Mohamed Salah'),
    (203376,'Virgil van Dijk'), (231747,'Kylian Mbappe'), (190871,'Neymar Jr'),
    (20801,'Cristiano Ronaldo'), (158023,'Lionel Messi');

-- Resource IDs, not asset IDs: only the Base version is affected.
CREATE TEMP TABLE balance_desirable_icons(resource bigint PRIMARY KEY, name text) ON COMMIT DROP;
INSERT INTO balance_desirable_icons VALUES
    (17015642,'Patrick Vieira'), (17015649,'Ruud Gullit'), (16967265,'Eusebio'),
    (17019737,'Johan Cruyff'), (17014279,'Ronaldo'), (16967259,'Pele'),
    (17014289,'Diego Maradona'), (17025665,'Zinedine Zidane'), (17024768,'Garrincha'),
    (17015922,'Ronaldinho'), (17015653,'George Best'), (16778325,'Paolo Maldini'),
    (17024543,'Carlos Alberto'), (17033229,'Ferenc Puskas'), (17032692,'Eric Cantona'),
    (17032573,'Samuel Eto o'), (17024916,'Kenny Dalglish'), (17016819,'Emilio Butragueno'),
    (17014281,'Rio Ferdinand'), (17024521,'Michael Essien'), (16999697,'Laurent Blanc'),
    (17016741,'Marcel Desailly'), (17020144,'Clarence Seedorf'), (17014287,'Roberto Carlos'),
    (17028105,'Gianluca Zambrotta'), (17033369,'Philipp Lahm'), (17015652,'Lothar Matthaus'),
    (17024290,'Kaka');

DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.promos WHERE code IN ('gold_common','gold_rare','base_icons')) <> 3 THEN
        RAISE EXCEPTION 'Gold Common, Gold Rare and Base Icons pools must exist';
    END IF;
    IF EXISTS (SELECT 1 FROM ultimateteam.promo_cards c JOIN ultimateteam.promos p USING(promo_id)
        JOIN ultimateteam.player_card_definitions pc ON pc.definition_id=c.player_card_definition_id
        WHERE (p.code='gold_common' AND (pc.card_type_id<>2 OR pc.rating NOT BETWEEN 75 AND 99))
           OR (p.code='gold_rare' AND (pc.card_type_id<>5 OR pc.rating NOT BETWEEN 75 AND 99))
           OR (p.code='base_icons' AND pc.rating NOT BETWEEN 85 AND 91)) THEN
        RAISE EXCEPTION 'A target pool contains an unexpected card type or rating';
    END IF;
END $$;

-- Gold Common uses rating-only buckets with a decreasing per-card rating curve.
CREATE TEMP TABLE balance_common_buckets ON COMMIT DROP AS
SELECT p.promo_id,pc.rating,'rating_' || pc.rating::text AS code,
    count(*) * CASE pc.rating WHEN 75 THEN 1 WHEN 76 THEN 0.8 WHEN 77 THEN 0.6
        WHEN 78 THEN 0.4 WHEN 79 THEN 0.25 WHEN 80 THEN 0.15 WHEN 81 THEN 0.08
        ELSE 0.04 * power(0.5::numeric,pc.rating-82) END AS weight
FROM ultimateteam.promos p JOIN ultimateteam.promo_cards c USING(promo_id)
JOIN ultimateteam.player_card_definitions pc ON pc.definition_id=c.player_card_definition_id
WHERE p.code='gold_common' GROUP BY p.promo_id,pc.rating;
INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight,enabled)
SELECT promo_id,code,rating::text,greatest(0.000001,round(weight,6)),true FROM balance_common_buckets
ON CONFLICT(promo_id,code) DO UPDATE SET name=excluded.name,draw_weight=excluded.draw_weight,enabled=true;
UPDATE ultimateteam.promo_cards c SET bucket_id=b.id
FROM ultimateteam.promos p,ultimateteam.player_card_definitions pc,ultimateteam.promo_buckets b
WHERE c.promo_id=p.promo_id AND p.code='gold_common' AND pc.definition_id=c.player_card_definition_id
    AND b.promo_id=p.promo_id AND b.code='rating_' || pc.rating::text;
UPDATE ultimateteam.promo_buckets b SET enabled=false FROM ultimateteam.promos p
WHERE b.promo_id=p.promo_id AND p.code='gold_common'
    AND NOT EXISTS(SELECT 1 FROM balance_common_buckets seed WHERE seed.promo_id=b.promo_id AND seed.code=b.code);

-- Keep the existing Gold Rare buckets and weights, including operator edits.
-- Base Icons also keep their existing rating buckets and weights. Replace migration
-- 099's synthetic rating-89 placement of Gullit/Vieira with an explicit per-card reduction.
UPDATE ultimateteam.promo_cards c SET bucket_id=b.id
FROM ultimateteam.promos p,ultimateteam.item_definitions d,ultimateteam.promo_buckets b
WHERE c.promo_id=p.promo_id AND p.code='base_icons' AND d.definition_id=c.player_card_definition_id
    AND d.native_resource_id IN (17015642,17015649) AND b.promo_id=p.promo_id AND b.code='rating_86'
    AND c.bucket_id=(SELECT old.id FROM ultimateteam.promo_buckets old
        WHERE old.promo_id=p.promo_id AND old.code='rating_89');

-- Marks belong to membership in a promo, so a Gold version does not suppress its
-- TOTW or other special versions, and a Base Icon mark does not affect Mid/Prime.
-- Missing/removed members stay absent. Other operator-assigned reductions survive.
UPDATE ultimateteam.promo_cards c SET draw_reduction=5
FROM ultimateteam.promos p,ultimateteam.item_definitions d
WHERE c.promo_id=p.promo_id AND d.definition_id=c.player_card_definition_id AND c.draw_reduction=1
    AND ((p.code='gold_rare' AND EXISTS(SELECT 1 FROM balance_desirable_gold s WHERE s.asset=d.native_asset_id))
      OR (p.code='base_icons' AND EXISTS(SELECT 1 FROM balance_desirable_icons s WHERE s.resource=d.native_resource_id)));

SELECT set_config('ultimateteam.content_publish','',true);


$balance$;
    END IF;
END $merge$;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(120);
