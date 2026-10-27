-- User confirmed Icon Swaps non-rare; remaining entries are explicit delegated local policy.
-- No new native group IDs; keep 4 as the existing Rare wire group.
-- Existing installations start a new transaction with deferred foreign keys.
-- Flush them and keep them immediate before trigger toggles and policy writes.
SET CONSTRAINTS ALL IMMEDIATE;
LOCK TABLE ultimateteam.card_types, ultimateteam.card_rarity_groups IN ACCESS EXCLUSIVE MODE;
DO $migration$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.card_types)<>88
       OR EXISTS(SELECT 1 FROM ultimateteam.card_types WHERE native_rareflag IS NULL OR native_rareflag NOT IN (0,1,3,4,5,6,8,9,10,11,12,16,21,22,25,30,32,33,34,37,42,43,45,46,47,48,49,50,51,52,53,54,55,57,58,59,60,61,62,64,65,68,70,71,75,76,77,79,81,82,84,86,87,89,90,91,92,93,94,95,96,98))
       OR EXISTS(SELECT 1 FROM ultimateteam.card_rarity_groups WHERE native_group_id<>4) THEN
        RAISE EXCEPTION 'Unexpected card type/group policy; refusing automatic reclassification';
    END IF;
END $migration$;
INSERT INTO ultimateteam.catalog_sources(source_kind,source_revision,game_year,content_sha256,manifest_revision,provenance)
VALUES('rules','explicit-card-rarity-groups:2026-09-17',2021,'916c7f46c1f622f4113222d45f279fe7af43617ea12fcbae81446ad28c71f0dc','runtime-migration-11',
'{"path":"runtime-source/ultimateteam21/rules/cards.json","basis":"user-confirmed Icon Swaps non-rare; delegated local classification; native grouping not attested"}');
ALTER TABLE ultimateteam.card_types DISABLE TRIGGER lfv4_trg_r2_card_types;
UPDATE ultimateteam.card_types SET name=CASE native_rareflag WHEN 47 THEN 'Champions League Non-Rare' WHEN 48 THEN 'Champions League Rare' ELSE name END WHERE native_rareflag IN(47,48);
ALTER TABLE ultimateteam.card_types ENABLE TRIGGER lfv4_trg_r2_card_types;
ALTER TABLE ultimateteam.card_rarity_groups DISABLE TRIGGER lfv4_trg_r2_card_rarity_groups;
DELETE FROM ultimateteam.card_rarity_groups g USING ultimateteam.card_types t WHERE g.card_type_id=t.card_type_id AND t.native_rareflag NOT IN (1,3,4,5,6,8,9,10,11,12,16,21,22,25,30,32,33,34,37,42,43,45,46,48,49,50,51,53,54,55,57,58,59,60,61,62,64,65,68,70,71,75,79,81,82,84,86,87,90,91,95,96,98);
UPDATE ultimateteam.card_rarity_groups SET source_id=(SELECT source_id FROM ultimateteam.catalog_sources WHERE source_revision='explicit-card-rarity-groups:2026-09-17');
INSERT INTO ultimateteam.card_rarity_groups(card_type_id,native_group_id,source_id)
SELECT card_type_id,4,(SELECT source_id FROM ultimateteam.catalog_sources WHERE source_revision='explicit-card-rarity-groups:2026-09-17')
FROM ultimateteam.card_types WHERE native_rareflag IN (1,3,4,5,6,8,9,10,11,12,16,21,22,25,30,32,33,34,37,42,43,45,46,48,49,50,51,53,54,55,57,58,59,60,61,62,64,65,68,70,71,75,79,81,82,84,86,87,90,91,95,96,98) ORDER BY card_type_id
ON CONFLICT(card_type_id,native_group_id) DO NOTHING;
ALTER TABLE ultimateteam.card_rarity_groups ENABLE TRIGGER lfv4_trg_r2_card_rarity_groups;
INSERT INTO ultimateteam.schema_migrations(version) VALUES(11);
