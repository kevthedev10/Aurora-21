-- The SBC "Rare players" rule asks for PLAYER_RARITY 1 instead of PLAYER_RARITY_GROUP 4 in existing databases, not
-- just new seeds. FIFA 21 labels a rarity group value as Player_Group_<n> (group 4 read "UEFA Champions League") and its
-- player-count check never matches a group; PLAYER_RARITY 1 reads "Rare" and matches rareflag 1 exactly, as the server
-- checks it. Only the old shipped values are replaced, so an operator's own edit is kept.
UPDATE ultimateteam.content_rules SET value='"Rarity"'::jsonb, updated_at=now()
WHERE file='sbc' AND path='["requirements","RARE","eligibilityKey"]'::jsonb AND value='"RarityGroup"'::jsonb;

UPDATE ultimateteam.content_rules SET value='1'::jsonb, updated_at=now()
WHERE file='sbc' AND path='["requirements","RARE","eligibilityValue"]'::jsonb AND value='4'::jsonb;

UPDATE ultimateteam.content_rules SET value=to_jsonb(replace(value #>> '{}',
        ' The RARE rule is PLAYER_RARITY_GROUP 4: PLAYER_RARITY would match rareflag 1 exactly and reject special cards.',
        ' The RARE rule is PLAYER_RARITY 1 (rareflag 1, the client label Player_Rarity_1 "Rare"): FIFA 21 names a PLAYER_RARITY_GROUP value as Player_Group_<n> (group 4 reads "UEFA Champions League") and never counts it, and its PLAYER_RARITY check matches rareflag exactly, so special cards are not rare players.')),
    updated_at=now()
WHERE file='sbc' AND path='["_imagesComment"]'::jsonb AND jsonb_typeof(value)='string'
    AND strpos(value #>> '{}', 'The RARE rule is PLAYER_RARITY_GROUP 4') > 0;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(114);
