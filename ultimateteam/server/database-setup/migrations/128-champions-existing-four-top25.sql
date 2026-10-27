-- Upgrade the already-populated four default bands missed by migration 127.
-- Preserve original reward ids, custom reward contents, win ranks and settled club history.
-- Custom position ranges and installations already using six bands remain untouched.
SET CONSTRAINTS ALL IMMEDIATE;

DO $$
DECLARE
    original_reward bigint;
    copied_reward bigint;
    extra record;
BEGIN
    IF (SELECT count(*) FROM ultimateteam.champions_reward_ranks WHERE positions_low IS NOT NULL) = 4
        AND NOT EXISTS (SELECT 1 FROM ultimateteam.champions_reward_ranks WHERE tier_level IN (17,18))
        AND (SELECT count(*) FROM ultimateteam.champions_reward_ranks c
             JOIN (VALUES (1,1,1),(2,2,20),(3,21,100),(4,101,200)) b(tier,low,high)
             ON c.tier_level=b.tier AND c.positions_low=b.low AND c.positions_high=b.high) = 4
    THEN
        -- Separate reward records allow later panel edits to either band independently.
        FOR extra IN SELECT * FROM (VALUES
            (3,17,11,15,'Rank 11-15'), (4,18,21,25,'Rank 21-25')
        ) b(original_tier,new_tier,low,high,name) LOOP
            SELECT reward_id INTO original_reward FROM ultimateteam.champions_reward_ranks
            WHERE tier_level=extra.original_tier;
            INSERT INTO ultimateteam.rewards(coins,fc_points,xp)
            SELECT coins,fc_points,xp FROM ultimateteam.rewards WHERE reward_id=original_reward
            RETURNING reward_id INTO copied_reward;
            INSERT INTO ultimateteam.reward_lines(reward_id,position,pack_id,card_resource_id,count,untradeable,source)
            SELECT copied_reward,position,pack_id,card_resource_id,count,untradeable,source
            FROM ultimateteam.reward_lines WHERE reward_id=original_reward;
            INSERT INTO ultimateteam.champions_reward_ranks(tier_level,name,positions_low,positions_high,reward_id)
            VALUES(extra.new_tier,extra.name,extra.low,extra.high,copied_reward);
        END LOOP;
        UPDATE ultimateteam.champions_reward_ranks c SET name=b.name,positions_low=b.low,positions_high=b.high
        FROM (VALUES (1,1,1,'Rank 1'),(2,2,5,'Rank 2-5'),(3,6,10,'Rank 6-10'),(4,16,20,'Rank 16-20')) b(tier,low,high,name)
        WHERE c.tier_level=b.tier;
    END IF;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(128);
