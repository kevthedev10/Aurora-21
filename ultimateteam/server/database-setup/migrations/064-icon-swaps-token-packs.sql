-- Icon Swaps foundations (FIFA 21 rarities 89, 92, 93 and 94; 18 token cards each; ultimateteam/icon-swaps.md).
-- Tokens are rewards only: rules/cards.json keeps them out of every pack and player pick that does not name them. These
-- four reward packs are how a reward hands one out: each opens one untradeable token of its round that the club does not
-- hold yet (any of them once it holds all 18), because an SBC squad takes each footballer once. Nothing pays them yet;
-- the Icon Swaps objectives and swap SBCs are documented but not switched on.
SET CONSTRAINTS ALL IMMEDIATE;

ALTER TABLE ultimateteam.store_packs ADD COLUMN IF NOT EXISTS swap_token_rareflag bigint;

-- Like the red picks (18), a reward-only rarity has no default pack weight: a pack reaches it only by naming it. The
-- FGS Swaps 1-2 tokens (76, 77; 10 each) are reward-only too, though nothing hands them out yet.
DELETE FROM ultimateteam.store_pack_rarity_weights WHERE native_rareflag IN (76, 77, 89, 92, 93, 94);

INSERT INTO ultimateteam.store_packs(pack_id, kind, name, bio, size, swap_token_rareflag, special_weights_exclusive,
    always_present, display_only)
VALUES
(560, 'reward', 'Icon Swaps 1 Token', 'One Icon Swaps 1 token you do not hold yet.', 1, 89, false, false, false),
(561, 'reward', 'Icon Swaps 2 Token', 'One Icon Swaps 2 token you do not hold yet.', 1, 92, false, false, false),
(562, 'reward', 'Icon Swaps 3 Token', 'One Icon Swaps 3 token you do not hold yet.', 1, 93, false, false, false),
(563, 'reward', 'Icon Swaps 4 Token', 'One Icon Swaps 4 token you do not hold yet.', 1, 94, false, false, false)
ON CONFLICT (pack_id) DO NOTHING;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(64);
