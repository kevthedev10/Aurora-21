-- Leaderboards info panel (live 10-06): the period and view rows read "undefined". FutLeaderboards.swf finds each
-- selection by group position (category 0, view 1, period 2, the order CardsDLL 0x180144750 reads CATEGORY_SEL,
-- VIEW_SEL and PERIOD_SEL), but the stored seasons rules kept the groups as category, period, view. The rules loader
-- appends list items in row order, so swap the two groups' values in place and keep every path and ordinal.
-- Only the stock shape is swapped: group 1 must be period and group 2 view, with the same keys, or nothing changes.
DO $$
DECLARE
    first_keys jsonb;
    second_keys jsonb;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules
            WHERE file = 'seasons' AND path = '["leaderboards", "groups", 1, "id"]'::jsonb AND value = '"period"'::jsonb)
        OR NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules
            WHERE file = 'seasons' AND path = '["leaderboards", "groups", 2, "id"]'::jsonb AND value = '"view"'::jsonb) THEN
        RETURN;
    END IF;
    SELECT coalesce(jsonb_agg(path - 0 - 0 - 0 ORDER BY (path - 0 - 0 - 0)::text), '[]'::jsonb) INTO first_keys
    FROM ultimateteam.content_rules
    WHERE file = 'seasons' AND path->>0 = 'leaderboards' AND path->>1 = 'groups' AND path->>2 = '1';
    SELECT coalesce(jsonb_agg(path - 0 - 0 - 0 ORDER BY (path - 0 - 0 - 0)::text), '[]'::jsonb) INTO second_keys
    FROM ultimateteam.content_rules
    WHERE file = 'seasons' AND path->>0 = 'leaderboards' AND path->>1 = 'groups' AND path->>2 = '2';
    IF first_keys <> second_keys THEN
        RETURN;
    END IF;
    -- One statement reads the old values of both groups, so the swap needs no temporary copy.
    UPDATE ultimateteam.content_rules target
    SET value = source.value
    FROM ultimateteam.content_rules source
    WHERE target.file = 'seasons' AND source.file = 'seasons'
        AND target.path->>0 = 'leaderboards' AND target.path->>1 = 'groups' AND target.path->>2 IN ('1', '2')
        AND source.path->>0 = 'leaderboards' AND source.path->>1 = 'groups'
        AND source.path->>2 = CASE target.path->>2 WHEN '1' THEN '2' ELSE '1' END
        AND (source.path - 0 - 0 - 0) = (target.path - 0 - 0 - 0);
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(162);
