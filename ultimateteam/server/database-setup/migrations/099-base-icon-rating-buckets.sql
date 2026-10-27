-- Approved Base Icon distribution. Bucket names describe the initial rating groups,
-- with the maintainer's explicit exceptions for Base Gullit and Base Vieira.
-- Native resources below are already members of base_icons in migration 076.
CREATE TEMP TABLE base_icon_bucket_seed (
    rating bigint PRIMARY KEY, draw_weight numeric(12,6) NOT NULL
) ON COMMIT DROP;
INSERT INTO base_icon_bucket_seed VALUES
    (85,35), (86,28), (87,20), (88,10), (89,5), (90,1.5), (91,0.5);

DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.promos WHERE code='base_icons') <> 1 THEN
        RAISE EXCEPTION 'Base Icons promo is missing';
    END IF;
    IF (SELECT count(*) FROM ultimateteam.promo_cards c
        JOIN ultimateteam.promos p USING(promo_id)
        JOIN ultimateteam.item_definitions d ON d.definition_id=c.player_card_definition_id
        WHERE p.code='base_icons' AND d.native_resource_id IN (17015642,17015649)) <> 2 THEN
        RAISE EXCEPTION 'Expected Base Vieira and Base Gullit membership is missing';
    END IF;
    IF EXISTS (
        SELECT 1 FROM ultimateteam.promo_cards c JOIN ultimateteam.promos p USING(promo_id)
        JOIN ultimateteam.player_card_definitions d ON d.definition_id=c.player_card_definition_id
        WHERE p.code='base_icons' AND d.rating NOT BETWEEN 85 AND 91
    ) THEN RAISE EXCEPTION 'A Base Icon is outside the approved 85-91 rating buckets'; END IF;
END $$;

SELECT set_config('ultimateteam.content_publish','on',true);
INSERT INTO ultimateteam.promo_buckets(promo_id,code,name,draw_weight,enabled)
SELECT p.promo_id,'rating_' || s.rating::text,s.rating::text,s.draw_weight,true
FROM ultimateteam.promos p CROSS JOIN base_icon_bucket_seed s WHERE p.code='base_icons'
ON CONFLICT(promo_id,code) DO UPDATE
SET name=excluded.name,draw_weight=excluded.draw_weight,enabled=true;

UPDATE ultimateteam.promo_cards c SET bucket_id=b.id
FROM ultimateteam.promos p,ultimateteam.player_card_definitions pc,
     ultimateteam.item_definitions d,ultimateteam.promo_buckets b
WHERE c.promo_id=p.promo_id AND p.code='base_icons'
  AND pc.definition_id=c.player_card_definition_id AND d.definition_id=pc.definition_id
  AND b.promo_id=p.promo_id AND b.code='rating_' ||
      (CASE WHEN d.native_resource_id IN (17015642,17015649) THEN 89 ELSE pc.rating END)::text;

-- Retain the old rows without exposing an empty Default bucket in the panel.
UPDATE ultimateteam.promo_buckets b SET enabled=false
FROM ultimateteam.promos p WHERE b.promo_id=p.promo_id AND p.code='base_icons'
  AND b.code NOT IN (SELECT 'rating_' || rating::text FROM base_icon_bucket_seed);
SELECT set_config('ultimateteam.content_publish','',true);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(99);
