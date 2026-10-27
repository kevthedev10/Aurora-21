-- Draft odds v2 (2026-10-02). Live Draft screens were 48% Icons and 21% TOTW: every special category weighed 4.5x a
-- standard one, and outside the weak rounds only 87+ cards (90+ for the captain) were offered, which mostly only Icons
-- clear. Measured with the real choice code over 100 runs, the new rules offer about 1 Icon and 3 in-forms per run
-- (1.3% and 3.2% of offers), with the best card on a screen averaging 87.1.
-- The new keys (picks.categoryWeights icon/totw, goldRareBucketWeights, the weak-round versions) are added at startup
-- from the bundled rules/draft.json, as missing keys are. Only values still at their shipped defaults change here.
UPDATE ultimateteam.content_rules r SET value = v.new, updated_at = now()
FROM (VALUES
    ('["picks","strongFloor"]'::jsonb, '87'::jsonb, '82'::jsonb),
    ('["picks","captainStrongFloor"]'::jsonb, '90'::jsonb, '86'::jsonb),
    ('["picks","specialBias"]'::jsonb, '4.5'::jsonb, '0.04'::jsonb),
    ('["picks","weakRoundSpecialBias"]'::jsonb, '1.35'::jsonb, '0.01'::jsonb)
) AS v(path, old, new)
WHERE r.file = 'draft' AND r.path = v.path AND r.value = v.old;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(115);
