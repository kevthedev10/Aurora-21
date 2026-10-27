-- Remove the empty Libertadores Flashback promo (maintainer, 2026-10-04). Migration 104 merged its two Tevez cards into the one
-- Flashbacks card, leaving the promo with no cards. A promo that has cards again (a panel edit since) is kept as it is.
-- Pack, pick and rule sources that name it lose that code; its buckets and event links go with it.
SET CONSTRAINTS ALL IMMEDIATE;
SELECT set_config('ultimateteam.content_publish', 'on', true);

CREATE TEMP TABLE removed_promos ON COMMIT DROP AS
SELECT p.promo_id, p.code FROM ultimateteam.promos p
WHERE p.code = 'libertadores_flashback'
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.promo_cards c WHERE c.promo_id = p.promo_id)
  AND NOT EXISTS (SELECT 1 FROM ultimateteam.icon_rotations r WHERE r.promo_id = p.promo_id);

DELETE FROM ultimateteam.promo_event_promos WHERE promo_id IN (SELECT promo_id FROM removed_promos);
ALTER TABLE ultimateteam.promo_buckets DISABLE TRIGGER promo_buckets_publish_guard;
DELETE FROM ultimateteam.promo_buckets WHERE promo_id IN (SELECT promo_id FROM removed_promos);
ALTER TABLE ultimateteam.promo_buckets ENABLE TRIGGER promo_buckets_publish_guard;
ALTER TABLE ultimateteam.promos DISABLE TRIGGER lfv4_trg_r2_promos;
DELETE FROM ultimateteam.promos WHERE promo_id IN (SELECT promo_id FROM removed_promos);
ALTER TABLE ultimateteam.promos ENABLE TRIGGER lfv4_trg_r2_promos;

-- Sources that still name the removed promo drop the code (a draw would otherwise refuse an unknown promo).
UPDATE ultimateteam.store_packs SET promo_codes = array_remove(promo_codes, 'libertadores_flashback')
WHERE 'libertadores_flashback' = ANY(promo_codes) AND EXISTS (SELECT 1 FROM removed_promos);
UPDATE ultimateteam.owned_legacy_pack_definitions SET promo_codes = array_remove(promo_codes, 'libertadores_flashback')
WHERE 'libertadores_flashback' = ANY(promo_codes) AND EXISTS (SELECT 1 FROM removed_promos);
CREATE FUNCTION pg_temp.without_libertadores_flashback(value jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM removed_promos) THEN
        RETURN value;
    END IF;
    CASE jsonb_typeof(value)
      WHEN 'array' THEN RETURN coalesce((SELECT jsonb_agg(pg_temp.without_libertadores_flashback(element) ORDER BY ord)
            FROM jsonb_array_elements(value) WITH ORDINALITY a(element, ord)
            WHERE element IS DISTINCT FROM '"libertadores_flashback"'::jsonb), '[]'::jsonb);
      WHEN 'object' THEN RETURN coalesce((SELECT jsonb_object_agg(key, pg_temp.without_libertadores_flashback(element))
            FROM jsonb_each(value) a(key, element)), '{}'::jsonb);
      ELSE RETURN value;
    END CASE;
END $$;
UPDATE ultimateteam.store_packs SET player_picks = pg_temp.without_libertadores_flashback(player_picks)
WHERE player_picks IS DISTINCT FROM pg_temp.without_libertadores_flashback(player_picks);
UPDATE ultimateteam.owned_legacy_pack_definitions SET player_picks = pg_temp.without_libertadores_flashback(player_picks)
WHERE player_picks IS DISTINCT FROM pg_temp.without_libertadores_flashback(player_picks);
UPDATE ultimateteam.player_pick_items SET draw_config = pg_temp.without_libertadores_flashback(draw_config)
WHERE draw_config IS DISTINCT FROM pg_temp.without_libertadores_flashback(draw_config);
UPDATE ultimateteam.content_rules SET value = pg_temp.without_libertadores_flashback(value)
WHERE value IS DISTINCT FROM pg_temp.without_libertadores_flashback(value);
DROP FUNCTION pg_temp.without_libertadores_flashback(jsonb);

SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(152);
