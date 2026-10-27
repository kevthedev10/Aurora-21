# Panel TODO

Updated 2026-10-03. React has replaced the classic panel. This is the remaining
backlog, replacing the
[archived plans](archive/panel-plans/README.md).
Use [README.md](README.md) for setup and [CONTRIBUTION.MD](CONTRIBUTION.MD)
for development and verification requirements.

The existing navigation pages are implemented. That does not mean all content
has a dedicated editor or that browser/native acceptance is complete.
The requested additions are SBCs, Modes and their rewards (Champions, Rivals,
Squad Battles and Draft), and Objectives including Season Pass. These dedicated sections are
still to be built; the generic Rules editor does not complete that scope.

## 1. SBCs — requested

- [ ] Add a Content → SBCs page with searchable groups/sets and their challenges.
- [ ] Create and edit SBC groups and individual SBCs within them.
- [ ] Edit names, descriptions, categories, availability and repeatability where
  supported by the native client and server.
- [ ] Build a structured requirements editor: squad size, rating, chemistry,
  quality/rarity, league, nation, club and other requirements already supported
  by the server. New requirement types need backend/client evidence first.
- [ ] Configure individual and group-completion rewards, using the shared reward
  controls below.
- [ ] Preview requirements and rewards before staging.
- [ ] Stage, validate and publish through Changes, with conflict/history support.
- [ ] Disable/retire content without deleting club completion or reward history.

**Backend work:** the authored catalogue loads from
[SbcCatalog.cs](ultimateteam/server/src/Aurora21.UltimateTeam.Core/Catalog/SbcCatalog.cs)
and bundled `sbc.json`. The separate `rules/sbc.json` configures translation and
reward mappings; editing it does not create catalogue groups/challenges.
Add persistent catalogue editing and a content area integrated with the existing
snapshot/publication pipeline.

**Required first:** replace list-position-derived numeric IDs with persistent,
stable IDs while preserving the IDs of existing groups/challenges. Inserting,
reordering or retiring an SBC must not change the meaning of saved club progress.
Inspect saved submissions and completion references before designing the migration.

**Acceptance:** isolated fresh/upgrade tests preserve existing progress; a new
group and challenge can be staged/published; submissions validate requirements,
consume the correct items and award only as permitted by repeatability.
Verify display and completion in FIFA separately.

## 2. Modes and rewards — requested, design agreed 2026-10-03

Progress and evidence: [panel-update-status.md](panel-update-status.md).

Content → Modes: one tab per mode (Champions, Rivals, Squad Battles, Draft)
showing that mode's settings and its reward tables. Objectives and Season Pass
are section 3 and reuse the shared reward model and editor from here.

### Agreed design

- **One reward format** for every mode: coins, FC Points, XP and a list of
  lines. A line is a pack/pick or a player card, with a count, its own
  tradeable/untradeable choice and an optional grant source (`RewardSources`).
- **Each line's tradeability is what pays** (owner, 2026-10-03). Today Rivals,
  Champions and Squad Battles ignore the per-pack `untradeable` field and use
  only `rules/tradeability.json` (source switches, `packs` and `sourcePacks`
  overrides); Draft and objectives combine both. For migrated rewards the line
  decides alone, and the reward screens show the same value. Each migration
  writes the effective value the current rules produce
  (`Tradeability.Untradeable(source, pack)`), so payouts do not change.
  `tradeability.json` still governs grants outside the reward tables (store,
  picks, pity, gifts, match packs, the operator default). Developer Cards stay
  forced untradeable.
- **Player cards in rewards** (owner request, 2026-10-03): a line names a
  catalogue card (card key `resourceId:rating:rareflag`, as the panel's grant
  uses) and is delivered to New Items with the existing grant path
  (`BuildPlayer` + `MarkReward`, as `GrantCardAsync`). Refuse exclusive/grant-only
  cards (they exist once on the server). Decide and test what happens when New
  Items is full (the panel grant refuses above 100). SBC and objective screens
  already render item tiles (`awards/sbc-player`); whether the Champions, Rivals
  and Squad Battles prize screens and claims show and accept a player tile is
  **unconfirmed**. Enable card lines for a mode only after its FIFA check passes.
- **Rewards are relational tables**, not rules JSON: a shared reward table plus
  its pack lines, with a foreign key to `store_packs(pack_id)`, so a reward
  cannot name a missing pack and a pack a reward uses cannot be deleted. Rewards
  get stable ids for SBCs and objectives to reuse later.
- **Each mode keeps its own qualification structure** in its own tier table
  referencing rewards; one generic tier table would be mostly mode-specific columns:
  - Champions: ranks by wins (must cover 0..`maxMatches`, at most 12 client
    tiers), six Top bands that need `positionsNeedWins`, their places set by the
    Top players number (`seasons.json` `weekendLeague.topX`, 25); band sizing
    (`leaderboardBands`) was removed by migration 130. Per-Weekend-League `eventOverrides` were
    removed by migration 125 (no scheduling; owner accepted that test weekend
    2961's Rank 1 pays 541 instead of 540 if this deploys before it settles).
  - Rivals: division × rank (I–VI) × 1–4 alternative reward options, plus the
    one-off promotion reward per division.
  - Squad Battles: ranked Top-N tiers, then percentile or points tiers.
  - Draft: by wins, a **weighted list of fixed rewards**, one drawn per run;
    single-player keeps coin multipliers and the Legendary+ pack replacement.
- **Mode settings stay validated JSON** (weekly score, rank shares, schedules,
  entry costs, leaderboard band sizing), edited on the same Modes tab.
- **No scheduling.** A published edit changes the current values. Rivals,
  Champions and Squad Battles take rewards from the rules when a week/event
  settles, so an edit also applies to the week in progress and FIFA's prize
  screens show the new values at once (accepted 2026-10-03). Settled results
  keep the copy of the reward they stored.
- **Draft draw:** FIFA does not show Draft rewards, so the server may choose
  freely. Draw once from the run's seed when the run ends and store it with the
  run, as `pendingCoinReward` is stored today. Packs are currently recomputed
  from the rules at claim (`DraftService.PayRunAsync`); that must stop.
- Edits stage, validate and publish through Changes with history/revert, as
  relational pack edits do.

### Order — one mode at a time

1. [ ] Shared reward tables, content area and validation; shared React reward
   editor with pack/pick names and links. Champions moves first: migrate
   `champions_rewards.json` ranks and bands unchanged, switch
   Weekend League settlement and the hub prize display to the tables, remove the
   JSON reward section, add the Champions tab.
2. [x] Rivals: weekly division/rank options and promotion rewards (migration 131; Modes →
   Division Rivals). Browser and FIFA checks still to do (see panel-update-status.md).
3. [x] Squad Battles: six Top slots placed by the Top players number (`competition.boardSize`,
   like FUT Champions, owner 2026-10-03; no band sizing), twelve points tiers, per-line
   tradeability (migration 133; Modes → Squad Battles). Browser and FIFA checks still to do.
4. [ ] Draft: weighted reward choices and the run-end draw described above.

Each step: migration preserving every current value, payout reading the tables,
isolated fresh/upgrade tests, unchanged client wire output (Champions client tier
numbers, Rivals options, prize screens), browser review, then FIFA checks.

**Safeguards:** reject invalid counts and overlapping or incomplete rank ranges.
Settled results remain readable in their stored shape (Squad Battles stores
`{type, amount}` / `{type, packId, count}` awards). Preserve settled/claimed rewards
and prevent duplicate payouts. Champions reds (550/552/553) must remain restricted to
Champions rewards. Developer Cards remain untradeable. History entries holding the old
rules-JSON reward sections must not be revertible into a migrated mode.

**Other reward sources** (placement, match coins, SBC completion, welcome and
dated gifts) move onto the shared format with their own sections; do not create
two independent definitions for the same reward.

**Retire `rules/tradeability.json` (owner, 2026-10-03)** once everything has moved:
Champions, Rivals and store listings with their own choice no longer use it. Still
using it: Squad Battles, Draft, SBCs, objectives/Season Pass, gifts, Match Packs,
player picks, pity and guaranteed-inform grants, the panel grant's default checkbox,
and store listings without their own choice. Each needs its own tradeability (a
reward line, a listing, or a per-pack/per-feature setting) before the file and
`TradeabilityRules` are removed.

## 3. Objectives and Season Pass — first version built 2026-10-04 (see panel-update-status.md, step 7)

- [ ] Add a dedicated page for campaigns, objective groups and individual objectives.
- [ ] Edit titles, targets, tracked statistics, XP and individual/group rewards.
- [ ] Include a Season Pass editor for Season Progress levels, XP thresholds
  and reward choices.
- [ ] Include welcome objectives and dated gifts already supported by the backend.
- [ ] Stage and publish through Changes using existing objective rules where suitable.
- [ ] Add server safeguards before exposing creation/removal: stable IDs,
  no reuse of retired identities, preserved claim history and supported stat names.
- [ ] Warn about retroactive completion and changed targets: counters currently
  accumulate over club progress, rather than automatically starting at creation.

The generic Rules page can edit objective data today. A dedicated editor and
these creation/impact safeguards remain to be built. Season Pass here means
FIFA's Season Progress XP/reward track within objectives, rather than a new
gameplay mode.

## 4. Other content editors — candidates, scope not yet agreed

These are possible later additions, not requirements to build immediately:

### Managers / staff

- [ ] Browse/search managers with portraits and nationality/league filters.
- [ ] Edit supported manager metadata, such as league, nationality and rarity.
- [ ] Configure pack availability and selection weights.
- [ ] Preview and grant a manager to a club, with tradeability controls.
- [ ] Support catalogue additions only with verified native IDs and metadata.

### Consumables

- [ ] Browse supported healing cards, chemistry styles and position modifiers.
- [ ] Show item effects, rarity, quality and pack eligibility clearly.
- [ ] Configure pack availability and selection weights. For example, make
  Hunter and Shadow rarer than other chemistry styles.
- [ ] Preview and grant consumables to a club, with quantity/tradeability controls.
- [ ] Validate changes against the native item contract and application behavior.
  Contracts/fitness removal is intentional; do not reintroduce them. Loan match
  limits remain separate from removed contract consumables.

### Club customisation

- [ ] Browse/search kits, badges, balls, stadiums and supported stadium items,
  with image previews and club/league filters where applicable.
- [ ] Enable/disable items in packs and configure selection weights.
- [ ] Preview and grant an item to a club, with tradeability controls.
- [ ] Edit supported catalogue metadata and import new items only with verified
  native metadata and the required assets.
- [ ] Preserve owned copies and equipped items when removing pack availability.

### Other candidates

- [ ] Mode configuration: structured controls for entry costs, schedules, rank
  thresholds, Squad Battles opponents and Draft selection weights. Keep these
  distinct from reward payouts; generic Rules already exposes underlying values.
- [x] Broader promo management: create reusable groups and add/remove existing
  native card versions, including draft scheduling (2026-10-07).
- [ ] Chemistry/stadium gameplay tuning only if there is a concrete need.

Audit catalogue storage, native metadata and live snapshot consumers before
promising these as frontend-only additions.

## 5. Complete existing panel review

### Deferred: refresh published cards inside FIFA

- [ ] Investigate a safe refresh of cards already loaded in a squad after panel
  publication, without removing cards or altering saved squad lineups.
- [ ] Investigate refreshing card images without leaving Ultimate Team.
- [ ] Show publication guidance explaining that squad cards and images may
  require leaving and re-entering Ultimate Team.

**Observed in game (2026-10-01):** club-only cards refresh rating, stats and card
type after editing, but their images require leaving and re-entering FUT. Cards
already in a squad also require FUT re-entry for their edits to appear. Fresh
server squad responses already contain published stats, as verified by an
isolated regression; the loaded client state still needs to refresh.

**Future investigation:** the existing Blaze `UserUpdated` notification is a
candidate to test, not a verified squad-refresh mechanism. Check whether FIFA
requests fresh squad data and actually updates the displayed cards after it.
The pre-match attribute-version handshake handles outdated stat records; it
does not establish refresh support for card types or images. Keep this work
deferred and retain FUT re-entry as the current workaround.

- [ ] Test Stop while startup is pending and after services are ready. Confirm
  startup stays cancelled and the launcher shows stopped services. Also verify
  direct FIFA launch with replacement LSX; no live launch was run for this fix.
- [ ] Retest recent Access, Matchmaking and Logs alignment fixes in the browser.
- [ ] Review packs and pack odds v3 (docs/pack-odds-v3.md, migration 119) in the browser and
  in FIFA. Browser: create a pack and a pick (unassigned, pack art, odds filled from the
  template), the usage filters (Store + Rewards, Store, Rewards, Unassigned), Grant only, the
  upgraded row on silver/bronze packs, a view-only pack, deleting an unused former reward pack,
  the lineup refusing an unpriced pack, the Events odds step after staging an event, and old
  `#/packs/store`, `#/packs/reward`, `#/packs/picks` and `#/store` addresses. FIFA: a new pack's
  tile shows its chosen art in the store and My Packs; bronze/silver store packs can now give
  TOTW and upgraded specials; reward packs open with their new odds; a former reward pack added
  to the lineup can be bought. None of this has been checked in game yet.
- [ ] Retest pack odds controls: only current event/icon categories and explicitly
  selected promos appear; unavailable saved odds remain visible in a warning.
  Special player requirements now use one control (none, minimum count, all).
  Player packs clear legacy manager/chemistry replacement chances when staged.
  Category odds always show, with Icon/TOTW labels and separate inputs for other
  selected promos. Totals above 100% block staging and preview. Eighteen focused
  UI tests and the panel build passed on 2026-10-01. For the event
  empty-pool test use TOTW 100% plus All players special; category odds alone can
  fall back to a standard rare when the selected category is empty.
- [ ] Test store purchase limits in FIFA: stage/publish a limit of 3, check the
  remaining count displays 3 → 2 → 1 and the tile disappears after purchase 3.
  Verify reopening the store and restarting preserves usage, another club has
  its own allowance, and a gifted copy still opens after exhausting the limit.
  Zero restores unlimited purchases; limits are lifetime per club/pack and count
  purchases made since migration 105, including purchases while unlimited.
  PARTIAL — maintainer confirmed enforcement works on 2026-10-01, but the count
  did not display, including after the availability-field correction. The
  maintainer's 2026-10-01 18:39 screenshot confirmed `Packs Remaining:` displays
  after adding the promo/quantity sale classification, but also exposed a bogus
  community limit. Per-club tiles now advertise no finite global stock. The
  maintainer subsequently confirmed both exclusive limit modes display
  `Packs Remaining:` correctly, without the extra community label. Remaining
  checks above cover the countdown, exhaustion, persistence and club isolation.
- [ ] Test community store allowances: stage/publish a community limit, confirm
  the per-club limit is cleared, and verify stock is shared by clubs, persists
  across restarts, and cannot oversell the last pack. Gifts/rewards and failed
  draws/payments must not consume stock. Native display passed the maintainer's
  game test on 2026-10-01: this mode also uses `Packs Remaining:`. Cross-club
  enforcement and persistence still need live confirmation. Migration 107 adds the shared counter;
  its counts start at that migration and are retained when changing limit mode.
- [ ] Add limited-time store packs: editable start/end dates, server enforcement
  at listing and purchase, and FIFA's native expiry countdown. Keep time windows
  independent from each club's purchase allowance and preserve already-owned packs.
- [ ] Add preview packs matching FIFA 21's native store flow: preview contents
  without charging or granting them, persist the preview and cooldown per club,
  and purchase exactly the previewed contents once. Verify preview artwork,
  buttons, expiry and replay/payment handling in FIFA before enabling them.
- [ ] Verify configured FIFA Points prices display and a Points purchase debits
  exactly the configured amount. Migration 106 enables the shared Points
  purchasing rule (store and Draft); it adds no Points or daily Points grants.
- [ ] Review each existing page against the
  [browser checklist](ultimateteam/panel-web/README.md#browser-review), recording
  acceptance and any failures separately from automated test results.
- [ ] Verify live editing end to end in an isolated database: stage, validate,
  publish, history/revert, conflicts, external SQL warning and explicit reload.
- [ ] Review Players → Overview → Reset club in the disposable panel database,
  then verify that the player repeats the full onboarding flow in FIFA. Keep
  browser, isolated API and native-game evidence separate.
- [ ] Review migration 120 desirable-card controls in the temporary panel: mark,
  change the reduction, unmark, move buckets, publish and revert. Confirm Gold
  Common has no desirability controls, then check pack opening in FIFA separately.
- [ ] Retest Season Progress level redemption in FIFA: compare coins/My Packs
  before and after a repeat claim and after re-entering UT. A reproduced concurrent
  progress update could erase saved claims; updates now lock before reading state.
  The reported repeat animation versus actual duplicate payout remains unconfirmed.
- [ ] Guard unsaved editor drafts when navigating through shared navigation.
- [ ] Verify recent migrations on fresh/upgrade test databases and confirm the
  rebuilt review runtime uses the expected build before checking their UI effects.
- [x] Fix the stale offline pack scenario: `PacksAndCustomisationRoutesUseRelationalPacks`
  passed on 2026-10-02 using a synthetic test pack; consumable assertions match
  migration 116's tradeable stacks. Removed testing packs remain absent from production.
- [x] Run the full regression suite before the next code commit: 953 tests,
  921 passed / 32 failed on 2026-10-02. Corrected retired-pack fixtures, historical
  migration replays, duplicate-card counts and published Pro-card expectations.
  Focused rechecks: 61 passed / 3 failed, then all final three corrections passed
  (39 seconds). Every original full-suite failure now passes in focused rechecks;
  the full suite was not rerun after these test-only corrections.
  See `outputs/verification/merge-develop-20261002/` for TRX results.
- [ ] Record refresh behavior for new content features; existing card/squad refresh
  limits are documented above and in the game acceptance record.

### Recorded acceptance

The completed local FIFA checklist is in [game acceptance](ultimateteam/panel-web/test/game-acceptance.md).
It covers grants, picks, card editing/creation, store edits, event availability,
history/revert and persistence. Store-limit display passed for both modes; broader
shared-stock tests remain above. Do not repeat these tests just to regenerate a log.

### React replacement: remaining release checks

- [ ] After rebuilding/restarting, review the React panel at `/panel/`, including
  an old `/panel/next/` bookmark and desktop/narrow navigation.
- [ ] Check private SSH access and the real panel hub framing path. The server's
  frame-denying headers remain; tunnel access alone does not prove iframe support.

## Suggested implementation order

1. Shared reward tables, validation and editor, with Champions as the first mode
   (section 2).
2. Remaining modes one at a time: Rivals, Squad Battles, Draft.
3. SBC persistence/stable IDs, then the SBC groups/challenges page.
4. Objectives and Season Pass, on the shared reward model.
5. Selected catalogue editors as needed.
6. Remaining browser and hosted acceptance checks.

For every new module: trace current behavior, agree its exact scope, implement
backend and UI together, run focused checks, and complete browser review before
marking it done. Proposed routes/menu layouts are not implemented by this document.
