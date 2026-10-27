-- SBC group rewards (owner, 10-05). No bundled SBC set named a set reward, so finishing a whole set paid nothing beyond its
-- challenges. sbc.json now gives the Foundations sets their retail group rewards, mapped to packs the catalogue holds:
-- Let's Get Started and Let's Move Forward 82 (2 Rare Gold Players), League and Nation Basics 534 (Mixed Players), Let's
-- Keep Going 305 (Premium Gold Players), Hybrid Nations and Hybrid Leagues 404 (Rare Mega), League and Nation Hybrid
-- 13,000 coins. A set pays its reward only on the submit that finishes it, so every club that finished one before this
-- migration is paid here, once: the same reward, from the sbc grant source, tradeable as rules/tradeability.json says.
-- Completions are stored by challenge key; this list matches sbc.json's challenge ids for these sets.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TEMP TABLE sbc_set_rewards(set_key text PRIMARY KEY, challenges text[] NOT NULL, pack_id bigint, coins bigint NOT NULL)
    ON COMMIT DROP;
INSERT INTO sbc_set_rewards VALUES
    ('FND_LETS_GET_STARTED',     ARRAY['CH_1','CH_2','CH_3'],               82,   0),
    ('FND_LEAGUE_NATION_BASICS', ARRAY['CH_4','CH_5','CH_6'],               534,  0),
    ('FND_LETS_KEEP_GOING',      ARRAY['CH_7','CH_8','CH_9','CH_10'],       305,  0),
    ('FND_HYBRID_NATIONS',       ARRAY['CH_11','CH_12','CH_13','CH_14'],    404,  0),
    ('FND_HYBRID_LEAGUES',       ARRAY['CH_15','CH_16','CH_17','CH_18'],    404,  0),
    ('FND_LEAGUE_NATION_HYBRID', ARRAY['CH_19','CH_20','CH_21','CH_22'],    NULL, 13000),
    ('FND_LETS_MOVE_FORWARD',    ARRAY['CH_131','CH_132','CH_133'],         82,   0);

-- Every (club, set) whose challenges are all completed.
CREATE TEMP TABLE sbc_set_backpay ON COMMIT DROP AS
SELECT c.club_id, r.set_key, r.pack_id, r.coins
FROM sbc_set_rewards r
JOIN ultimateteam.sbc_completed c ON c.challenge_key = ANY(r.challenges)
JOIN ultimateteam.clubs k ON k.id = c.club_id
GROUP BY c.club_id, r.set_key, r.pack_id, r.coins, r.challenges
HAVING count(DISTINCT c.challenge_key) = cardinality(r.challenges);

-- The sbc source's switch (true = untradeable), with a per-pack or per-source-pack override, as TradeabilityRules reads it.
INSERT INTO ultimateteam.unopened_packs(club_id, pack_id, source, created, untradeable)
SELECT b.club_id, b.pack_id, 'sbc', extract(epoch FROM now())::bigint,
       COALESCE(
           (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules
            WHERE file = 'tradeability' AND path = jsonb_build_array('sourcePacks', 'sbc', b.pack_id::text)),
           (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules
            WHERE file = 'tradeability' AND path = jsonb_build_array('packs', b.pack_id::text)),
           (SELECT (value #>> '{}')::boolean FROM ultimateteam.content_rules
            WHERE file = 'tradeability' AND path = '["sources", "sbc"]'::jsonb),
           false)
FROM sbc_set_backpay b
WHERE b.pack_id IS NOT NULL
ORDER BY b.club_id, b.set_key;

UPDATE ultimateteam.clubs k SET credits = credits + p.coins
FROM (SELECT club_id, sum(coins) AS coins FROM sbc_set_backpay WHERE coins > 0 GROUP BY club_id) p
WHERE k.id = p.club_id;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(159);
