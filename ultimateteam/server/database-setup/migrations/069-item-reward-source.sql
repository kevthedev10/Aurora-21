-- An owned item remembers where it came from when a grant says so (PLAN 3.1 / 4.2, phase 1 economy): quick sell pays
-- nothing for an item of a zero-value source (the Match Pack, rules/cards.json quickSell.zeroValueRewardSources). The
-- source is a grant source of rules/tradeability.json (match_pack, pick, sbc, ...) or the source an unopened pack was
-- granted with. Items from before this migration, and store or market items, have none.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.items ADD COLUMN IF NOT EXISTS reward_source text
    CHECK (reward_source IS NULL OR (length(reward_source) BETWEEN 1 AND 64));

INSERT INTO ultimateteam.schema_migrations(version) VALUES(69);
