# Packs and pack odds v3

Agreed with the maintainer on 2026-10-02 and implemented on branch `feat/panel-store-reward-packs` as one change with
migration 119. Browser and in-game acceptance are still open (see the panel TODO). The API is described in
[the panel API](../ultimateteam/panel-api-contract.md#pack-publication); the draw lives in `PackOpeningService.cs` and
`PlayerPickDraw.cs`.

## Why

- A pack's odds currently come from five overlapping places: the global `iconChance` and pick
  TOTW chance, promo bucket weights, a hidden per-pack `special_chance` (with a 10% store / 1.1%
  reward fallback), optional per-pack `category_chances` that replace the first and third, and
  the `banded_odds` / `odds_table` switches. Empty odds silently mean "use the defaults".
- Store packs and reward packs are separate kinds, so a reward pack cannot be sold and a store
  pack's art and odds follow different rules from a reward pack's.

v3 has one kind of pack, and per-pack odds are the only source: always visible and complete.

## One kind of pack

- Every pack is a pack. A new pack (or pick) starts **unassigned**: not in the store and not
  named by any reward.
- **Store** use is the store lineup: packs are switched on and off there, and the lineup can
  list every pack. **Grant only** stays: such a pack can never be added to the lineup; rewards
  and panel grants can still use it.
- **Rewards** use is any published rule that names the pack (tradeability does not count). The
  future Rewards and SBC screens edit those rules, so their use appears automatically.
- The Packs / Picks list shows each pack's use as **Store + Rewards**, **Store**, **Rewards** or
  **Unassigned**, with a filter for each and a Grant only badge. Tabs: Packs (one list with a
  Packs/Picks filter), Store lineup, Simulate, Gifts. Old Store/Reward addresses redirect.
- **Pack art** is chosen per pack (bronze, silver, gold, special; default special) and stored as
  the pack's store category. The server uses that art whenever it is set and falls back to the
  id-based art only for packs without one. Every existing pack keeps its current art (store packs
  already have a category; former reward packs have none).
- **Ids** are the next id after the highest existing pack (564 and up today), clear of the
  special-cased 550–553 range. Ids are never reused.
- **Delete** is refused only while a pack is in use: in the store lineup, named by a published
  rule, or backing a player pick a club still holds. Owned unopened copies still need Force
  delete. Reverting a pack's creation takes it out of the lineup.
- **View only** (contents the editor cannot control; derived, not stored): 540–542 (fixed TOTW
  1–3 pool, `resource_pool`), 544 (the pack named by `seasons.matchPack`, rolled from the Match
  Pack rules), 550–553 (the code's red-pick id range), 560–563 (Icon Swaps token generator).
  They can still be put in the lineup, granted and named by rewards.

## The draw

For each rare player slot (and each choice of a player pick):

1. **Tier** — the pack's gold/silver/bronze weights choose the slot's tier.
2. **Promo hit chance** — the pack's odds table chooses one row: a promo (TOTW, Icon, each
   live promo), *Upgraded specials*, or *Standard*, which is whatever chance is left.
3. **Promo bucket** — the chosen promo's buckets, by their saved weights. Buckets with no
   eligible card for this pack are skipped and the rest renormalised. Standard means the
   tier's rare category (for example Gold Rare and its rating buckets).
4. **Card** — an eligible card in that bucket: within the slot's tier and the pack's
   rating floor/ceiling, not grant-only, not already drawn in this pack. Migration 120 adds
   per-card reduction factors: eligible cards draw with weight `1 / reduction`, default 1.
   See the [Gold and Base Icon balance profile](gold-and-base-icon-bucket-balance.md).

A row with no eligible card for the slot (an Icon in a silver slot, a promo with nothing in
range) falls back to Standard. Common slots stay standard.

Champions red rewards are gold only, using the red card's own rating against the
configured gold threshold. The fixed TOTW 1–3 red pool, random picks and player-pack
eligibility exclude silver and bronze reds. Fixed red picks reject those cards too.
The underlying card definitions and ordinary TOTW reward pool remain intact; previously
revealed choices and owned items are not rewritten.

## The odds table

- Every pack and every pick group stores a complete table: one chance per promo it can hit.
  There is no "empty means default". New packs start pre-filled from a template.
- The editor shows Standard as the calculated rest. A total above 100% is refused.
- Specials-only packs ("all players special") and specials-only picks must total exactly 100%
  (no Standard row). "At least N specials" draws its forced slots from the special rows,
  rescaled to 100%.
- Publishing an event that brings a new promo offers a bulk step: "set *Promo X* to *n*% in
  [x] Gold Pack [x] Premium Gold Pack …". Packs not selected give it 0%. There is no per-event
  default chance.

## Tiers and upgraded specials

- A special belongs to the tier of its own rating (bronze ≤ 64, silver 65–74, gold 75+).
- If its base card (same `assetId`, rare flag 0/1) is in a lower tier, it can *also* drop in
  that lower tier, but only through the pack's **Upgraded specials** row. Example: Pandev's
  79 Hero (base 72) drops in gold slots through the Hero row and in silver slots only through
  Upgraded specials. A hit draws a random eligible upgraded card.
- No comparison with gold packs is shown and nothing is enforced.
- Seed catalogue count: 303 gold specials with a silver base, 33 silver with a bronze base,
  10 gold with a bronze base (`players.json`, 2026-10-02). Specials without a base card in the
  catalogue (Icons, Libertadores, Sudamericana) keep their own tier. Custom cards from the card
  editor must carry their base card's `assetId`; verify during implementation.

## Database and startup

- One migration: former reward rows become ordinary packs (`enabled = false`, so nothing joins
  the store); every pack and pick group gets the odds table below; the columns
  `store_packs.special_chance`, `store_packs.banded_odds`, `store_packs.odds_table` and
  `store_pack_details.reward_pack_special_chance` are dropped, with `goldRareBandTables` removed
  from `rules/pack_odds.json`. `admin_only` (Grant only) and `enabled` stay. `kind` stays only to
  mark the template (pack 0) and display (pack 500) rows.
- Startup readiness checks stop requiring particular pack rows: the migration 59 pack count, the
  552/553 part of 61, the 560–563 part of 64 and the 542/543 `banded_odds` part of 68 are
  removed; their structural table/column checks stay (the 68 column check only applies before
  the new migration). Migrations are proven by `schema_migrations`, not by data.
- Content history: a pack change staged or published before the migration that names a dropped
  field (`special_chance`, `banded_odds`, `odds_table`) is published or reverted with its other
  fields; the dropped ones are ignored. The old rewind-test helper was removed on 2026-10-04
  when migration 140 became the supported test baseline; current tests use fresh databases
  and forward-built supported upgrade fixtures (see [the test policy](../ultimateteam/server/tests/README.md)).

## Focused verification (2026-10-02)

Observed: `PromoGroupsMigrationTests` reproduced four failures (12 passed), then passed all
16 tests after correcting expectations in that class. The run used a throwaway PostgreSQL
cluster; results are in `outputs/verification/promo-groups-fixed-20261002/promo-groups.trx`.

The failures matched migrations already present at branch HEAD: 100 rotates Base to Mid
Icons, 101 extends the initial TOTW event through 1 January 2027, and 104 merges Tevez's
duplicate and moves the surviving card to Flashbacks, leaving Libertadores Flashback empty.
The Pro-card membership and exclusion assertions passed unchanged. Membership checks now
compare exact resource IDs, and availability checks cover the event boundaries and Icon
transition. No catalogue or production draw changes were needed for these failures.

The full suite was not run. Browser review and native FIFA pack acceptance remain open.

## Out of scope

Draft's own pick rules, Match Pack contents (rules), the fixed TOTW 1–3 pool and red picks keep
their own draw logic; mixed packs keep their non-player item ranges.

## Agreed odds (per rare slot; per choice for picks)

Icon applies only to gold slots and Upgraded specials only to silver and bronze slots; elsewhere
those rows fall back to Standard. Live specials at the time of writing: TOTW and Icons.

| Packs | TOTW | Icon | Upgraded | Today (TOTW) |
| --- | --- | --- | --- | --- |
| Store 74 Gold *(unchanged)* | 0.6% | 0.05% | – | 0.6% |
| Store 75 Premium Gold, 78 Jumbo Premium Gold *(unchanged)* | 0.8% | 0.08% | – | 0.8% |
| Store 70 Bronze, 72 Silver; reward 100 Bronze, 200 Silver | 0.2% | – | 0.02% | 0% / 1.1% |
| Store 71 Premium Bronze, 73 Premium Silver; reward 201 Premium Silver Players | 0.3% | – | 0.03% | 0% / 1.1% |
| Store 76 Jumbo Premium Bronze, 77 Jumbo Premium Silver | 0.4% | – | 0.03% | 0% |
| Store 80, 81, 82 (2 Rare … Players, rare flag 1 only) | 0% | 0% | 0% | 0% |
| Standard: 300 Gold, 302 Jumbo Gold, 304 Gold Players, 306 Electrum, 534 Mixed | 0.6% | 0.05% | 0.02% | 1.1% |
| Premium: 301, 303, 305, 319 | 0.8% | 0.08% | – | 1.1% |
| Prime / Rare: 308, 315, 543, 400, 401, 402, 403; 307, 518 Electrum; 309, 406 Mixed | 1.0% | 0.10% | 0.04% | 1.1% |
| Ultimate: 404 Rare Mega, 405 Ultimate | 1.2% | 0.12% | – | 1.1% |
| Pick 513 82+ | 3% | 0.05% | – | 3% |
| Pick 514 84+ | 4% | 0.10% | – | 3% |
| Pick 515 86+ | 5% | 0.15% | – | 3% |
| Pick 516 88+ | 6% | 0.20% | – | 3% |

View-only packs (540–542, 544, 550–553, 560–563) keep their own rules. Pack 405 keeps its three
84+ rare slots (`rated_rare_slots`).

### Today's values (for reference)

Observed on a fresh install (isolated panel review server, server build `78E44E4A…3F01`,
2026-10-02) through `GET /panel/api/packs/catalog`, `promos/availability` and the `pack_odds`
rules: bronze/silver store packs 0% specials; 74 TOTW 0.6% / Icon 0.05%; 75 and 78 0.8% /
0.08%; 80–82 explicit zeros; reward player packs TOTW 1.1% (migration 068) and Icon 0.05% in
gold slots; picks 513–516 TOTW 3% / Icon 0.05% per choice. No pack used `odds_table`.

## Implementation notes

- New packs start their odds table from `oddsTemplate` (rules `iconChance`, and `picks.informChance`
  for picks), with every other row at 0%; TOTW starts at 0% for packs. Changing that start value
  would need a new rules value.
- The store lineup refuses a pack without a coin or FIFA Points price (a former reward pack has
  none until one is set).
- The maintainer confirmed the live store packs match a fresh install. Check whether the
  current TOTW 1–3 contain silver/bronze cards; if not, the silver/bronze TOTW rows fall back to
  Standard until a week has them.

## Merge upgrade compatibility

Develop's published migrations 117/118 belong to SBCs. Pack odds v3 is now 119
and the desirable-card profile is 120. Migration 119 also fills missing SBC
formation/manager columns and retail rules on databases that previously applied
the pack branch's 117/118. Existing pack odds and card reductions identify that
branch's completed migrations, so the merge preserves subsequent operator edits.
No club reset is required.
