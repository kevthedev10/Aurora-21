# Promos, events and buckets — plan

> Archived on 2026-10-01. This is implementation history with historical status
> claims, not the current plan. Use the [repository README](../../README.md),
> [contribution guide](../../CONTRIBUTION.MD) and
> [panel guide](../../ultimateteam/panel-web/README.md) for current instructions.

Status (2026-09-30, `feat/live-db-edits`, main merged): **the promo catalogue, event-based availability and standard/TOTW bucket draws are built.** Migrations 072–094 follow main's live migrations 064–071. TOTW 1–3 is the first seven-day event. Gold Rare and TOTW 1–3 have editable rating-seeded buckets. Migration 089 adds always-available Gold Common, Silver Common/Rare and Bronze Common/Rare promos, each with one uniform weight-1 bucket as specified in `PACK-ODDS-SPEC.md`. Migration 090 disables the legacy retail red-pick pack; owned Champions rewards retain their dedicated red sources. Migration 091 renames timing tables to `promo_events`/`promo_event_promos` and weighted card sets to `promo_buckets`, preserving data. Player-pack specials, including promo-only and guaranteed-special slots, now use category → promo → bucket → card selection. Migration 092 preserves legacy exclusive packs as explicit rarity filters. Migration 093 adds pack/pick promo sources and captured token draw rules. Player-pick previews, reveals and bonus players now use saved buckets; the React pack editor can create pick groups and configure their sources and category odds. Migration 094 adds publishable promo/bucket/event edits, independent Icon rotations and external SQL revision tracking. Draft now selects category → promo → bucket → uniform card. Availability, conditional card probabilities and reproducible whole-pack odds are available through the API, including pending changes. React management pages and live verification remain unfinished; old schema/configuration fields remain compatibility metadata.

## Goal

Promos describe **which cards belong together** (TOTW 5, TOTS Premier League, Summer Stars, Icons Prime, and so
on). A separate **Promo Event** has a start, an end and one or more promos. Each promo contains **Promo Buckets**:
saved card lists with relative weights; cards within a bucket have equal odds. The standard pool is always eligible:
ordinary bronze, silver and gold cards, plus the currently selected Icon tier (`base_icons` outside rotation windows). At draw time, ordinary packs may offer the
standard pool and cards in any currently active event. With no active event, they offer only the standard pool.
Pack rarity, tier, rating, weight and source rules still narrow that eligible set; "standard" does not guarantee a
card in every pack.

Granting one specific card (the panel's Grant, SBC rewards that name a card) is not affected by events. FUT
Champions red cards remain exclusive to Champions rewards. Each pack can choose whether to include the general
pool (standard cards plus the current event) and can also select specific promos. This supports normal,
additive and specific-promos-only packs.

## Decisions and proposed rules (2026-09-29)

1. **The curated special cards get groups, with 11 remaining exceptions.**
   - The historical CSV's groups stay as they are. `tools/fifa21_promo_groups.csv` assigns 192 more cards and marks
     17 with `-`; six current Pro cards are assigned to Developer Cards by migration 077, leaving 11 without a group.
   - Two new groups are `silver_totw` and `next_gen`.
   - The cards the CSV leaves out get their own groups:
     - Icons are split into Base, Mid, Prime and **Moments**;
     - **UCL** (rare and non-rare together) is one group;
     - **Sudamericana** base cards are one group;
     - **Libertadores** base cards are one group.
   - Any other special card present when migration 076 runs goes into an `unsorted_<card type>` group.
   - The six current Pro cards belong to `developer_cards` (display name Developer Cards). They remain grant-only.
   - Cards without a group are excluded from every random draw that can return a special. Their definitions remain
     available for specific-card grants, subject to existing grant rules.
2. **Standard pool (new decision, 2026-09-29):** ordinary bronze, silver and gold cards and the `base_icons` group
   are always eligible for general random draws. Icons have a separate category chance. Mid, Prime and Moments
   replace the current Icon tier through the independent standard-pool rotation. UCL, Libertadores and Sudamericana
   promos require an ordinary event; their cards are not part of the standard pool.
3. **Timing (revised):** create Promo Events separate from promos. An event has a UTC start and end,
   an enabled switch, and one or more promo memberships. Multiple events may overlap. A promo is generally
   available when any enabled event containing it is active. The old `promos.enabled`, `starts_at` and `ends_at`
   fields must not create a second availability rule. **Icon tiers are an exception:** Base, Mid, Prime and Moments
   rotate in the standard Icon slot through a separate standard-pool rotation. Mid replaces Base for its
   window; ordinary Promo Events cannot add an Icon tier alongside the current one.
4. **Scope:** store packs (bought or gifted), player picks, FUT Draft and random rewards use the same general
   eligibility rule. Named pools and source restrictions intersect with that rule. Existing revealed pick choices
   remain fixed. A pack that explicitly names promos is handled in a later step.
5. **Per pack (maintainer clarification):** a pack has `include_general_pool` and optional `promo_codes`. The general
   pool is the standard pool plus groups in the currently active event. A pack can draw from the general pool,
   from the general pool plus its named groups, or only from its named groups. Other pack filters and source
   restrictions still apply.
6. **FUT Champions red cards (maintainer, 2026-09-29):** red cards are obtainable only from FUT Champions rewards.
   Main already implements that restriction. Champions red picks use their reward-specific live red pool, independent
   of the general Promo Event. Their `fut_champions` group records membership; the draw work must preserve the
   source restriction.

## Baseline before migration 076 (observed)

- **Tables:** `ultimateteam.promos` (`promo_id`, `code`, `name`, `starts_at`, `ends_at`, `status`) and
  `ultimateteam.promo_cards` (`promo_id`, `player_card_definition_id`, `design_override_id`) existed and were loaded
  into the content snapshot (`RelationalCatalogSnapshot.Promos`/`PromoCards`). The older seed filled neither;
  migration 076 now fills both. No draw reads them yet.
- **The guard applies:** both are catalogue tables under the recipe 2 guard (migration 072). An insert is allowed; an
  update only inside a content publish; a delete never. Migration 075 already moves `promo_cards` rows when it
  merges the duplicate RTTF cards.
- **How specials drop today:** every special card (rareflag above 1) that is not grant-only or reward-only is in the
  special pool. Store packs draw from it by card-type weight (`PackOpeningService`, `special_weights`). A pack's
  `promo_only` means "specials only", not "certain promos".
- **Red-card source:** main's phase reward work distinguishes Champions reward picks from ordinary pack draws.
  Step 3 must recheck this path after filtering is added; group membership cannot override source restrictions.

## The CSVs (`tools/fifa21_promo_group_cards.csv`, `tools/fifa21_promo_groups.csv`)

- **Size:** 2,233 rows, 107 groups and 2,213 distinct cards (20 cards are in two groups).
- **Categories:** `historical_squad` (1,284 rows), `event_item_group` (545) and `icon_tier` (404).
- **Checked against the local database** (read-only, 2026-09-29; the local cluster was at schema 63):
  - 2,230 rows match on definition ID, resource ID, footballer, card type, rating and position.
  - 3 rows do not exist: the Flashbacks rows for 50522519 Neymar Jr, 50527512 Pogba and 83886121 Iniesta, the
    cards migration 050 removed because they never existed in FIFA 21. The import drops them.
  - The two RTTF cards migration 075 merges already use the surviving resource IDs (67297667, 67317672).
- **Special cards the historical CSV leaves out, counted against the database** (the local cluster, 2026-09-29):
  - The catalogue has 5,437 special cards (rareflag above 1, or a special card type), and the CSV covers 2,210 of
    them.
  - An earlier count from `players.json` (4,598 specials, 2,489 left out) was wrong, because some `players.json`
    ids differ from the database's. The Icon Moments, for example, *are* in the CSV (`prime_icon_moments`, 101
    cards).

  | Card type (rareflag) | Cards | Group (step 1) |
  | --- | --- | --- |
  | Sudamericana (52) | 928 | `sudamericana` (decision 1) |
  | Libertadores (53) | 700 | `libertadores` (decision 1) |
  | Champions League Rare (48) and Non-Rare (47) | 354 + 201 | `ucl` (decision 1) |
  | FUT Champions (18), the red reward cards | 833 | `fut_champions` (added in step 1, see below) |
  | 23 other card types: TOTS 30, FUT Freeze 23, TOTW 21, Future Stars 20, FUT Birthday 20, Next-Gen 14, Premium SBC Reward 9, UEL RTTK 9, Rulebreakers 9, UCL RTTK 8, Summer Stars 8, Path to Glory 7, OTW 6, What If Upgraded 6, Professional Player 6, TOTGS 3, What If 3, Headliners (96) 3, TOTGS Europa 2, Beckham 1, Hero 1, Sudamericana TOTGS 1, Headliners (95) 1 | 211 before migration 075 | 2 duplicate RTTF cards are removed; the remaining 209 appear in the manual CSV: 161 join existing groups, 31 join `silver_totw` or `next_gen`, and 17 deliberately remain ungrouped. |

- **Manual CSV:** all 209 rows match the missing-card report on definition, resource, footballer, card type, rating and
  position. The migration checks those identities against the catalogue before inserting memberships.

## Design

### Data

Current terminology (migration 091):

| Concept | Table | Contents |
| --- | --- | --- |
| Promo event | `promo_events` | UTC start/end, enabled switch; `promo_event_promos` links its promos |
| Promo | `promos` | Identity, odds category and promo weight |
| Promo bucket | `promo_buckets` | Weighted card list within one promo; `promo_cards.bucket_id` assigns each member |

- **Promo catalogue already built (migrations 076–077):** `promos` names groups and `promo_cards` records their
  members. The existing group `status` is a catalogue row state. Existing group-level `enabled`, `starts_at` and
  `ends_at` reflect the superseded timing design; the availability lookup ignores them. A later editor can retire
  these fields.
- **Promo seeding:**
  - One `promos` row per historical CSV group (`code` = `group_key`, `name` = `group_name`), plus `silver_totw` and
    `next_gen` from the manual assignments.
  - The groups from decision 1.
  - Historical membership in `promo_cards` is matched by native resource ID. Manual membership must also match the
    listed definition, footballer, card type, rating and position. `-` rows get no membership.
- **Provenance:** both CSVs stay in `tools/`. The migration records both SHA-256 hashes and their row counts.
- **Events (migrations 079 and 091):** `promo_events` stores a stable code, display name, enabled switch,
  `starts_at` and `ends_at` as UTC instants; both endpoints are required and `starts_at < ends_at`. A join table
  `promo_event_promos` links each event to one or more existing promos, with a unique pair and an
  enabled switch for reversible membership removal. Updates and membership changes go through the same content
  publish guard as other live catalogue edits.
- **Odds metadata:** each promo has one stable `odds_category` (for example `totw`, `toty`, `icon`, `gold_rare`) and a
  positive `draw_weight` (default 1) for selecting that promo within its category. Inside each promo,
  `promo_buckets` holds one or more card sets, each with its own positive `draw_weight`. Each
  `promo_cards.bucket_id` identifies exactly one bucket within that promo. Existing members start in a
  single default bucket with weight 1. The promo weight and its internal buckets are saved once and reused
  across events and packs. Gold Rare is an always-available pool using the same promo, bucket and card
  selection model as time-limited promos. Its seven initial buckets are named for the existing rating bands
  (75–79, 80–81, 82–83, 84–85, 86–87, 88–89, 90–99), seeded with every Rare Gold player in that range and the
  current band's weight. These are editable membership lists, not permanent rating predicates: an operator can
  later move a card, add a singleton bucket for a particular player, or create a bucket from a selected set of players.
  Ratings do not implicitly change a bucket's weight. Migration 089 defines `gold_common`, `silver_common`, `silver_rare`, `bronze_common` and `bronze_rare` as always-available promos, each with a single `default` bucket of weight 1. Their cards draw uniformly, as required by the odds spec; operators can split or curate their buckets later.
- **First state:** `initial_totw_1_3` is enabled from 2026-09-29 18:06:34 UTC to 2026-10-06 18:06:34 UTC and contains
  TOTW 1, 2 and 3. The general pool includes ordinary bronze, silver, gold and Base Icons at all times. After this
  window, only that standard pool remains until another event is enabled. This changes which cards are packable
  when draw filtering is deployed.
- **TOTW 1–3 buckets (migration 083):** each promo has only the rating buckets it can fill. Bronze and silver cards
  use 0–64 and 65–74 buckets with initial weight 1; gold cards use the seven existing inform ranges with weights
  15, 27, 30, 15, 9, 3 and 1. Membership is an editable saved list. Ordinary TOTW slots select a promo and its saved buckets, then a card; the old inform-band draw helper has been removed.
- **Icon rotation (backend built, migration 094):** the odds category is `icon`, and its 0.05% default chance belongs to the standard
  Icon slot. Today that slot contains `base_icons`. `icon_rotations` selects exactly
  one Icon tier at a time: each enabled window replaces Base with its selected tier. Windows may meet at their boundaries but cannot overlap. Base returns outside all windows; create adjacent windows to continue from Mid to Prime to Moments. This uses neither `promo_events` nor concurrent Icon tiers.

### Availability (Core)

- **Standard pool:** use explicit classification from the catalogue: the ordinary bronze, silver and gold card
  types, plus membership in `base_icons`. Do not treat every low rareflag or every non-rare competition card as
  standard. Validate the precise card-type IDs against the seeded catalogue before coding this rule.
- **Active event:** enabled and `starts_at` ≤ current UTC time < `ends_at`. No background job is needed; a
  published event or membership change reaches the next draw through the content snapshot.
- **Generally available card:** in the standard pool, or in any promo attached to any active event.
  Membership in several groups or events is a union. Ungrouped specials are unavailable to general draws.

  | Current events | General candidate pool before pack filters |
  | --- | --- |
  | None | Ordinary bronze, silver, gold and Base Icons |
  | One event with TOTW 5 and Road to the Final | Standard pool plus both groups |
  | Two overlapping events | Standard pool plus the union of both events' groups |
- **Restrictions remain separate:** grant-only, reward-only, Champions red, named resource pools, rating and tier
  filters apply in addition to availability. Scheduling a group never grants a card or changes the configured
  special chance; the distribution among eligible specials changes when the set changes. The standard pool has no
  bypass around a pack's own filters. Player-pack, player-pick and bonus draws no longer use `special_weights`, global rarity weights or per-player rating weights. Legacy columns and settings remain as metadata until the panel and schema cleanup; they must not be presented as active player-pack odds.
- **Grouped-pack draw:** packs with `banded_odds` use the standard base pool and active promo availability.
  Gold Rare and TOTW slots choose saved buckets; Icon cards have their own rare-slot chance. The authored
  `totw_1_3` and `totw_1_3_red` reward pools remain fixed named restrictions. Other active categories use a
  pack's `category_chances`. The former phase draw helper has been removed.
- **One lookup:** revise `PromoAvailability` to read the standard classification, events and memberships from
  one pinned content snapshot. Pass one draw time into every candidate check in a draw; avoid clock-boundary
  differences between cards. A public preview uses the same lookup and filters.
- **Pack selection (built, migration 093):** `include_general_pool` defaults to true and controls whether the pack uses the
  standard pool plus the groups in the current event. `promo_codes` adds the named groups independently of
  events. Both sources are combined before applying the pack's rating, rarity, tier, weight and named-resource
  filters. `include_general_pool: false` requires at least one named group for a player pack. `guaranteed_specials`
  still controls the guaranteed count. Validate that the selected groups can fill the pack's required rare and common
  slots; an empty or incompatible pool produces a clear configuration error instead of silently using other cards.

  | `include_general_pool` | `promo_codes` | Player candidates before other pack filters |
  | --- | --- | --- |
  | `true` | empty | Standard pool + current event |
  | `true` | `totw_5`, `road_to_the_final_team_1` | Standard pool + current event + those groups |
  | `false` | `totw_5` | TOTW 5 only |

### Pack odds simplification proposal (draft for maintainer review)

Player packs, player-pick previews/reveals and bonus players now use bucket draws. Draft still uses its older card weights. The hosted store also changes which pack
definitions are sold through deploy SQL. Unify ordinary store and reward player packs under one draw path:

1. **Build pools once per draw.** Resolve the pack's general-pool switch and specific groups, then apply grant-only,
   reward-only, source, named-resource, tier, rating and rarity filters. Keep the six ordinary base categories and
   the eligible promos. A group is one selectable outcome, whether it contains one card or hundreds.
2. **Choose a slot outcome with explicit category chances.** A common slot draws base common. A rare slot has
   pack-defined chances for Icons, TOTW, TOTY and other supported promo categories; the remainder draws base
   rare. The category codes come from the promos, not display names. Only available groups with eligible cards
   join a category. If a category has none, its chance returns to base rare rather than making another
   promo more common. `guaranteed_specials` and `promo_only` force a special from the allowed nonempty categories;
   an impossible forced draw produces a clear pack configuration error. **Built:** forced slots normalize positive entries in `category_chances` among eligible special categories; explicit zero categories are excluded. With no category entries, eligible special categories have equal weight (including Icons when allowed). The fixed TOTW 1–3 rewards remain restricted to their named pool. Validate that category chances sum to at
   most 100%. This prevents an empty event from turning every special roll into a Base Icon.
3. **Choose promo, bucket, then card.** Within a selected promo category, choose an eligible promo by its
   `draw_weight`; inside it choose an eligible bucket by its `draw_weight`, then choose uniformly among
   the eligible cards in that bucket. To make a Messi card rarer, put it in a singleton bucket with a smaller weight;
   other cards can share one bucket. Bucket weights are relative chances, not per-card weights. Empty buckets after
   pack filters are omitted and the remaining bucket weights are normalized. **Gold Rare uses these buckets directly:**
   the slot does not choose a rating band or apply an additional per-card rating weight. A rating floor such as
   `rated_rare_slots` filters eligible cards within each bucket before buckets are drawn. The Gold Pack's volume
   profile needs its own explicit bucket weights if its lower highlight odds are retained. Do not also apply
   `specialRarityWeights` or `eliteWeight` to an ordinary draw. Reuse the selector for ordinary packs and player picks.
   A card in two active groups can be reached through either group, so its total chance is the sum of both routes;
   the panel shows that combined chance. Still prevent two versions of one player in the same pack.
4. **Keep explicit reward mechanics explicit.** Champions red picks use only their live red reward pool. A named
   fixed TOTW 1–3 reward pack stays restricted to that named pool. The post-match Match Pack has its own tier and pity
   rules; first share candidate eligibility with the new model, then decide separately whether to replace its
   reward-specific odds.
5. **Display EA-style pack odds.** The panel computes the chance that **at least one card in the whole pack** is
   an 82+ gold rare, 84+ gold rare, 88+ gold rare, TOTW, TOTY and each other supported category. These are
   overlapping events: an 88+ gold rare also satisfies 84+ and 82+. The displayed numbers are outputs of the
   actual draw rules, not a second independent set of promises. For packs with several rare slots, a per-slot
   category chance is not the same as the displayed per-pack chance. Estimate the latter with a reproducible
   simulator and show its sample size and error bound. Recompute the preview when pack rules, events or card
   weights or group membership change. An operator can tune the simple inputs against a desired displayed result.
6. **Translate existing packs before switching the draw path.** Map each live pack's current fields and special
   guarantee to the new fields; validate packs whose old `special_weights_exclusive` or `banded_odds` behavior cannot
   be represented. Retire old fields from active logic only after that migration and its tests. Simulate representative
   Gold, Premium Gold, Rare Gold, TOTW and special packs, and show the old and proposed 82+, 84+, Icon and promo rates
   before publishing. Tune Gold Rare's group weights against the current published headline rates where practical;
   those rates are targets rather than a second rating-band roll. Do not require identical card-level odds from
   two different models.

### Draws in scope (each to be confirmed in code before changing it)

| Draw | Today | Change |
| --- | --- | --- |
| Store and gifted packs (`PackOpeningService`) | special pool of every special | standard pool plus active event groups, intersected with pack filters; explicit pack groups in the later step |
| Player picks (`MakePlayerPickAsync`, `promo_only`) | any special | the same eligible set, subject to the pick's rating, rarity and named pool |
| FUT Draft special picks (`DraftService`) | any special above the pick rating | the same eligible set, subject to Draft's own pool and position rules |
| Rewards that draw a random special (objectives, season and competition rewards) | any special | the same eligible set at the actual draw; unopened reward packs follow the pack path |
| Pack bonus card (`bonus_promo_only`) | any special | apply the same eligibility if the admin-only Dev Pack remains available |


### Configurable player picks (built, 2026-09-30)

- Create a pack in `/panel/next/`, choose **Player picks**, and add one or more groups. A group can grant several
  tokens; each token offers 2–5 choices, within the server's native limits. Optional bonus players are separate awards.
- `size` counts tokens plus bonus players, not all offered choices. The editor calculates it automatically.
- Pack-level promo sources and category odds apply to all groups; each group may override them. Specific promos
  remain eligible outside events. Source selection cannot make Champions reds or Developer Cards generally packable.
- An explicit category map replaces defaults, including zero chances for omitted categories. Specials-only picks
  normalize positive probabilities over eligible special categories; without explicit odds they use equal category
  weights. Within each category, the saved promo and bucket weights determine the choices.
- Newly created tokens store their draw rules. Unrevealed tokens still evaluate event dates and bucket weights at
  reveal time. An event-only forced pick cannot reveal after its specials expire; it keeps its token and returns an
  error. Revealed choices stay fixed, including their reserved item ids and tradeability on selection.
- Existing unrevealed tokens have no captured JSON settings: their stored rating/rarity/named-pool filters are retained,
  while any applicable pack-level source configuration is read when they reveal. Existing revealed tokens do not reroll.

### Editing (content changesets, like packs and rules)

- **A `promo` content area** keyed by `{ "code": "totw_5" }` edits its identity, display fields and card members.
- **A `promo_event` content area** keyed by stable code edits name, enabled switch, UTC start/end and promo-group
  list together. The API rejects an empty group list, duplicate group codes, missing groups, Icon-tier groups and
  invalid windows. Icon-tier timing belongs to the separate standard-pool rotation.
- **Changes wait for publish** like any content, and History can revert them.
- **Membership:** both promo cards and event-promo links need safe removals through a content publish.
  Extend the recipe 2 guard or use a soft-removal design; direct operator SQL edits remain outside this API.
- **Bucket creation:** the panel offers a rating-range shortcut that preselects matching cards in the chosen pool,
  then lets the operator review and save that membership. The same editor also supports selecting individual
  players or multiple players directly. A bucket's saved membership stays explicit; changing a player's rating
  later does not silently move it to another bucket.
- **Pack fields:** the pack editor gains `include_general_pool` and `promo_codes`.
- **Odds fields:** the pack editor shows category chances and the calculated whole-pack odds. The promo editor
  sets its category and draw weight, then assigns each member to one bucket. Show the normalized
  chance of each promo within its active category, each internal bucket within its promo, and each card within its
  internal bucket for the selected pack's filters; ratings or named pools can change those conditional percentages.
  Publishing validates category coverage, positive weights and at least one eligible card in every selected group.

### Panel (new panel, Content → Promos)

- **Promos:** list by family and category, with card counts and artwork; edit promo identity, weights and buckets.
- **Promo Events:** list upcoming, active and past windows; create an event, choose multiple promos, edit dates
  and enable or disable it. Show active promos and the resulting pool for a selected time.
- **Standard Icon rotation (API built; editor pending):** a separate editor selects dates for the Icon tier that occupies the always-available
  Icon slot. Its preview shows which tier replaces the current one at each boundary.
- **Standard pool:** show the always-available bronze, silver, gold and Base Icons as a read-only summary, so an
  operator can distinguish it from active promos.
- **Availability preview:** show the currently eligible card count and each pack's eligible contents after its own
  filters, plus the computed EA-style whole-pack odds; warn when a guaranteed-special pack has no eligible special.
- **The pack editor:** an "Include standard pool and current event" switch and a "Specific promos" picker.

## Steps

0. **Validate the CSV.** Done on 2026-09-29: 2,230 of 2,233 rows match; the 3 removed Flashbacks cards are dropped.
1. **Group migration and seed.**
   - The `promos` columns, the groups and membership from the CSV, and the groups from decision 1.
   - Tests: the counts per group, every manually assigned card exists, and only the 11 remaining exceptions lack a
     group on a fresh database. An isolated upgrade from main schema 71 produces the same memberships.
2. **Original availability model and draw inventory.**
   - Done: group-membership lookup and the original per-group window tests. Replace its timing rule with events.
   - Done: random draw sites found in the code and listed below.
3. **Standard pool and events (built and applied to draws).**
   - Ordinary bronze/silver/gold card types 1–6 and Base Icon membership define the standard pool.
   - Promo weights and one weight-1 bucket per promo are seeded. Main odds categories have explicit codes;
     remaining promos start in `other_promo` until their odds are configured.
   - Event tables and snapshot loading are in place. `PromoAvailability` unions standard cards and active
     event groups; old group-level timing fields are ignored. Tests cover one, overlapping and disabled events,
     UTC boundaries, cards in multiple groups, and live content reload.
   - The initial event is TOTW 1–3 for seven days. Pack, pick and Draft candidate filtering has been added;
     dedicated TOTW 1–3 and red reward paths retain their fixed source pools. The Gold Rare pack slot now selects
     through saved buckets; the five other ordinary categories use uniform editable buckets. TOTW slots also use their saved buckets. Optional `category_chances` on a pack can select other active
     promo categories. Player picks use saved promo buckets; ordinary choices prefer Gold Rare and use the default 3% TOTW and 0.05% Icon chances unless overridden. Draft now uses the same saved buckets and independent category weights. Guaranteed-special and promo-only category selection and per-pack promo controls are built.
4. **Apply eligibility and simplify odds (draw backend built).**
   - Done for player packs: promo-only and guaranteed-special slots select category, promo, bucket and uniform card.
     Impossible forced slots fail and the purchase transaction rolls back. All tier, rating, rarity, team, nation,
     availability and uniqueness filters apply before selection. A promo-only player pack requires every player
     slot to be special. Empty optional categories return their probability to a standard rare slot.
   - Migration 092 replaces exclusive legacy special rarity weights with explicit `allowed_rareflags` filters,
     preserving ordinary common/rare eligibility for mixed packs. The engine ignores the old numeric rarity weights;
     pack loading no longer merges them into the configuration. Old unbanded special-card distributions intentionally
     change to the promo/bucket model; published ordinary Gold Rare odds retain their existing inputs.
   - Done for picks and bonus players: one bucket-based selector drives token previews, panel samples, reveal choices and bonus awards. New tokens capture their source, category and filter settings; event availability and saved bucket weights are evaluated on first reveal. Revealed choices remain immutable. Named Champions red rewards keep their source restrictions.
   - Draft uses category weights and the shared bucket selector, retaining position, rating-floor, weak-round, uniqueness and elite-round filters. `categoryWeights` and `weakRoundCategoryWeights` override categories; unspecified standard categories have weight 1 and specials use the existing `specialBias`/`weakRoundSpecialBias` values (4.5/1.35 initially). These are relative weights, not pack probabilities. Draft offers the currently selected Icon tier. The old rating-power settings are no longer read by the draw.
   - Remaining cleanup is compatibility metadata and panel labels: legacy `banded_odds`, rating profile and rarity-weight fields are retained for existing definitions; named Gold-volume profiles still override saved bucket weights. Match Packs retain their explicitly authored reward odds.
   - Optional compatibility cleanup: consolidate ordinary `banded_odds` and unbanded player-pack classification
     with one draw path. Preserve authored TOTW 1–3 and red reward restrictions. Compare simulated old and proposed
     whole-pack odds for representative packs and update focused odds tests to the approved numbers. Seed explicit
     always-available Gold Rare buckets before removing `goldRareBands`; migration 081 seeds the seven
     rating-range groups and their current weights. Use group selection for rated slots
     and picks too. Test the group
     and bucket weight ratios, plus the combined odds of a card that belongs to two active promos.
   - Store and gifted packs, player picks (including previews and reveal), Draft and random rewards.
   - Enforce decision 6 at the source as well as at the draw: no other reward, generic pack/pick or direct grant
     should issue a red card simply by naming rareflag 18 or a red resource ID.
   - Tests per draw: with no event, ordinary cards and Base Icons remain eligible while other promos do not;
     an active event adds only its groups; an expired or disabled event removes them. Authored bucket weights,
     source restrictions and saved pick choices remain intact; legacy per-card pick distributions intentionally change.
5. **Per-pack promos and configurable picks (built).** Migration 093 adds `include_general_pool` and `promo_codes`;
   sources are shared by ordinary packs and picks. The React editor offers Players or Player picks, multiple pick
   groups, choices per token, rating limits, labels, specials-only, bonus players, promo sources and category percentages.
   Groups inherit pack sources/odds unless overridden. New packs and edits use existing review/publish changesets.
   Test contents displays separate sample choices for each pick. Tests cover all three source combinations,
   including a TOTW 5-only pack while TOTW 1–3 is active, and captured token rules after pack edits.
6. **Content areas (backend built) and panel pages (pending).** `promo`, `promo_event` and `icon_rotation` changes use the existing atomic publish, conflict, history and revert pipeline. API list/detail, rating-range bucket proposals, availability preview, conditional card distribution and whole-pack simulation are built. React management pages, normalized-weight displays and the outside-change banner are the next work.
7. **Documents.** `panel-api-contract.md`, `live-content-editing-plan.md` step 8, and deploy notes.

## Backend completion (2026-09-30)

- A promo is edited as one definition containing all buckets and explicit resource-ID lists. Moving cards or removing membership is atomic. Removed buckets are disabled, not deleted. Player definitions, owned items and promo identities remain intact.
- Standard pool categories retain their native type classification. Icon tiers retain their curated card membership while buckets may be split or reweighted. Ordinary events cannot activate standard pools, Icons, Developer Cards or Champions reds.
- New cards, promos, events and pack sources may be staged in dependency order and published together. Final validation refuses enabled events or pack sources referring to hidden promos, and overlapping enabled Icon rotations.
- `pending` previews apply and validate the entire changeset in a rolled-back transaction, then draw from that immutable result. They neither publish content nor create club items. Whole-pack odds report seeded sample estimates and Wilson 95% intervals; pick odds describe offered choices, since the user's selection is not random.
- Conditional card probabilities add every eligible promo route. Earlier draws can remove a footballer's other versions, so these conditional probabilities are explicitly before those removals.
- Named TOTW reward and Match Pack caches now follow the pinned content revision, including dry-run preview revisions. They no longer freeze the first loaded catalogue for an existing club.
- SQL changes update transactional table revisions. `GET /content/status` detects a committed change not loaded by this process and offers explicit reload; the backend never reloads automatically. Own publishes and rolled-back edits do not report as outside changes. The React banner remains to be built.
- Wider live-editing areas (SBC sets, objectives safeguards, managers, consumables and customisations) remain separate work in `live-content-editing-plan.md`; they are not completed by the promo backend.

## Latest verification (2026-09-30)

- Backend completion: **31/31** focused promo editing, conditional probabilities, pending odds, Icon rotation, Draft reward, external-change, coverage and migration checks passed; **20/20** publish/pick/Match Pack/availability regressions passed. The latter includes a new card → new promo → new event → new pack in one changeset, with preview before publication. An earlier Draft service run passed **32/32**. Test databases were isolated; the full suite and live upgrade/client behavior remain unverified.
- Configurable picks: 21 initial source/publish/migration checks passed, including fresh and upgrade migrations;
  31 focused pack, pick, Champions reward, transactional selection and panel regressions passed. The final source
  and panel run after cleanup passed 8/8. React pack/API tests passed 10/10; type checking, production bundle build
  and build-stamp verification passed. The rebuilt bundle is embedded by the server build. No live client test was run.
- Focused event, bucket, migration and pack checks passed in isolated test databases. The special-pack cutover run passed 30/30, including fresh and upgrade migrations. After removing legacy weight merging, the final pack/configuration regression run passed 23/23. The earlier published rare-slot pack-rate simulation and rated-slot guarantee also passed.
- Fresh installs and upgrades from main schema 71 agree on memberships. An upgrade with edited event dates and
  an edited Icon bucket weight preserves those values through the rename.
- All five remaining standard categories obey edited bucket weights in both grouped and legacy pack paths,
  including their highest-rated common cards. Rating no longer changes a common card into a rare candidate.
- Published 82+/84+ rate checks count rare/special outcomes. Gold Commons remain uniform over all Gold Common
  definitions and are excluded from the rare-slot headline metric.
- Legacy promo-pack fixtures activate their required promos through a test event. Retail red picks are refused
  without charge; owned Champions reward packs still open and offer red cards.
- The full solution suite and a live database upgrade have not been run for this pass. Migrations apply on server
  startup; this work did not restart the live runtime or change its database.

## Operational notes

1. **Dev Pack 57:** it is still an admin-only definition in the merged `packs.json` seed. This plan applies the same
   eligibility to its bonus draw if it remains; remove the pack in the seed if it is no longer wanted.
2. **Cutover:** no active event means standard pool only. Before enabling the new draw filter in production,
   operators should preview the resulting packs and create any further events they want live.

## Step 2 draw inventory

Step 3 replaced the original group-level timing lookup with event membership and the standard pool.
`CatalogRepository` loads events and promo odds metadata into the same catalogue revision, and `ContentSnapshot`
builds `PromoAvailability` from it. Step 4 has since applied general availability to ordinary pack, pick and Draft
candidates, and ordinary standard-card and TOTW pack slots use the seeded buckets. The table below records the original
draw inventory and the intended filtering scope; it is not a current-state checklist. Focused event, migration
and reload tests passed against isolated databases on 2026-09-29; focused Gold Rare and published pack-rate tests
passed on 2026-09-30.

| Path | Actual draw or handoff | Step 3 work |
| --- | --- | --- |
| `PackOpeningService.PackRoll.BuildPools` | Builds the special pool for a player pack. `StoreService.BuyAndOpenPackAsync` uses this path for both bought packs and unopened reward/gift packs. | Filter special candidates when the pack is opened. Keep the guaranteed-special empty-pool error explicit. |
| `PlayerPickService.DrawChoices` | Reveals the final choices for both single-pick tokens and pick-group tokens. | Filter special candidates before the weighted draw; preserve already revealed choices. |
| `PackOpeningService.CreatePlayerPickPackAsync` and `CreatePickGroupPackAsync` | Draw a player for each pick token's preview before the token is redeemed. | Filter special previews too, so a disabled card is not shown on a new token. |
| `DraftService.PlayerChoices` | Draws captain, pitch and bench choices from a mixed player pool, including specials. | Filter special candidates before the elite-99 and weighted selections. |
| `PackOpeningService.CreatePickGroupPackAsync` bonus loop | Draws `bonus_player_count` players directly; `bonus_promo_only` limits it to specials. Currently only admin Dev Pack 57 uses this special bonus. | Apply the same pack pool selection if this admin pack remains available. |

Objective, season, Rivals, Squad Battles, Weekend League and SBC pack rewards award an **unopened pack ID**; their
random cards are selected later by the shared pack-opening path. SBC player rewards resolve an authored player by
name/rating in `SbcService.PlayerResourceId`; they are specific-card grants, not random-special draws.

## Step 1 results

Built on 2026-09-29; merged with main's migrations 064–071 in this worktree.

**Files:**
- `tools/generate-promo-groups-migration.mjs` writes `ultimateteam/server/database-setup/migrations/076-promo-groups.sql`
  from both CSVs. It is deterministic, refuses malformed group keys, duplicate manual resources and unknown manual
  groups, and records both CSV SHA-256 hashes in the migration. The SQL checks all manual card identities.
  - Generated from SHA-256 `4522eda582e9f82e99b4599133dce1b9f54853bf653d467ac550aef3e2b1877e`: 2,233 rows, 107
    groups.
  - The manual CSV has SHA-256 `52b32cc4dfe5c594d21c2cda7f7b4874df2046ed2cc4738f228b42b3d2d910b8`: 209
    rows, 161 assignments to existing groups, 31 to two new groups, and 17 `-` exclusions.
  - To change the groups: edit the relevant CSV, run `node tools/generate-promo-groups-migration.mjs`, and commit
    the inputs and generated migration together.
    (Once live, groups change through the panel; see step 5.)
- The migration is registered like the others: `RelationalCatalogMetadataMigration.CurrentVersion` = 76, the
  migration list, and the Core csproj embed.
- `ultimateteam/server/tests/Aurora21.UltimateTeam.Tests/Core/PromoGroupsMigrationTests.cs`.

**Schema added by migration 076:**

| Table | Column | Meaning |
| --- | --- | --- |
| `promos` | `family` | the promo family, for example "Team of the Week" |
| `promos` | `category` | `historical_squad`, `event_item_group`, `icon_tier` (from the CSV), `competition` or `unsorted` (checked) |
| `promos` | `sort_order` | the display order: historical groups, the two new groups, then competition groups; unexpected unsorted groups follow |
| `promos` | `enabled` | the manual switch (step 2 reads it); every group starts `true` |
| `promos` | `source` | where the group came from: the CSV and its SHA-256, the card types, or "no other group" |
| `promos` | (check) | `starts_at < ends_at` when both are set |
| `promo_cards` | `member_order` | the CSV's order within a squad (for example TOTW positions), null elsewhere |

- The existing `code`, `name`, `starts_at`, `ends_at` and `status` columns keep their meaning. Seeded groups have
  `status = 'active'` (a catalogue row state, not availability); active event membership now decides general special-card availability; the old timing fields are ignored.

**Groups seeded:**
- **The historical CSV's 107 groups**, members matched by native resource id. The three cards migration 050 removed are skipped.
  A card in two CSV groups is in both.
- **Two new event groups**, `silver_totw` (17 cards) and `next_gen` (14 cards). Another 161 manual rows join existing
  groups. The 17 `-` rows receive no group from migration 076; migration 077 adds the six current Pro cards to Developer Cards.
- **Four competition groups**, each holding every card of its card types: `ucl` (rareflags 47 and 48),
  `sudamericana` (52), `libertadores` (53) and `fut_champions` (18). Red cards have their own group but are
  reserved for Champions rewards; see decision 6 above.
- **Other cards present when migration 076 runs** fall into an `unsorted_<card type id>` group if no CSV names them.
  The seeded catalogue has none after the manual assignments and explicit exclusions. Cards added after migration 076
  will need membership assigned when they are created; the migration does not run again.

**Historical step-1 result:** this seed initially changed no player draws. Step 4 now uses event availability and saved buckets.

### Base Icon rating buckets — approved, migration 099

Split `base_icons` into seven buckets named `85`, `86`, `87`, `88`, `89`, `90` and `91`,
with codes `rating_85` through `rating_91`. Seed each with the existing Base Icon members
of that exact rating, preserving the tier's curated membership, with Base Gullit
(`17015649`) and Base Vieira (`17015642`) assigned to `89` instead of their actual
rating of 86. Their card ratings stay unchanged. These are explicit card
memberships and remain editable through the panel.

| Rating | Relative weight | Share after Base Icons are selected |
| --- | ---: | ---: |
| 85 | 35 | 35% |
| 86 | 28 | 28% |
| 87 | 20 | 20% |
| 88 | 10 | 10% |
| 89 | 5 | 5% |
| 90 | 1.5 | 1.5% |
| 91 | 0.5 | 0.5% |

These approved weights sum to 100 and make higher-rated buckets progressively rarer.
They are authored starting values, not recovered EA probabilities. Each eligible card
within a selected bucket has an equal chance. Empty or ineligible buckets are excluded
and the remaining weights normalize again. This does not change a pack's Icon category
chance or the Icon rotation. Migration 099 creates these buckets, moves existing
memberships, and disables the old empty buckets without deleting their rows. Mid,
Prime and Moments buckets are unaffected. The migration runs on the next server
startup; no live database update has been performed during implementation.

Verification (2026-10-01): five focused isolated database checks passed, covering
fresh and upgrade bucket seeding, unchanged ratings and tier membership, the earlier
operator-rule rename, the TOTY merge upgrade, and other promos' default buckets.

### Initial Icon calendar — migration 100

The Events panel manages the standard pool, Icon rotations and ordinary promo
events in separate sections. The initial calendar uses Europe/Amsterdam time:

| Tier | Start (19:00) | End (19:00) |
| --- | --- | --- |
| Base | 30 September 2026 | 1 January 2027 |
| Mid | 1 January 2027 | 1 March 2027 |
| Prime | 1 March 2027 | 1 May 2027 |
| Moments | 1 May 2027 | 1 January 2028 |

Migration 100 seeds this only when no enabled operator calendar exists. Migration
101 gives the initial TOTW 1–3 event the same September 30, 2026 → January 1, 2027
window as Base Icons, with both endpoints at 19:00 Amsterdam time. Icon tiers replace one another
in the standard pool, while promo events add specials. Base remains the fallback
outside enabled Icon windows. Schedule edits are staged and then published in
Changes. No live database update was performed during implementation.

**Developer Cards (migration 077):** all six current Pro Player cards (rareflag 10), including the three added on main,
join the `developer_cards` group. The group starts enabled and the cards remain grant-only. Eleven other special cards
remain ungrouped. This migration follows 076 so main's already-live migrations are left intact.

**Tests** (`PromoGroupsMigrationTests`, against a fresh test database built with every migration):
- migration 076 initially leaves 17 special cards ungrouped; migration 077 groups six Pro cards under Developer Cards, leaving 11 ungrouped. historical promos hold special cards, with ordinary card pools added by migrations 081 and 089; none is empty;
- every group starts enabled, with no group-level start or end time (the original timing design);
- each historical or manual group holds exactly its assigned cards, and the only historical CSV rows left out are
  the three removed cards;
- each competition group holds every card of its card types;
- the migration records both current CSV SHA-256 hashes, so an edited CSV that was not regenerated fails.

**Test run (observed, 2026-09-29):**
- `dotnet.exe test --filter FullyQualifiedName~PromoGroupsMigrationTests --no-restore` on Windows after the merge:
  4/4 passed. The isolated main-version-71 upgrade comparison passed 1/1.
- Not yet run: the full solution suite (before a commit), and the migration against a real, older club database.
  The local cluster was at schema 63 when inspected, so migrations 64–76 would run there on the next server start.
