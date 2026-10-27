-- Live SBCs (owner, 2026-10-03): like the store listing, the SBC catalogue (bundled sbc.json with the panel's edits,
-- migration 136) holds every set, and the game shows a set only when it is on the live list and switched on. The list
-- replaces rules/sbc.json hiddenSets and the panel's per-set hide (sbc_sets.hidden); no set is deleted.
--
-- The list starts as exactly what the game showed: every catalogue set the stored hiddenSets patterns did not hide
-- (a fresh database, which has no stored rules yet, uses the shipped patterns), switched off when the panel had hidden
-- it. The bundled keys are sbc.json's as of this migration, in file order; panel-added sets follow in creation order.
-- The hidden column is the run-once marker: on a replay it is gone and the list is left as it is.
CREATE TABLE IF NOT EXISTS ultimateteam.sbc_live (
    set_key    text PRIMARY KEY CHECK (set_key ~ '^[A-Za-z0-9_]{1,64}$'),
    enabled    boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

DO $$
DECLARE
    stored_rules boolean;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'ultimateteam' AND table_name = 'sbc_sets' AND column_name = 'hidden') THEN
        RETURN;
    END IF;
    stored_rules := EXISTS (SELECT 1 FROM ultimateteam.content_rules WHERE file = 'sbc');

    WITH patterns AS (
        SELECT value #>> '{}' AS pattern FROM ultimateteam.content_rules
        WHERE stored_rules AND file = 'sbc' AND jsonb_array_length(path) = 2 AND path->>0 = 'hiddenSets'
            AND jsonb_typeof(value) = 'string'
        UNION ALL
        SELECT pattern FROM unnest(ARRAY['LEAGUE_*', 'NATION_*', 'UP_*', 'ICON_*', 'PLAYER_*', 'CH_*', 'SP_*', 'FND_ADVANCED']) AS shipped(pattern)
        WHERE NOT stored_rules
    ), bundled(position, set_key) AS (
        VALUES (0, 'FND_LETS_GET_STARTED'), (1, 'FND_LEAGUE_NATION_BASICS'), (2, 'FND_LETS_KEEP_GOING'), (3, 'FND_HYBRID_NATIONS'),
            (4, 'FND_HYBRID_LEAGUES'), (5, 'FND_LEAGUE_NATION_HYBRID'), (6, 'FND_ADVANCED'), (7, 'LEAGUE_PL'), (8, 'LEAGUE_LALIGA'),
            (9, 'LEAGUE_SERIEA'), (10, 'LEAGUE_BUNDESLIGA'), (11, 'LEAGUE_LIGUE1'), (12, 'NATION_ENG'), (13, 'NATION_ESP'),
            (14, 'NATION_GER'), (15, 'NATION_ITA'), (16, 'NATION_BRA'), (17, 'NATION_ARG'), (18, 'NATION_FRA'), (19, 'NATION_POR'),
            (20, 'UP_82'), (21, 'UP_84'), (22, 'UP_85'), (23, 'UP_86'), (24, 'UP_88'), (25, 'ICON_Pele'), (26, 'ICON_Cruyff'),
            (27, 'ICON_Ronaldinho'), (28, 'ICON_Gullit'), (29, 'ICON_Eusebio'), (30, 'ICON_Matthaus'), (31, 'ICON_Vieira'),
            (32, 'ICON_R9'), (33, 'PLAYER_MESSI'), (34, 'PLAYER_KANTE'), (35, 'PLAYER_DEPAY'), (36, 'PLAYER_SANE'),
            (37, 'PLAYER_ZAHA'), (38, 'PLAYER_COMAN'), (39, 'PLAYER_UMTITI'), (40, 'CH_TOTY'), (41, 'CH_FUTURE'), (42, 'CH_FREEZE'),
            (43, 'CH_WHATIF'), (44, 'CH_HEADLINERS'), (45, 'CH_RTTF'), (46, 'CH_SHAPESHIFTERS'), (47, 'SP_PARTY'),
            (48, 'SP_SUMMER'), (49, 'SP_FOF'), (50, 'SP_FPD'), (51, 'SP_SILVER'), (52, 'UPG_BRONZE'), (53, 'UPG_SILVER'),
            (54, 'UPG_GOLD'), (55, 'FND_LETS_MOVE_FORWARD')
    ), catalogue AS (
        SELECT b.position::bigint AS position, b.set_key, coalesce(s.hidden, false) AS hidden
        FROM bundled b LEFT JOIN ultimateteam.sbc_sets s ON s.set_key = b.set_key
        UNION ALL
        SELECT 1000 + s.ordinal, s.set_key, s.hidden
        FROM ultimateteam.sbc_sets s WHERE NOT EXISTS (SELECT 1 FROM bundled b WHERE b.set_key = s.set_key)
    )
    -- A trailing * matches the prefix (starts_with, not LIKE: an underscore in a key is not a wildcard).
    INSERT INTO ultimateteam.sbc_live(set_key, enabled)
    SELECT c.set_key, NOT c.hidden FROM catalogue c
    WHERE NOT EXISTS (SELECT 1 FROM patterns p WHERE CASE WHEN right(p.pattern, 1) = '*'
        THEN starts_with(c.set_key, left(p.pattern, -1)) ELSE c.set_key = p.pattern END)
    ORDER BY c.position
    ON CONFLICT (set_key) DO NOTHING;

    DELETE FROM ultimateteam.content_rules
    WHERE file = 'sbc' AND path->>0 IN ('hiddenSets', '_hiddenSetsComment');

    ALTER TABLE ultimateteam.sbc_sets DROP COLUMN hidden;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(142);
