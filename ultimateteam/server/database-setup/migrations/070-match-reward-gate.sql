-- The match reward gate and the Match Pack (PLAN 4.3 and 3.7; rules/seasons.json rewardEligibility and matchPack).
--   match_clock:    when this club's current match was created (CreateMatch POST) and kicked off (the first PUT /match
--                   after it), and the opponent persona of an online pairing; the match end takes it.
--   match_rewards:  every settled match the gate paid, flagged or granted a Match Pack for: the pair caps count its paid
--                   games per opponent, the Match Pack cap counts its grants in a rolling window, and the pity streak
--                   counts grants since the last Special or Jackpot.
--   unopened_packs.contents: a pack's contents rolled when it was granted (a Match Pack), opened as stored.
--   store_packs 544: the Match Pack reward pack (never in the store). Its contents come from the roller at grant time;
--                   this row names the pack and is the fallback roll of a copy granted without contents.
-- Number 070 is this feature's reservation (pack-odds phase 1); it installs as the next file after 067 on its own branch.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE IF NOT EXISTS ultimateteam.match_clock (
    club_id          bigint PRIMARY KEY REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    created_at       bigint NOT NULL,
    kickoff_at       bigint,
    opponent_persona bigint
);

CREATE TABLE IF NOT EXISTS ultimateteam.match_rewards (
    id               bigserial PRIMARY KEY,
    club_id          bigint  NOT NULL REFERENCES ultimateteam.clubs(id) ON DELETE CASCADE,
    settled_at       bigint  NOT NULL,
    mode             text    NOT NULL,
    verdict          text    NOT NULL,
    opponent_persona bigint,
    weekend_league   bigint,
    coins            bigint  NOT NULL CHECK (coins >= 0),
    pack_tier        text,
    pack_highlight   boolean NOT NULL DEFAULT false
);
CREATE INDEX IF NOT EXISTS match_rewards_club_settled ON ultimateteam.match_rewards(club_id, settled_at);
CREATE INDEX IF NOT EXISTS match_rewards_club_opponent ON ultimateteam.match_rewards(club_id, opponent_persona) WHERE opponent_persona IS NOT NULL;

ALTER TABLE ultimateteam.unopened_packs ADD COLUMN IF NOT EXISTS contents jsonb;

INSERT INTO ultimateteam.store_packs(pack_id, kind, name, size, gold_weight, silver_weight, bronze_weight, rare_slots,
    guaranteed_specials, special_chance, allow_unweighted_specials, special_weights, special_weights_exclusive, player_picks,
    always_present, display_only)
VALUES (544, 'reward', 'Match Pack', 6, 100, NULL, NULL, 1, NULL, NULL, NULL, NULL, false, NULL, false, false)
ON CONFLICT (pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(70);
