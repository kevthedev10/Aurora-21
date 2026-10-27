-- Keep the genuine reward catalogue and the new store lineup. Migration 095
-- retired the former testing store as reward-kind rows; these IDs came from
-- 041/049/051/053, rather than the competition/objective reward catalogue.
-- An already-owned testing pack/pick retains a compatibility definition only.
CREATE TABLE ultimateteam.owned_legacy_pack_definitions
    (LIKE ultimateteam.store_packs INCLUDING DEFAULTS);
ALTER TABLE ultimateteam.owned_legacy_pack_definitions ADD PRIMARY KEY(pack_id);

INSERT INTO ultimateteam.owned_legacy_pack_definitions
SELECT p.* FROM ultimateteam.store_packs p
WHERE (p.pack_id BETWEEN 1 AND 24 OR p.pack_id BETWEEN 42 AND 66)
AND p.kind='reward'
AND (EXISTS (SELECT 1 FROM ultimateteam.unopened_packs u WHERE u.pack_id=p.pack_id)
     OR EXISTS (SELECT 1 FROM ultimateteam.player_pick_items i WHERE i.source_pack_id=p.pack_id));

DELETE FROM ultimateteam.store_packs
WHERE (pack_id BETWEEN 1 AND 24 OR pack_id BETWEEN 42 AND 66) AND kind='reward';

-- Player reward packs use the same promo/bucket selection mode as new packs.
-- Preserve their tier mix, floors, guaranteed specials and explicit sources.
UPDATE ultimateteam.store_packs SET banded_odds=true, manager_chance=0, chem_style_chance=0
WHERE kind='reward' AND coalesce(swap_token_rareflag,0)=0;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(97);
