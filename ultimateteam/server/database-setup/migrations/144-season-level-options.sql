-- Season Pass level choices (owner, 2026-10-04): a level pays one reward, which may be the club's pick from several options
-- (FIFA 21's rewardOptionList; the claim names the option). The option a club picked is a claim of scope level_option, with
-- claim_id level * 100 + option id, so the level shows it as claimed (chosenOption). Replay-safe.
ALTER TABLE ultimateteam.objective_claims DROP CONSTRAINT IF EXISTS lfv4_ck_objective_claims_scope;
ALTER TABLE ultimateteam.objective_claims ADD CONSTRAINT lfv4_ck_objective_claims_scope
    CHECK (scope IN ('level', 'objective', 'group', 'level_option'));

INSERT INTO ultimateteam.schema_migrations(version) VALUES(144);
