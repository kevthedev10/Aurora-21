# Aurora 21 pack odds: what we want (spec for the rewrite)

> Archived on 2026-10-01. This predates category odds, promo buckets and events.
> Its phase-pool and rating-band instructions are historical, not the current
> selection contract. See the [API contract](../../ultimateteam/panel-api-contract.md)
> and [contribution guide](../../CONTRIBUTION.MD).

For Atlaz's optimised rewrite of pack opening. This is the **intended behaviour** as of 2026-09-29, live on the VPS since
main b9428f9 (schema 71) and unchanged through 9e4cfe8. A rewrite can change the code freely. It should keep every
behaviour and figure below, unless the owner decides otherwise.

Sources, if you need the reasoning: `PLAN.md` (section 3 = pack design), `EXECUTION.md` (owner decisions), the calculator in
`sim/` (`pack-sim.mjs` + `proposal.json`, results in `sim/results-v1.txt`), the published page `pack-odds-v1.0.md`. In
the repo: `ultimateteam/runtime-source/ultimateteam21/rules/pack_odds.json`, migrations 068-071,
`ultimateteam/deploy/launch/phase1-store.sql` and `consumables-store.sql`.

---

## 1. The idea in one paragraph

**Fat middle, thin top.** We're more generous than retail FIFA 21 in the 82-85 band (the private-server perk: about
1.6-2x retail there). At 86+ and for in-forms we're about retail. Everything comes from one small **phase pool**: base
cards plus the live Team of the Week in-forms, nothing else. Every gold rare slot, store or reward, uses **the same
disclosed per-slot table**, so one published table explains every pack. The Gold Pack is the one exception: it uses a
thinner "volume" table (section 4.2).

---

## 2. The card pool (phase pool)

Only these cards can come out of banded packs, player picks and the live TOTW reward pools:

- **Base cards:** rareflag 0 (common) and 1 (rare).
- **Excluded (owner decision D2):** UEFA Champions League, Libertadores and Sudamericana base cards (rareflags 47, 48, 52, 53).
  They are also excluded from picks and AI market listings.
- **Live in-forms:** the Team of the Week cards for weeks **1, 2 and 3** from `rules/sqbt_totw.json`, selected **by resource
  id**. Some are stored as rareflag 4, so don't select by rareflag. That's 69 cards, all three weeks at equal weight
  (`phase.weekWeights` empty = weight 1 each).
- **Never in packs:** grant-only, Aqua/Pro/developer, custom, reward-only and any other special.
- **Planned, not built (B03, public-launch blocker):** TOTW rotation by date. From Wed 14 Oct 2026 the newest week weighs
  1.0, the previous two 0.5 each, and older weeks leave. It's config (`phase.totwWeeks` + `weekWeights`), so the rewrite
  must keep those inputs.

---

## 3. How one gold rare slot rolls (the core table)

For every gold rare slot in a banded pack:

1. **In-form roll.** With the slot's special chance, the slot is an in-form, drawn by the **in-form band table**:

   | In-form rating | 75-79 | 80-81 | 82-83 | 84-85 | 86-87 | 88-89 | 90-99 |
   |---|---|---|---|---|---|---|---|
   | Weight % | 15 | 27 | 30 | 15 | 9 | 3 | 1 |

   The special chance is `store_packs.special_chance` for store packs and `store_pack_details.reward_pack_special_chance`
   = **0.011** (1.1%) for reward packs. Before 068 it was 10%.

2. **Otherwise a base gold rare,** drawn by the **gold rare band table** (`goldRareBands`):

   | Base rare rating | 75-79 | 80-81 | 82-83 | 84-85 | 86-87 | 88-89 | 90-99 |
   |---|---|---|---|---|---|---|---|
   | Weight % | 60.4 | 28 | 8.7 | 2.4 | 0.35 | 0.11 | 0.04 |

3. **Within a band, every card is equally likely.** The old "elite weight" rating boost is off (`elite_per_rating = 0`).

Rules that go with it:

- **Band truncation.** If a pack or slot has rating limits (e.g. a min rating of 84) that cut a band, that band keeps
  its weight **in proportion to the cards left in it**. Bands entirely outside the limits drop out.
- **Gold commons:** uniform over base gold commons (no bands).
- **Silver and bronze:** uniform within their own tier only. A Premium Bronze or Premium Silver rare slot is one of
  **that tier's** in-forms 0.5% of the time.
- Weights are percentages but don't need to sum to 100: normalise.

---

## 4. Store packs (live)

Coins only: FIFA Points buy nothing (400 response). **No odds or percentages in any in-game pack bio** (owner decision
D15). Odds are published only outside the game (#pack-odds post).

| Id | Pack | Coins | Contents | In-form chance per rare slot | Rare table |
|---|---|---|---|---|---|
| 1 | Bronze | 400 | 12 bronze items | – | – |
| 4 | Premium Bronze | 750 | 11 bronze + 1 bronze rare | 0.5% (bronze in-form) | – |
| 2 | Silver | 2,500 | 12 silver items | – | – |
| 5 | Premium Silver | 3,750 | 11 silver + 1 silver rare | 0.5% (silver in-form) | – |
| 3 | Gold | 5,000 | 11 gold + 1 gold rare, **players only** | **0.4%** | **volume** (4.2) |
| 6 | Premium Gold | 7,500 | 9 gold + 3 gold rares, **players only** | 1.1% | standard (section 3) |
| 7 | Cosmetics | 5,000 | unchanged | – | – |
| 8 | Rare Gold | 25,000 | 12 gold rares, **players only** | 1.1% | standard |
| 9 | Consumables | 3,000 | 12 distinct: 4 manager league cards + 4 position modifiers + 4 chemistry styles (never Basic / GK Basic) | – | – |

- **Players only** = packs 3, 6 and 8 have manager chance 0 and chemistry-style chance 0.
- **Bronze/silver manager slot:** when a bronze or silver pack's manager chance hits, it's a manager or a **manager league
  card**, half each (`manager_league_card_share = 0.5`).

### 4.1 Published per-pack figures (chance of at least one, one pack)

These are the numbers we published (`pack-odds-v1.0.md`). A rewrite must reproduce them.

| Pack | 82+ | 84+ | 86+ | 88+ | 90+ | TOTW in-form |
|---|---|---|---|---|---|---|
| Premium Bronze | – | – | – | – | – | 0.5% |
| Premium Silver | – | – | – | – | – | 0.5% |
| Gold (5k, volume) | 4.2% | 1.1% | 0.2% | 0.1% | <0.1% | 0.4% |
| Premium Gold (7.5k) | 32.1% | 9.2% | 1.9% | 0.6% | 0.1% | 3.3% |
| Rare Gold (25k) | 78.8% | 32.1% | 7.4% | 2.3% | 0.6% | 12.4% |

### 4.2 Why the Gold Pack is different (the "volume" table)

Owner, 2026-09-29: gold quick-sells at a flat **360 common / 700 rare**, so a 5k Gold Pack gives back about 93% of its
price. We made it a **volume pack**: good for bulk opening and quick-selling, bad for hunting highlights. Its rare slot uses
`goldRareBandTables.volume`, same band order as section 3:

| Base rare rating | 75-79 | 80-81 | 82-83 | 84-85 | 86-87 | 88-89 | 90-99 |
|---|---|---|---|---|---|---|---|
| Weight % | 66 | 30.035 | 3 | 0.8 | 0.12 | 0.035 | 0.01 |

The in-form chance is 0.4% instead of 1.1%. The result: every highlight costs more net coins from Gold than from Premium
Gold. A pack picks its table with `store_packs.odds_table` (null = standard). Keep that as a named, data-driven table,
not a special case in code.

### 4.3 Value gradient (why 25k Rare Gold exists)

Highlight value per coin rises gently: Gold 0.5x, Premium Gold 1.0x, Rare Gold about 1.2x. What an 85,000-coin start buys
as 11 Premium Golds: on average 4.0 cards at 82+; P(at least one 84+) 65.5%, 86+ 19%, TOTW 30.6%, 88+ 6.2%. Retail gives
about 2 cards at 82+ and a ~40% chance of an 84+.

---

## 5. Reward packs

All reward packs are banded and use section 3 with the 1.1% in-form chance per rare slot. Figures are "at least one" per pack.

| Id | Pack | Contents | 82+ | 84+ | 86+ | 88+ | 90+ | ≥1 TOTW |
|---|---|---|---|---|---|---|---|---|
| 300 | Gold | 12, 1 rare | 12.1% | 3.2% | 0.6% | 0.2% | 0.1% | 1.1% |
| 301 / 305 | Premium Gold (Players) | 12, 3 rare | 32.1% | 9.2% | 1.9% | 0.6% | 0.1% | 3.3% |
| 543 | Small Rare Gold Players | 6, 6 rare | 53.9% | 17.6% | 3.8% | 1.1% | 0.3% | 6.4% |
| 308 | Prime Gold Players | 12, 6 rare | 53.9% | 17.6% | 3.8% | 1.1% | 0.3% | 6.4% |
| 303 | Jumbo Premium Gold | 24, 7 rare | 59.5% | 20.2% | 4.4% | 1.3% | 0.4% | 7.4% |
| 400 / 401 | Rare Gold / Rare Players | 12, 12 rare | 78.8% | 32.1% | 7.4% | 2.3% | 0.6% | 12.4% |
| 403 | Mega | 30, 18 rare | 90.2% | 44.1% | 10.9% | 3.4% | 0.9% | 18.1% |
| 402 | Jumbo Rare Players | 24, 24 rare | 95.5% | 53.9% | 14.2% | 4.5% | 1.2% | 23.3% |
| 404 | Rare Mega | 30, 30 rare | 97.9% | 62.0% | 17.5% | 5.6% | 1.5% | 28.2% |
| 405 | Ultimate | 30 rare, **3 of them 84+** | 100% | 100% | 52.9% | 19.3% | 5.5% | 28.2% |
| 540 | Premium TOTW | **3 in-forms** + 9 gold | 92.6% | 62.7% | 34.2% | 11.5% | 3.0% | 100% |
| 541 | Ultimate TOTW | **11 in-forms** + 1 gold | 100% | 97.3% | 78.4% | 36.2% | 10.5% | 100% |
| 542 | TOTW Player | 1 in-form | 58.0% | 28.0% | 13.0% | 4.0% | 1.0% | 100% |

Notes:
- **405** has `rated_rare_slots = [{count: 3, min_rating: 84}]`: after any guaranteed specials, 3 rare slots have a floor of 84
  (band truncation from section 3 applies). It's no longer a copy of 404.
- **540/541** slots are guaranteed in-forms (`guaranteed_specials`), drawn by the in-form band table.
- Banded reward packs (migration 068): ids 100-406 plus 513-516, 518, 534, 540, 541, and the new 542/543.

---

## 6. Player picks (513-516)

| Pick | Min rating | Choices |
|---|---|---|
| 513 | 82+ | 4 |
| 514 | 84+ | 4 |
| 515 | 86+ | **4 as built** (seed migration 041 and `rules/packs.json`; 068 didn't change it). PLAN 3.5 wanted 2. Ask the owner before changing it. |
| 516 | 88+ | 2 (not used in phase 1) |

- Each choice is an **in-form at or above the minimum 3% of the time** (`picks.informChance` 0.03). Otherwise it's a base gold
  rare at or above the minimum, by the section 3 table truncated at the minimum.
- **Never two versions of the same player** among the choices.
- Picks are **untradeable**, and the chosen player **inherits the token's untradeable flag**.
- Welcome reward (owner decision D3) = 3 x 513, untradeable. Expected: about 2 players rated 84+ per club; 45% of clubs get
  an 86+, about 5% a 90+.
- **Red picks (550-553, FUT Champions reds)** draw from the phase pool too. Red copies are looked up by resource id =
  asset id + revision x 2^24, revisions `[22, 20]` in order. 22 = the phase copy (generated in 068 from the phase card; **not
  FIFA data**). 20 = the all-time copy from 051, used only when its rating equals the phase card's.

---

## 7. Match Pack (reward pack 544, after each eligible match)

- **Granted:** one per match that passes the reward-eligibility gate (`seasons.json` `rewardEligibility`), in Rivals, the
  Weekend League, online Draft, and Squad Battles / single-player Draft on **Professional or harder**. Never for a desync or
  a quit.
- **Cap:** 20 per club per rolling 24 hours.
- **Contents are rolled when the pack is granted** and stored with the unopened pack. A retune never changes a pack a player
  already earned, and players can't hoard packs for later promos. **The rewrite must keep roll-at-grant.**
- Items open untradeable with reward source `match_pack`, which means **quick sell 0**.

**6 items:** 3 players by tier, then 1 chemistry style, 1 modifier and 1 kit.

| Tier | Chance | Players |
|---|---|---|
| Standard | 70% | rare 75-81, common 75-81, common 75-81 |
| Rare | 20% | rare 80-83, rare 75-81, rare 75-81 |
| Premium | 8% | rare 83-86, rare 75-81, rare 75-81 |
| Special (highlight) | 1.6% | **in-form 82-85**, rare 75-81, rare 75-81 |
| Jackpot (highlight) | 0.4% | one of {in-form 86-87, base rare 86-89}, rare 75-81, rare 75-81 |

- Within a slot's rating range, draw uniformly.
- **Never a player the club already owns** while another eligible one is left.
- **Pity:** after 59 granted packs in a row without a highlight, the next is Special (effective rate about 1 in 35).
- **Chemistry style:** never Basic (250) or GK Basic (273). The meta styles (268, 266, 260, 265, 254, 253, 255) share 30% of draws.
- **Modifier:** a manager league card 40% of the time, else a position modifier.
- **Kit:** prefers a kit the club doesn't own.
- Expected for a regular player (25 games a week): 75 players, 2.5 highlights, about 0.65 in-forms.

---

## 8. Things around the odds the rewrite must not break

- **Tradeability is a per-source switch** in `rules/tradeability.json` (owner decision D14, "levers we pull in balancing"),
  with **no code change needed to flip a source**. Current split:
  - Tradeable: store packs, competition packs, random reward packs.
  - Untradeable: picks, guaranteed in-form grants (542 from objectives/gifts), pity, Rivals option C, single-player Draft,
    the Match Pack.
  - Every pack item carries its `reward_source` (migration 069).
- **Quick sell** (`rules/cards.json` quickSell):
  - Gold base cards: 360 common / 700 rare flat.
  - **Any untradeable item: 0** (`rewardUntradeableCap` 0 since 2026-09-29).
  - Match Pack and gift items: 0.
  - This is why the Gold Pack is a volume pack (section 4.2). Changing quick sell changes pack economics.
- **Starter team** (onboarding): gold non-rares and rares rated 75-80, like retail.
- **Config, not code:** tables live in `rules/pack_odds.json`, pack shapes in `store_packs` (operator data, set by the
  `deploy/launch/*.sql` scripts) and `rules/packs.json` for local installs. AGENTS.md: tunables go in `rules/*.json`.

---

## 9. How to check a rewrite

- **Statistical test (already exists):** `Phase1StoreScriptTests.ThePhaseOneStoreSellsBandedPacksAndTheRareGoldPack`
  rolls the Gold Pack 6,000 times. It checks 82+ 4.2%, 84+ 1.1% and in-form 0.4% within 4 standard errors (+ 0.5/n).
  Use the same method for Premium Gold, Rare Gold and a few reward packs against the tables above.
- **Existing suites to keep green:** `PackOddsTests`, `PhaseGatingTests`, `Phase1StoreScriptTests`, `MatchPackTests`,
  `PickTradeabilityTests`, `RewardQuickSellTests`, `ManagerLeagueCardTests`, `EconomyInvariantTests` (no quick-sell arbitrage).
- **Changing any number:** edit `sim/proposal.json` first, rerun `node sim/pack-sim.mjs` and `node sim/odds-page.mjs`,
  then copy the tables into `rules/pack_odds.json`. The published page and the server must agree.

---

## 10. Retune Charter (a promise we published; applies to any rewrite that shifts odds)

1. At most once a week, on a fixed day (Monday 18:00 UK).
2. **At least 7 days' notice for anything that lowers value.** Buffs apply at once.
3. **Nothing is retroactive.** Earned packs keep what they were granted with (hence roll-at-grant for the Match Pack).
4. Sinks come before cuts.
5. Every change goes in the patch notes and a new version of the odds page.
6. Cards never leave packs early.

**So an "optimised" rewrite must be odds-neutral.** If it changes any published figure beyond sampling noise, that's a
nerf under the charter and needs the owner's OK plus 7 days' notice.

---

## 11. Not built yet (stage B, still wanted)

- TOTW rotation by date (section 2).
- **TOTW pity:** after 140 rare slots without an in-form (a store slot counts 1, a reward-pack slot 0.5), the next rare
  slot is an in-form, untradeable. Published only once built.
- No 84+ pity (84+ is plentiful in phase 1).
