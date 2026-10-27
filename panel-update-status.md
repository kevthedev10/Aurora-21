# Panel update status: Modes and rewards

Working log for the Modes and rewards work on branch `feat/panel-shared-reward-controls`
(from `develop` at `18be8c6f`). The agreed design and remaining scope are in
[todo.md section 2](todo.md#2-modes-and-rewards--requested-design-agreed-2026-10-03).
This file records what has changed, the evidence for it, and what is still open.
Nothing here is committed yet; the maintainer commits.

Updated 2026-10-03.

## Decisions (owner, 2026-10-03)

- One reward format for every mode: coins, FC Points, XP and lines (pack/pick or
  player card) with count, tradeable/untradeable and optional grant source.
- Each reward line's own tradeability is what pays; migrations keep the value
  that pays today.
- Player cards may be reward lines; enable them per mode only after a FIFA check
  of that mode's prize screen and claim.
- Rewards and per-mode tiers become database tables; mode settings stay
  validated rules JSON. One Content → Modes page with a tab per mode.
- No scheduling: published edits change the current values, including the week
  in progress. Settled results keep their stored reward.
- Draft: each win count gets a weighted list of fixed rewards, drawn once when
  the run ends and stored with it (FIFA does not show Draft rewards).
- Order: Champions, Rivals, Squad Battles, Draft. Objectives and Season Pass are
  a separate section (todo.md section 3).
- Champions `eventOverrides` removed. Accepted: if this deploys before test
  weekend 2961 settles (Monday 2026-10-05 17:00 UK), its Rank 1 gets 541, not 540.
- New rule: Weekend League matches counting toward the Rivals Weekly Score can be
  switched off.

## Done

### Champions event overrides removed; Weekend League → Rivals switch

- `ChampionsEventOverride` and `ChampionsRewardRules.EventOverrides` removed;
  `Reward(rank)` and `Standings(...)` no longer take an event id
  (`Rules/RivalsRewardRules.cs`, `Services/Modes/WeekendLeagueService.cs`).
- `champions_rewards.json`: `eventOverrides` and its readme line removed.
- New `rivals_rewards.json` `weeklyScore.weekendLeagueCounts` (default `true`,
  today's behaviour). Off: a Weekend League match adds no Weekly Score and uses
  none of the 30 counting matches (`RivalsService.CountWeekendLeagueMatchAsync`).
  Editable on the Rules page now.
- Migration `125-champions-without-event-overrides.sql`: deletes the stored
  `eventOverrides` rows and both files' `_readme` (startup re-adds them and the new
  key from the bundled files). Registered in `RelationalCatalogMetadataMigration`
  (`CurrentVersion` 125) and the Core `.csproj`.
- Tests (`WeekendLeagueTests`): reward table test updated; the 2961 claim test is
  now `TheTestWeekendsRankOneIsPaidTheRankOneReward` (541); new
  `AWeekendLeagueMatchAfterTheWeeksCountingMatchesAddsNoWeeklyScore`,
  `WithWeekendLeagueCountsOffAMatchLeavesTheRivalsWeeklyScoreAlone`,
  `AnUpgradeDropsTheChampionsEventOverridesAndAddsWeekendLeagueCounts`.

Evidence: focused run of `WeekendLeagueTests`, `RivalsSkillTests`,
`RivalsRewardTests`, `StaffPrizeTests`: 39 tests, 38 passed, the one failure
being the old 540 claim test (the intended change); after rewriting it, that
test passed on its own.

### Documentation

- `todo.md` section 2 rewritten with the agreed design and order; implementation
  order updated. `ultimateteam/panel-web/README.md` wording matches.

### Step 1, part 1: shared reward tables, Champions backend data

- Shared reward model `Rules/Rewards.cs`: `Reward` (coins, FC Points, XP, lines) and
  `RewardLine` (pack or player card, count 1-100, own untradeable, optional source);
  `Reward.Problem(cards)` validates and refuses card lines where not yet allowed.
- Migration `126-mode-reward-tables.sql`: tables `rewards`, `reward_lines` (FK to
  `store_packs`, so a pack in a reward cannot be deleted) and `champions_reward_ranks`
  (tier_level as stable id, wins or positions range, reward). Creates tables only.
- Start-up fill (`ModeRewardRepository.FillAsync`, run right after the rules fill in
  `GameContext`): once, when the table is empty. Upgraded database: converts the stored
  `champions_rewards` ranks (operator edits kept), each pack line with the tradeability
  it was paid with (`tradeability.json`, source `weekend_league`), and deletes those
  rule rows. Fresh database: bundled seed `runtime-source/ultimateteam21/rewards/champions.json`
  (generated from the old ranks; every flag already matched what paid).
- `champions_rewards.json` keeps only settings (`minMatchesToRank`, `positionsNeedWins`,
  `fcPointsCap`, `leaderboardBands`). `ChampionsRewardRules.WithRanks` builds the table
  from the ranks with the same validation as before plus reward-line checks.
  `GameRules.FromDocuments` takes the ranks; the content loader reads them in the
  rules' transaction; `GameRules.Load` (file-only) uses the seed.
- Champions claim and prize screens use each line's own untradeable
  (`RenderOption(..., ownTradeability: true)`, `AddUnopenedPackAsync(..., untradeable)`).
  Results settled earlier stored the old JSON flag, which matched `tradeability.json`
  for every Champions pack in the bundled files (inferred to hold on live servers
  unless an operator changed those flags).
- Rules editor refuses edits to `champions_rewards` `ranks` / `eventOverrides` with
  a message pointing to Modes → Champions.
- Panel pack usage and the pack delete check also list reward-table uses
  ("Champions: Rank 1").
- `STORAGE-COVERAGE.json` +3 tables; `RelationalMaintenanceTests` count +3.
- `Test-RepositoryHygiene.ps1` requires `rewards/champions.json` (it must be staged in
  git before that check passes).
- Tests: new `ModeRewardTests` (fresh seed, upgrade conversion with an edited value
  and a stale flag, fill-once, pack uses + FK delete refusal, reward validation);
  `WeekendLeagueTests.EachRewardLinesOwnTradeabilityIsWhatPaysAndShows`.

### Step 1, part 2: Champions editing backend

- Content area `champions_rank` (`Core/Content/ModeRewardAreas.cs`, registered in
  `ContentAreas.Default`, apply order 30: after packs, before rules). Key
  `{tierLevel}`; value is the whole rank `{name, wins|positions, reward}`. Staging
  checks the rank alone (name, range, reward lines, no XP, no cards, no source);
  publishing checks packs exist and loads the whole table (coverage 0..maxWins,
  at most 12 win ranks, bands from position 1). Hide removes a rank; revert works.
- `GET /panel/api/content/modes/champions`: settings, maxWins, limits and the
  published ranks with FIFA's tier numbers.
- Migration 126 also adds the content-revision triggers to the three tables, so the
  panel's "changed outside the panel" warning covers them.
- `panel-api-contract.md`: new "Modes and rewards" section.
- Tests: `Core/OperatorPanelModeTests.cs` (read, edit, publish, revert; refusals for
  cards, XP, count 0, tierLevel 0, a missing pack, an uncovered table, and a Rules
  edit of the old `ranks` path).

### Step 1, part 3: panel page

- Content → **Modes** (`#/modes/champions`), menu entry after Events. The classic
  `#/modes` redirect to Players is gone (that view is Players → club → Modes).
- `src/ui/RewardEditor.tsx`: shared editor (coins, FC Points, XP when the mode allows,
  pack lines by name with count, Tradeable/Untradeable and an "Open pack" link),
  `rewardProblems` and `rewardSummary`.
- `src/api/modes.ts`: types, canonical rank shape, merging staged changes, the
  whole-table checks shown before publishing, stage rank / stage setting.
- `src/pages/modes/ModesPage.tsx`: settings (5 fields, staged as `champions_rewards`
  rules with a changed-meanwhile check), Top 200 bands and ranks by wins with reward
  summaries, tier numbers and staged badges, Add rank, Edit dialog, Remove and Undo.
- Changes page labels the area "Champions rewards" and keys as "tier 16". The Rules
  page calls `champions_rewards` "Champions settings".
- `panel-web/README.md`: page table and browser checklist.
- Tests: `src/pages/modes/modes.test.tsx` (7); routes/App menu tests updated.

Evidence: `npm run typecheck` passed; vitest `src/pages/modes`, `src/app`,
`src/pages/rules`: 6 files, 38 tests passed.

### Step 1 test evidence (2026-10-03)

- Focused .NET run (`ModeRewardTests`, `WeekendLeagueTests`, `RivalsSkillTests`,
  `RivalsRewardTests`, `StaffPrizeTests`, `RuleRowsTests`, `RelationalMaintenanceTests`,
  `DiscardRuleUpgradeTests`, `DraftOddsTests`, `PackOddsV2MigrationTests`,
  `RelationalBootstrapTests`): 101 tests, 99 passed. The 2 failures were test-side and
  fixed: the coverage count literal was already one behind the inventory before this
  work (103 entries, 102 expected), and the event-overrides upgrade test had to rewind
  the ledger from 125, not only 125, now that 126 exists.
- Rerun after `npm run build` (embedded bundle rebuilt and stamped): the 2 fixed tests,
  `ModeRewardTests`, both `OperatorPanelModeTests`, the pack usage/delete panel tests,
  `PanelBuildTests` and others: 19 tests, 19 passed.
- Frontend: typecheck passed; 38 vitest tests in `src/pages/modes`, `src/app`,
  `src/pages/rules` passed.

### Champions Top 25 with a client mod (owner's own change, 2026-10-03)

Made by the owner, not by Claude; recorded from the files, **not yet re-tested by Claude**.
It supersedes the Top 200 layout and the 300 minimum in the section below.

- A client mod makes FIFA show a Top 25 and its leaderboard (`Resources/client/leaderboard-ui.json`,
  `Http/LeaderboardUiManifest.cs`: the `FUT_Champions_TopX_upper` caption takes `topX`).
- `seasons.json` `weekendLeague.topX` = 25 is the "Top size" (6 to 200, `Rules/LeaderboardSizes.cs`).
  The six band slots are fixed: Rank 1, then the rest split over five slots
  (`LeaderboardSizes.ChampionsBands`): for 25, Rank 1, 2-5, 6-10, 11-15, 16-20, 21-25.
- Changing the Top size (Modes page setting, a `seasons` rule) rewrites the six slots'
  positions and names when published (`RulesArea.ApplyAsync`). The panel can no longer
  edit positions, add or remove band slots (`ChampionsRankArea.ValidateAsync`).
- No scaling: `leaderboardBands` reference and minimum are both 1 and no fixed weekend sizes.
- Seed `rewards/champions.json` and the one-time conversion (`SplitTopBands`) produce
  Top 25 directly; migration `127-champions-top25.sql` converts databases that already
  hold the six default Top 200 ranges (operator-defined ranges are left alone), sets
  `topX` 25 and the band settings to 1, and refreshes the readmes.
- Stable tierLevels and each slot's reward are kept (1, 2, 3, 17, 4, 18 by position).

### Step 3: Squad Battles (2026-10-03)

Owner: the Top reward tiers work like FUT Champions: six slots whose places follow the Squad
Battles Top players number (`squad_battles.json` `competition.boardSize`, 25 → 1, 2-5, 6-10,
11-15, 16-20, 21-25), no band sizing; the Top players number is in the Squad Battles settings.

- Seed `rewards/squad_battles.json`: six Top slots (`rank1`, `top20`, `top40`, `top100`, new
  `ranked5` copying top100's prize, `top200`; ids kept because settled results name them) and
  the twelve points tiers; coins become the reward's coins, packs keep their order; each
  line's untradeable is what `tradeability.json` paid for `squad_battles` (all tradeable).
- `sqbt_rewards.json` keeps the settings (version, mode, settleDelaySeconds,
  minPointsToQualify, rankedTiers); `tiers` and `leaderboardBands` go. Migration
  `133-squad-battles-reward-tiers.sql`: table `squad_battles_reward_tiers` (+ revision
  trigger), Top size raised to at least 6, readme refresh. Start-up `FillSquadBattlesAsync`
  converts stored tiers once (five Top tiers → six slots placed by the stored Top size) or
  seeds a fresh database.
- Model: `SquadBattlesRewardRules.WithTiers` (exactly six Top slots, the old table checks,
  coins and packs only); `RankedBands()` are the stored places; `LeaderboardBandRules` and
  the settled-participants lookups are gone (no band sizing left anywhere).
  `SquadBattlesAward` carries `untradeable` (results settled before read as tradeable,
  which is what they paid).
- Payout pays each pack's own tradeability; the prize tiles now show it. **Visible change:**
  Squad Battles prize tiles used to show every pack as untradeable (the plain pack tile);
  they now show the real value. Check in FIFA.
- Top players: minimum 6 (`SquadBattlesRules`, the leaderboards view); publishing a new number
  re-places the six slots (`RulesArea.ApplyAsync`, as the owner's Champions code does).
- Content area `squad_battles_tier` (update only; Top slot places and points tier levels
  fixed; points percent band and minimum editable); `GET /content/modes/squad-battles`;
  Rules editor refuses old `tiers`/`leaderboardBands` edits; pack usage lists
  "Squad Battles: Rank 2-5" / "Squad Battles: elite1".
- Panel: Modes → **Squad Battles** (`SquadBattlesTab.tsx`, `api/squadBattlesMode.ts`):
  settings (Top players with Change…, tiers by percentile/points, minimum points to qualify,
  clubs before Top slots pay, Top slots on/off), the six Top slots (staged Top size previews
  their places), the twelve tiers; edit dialogs.
- Tests: `SquadBattlesModeRewardTests` (fresh seed; upgrade from five Top tiers with a Top
  size of 30); `SquadBattlesTiersAreReadEditedAndPublished` (panel); existing tests updated:
  six slots instead of Top 200 and scaling, the prize table with `ranked5`, the hub's 6+12
  tiers, slots not following the field, and the owner's board-size test (slots move, prizes
  kept, 5 now invalid). Frontend: `squadBattles.test.tsx`; Modes vitest 25 passed; typecheck
  passed. .NET run (Squad Battles, reward tables, Champions/Rivals/Squad Battles panel tests,
  leaderboard, coverage, build check, staff prizes, Weekend League, rule rows): 70 tests, 69
  passed; the failure was the settlement test still expecting second place on Silver 3 (the
  old band sizing left only Rank 1 after an empty week); now Rank 2-5, then it passed.

### Packs: store listing (2026-10-03)

Owner: the store listing shows only listed packs, "+" adds any available pack, listed packs
can be switched on/off (sold only when listed *and* on), and tradeability is chosen when
listing a pack (not when making one; there was no such field, owner confirmed).

- Migration `132-store-listing.sql`: nullable `store_listed` and `store_untradeable` on
  `store_packs` and `owned_legacy_pack_definitions` (the pack configs read both tables);
  today's lineup becomes the listing, switched on. `enabled` stays the effective "sold"
  flag, so every existing reader is unchanged.
- Purchases: a listing's own `store_untradeable` decides; null keeps
  `tradeability.json`'s store switch (so nothing changes until a choice is made).
- `store_lineup` area value is now `{revision, listing: [{packId, enabled, untradeable}]}`;
  an older change's `publicPackIds` (pending or reverted from History) still applies (listed
  and on, tradeability kept). `PUT /packs/lineup` accepts either; the catalogue adds
  `listing` and per-pack `listed`. Required packs must stay listed and on; grant-only packs
  cannot be listed; a switched-on pack needs a price.
- Panel: Packs → Store listing (`LineupTab.tsx`, `listing.css`): listed packs only, each
  with On/Off, Tradeable/Untradeable (legacy listings show "Store default" until changed)
  and Remove; "+ Add pack" dialog (pack, tradeability, switched on). Fixed a render loop:
  the draft resets only when the listing's contents change.
- Tests: vitest "lists only listed packs, adds one…" and "removes a pack…" replace the old
  lineup test (Packs: 29 passed). Server `TheStoreListingSellsListedSwitchedOnPacksWithTheirOwnTradeability`
  (migration backfill, switched-off listed pack not sold, untradeable purchase, unlisting,
  required pack, old client format). .NET run (all OperatorPanelTests, store purchase
  limits, relational store packs, reward tables, migration replays, RelationalBootstrapTests,
  tradeability, PanelBuildTests): 146 tests, 146 passed.

### Step 2: Division Rivals (2026-10-03)

- Seed `rewards/rivals.json` (all ten divisions' six ranks of options, and promotion);
  every line's untradeable is what paid before: weekly options by their option source in
  `tradeability.json` (fixed, A, B, C), promotion the pack flag or the `rivals_promotion`
  switch. Checked against the bundled files: no flag differed from what paid.
- `rivals_rewards.json` keeps the settings only. Migration `131-rivals-reward-tables.sql`:
  `rivals_reward_options` (division, rank, option 1-4 → reward) and
  `rivals_promotion_rewards`, with content-revision triggers. Start-up
  `FillRivalsAsync` (once): converts stored `divisions`/`promotion` rules (operator edits
  kept, line sources kept) and removes them, or seeds a fresh database.
- Model: `RivalsRewardRules.WithRewards(RivalsRewardTables)` (same checks as before plus
  reward lines, no XP, no cards); `Divisions`/`Promotion` keep their types, so payouts and
  tests read them as before. Rule loading now takes `ModeRewardTables` (Champions and
  Rivals); `GameRules.Load` uses both seeds.
- Payouts: the weekly claim and prize screens pay/show each line's own tradeability,
  still granted under the option's source; promotion pays the line's own choice.
  Settled weeks keep the options they stored (same flags).
- Content areas `rivals_rank` (a rank's 1-4 options) and `rivals_promotion`, update-only;
  `GET /content/modes/rivals`. Rules editor refuses old `divisions`/`promotion` edits.
  Pack usage lists "Rivals: Division 3 Rank II" / "Rivals promotion: Division 1".
- Panel: Modes → **Division Rivals** tab (`RivalsTab.tsx`, `api/rivalsMode.ts`): settings
  (Weekend League switch, win/draw/loss, per goal, goal cap, counting matches, Weekend
  League match score, pool below clubs; rank shares as one list that must total 100),
  weekly rewards by division (chips) with each rank's options (edit, add up to 4, remove),
  and the ten promotion rewards. Changes page labels "Rivals rewards" and
  "Division 5 › Rank II".
- Coverage +2 tables; hygiene requires `rewards/rivals.json` (stage it in git).
- Tests: `RivalsModeRewardTests` (fresh seed, upgrade conversion incl. a stale flag and
  an edit, fill-once, pack labels), `RivalsRewardsAreReadEditedAndPublished` (panel);
  existing Rivals payout tests unchanged and passing; Champions upgrade test setup now
  deletes only Champions rewards; pack-usage test expects the new label. Frontend: 6 new
  vitest tests; 41 passed in `src/pages/modes` + `src/app`; typecheck passed; bundle
  rebuilt. Evidence: focused run 57 tests, 54 passed, the 3 failures test-side and fixed;
  rerun of those, the Rivals panel/table tests, Champions panel tests, coverage and
  `PanelBuildTests`: 11 tests, 10 passed, the panel test failing on its own key reuse;
  fixed, it then passed.

### Band sizing removed; single places shown as one number (2026-10-03)

- Owner: remove the band reference/minimum settings from the page and from the rules.
  `ChampionsRewardRules.LeaderboardBands` is gone; `Bands()` returns the six slots' places
  exactly as the Top size set them, numbered 1-6 by position. `WeekendLeagueService` no
  longer sizes bands from the previous weekend's ranked clubs (`BandsAsync` →
  `Bands()`, prize tiers and Top positions no longer async;
  `ChampionSettledParticipantsAsync` removed, settlements still store their count).
  Squad Battles keeps its own `leaderboardBands`.
- `champions_rewards.json`: `leaderboardBands` and its readme lines removed. Migration
  `130-champions-no-band-sizing.sql` deletes the stored rows and the readme (owner's 128
  and 129 already existed; `CurrentVersion` 130). The Rules editor refuses an old edit of
  `champions_rewards` `leaderboardBands` with a message.
- Panel: "Band reference field" and "Band minimum field" removed; the reply no longer
  carries `leaderboardBands`.
- Panel: a single place or win count shows as one number: "Top 1" (not "Top 1–1"),
  "18 wins", "1 win", and "Leaderboard place 1" in the edit dialog (`span`,
  `rankRangeText` in `api/modes.ts`).
- Tests: `TopBandsAreTheTopSizesPlacesWhateverTheField` and
  `TopBandsDoNotFollowTheFieldAndArePaidAtSettlement` replace the two scaling tests; the
  owner's Top 25 upgrade test checks no `leaderboardBands` rule remains. Frontend:
  typecheck, 35 vitest tests passed, bundle rebuilt. .NET focused run (Weekend League,
  ModeReward, Champions panel tests, staff prizes, rule rows, build check): 42 tests,
  41 passed; the one failure was the owner's `SquadBattlesTop25Upgrade...` test, which
  rewound only ledger row 129 (with 130 present it no longer re-ran); changed to
  `>= 129`, both cases then passed.

### Panel: names and Top places are not editable (2026-10-03)

- The rank editor no longer has a Name field or "Ranked by" choice. A Top slot's
  dialog has no position fields: it states "Leaderboard places a–b, set by the Top size
  (25)"; only its reward is edited. Win ranks keep their wins range (needed when the
  matches per Weekend League change) and reward.
- Server: `ChampionsRankArea` also refuses a changed name ("Rank names are set
  automatically"), next to the owner's existing refusal of changed positions.
- Tests: new vitest "edits a Top slot's reward only"; the owner's "protects leaderboard
  slots" test now expects no position fields instead of disabled ones. Typecheck and
  34 vitest tests in `src/pages/modes` + `src/app` passed; embedded bundle rebuilt.
- Server: Champions panel tests, the matches test and `PanelBuildTests`: 4 passed.
- "Top players" is now the first tile of the Champions Settings grid (read-only number,
  hint about the six slots, staged badge, "Change…" opens the owner's dialog) instead of
  a loose button above it. Vitest `src/pages/modes`: 12 passed; bundle rebuilt;
  `PanelBuildTests` passed.

### Six Top 200 bands, 300-player minimum, matches setting (2026-10-03, superseded for bands)

Owner decisions: six bands (Rank 1, 2-20, 21-50, 51-100, 101-150, 151-200) sized from the
previous weekend's ranked clubs with a minimum of 300 (expected 300-400 active players);
no "Add rank" in the panel; the six-band Top 200 layout stays (a Top 50 was considered).

- Seed `rewards/champions.json`: tierLevels 3 and 4 become 21-50 and 101-150; new 17
  (51-100) and 18 (151-200) copy their rewards, so every position pays as before.
- Upgrade: the one-time conversion splits the bands the same way only when they are still
  exactly the original four; operator-changed bands are kept and the log says so.
- `champions_rewards.json` `floorParticipants` 0 → 300 (fresh databases). **Existing
  databases keep 0 until "Band minimum field" is changed on Modes and published.**
- Server: at most 6 Top 200 bands (`ClientTopBands`, from a client-service code comment,
  not yet checked in game). Fixed: FIFA's band and tier numbers are now assigned by
  position and by wins, not by tierLevel (the new tierLevel-17 band would have been
  numbered after 101-150).
- Panel: Add rank removed; each rank shows "FIFA: Elite 1" / "FIFA: Band 2"; the band
  limit is in the page warning; new setting "Matches per Weekend League"
  (`seasons.json` `weekendLeague.maxMatches`), and the coverage warning follows a staged
  value so matches and win ranges can change in one publish.
- Answered: "Top 200" is data on the server (band positions; `seasons.json`
  `weekendLeague.topX` = 200 for the leaderboard and monthly place). Whether FIFA has
  "Top 200" in its own text is unconfirmed.
- Tests updated: band edges for 500/50/0/300/5000 fields, frozen bands, the entry test's
  prize tiers (5 bands at the 300 minimum), the reward table, the upgrade split and
  keep cases, matches + ranks publishing together.

Evidence: after `npm run build`, focused .NET run (`ModeRewardTests`, `WeekendLeagueTests`,
both Champions panel tests, `TheMatchesPerWeekendLeagueChangeWithTheRanksThatCoverThem`,
`PanelBuildTests`, `StaffPrizeTests`): 36 tests, 36 passed. Frontend typecheck passed;
vitest `src/pages/modes` + `src/app`: 30 passed.

## Next

- Owner, 2026-10-03: **remove `rules/tradeability.json` once everything has moved**
  (remaining users listed in todo.md section 2).


- Owner, 2026-10-03: Rivals is next. **For Squad Battles later: include the Top players
  number the owner wired in (`competition.boardSize`) in its settings, same as FUT
  Champions.**

1. Browser review in the isolated review server (`Start-Panel-Review.ps1`, see
   `panel-web/README.md`): Modes → FUT Champions checklist item.
2. FIFA checks: the Weekend League prize tiers show the published ranks; a claim pays
   each line with its own tradeability.
3. Step 2: Rivals (division × rank × options, promotion rewards, Weekend League switch
   on the tab).

## Not run / still owed

- Focused tests after the owner's Top 25 change (ModeRewardTests, WeekendLeagueTests,
  Champions panel tests, migration replays, modes.test.tsx) have not been run by Claude.

- Full solution test suite (before commit).
- `Test-RepositoryHygiene.ps1`: needs `rewards/champions.json` staged in git first.
- `verify-startup-runtime.mjs` (needs Windows Node and a published server exe).
- No live server, browser or FIFA checks for any of this yet.

---

# SBCs: catalogue, Live SBCs and settings

Working log for the SBC section on branch `panel/semi-finalisation` (from `762d1284`). Agreed with the owner
2026-10-03: SBCs work like Packs / Picks. The catalogue holds every set (bundled `sbc.json` plus panel edits);
**Live SBCs** is the list of sets the game shows, each switched on or off; `hiddenSets` and the per-set Hide go.
Then: legacy pack names to pack ids, an SBC → Settings tab for the rest of `rules/sbc.json` (removed from the
Rules page), and editor work (all reward types, formation/description, reorder, duplicate, game-text preview).
Nothing here is committed yet; the maintainer commits.

## Done

### Step 1: catalogue and Live SBCs

- Migration `141-sbc-live-list.sql`: table `sbc_live (set_key, enabled)`. Filled once with what the game showed: every
  catalogue set the stored `hiddenSets` patterns did not hide (a fresh database uses the shipped patterns), switched off
  when the panel had hidden it. Deletes the stored `hiddenSets` and `_hiddenSetsComment` rules and drops
  `sbc_sets.hidden` (the run-once marker, so a replay keeps an operator's list). Registered (`CurrentVersion` 141, Core `.csproj`).
- `rules/sbc.json`: `hiddenSets` and its comment removed; `SbcRules.HiddenSets` / `IsHiddenSet` removed.
- `SbcCatalog` keeps every set and reads the live list (`Live`, `LiveKeys`, `IsLive`); `SbcService.IsHidden` is
  "not listed or switched off". Challenge-key uniqueness is enforced between live sets.
- Fixed challenge ids: a challenge's id is its set id plus its `number`; `SbcSetArea` writes numbers on publish
  (kept by key; a new challenge gets the next number no challenge or saved squad of the set used). Removing a
  challenge in the editor no longer shifts later challenges' saved squads.
- `SbcSetArea`: `hide` now removes a panel-added set (refused while listed or when any club has progress) or reverts
  a bundled set's edit. `retail` is an allowed challenge field (bundled sets carry it, so editing one was refused).
- New content area `sbc_live` (`SbcLiveArea`, applies after `sbc_set`), registered in the panel.
- `GET /panel/api/sbc`: each set has `listed`/`enabled`; reply has `live`; `hiddenByRules`/`hiddenSetRules` removed.
- Panel: SBCs page has tabs **SBCs** (catalogue: Live / Off / Not live badges, Remove for panel sets, Revert for
  edited bundled sets) and **Live SBCs** (switch per set, + Add SBC, Remove, Stage / Reset, like the store listing).
- Changes page (`app/changes.ts`): labels "SBC" / "Live SBCs"; a staged list reads "UP_82 listed, on",
  "UPG_GOLD switched off", "UPG_SILVER taken off Live SBCs". Panel bundle rebuilt (`npm run build`, stamp check clean).
- Docs: `panel-update-status.md` (this), `panel-api-contract.md` SBC section, `icon-swaps.md`.

### Failures found by the focused runs (fixed, 2026-10-04)

- `CompletionRewardTests` (caused by 141): it deleted only version 140, so with 141 present 140 never replayed.
  Now deletes `>= 140`. Test fix.
- `OperatorPanelTests.AViewOnlyPackKeepsItsDrawButCanBeRenamedAndPriced` (from 762d1284): Match Pack 544 is retired
  and left out of the catalogue on purpose (migration 140). The test now checks 544 is absent and uses red pick 550
  for the view-only refusal and rename. Test fix.
- `OperatorPanelTests.AFormerRewardPackShowsWhereItIsUsedAndTheStoreCanSellIt`: pack 319 is now a real objectives,
  Champions and Rivals reward, so it cannot show "tradeability alone is not a use"; uses unused pack 534. Test fix.
- `OfflineApiTests.FullOfflineScenario` (regression from dbda9bc4): keeping the home side's own stadium card for
  Squad Battles also took the match venue card off Draft squads, whose flat ids still name the Bernabeu. Code fix:
  `SquadBattlesService.VenueActivesAsync` (venue card), used by `DraftService`; Squad Battles home fixtures unchanged.
- `OfflineApiTests` stadium and Squad Battles scenarios and `OnlineMatchDocumentTests` (stale since 292b6a58 / dbda9bc4): the active
  default stadium card is now the venue's own card (6200000 + venue, 6200443 for a new club, 6200002 after selecting
  venue 2; both catalogued by migrations 053/139), and a home Squad Battles fixture keeps the club's stadium while the
  CPU opponent gets the match venue. Expectations updated to that design. Test fix.

Evidence:
- `dotnet test --filter SbcLiveTests|SbcPhaseOneTests|SbcBrickTests|IconSwapsFoundationTests|ObjectiveProgressTests`:
  41 passed, 0 failed. New `SbcLiveTests`: upgrade from stored patterns + panel hides + panel sets, replay keeps the
  list; publishing the list; challenge ids kept through reorder/remove/add; remove/revert guards.
- panel-web `npm run typecheck` clean; `vitest run src/pages/sbc src/app`: 26 passed (new `sbc.test.tsx`).
- Second focused run (CompletionReward, ModeReward, OfflineApi, ContentChangeset, OperatorPanel, DiscardRuleUpgrade,
  RelationalCatalogAndAccount, LiveRules): 110/114, the 4 failures below. After the fixes: OperatorPanel, PanelBuild,
  SquadBattles*, Draft*, Stadium*, CompletionReward and OnlineMatchDocument classes pass (159/161, then the last two
  fixed), and `OfflineApiTests` 3/3.

### Step 2: reward packs named by id (2026-10-04)

- Bundled `sbc.json`: all 118 PACK rewards now carry `packId` (byte-exact edit, CRLF kept) instead of a pack name, using
  the shipped mapping; the four player-pick upgrades pay their own pick (UP_82 513, UP_84 514, UP_86 515, UP_88 516).
  The unused top-level `packTypes` name list is removed (only archived Python read it).
- `rules/sbc.json`: `rewardPacks`, `legacyRewardPacks`, `defaultRewardPack` and their comment removed;
  `playerPickRewardName` stays with its own comment. `SbcRules` and `SbcService.PackId` lose the name resolution;
  `SbcService.RewardPackId(attributes)` is the packId.
- Start-up conversion `Catalog/SbcRewardPackConversion` (runs once, while the stored rules still hold `rewardPacks`;
  called from `GameContext.FillRulesAsync`): panel edits get the packId their names paid under the database's stored
  mapping; a bundled set whose names the stored mapping paid differently gets an edit row with that pack (old names from
  `Resources/Upgrades/sbc-reward-pack-names.json`, generated from HEAD `sbc.json`); then the mapping rules are deleted.
- `SbcCatalog` refuses a PACK reward without a packId; `SbcSetArea` requires an existing packId.
- Pack catalogue: SBC rewards (every catalogue set) are listed in a pack's `usedBy` and block deleting it
  (`SbcCatalog.PackUses`, `InspectPackDeleteAsync`); before, only the `rewardPacks` rule did that.
- Panel: `rewardPackKeys` removed from `GET sbc`; the reward row reads `packId` only. Bundle rebuilt, stamp clean.
- Tests: new `SbcRewardPackConversionTests` (bundled ids and picks; upgrade converts a panel edit by name, legacy number
  and player pick, keeps an edited mapping's pack for an unedited bundled set, leaves others unedited, drops the rules;
  staging a name is refused). `PlayersPackMergeTests` and `OperatorPanelPackDeleteTests` seed or use real SBC rewards
  instead of `rewardPacks` rows; `OfflineApiTests` / `SbcPhaseOneTests` use `RewardPackId`.
- Docs: `panel-api-contract.md` (SBCs, pack delete `referenced`).

Evidence: solution builds with 0 warnings. Focused run (SbcRewardPackConversion, SbcLive, SbcPhaseOne, SbcBrick,
IconSwapsFoundation, OfflineApi, PlayersPackMerge, OperatorPanel, SbcUpgradeMigration, ObjectiveProgress, LiveRules):
124/126; the two failures were test assertions (a key check matching the new comment text, and a delete message
still expecting "sbc"), fixed and re-run with `PanelBuildTests`: 3/3. panel-web: SBC + changes tests 6/6, `npm run
build` (typecheck included) and stamp check clean.

### Step 3: SBCs → Settings (2026-10-04)

- New tab `SbcSettingsTab` (`#/sbc/settings`) for `rules/sbc.json`, each value staged as a rules change of file `sbc`:
  General (default formation from `chemistry.json`, shown as 4-3-3 (2) etc.; player pick reward name with a preview;
  fallback badge tab; badge cache seconds; repeatable rewards untradeable), Requirement types (label and exact label
  with a "Shows as" preview, Used switch = `ignoredRequirements`, the fixed client check in words), Game text (the eight
  `text` strings with their placeholders), Fixed values (squad size, id bases, art folder; read-only), and "Restore shipped
  settings" (imports only `sbc`). Edits are checked against the value as staged/published before they are staged.
- Rules page: `sbc` is no longer listed; `#/rules/sbc` points to SBCs → Settings.
- Server: `GET sbc` adds `settingsFile`, `settings`, `requirementRules`, `formations` (camelCase written out: the
  panel serializer has no naming policy). `SbcRules.Checked()` refuses empty labels and game text, unknown ignored types
  and a negative cache time when staged (`GameRules.ValidateDocument`); `ContentSources` refuses an unknown default
  formation or fallback tab when the content loads, so such a publish rolls back.
- Tests: new `SbcSettingsTests` (staging refusals; a label, formation and ignored type publish live; an unknown formation
  fails validation), `OperatorPanelTests.TheSbcReplyCarriesTheLiveListAndTheSettings`, panel `sbc.test.tsx` settings test
  and formation names, `rules.test.tsx` (sbc not listed, old link points to the tab).
- Docs: `panel-api-contract.md`.

Evidence: solution builds, 0 warnings. `SbcSettingsTests`, the panel reply test, `LiveRulesTests`, `SbcPhaseOneTests`:
22/22; `PanelBuildTests` 1/1 after the rebuild. panel-web: typecheck clean, `vitest run src/pages/sbc src/pages/rules
src/app`: 39/39, build and stamp check clean.

### Step 4: the set editor, tab groups and names (2026-10-04)

Owner feedback: the editor's inputs were unstyled, it could not pick a formation, set requirements properly or place
locked (brick) players; SBCs should be grouped like the game's tabs; titles should be English names, not keys.

- Editor rebuilt (`SbcEditor`, `SquadEditor`, `RequirementsEditor`, `RewardsEditor`, `CardPicker`): every control is a
  styled `Field`; each challenge is a card with name, id, description, Squad (formation picker, "Locked slots" switch and a
  pitch drawn from the formation's slots; a slot is open, locked empty, a locked card picked by name, or a league/nation
  placeholder copied from a card), Requirements (Players in squad, which follows the open slots, and one row per rule with
  comparison and number or quality and a "Shows as" label; authored attributes such as a rating's max are kept) and Rewards
  (pack and count, coins, XP, a player card picked by name, a manager league card). Challenges move up and down; a set can be
  duplicated (`#/sbc/new/<key>`: copy with fresh challenge ids that follow the new key); a repeatable set has "Rewards
  tradeable". A sticky preview shows the badge, the set description and each challenge's requirement line, squad size and
  rewards as FIFA will show them, and why staging would be refused (`POST sbc/preview`). Styles in `sbc.css`.
- SBCs and Live SBCs are grouped by the game's nine tabs (Foundations ... Special) with each tab's badge and live count;
  the add dialog groups its choices the same way.
- Names, not keys: catalogue cards, Live SBCs rows, the add dialog, the editor header and the Settings requirement rows
  show English names (the key stays in the Key field and as a tooltip). Changes and the pending drawer title an SBC change by
  its name and list Live SBCs edits by set name (`changeTitle`, `useSbcNames`).
- Server: `SbcText` (new, Services/Modes) holds the requirement translation, challenge description, squad size and set
  description that `SbcService` used privately; the service delegates and the preview calls it. `SbcSetArea` now checks a
  challenge's formation, description and bricks (open slots, locked cards that exist, placeholders with a league and nation,
  PLAYERS max equal to the open slots), and accepts PLAYER and MANAGER_LEAGUE rewards and `rewardsTradeable`. A PLAYER reward
  without a name pays exactly its `resourceId` (an empty name used to match almost any card). `GET sbc` adds formation slots,
  `positions`, `squadSize` and `managerLeagues`.
- Tests: new `SbcEditorTests` (bricks/formation/reward checks; a picked player pays its card), preview and reply tests in
  `OperatorPanelTests`; panel `sbc.test.tsx` editor tests (lock slots and place a card with PLAYERS kept in step, formation,
  rating max kept; manager league and picked player rewards; preview problem shown; duplicate; tab groups) and
  `changes.test.ts` (names in summaries and titles).
- Docs: `panel-api-contract.md`.

Evidence: solution builds, 0 warnings. `SbcEditorTests`, `OperatorPanelTests.TheSbc*`, `SbcPhaseOneTests`, `SbcBrickTests`,
`SbcSettingsTests`, `IconSwapsFoundationTests`: 38/38; `OfflineApiTests`, `ObjectiveProgressTests`, `SbcLiveTests`,
`SbcRewardPackConversionTests`, `SbcUpgradeMigrationTests` (the `SbcText` move): 19/19; `PanelBuildTests` 1/1. panel-web: typecheck clean, full `vitest run` 268/268 (34 files),
build and stamp check clean.

### Step 5: automatic ids, fixed player picks, alignment (2026-10-04)

- Reward card control aligned: a labelled box like the other fields (`.sbc-control-group`); fields in a row align at the bottom.
- Keys and challenge ids are automatic. A new or duplicated set's key is made from its name (`keyFor`: accents dropped,
  upper case, `_`, a `_2` suffix when taken by the catalogue or a staged set); challenge ids are read-only, existing ones
  kept, new ones `<KEY>_<n>` with the smallest n no catalogue or staged challenge uses (`withIds`). Server:
  `SbcSetArea` refuses an update that renames a numbered challenge.
- Two kinds of player pick (owner: random, which existed, and fixed). A pick group may list `choices` (2-5 different
  cards, `choice_count` = their number): `PlayerPickDraw.Configuration` carries them into the token's draw config as
  `fixed_choices`, `Draw` returns exactly those cards in order (a card removed since is left out), `Validate` checks them.
  Pack editor: each pick group has Choices = Random / Fixed (chosen cards via the card picker, reorderable; the token's
  min rating is the lowest card's). SBC rewards: new "Player pick" type listing random and fixed picks apart (a PACK
  reward naming a pick pack), with a link to make a new pick; plain Pack lists no picks. `GET sbc` marks packs `pick`/`fixed`.
- Tests: `FixedPlayerPickTests` (a fixed pick bought and revealed offers exactly its cards in order; validation),
  `SbcEditorTests.AChallengeCannotBeRenamedOnceItHasANumber`; panel: fixed pick in the pack editor, Player pick reward,
  automatic key/ids and `keyFor`/`withIds`.

Evidence: `FixedPlayerPickTests`, `SbcEditorTests`, `PlayerPickPromoTests`, `OperatorPanelTests.TheSbc*`: 13/13;
`PanelBuildTests` 1/1. panel-web: typecheck clean, full `vitest run` 271/271, build and stamp check clean.

### Step 6: alignment, FIFA Points per completed match (2026-10-04)

- SBC requirement and reward rows align their fields at the top, so a hint (the "Make a new pick" link under Player pick)
  no longer pushes its input up; Remove sits level with the inputs.
- Match completion rewards (rules/seasons.json `matchCompletionRewards`, Modes → each mode) pay FIFA Points too:
  `fifaPoints` per mode, 0 by default (bundled file; start-up fill adds the key to stored rules). Paid once with the coins
  and pack for a paid match (`CompletionPackService.GrantPointsAsync` from `MatchService.SettleRewardAsync` and the Weekend
  League connection-loss win); flagged or void matches pay none. Panel: "FIFA Points per completed match" field.
- Tests: `CompletionRewardTests.AModePaysItsFifaPointsOnlyForAPaidMatch`; panel `completionReward.test.tsx`.

Evidence: `CompletionRewardTests`, `LiveRulesTests`, `WeekendLeagueTests`: 35/35. panel-web: typecheck clean, modes + SBC
tests 44/44, build and stamp check clean.

### Step 7: Objectives page, live objectives and the Season Pass (2026-10-04)

Owner decisions: the live list holds objective groups; a new group counts from when it goes live; the welcome group and
gifts are group kinds in the same catalogue; a Season Pass level pays a full reward (30 levels).

- Storage stays rules/objectives.json (published under Changes; the Rules page no longer lists it): the welcome group,
  `grindGroups` and `gifts` are the catalogue; new `live` list `[{group, enabled}]` in order (null = every group,
  welcome first). Only live groups are served (FIFA priority follows the list) and claimable.
- Counting from go-live: migration 142 (`objective_group_starts`, `objective_baselines`). `ObjectiveStartRepository`
  starts a group the first time it is live (on publish, from `RulesArea`, and at start-up), storing each club's counters
  for its stats; `ObjectivesState.Baselines` (read in the same `objectives.state` query) and `Progress(group, stat)`.
  Groups present when 142 ran are marked started without baselines, so no club loses progress.
- Rewards: `ObjectiveReward` adds `fifaPoints` and `packs` (count and tradeability per line) next to coins, the older
  single pack and the My Stadium extras; a reward must pay something. Objective, group and level documents now carry an
  award list (templates `{{awards}}`): a tile per coins, pack line and My Stadium extra. FIFA Points are paid without a
  tile (no known FIFA 21 award type: unconfirmed). Publish refuses a missing pack or a live entry naming no group.
- Panel `GET objectives`; page `#/objectives` with tabs Objectives (catalogue by kind, Live/Off/Not live, counting-since
  date, Remove for unlisted), Live objectives (order, switches, add, stage), Season Pass (XP per level, levels open,
  30 levels each with the reward editor), Settings (Season XP per event). Group editor for normal groups (objectives:
  stat, target with its description, XP, reward; group reward), gifts (window, optionally earned by playing) and the
  welcome group; ids automatic (`nextGroupId`/`nextObjectiveId` skip claimed ids); a new group can go live in the
  same publish.
- Tests: `ObjectivesLiveTests` (live list order and claims; a new group counts from go-live while older groups keep
  progress, off/on keeps it; a level paying coins, FIFA Points and two pack lines; missing group/pack refused),
  `OperatorPanelTests.TheObjectivesReplyCarriesTheCatalogueAndTheNextFreeIds`; panel `objectives.test.tsx`;
  `SbcLiveTests` now delete `>= 141` to replay 141.
- Level choices (owner, same day: "each level gives 1 reward, but there are choices, like 1 of 3 packs"): a level reward
  may be `{options: [...]}` (2-5 rewards, nothing beside them; objectives, groups and gifts refuse options). The level
  serves one `rewardOptionList` entry per option (new template `objectives/level-option`); the claim's option id picks
  what is paid; the pick is stored as a claim of scope `level_option` (migration 143 widens the scope check) and served as
  `chosenOption`. Panel: "Club picks one of several rewards" in the level editor with an editor per option.

- Match XP per mode (owner: "you always get a reward, regardless of win/loss/draw"): Season XP per completed match is now
  each mode's completion reward `xp` (Modes → each mode, "Season XP per completed match"), paid from the match end with the
  mode (`RecordMatchEndAsync(..., mode)`), the same for a win, draw or loss. The objectives win bonus (`xp.win`, 500) is
  retired and `xp.match` removed; migration 144 copies the stored `xp.match` (300 unless edited) into every mode and
  deletes both rules. Objectives → Settings was then removed (owner, 2026-10-04); the Season Pass tab points to Modes for match XP. SBCs
  and opened packs then stopped earning Season XP altogether (owner): migration 145 deletes rules/objectives.json `xp`
  (sbc 400, pack 50) and its comment; they still count for objectives (`sbcs`, `packs`). Season XP now comes only from
  completed matches (per mode) and claimed objectives. Test: `SbcsAndOpenedPacksCountForObjectivesButEarnNoSeasonXp`.

Match XP evidence: CompletionReward (incl. `ACompletedMatchEarnsItsModesSeasonXpWhateverTheResult`,
`TheUpgradeMovesMatchXpIntoEveryModeAndDropsTheWinBonus`), objectives, ClubFeature, RelationalProgression, MatchDayStadium,
AtomicCommand, TradeabilitySwitch, LiveRules, SbcLive, OfflineApi, WeekendLeague, Draft*, OnlineMatchDocument: 172/172;
`PanelBuildTests` 1/1. panel-web: typecheck clean, full `vitest run` 279/279, build and stamp check clean.

Evidence: solution builds. Broad run (objectives, ClubFeature, MatchDayStadium, RelationalProgression, CompletionReward,
ModeReward, SbcLive, OperatorPanelTests, PanelBuild, AtomicCommand, LiveRules): 180/180 before the level choices; after
them `ObjectivesLiveTests` (6, incl. the choice and its refusals), ClubFeature, RelationalProgression, ObjectivePhaseOne,
SbcLive: 54/54; `PanelBuildTests` 1/1. panel-web: typecheck clean, full `vitest run` 278/278, build and stamp check clean.

## Next

- Live check in the panel and FIFA (locked slots, placeholders and player rewards render and pay as staged).
- In FIFA: objectives and levels with several rewards (does the tile row show every award?), a new live group starting
  at 0, a disabled group disappearing; FIFA Points paid by a level arrive in the balance.
- In FIFA: a fixed pick from an SBC opens as a pick token whose choice screen shows exactly the chosen cards.

## Not run / still owed

- Full solution suite (before commit); `verify-startup-runtime.mjs`; any live panel or in-game check.
