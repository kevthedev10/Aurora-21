-- Anti-cheat logs (owner, 2026-10-08).
-- match_leaves: every online match an opponent left (Rivals, Weekend League, online Draft), written by the stayer's match
-- end: who left, at which minute and score. A leave before rewardEligibility.opponentLeft.winFromMinute gives the stayer no
-- result, so a player who keeps quitting early denies wins; serialLeaver counts these rows to end that.
-- market_trades: every completed transfer market sale (Buy Now or a won auction), kept when listings or clubs go.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.match_leaves (
    id bigserial PRIMARY KEY,
    leaver_player text NOT NULL,
    leaver_persona bigint,
    stayer_player text NOT NULL,
    stayer_club_id bigint NOT NULL,
    mode text NOT NULL,
    match_key text NOT NULL,
    minute integer NOT NULL,
    stayer_goals integer NOT NULL,
    leaver_goals integer NOT NULL,
    early boolean NOT NULL,
    serial boolean NOT NULL DEFAULT false,
    stayer_outcome text NOT NULL,
    left_at bigint NOT NULL,
    CONSTRAINT match_leaves_one_per_match UNIQUE (leaver_player, match_key)
);
CREATE INDEX IF NOT EXISTS match_leaves_leaver_time ON ultimateteam.match_leaves(leaver_player, left_at DESC);
CREATE INDEX IF NOT EXISTS match_leaves_time ON ultimateteam.match_leaves(left_at DESC);

CREATE TABLE IF NOT EXISTS ultimateteam.market_trades (
    id bigserial PRIMARY KEY,
    trade_id bigint NOT NULL UNIQUE,
    sold_at bigint NOT NULL,
    sale text NOT NULL,
    seller_club_id bigint,
    seller_name text NOT NULL,
    buyer_club_id bigint NOT NULL,
    definition_id bigint NOT NULL,
    resource_id bigint NOT NULL,
    rating bigint NOT NULL,
    rareflag bigint,
    price bigint NOT NULL,
    seller_proceeds bigint NOT NULL,
    starting_bid bigint NOT NULL,
    buy_now bigint NOT NULL,
    listed_at bigint NOT NULL
);
CREATE INDEX IF NOT EXISTS market_trades_time ON ultimateteam.market_trades(sold_at DESC);
CREATE INDEX IF NOT EXISTS market_trades_resource_time ON ultimateteam.market_trades(resource_id, sold_at DESC);
CREATE INDEX IF NOT EXISTS market_trades_buyer ON ultimateteam.market_trades(buyer_club_id, sold_at DESC);
CREATE INDEX IF NOT EXISTS market_trades_seller ON ultimateteam.market_trades(seller_club_id, sold_at DESC);

-- Sales before this log: the closed listings still on the market (a deleted club's listings are gone).
INSERT INTO ultimateteam.market_trades(trade_id, sold_at, sale, seller_club_id, seller_name, buyer_club_id, definition_id, resource_id,
    rating, rareflag, price, seller_proceeds, starting_bid, buy_now, listed_at)
SELECT l.market_listing_id, l.end_time, 'unknown', l.seller_club_id, l.seller_name, l.bidder_club_id, l.definition_id, l.resource_id,
    l.rating, l.rareflag, l.sold_price, 0, l.starting_bid, l.buy_now, l.created_at
FROM ultimateteam.market_listings l
WHERE l.state = 'closed' AND l.bidder_club_id IS NOT NULL AND l.sold_price > 0
ON CONFLICT (trade_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(179);
