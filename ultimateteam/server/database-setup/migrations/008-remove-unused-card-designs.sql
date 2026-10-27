-- Remove unused design infrastructure; preserve all card types and gameplay data.
LOCK TABLE ultimateteam.card_designs, ultimateteam.card_type_designs, ultimateteam.promo_cards IN ACCESS EXCLUSIVE MODE;
DO $migration$ BEGIN
    IF EXISTS(SELECT 1 FROM ultimateteam.card_designs)
       OR EXISTS(SELECT 1 FROM ultimateteam.card_type_designs)
       OR EXISTS(SELECT 1 FROM ultimateteam.promo_cards WHERE design_override_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Cannot remove populated card design infrastructure';
    END IF;
END $migration$;
ALTER TABLE ultimateteam.promo_cards DROP COLUMN design_override_id;
DROP TABLE ultimateteam.card_type_designs;
DROP TABLE ultimateteam.card_designs;
DROP FUNCTION ultimateteam.lfv4_r1_card_type_designs();
DROP FUNCTION ultimateteam.lfv4_r1_card_designs();
INSERT INTO ultimateteam.schema_migrations(version) VALUES(8);
