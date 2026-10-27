-- Leaderboards categories (live 10-06: "missing tabs"). FIFA 21 ships the FUT Hub Leaderboards screen for categories Match
-- Earnings, Transfer Profit, Club Value and Top Squad (competitor, trader, collector, builder), compared over the Top 100 or
-- Friends, Weekly, Monthly or All-Time; its info panel draws one row per category. The stored seasons rules held the FUT
-- Champions options (regions, Current/Previous) instead. Stored rules never refresh list items, so drop the stored option
-- groups and their comment: start-up adds the bundled ones back as missing keys (RuleRows.FillAsync), after every stored
-- row. The board url template, sizes and row settings are kept.
DELETE FROM ultimateteam.content_rules
WHERE file = 'seasons'
    AND ((path->>0 = 'leaderboards' AND path->>1 = 'groups') OR path = '["_leaderboardsComment"]'::jsonb);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(163);
