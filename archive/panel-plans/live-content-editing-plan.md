# Live content editing from the operator panel — plan

> Archived on 2026-10-01. This is implementation history with historical status
> claims, not the current plan. Use the [repository README](../../README.md),
> [contribution guide](../../CONTRIBUTION.MD) and
> [panel guide](../../ultimateteam/panel-web/README.md) for current instructions.

Status (checked 2026-09-30 on `feat/live-db-edits`): **steps 0–4, 6 and 7 are implemented in the uncommitted
working tree; step 5 now implements player-card edits and new cards under the current panel API contract; step 8 remains open for other content areas, step 9 has backend detection but no React banner, and step 10 remains open. Promo/event/bucket and Icon-rotation backend editing is now built under `promo-groups-plan.md`.** The baseline
"current state" table below describes the code before this branch's uncommitted changes. The
step notes record what has been built and what has actually been verified.

## Goal

An operator can change any game content (card definitions, rules, packs, SBCs, objectives and so on)
from the private operator panel, review every pending change on one page, and publish the whole set
with one **Confirm**. The running server picks the change up without a restart, and every publish is
recorded and can be reverted.

Operational actions (block a player, maintenance mode, coin or card gifts, mode resets) stay
immediate. Only *content* goes through review.

## Current state (observed)

| Data | Source | Loaded | Live today? |
| --- | --- | --- | --- |
| Club state: items, coins, squads, progress, access | PostgreSQL | Per request through `UnitOfWork` | **Yes, server-side.** The panel edits balances and blocks directly. The FIFA client may not show a change until it refetches (inferred). |
| Store packs | PostgreSQL (`store_packs`, migration 041) | Startup, then `PackSettings.Publish()` after a panel edit (`Core/Config/PackSettings.cs:46`, `Server/Panel/OperatorPanel.PackCatalog.cs:126`) | **Yes.** An atomic snapshot swap guarded by a revision hash (`PackRevisionAsync`, 409 on a stale revision). |
| Catalogue: player cards, managers, kits, consumables, customisations | PostgreSQL | Once, in `GameContext.CreateAsync` → `RelationalCatalogLoader.LoadWithPacksAsync` (`Core/GameContext.cs:200`) | **No.** Frozen in `PlayerCatalog`, `Cards`, `ContentCache` and `RelationalItemWireMapper`. Player contexts copy the same references (`GameContext.ForPlayerAsync`). |
| Rules: `runtime-source/ultimateteam21/rules/*.json` (14 files) | Files in the image | Once, in `GameRules.Load` (`Core/Rules/GameRules.cs:31`) | **No.** Not in the database at all. |
| SBC sets (`sbc.json`), chemistry (`chemistry.json`), stadiums | Files in the image | Once, in `GameContext.CreateAsync` | **No.** |

Other facts that shape the design:

- **Consumers are widespread.** Rough counts of files that read frozen content through a game context:
  `Rules` ~42, `Cards` ~22, `Players` ~19, `Packs` ~11, `Managers` ~7, `Chemistry` ~4, `Stadiums` ~4.
  Step 0 turns this into an exact inventory.
- **Club maintenance keys on the catalogue hash.** `MaintenanceRevision` includes
  `RelationalCatalogSnapshot.RevisionSha256` and every `rules/*.json` file (`GameContext.cs:324`). A club reruns
  maintenance when that value changes (`Services/Club/ClubMaintenanceService.cs:124`). Packs were deliberately kept
  out of the hash for this reason (comment in `Core/Catalog/StorePackCatalog.cs`). *Removed in step 2:* club
  maintenance is now starter setup for new clubs only.
- **Draft depends on catalogue order.** Seeded Draft generation consumes candidates in catalogue order
  (`Core/Catalog/PlayerCatalog.cs:11`).
- **Owned items must keep their definition.** Rendering an owned item whose definition is missing from the snapshot
  throws "No catalog definition for owned item" (`Core/Items/RelationalItemWireMapper.cs:232`).
- **The seed files are immutable.** Changing a seed file changes the package hash, and existing databases then fail
  with "Installed setup package hash mismatch" (`Core/Data/RelationalStorageReadiness.cs:84`). Content changes that
  ship in the repo are migrations. The server refuses to start unless the database's migration version equals
  `RelationalCatalogMetadataMigration.CurrentVersion`, currently 63.
- **`panel_edits`** (migration 063) already records per-persona panel edits as `jsonb` patches. It is
  persona-scoped, so it does not fit content changesets.
- **The panel is loopback-only** (`127.0.0.1:18448`, SSH tunnel), and a direct loopback connection is its credential
  ([panel contract](../../ultimateteam/panel-api-contract.md)). New routes keep that access model.

## Design

### 1. One content snapshot, swapped atomically

- **Holder.** Introduce `ContentSnapshot`: an immutable object holding the catalogue (`PlayerCatalog`, `Cards`,
  managers, kits, the item wire mapper's lookups), `GameRules`, the SBC catalogue, chemistry, stadiums, pack settings
  and a `Revision` number.
- **Access.** One `LiveContent` holder owns the current snapshot. It is read with `Volatile.Read` and replaced with
  `Volatile.Write`, the same pattern as `PackSettings`.
- **Per-request consistency.** A request, Blaze command or background job captures the snapshot **once** at its start
  and uses only that copy until it ends. A pack opening or SBC submission therefore never sees half of a publish.
- **Replacing `GameContext`'s frozen properties.** `Players`, `Cards`, `Rules`, `Sbc` and the others become reads
  through the captured snapshot. The mechanics (a scoped capture, or a property that reads the holder) are decided in
  step 1, after the inventory.
- **Startup builds the first snapshot through the same function a publish uses**, so there is one loader and one
  validator.

### 2. Changesets: pending edits as rows

Pending content edits are stored as a list of changes rather than as a second copy of the content tables. A draft
schema would copy about 22,500 card definitions, about 7 MB of compatibility data and every trigger.

Proposed tables (a new migration, 064 or later):

```sql
CREATE TABLE ultimateteam.content_changesets (
    changeset_id   bigserial PRIMARY KEY,
    status         text NOT NULL CHECK (status IN ('open', 'published', 'discarded', 'failed')),
    base_revision  bigint NOT NULL,          -- the content revision the edits were made against
    note           text,
    created_by     text,                     -- operator label; the panel has no user accounts today
    created_at     timestamptz NOT NULL DEFAULT now(),
    published_at   timestamptz,
    published_revision bigint,
    reverts_changeset_id bigint REFERENCES ultimateteam.content_changesets(changeset_id)
);

CREATE TABLE ultimateteam.content_changes (
    change_id      bigserial PRIMARY KEY,
    changeset_id   bigint NOT NULL REFERENCES ultimateteam.content_changesets(changeset_id),
    area           text NOT NULL,            -- 'player_card', 'rules:market', 'store_pack', 'sbc_set', ...
    target_key     jsonb NOT NULL,           -- the row's key, e.g. {"definition_id": 22507}
    operation      text NOT NULL CHECK (operation IN ('insert', 'update', 'hide')),
    before_value   jsonb,                    -- the row as it was when edited (null for insert)
    after_value    jsonb,                    -- the intended row (null for hide)
    created_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (changeset_id, area, target_key)  -- editing the same row twice updates one change
);

CREATE TABLE ultimateteam.content_revision (
    singleton      boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    revision       bigint NOT NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);
```

Rules for changesets:

- **One open changeset at a time** to start with. Every panel content edit adds to or updates a row in it. Multiple
  named drafts can come later if needed.
- **No hard deletes.** `hide` sets a status (for example `item_definitions.status`, `store_packs.enabled`), never
  removes a row a club might own.
- **Every `area` has one typed handler** that knows its table(s), validates `after_value`, applies the change and
  describes its impact. No generic "write any column" handler.

### 3. Validate and publish share one path

`ApplyAsync(changeset, commit)`:

1. Take the content-edit lock. The panel already serialises catalogue edits with `_catalogEdits`.
2. Open a transaction. Check `content_revision` against the changeset's `base_revision` and each change's
   `before_value` against the live row (see conflicts below).
3. Apply every change through its area handler, in a fixed area order so dependencies resolve. Example: a new
   `item_definitions` row before its `player_card_definitions` row. The database's constraints and triggers run here.
4. Build a candidate `ContentSnapshot` from the transaction's view, using the startup loader and validation.
5. **Validate** (`commit = false`): return the errors, warnings and impact report, then roll back.
6. **Publish** (`commit = true`):
   - bump `content_revision`, mark the changeset `published` with its revision, and commit;
   - swap in the candidate snapshot;
   - write one audit line: changeset id, revision and change count, never credentials.

A publish that fails validation changes nothing: the transaction rolls back and the old snapshot stays.

### 4. Conflicts

A change conflicts when the live row no longer equals its `before_value`. That happens after a DataGrip edit, a
migration, or another publish. The review page lists conflicts, and Publish is refused until each one is rebased
(keep my value on top of the new row) or dropped. Nothing is overwritten silently.

### 5. History and revert

- **History.** Published changesets are the history: note, author, time, revision and every change with its
  before and after values.
- **Revert.** "Revert" creates a new open changeset whose changes restore each `before_value`
  (`reverts_changeset_id` set). It goes through the normal review and publish, including conflict checks.

### 6. Changes made outside the panel

- **Detection (implemented, migration 094).** Content-table triggers record transactional, serialized revisions and call `pg_notify('ultimateteam_content', ...)`. `GET /panel/api/content/status` compares committed table revisions with the loaded snapshot. The panel can poll this read-only endpoint; no persistent `LISTEN` connection or connection-reserve increase is required.
- **Response.** On a notification not caused by its own publish, the server does **not** apply the change silently.
  The status endpoint returns affected tables and `reloadRequired`; the React banner still needs to show them and offer **Reload**, which rebuilds
  and swaps the snapshot through the same validation.
- **Migrations.** A deploy runs migrations at startup, so no special case is needed.

### 7. What stays immediate

These keep their current immediate behaviour, recorded in `panel_edits`:

- player blocks, maintenance bypass and access notes;
- maintenance mode and the player cap;
- balances, gifts and card grants;
- per-club mode resets.

## Steps

Each step ends with focused tests (`dotnet test ... --filter`) in isolated throwaway databases. The full suite runs
before each commit. No step touches a real club or the maintainer's cluster.

### Step 0 — Inventory frozen content

**Done 2026-09-28.** See [Step 0 results](#step-0-results-inventory-and-capture-mechanism) at the end of this document.

- List every read of `GameContext.Players`, `Cards`, `Rules`, `Managers`, `Kits`, `Stadiums`, `Chemistry`, `Sbc`,
  `Packs` and `Content`, and every object that captures one in its constructor. Examples: `Cards`, `ContentCache`,
  `RelationalItemWireMapper`, services held by `PlayerRegistry`, and background jobs such as market maintenance that
  read intervals at startup.
- Classify each as **per-request** (easy to move to a captured snapshot) or **held** (needs rebuilding with the
  snapshot, or a lookup through the holder).
- **Deliverable:** the inventory table appended to this document, plus the chosen capture mechanism.

### Step 1 — `ContentSnapshot` holder and manual reload

**Implemented 2026-09-28 on `feat/live-db-edits`, not yet committed.**

What was built:

- **Holder and loader.** `Core/Catalog/ContentSnapshot.cs`: `ContentSources` (the one loader), `ContentSnapshot`,
  and `LiveContent` (`Current`, `Pin()`, `ReloadAsync`).
- **`GameContext`.** Its content properties read `Live.Snapshot`; player contexts share `Live`;
  `ReloadContentAsync` reloads through the start-up loader and validation.
- **Holders from step 0:**
  - B3: `ContentCache` reads the live players and cards.
  - B4: the item mapper's lookups moved into `RelationalCatalogSnapshot` (`PlayersById`, `ConsumablesById`,
    `CustomisationsById`) and are read through the game context.
  - B5: the market job re-reads its interval before each wait.
  - B6: `MathGroups` reads its rules per call.
  - B7: the SBC name cache is keyed by the catalog entry.
  - B8 is deferred to step 7, because tests read `Rules.Packs` and a reload refreshes it.
  - B9 unchanged: `MaintenanceRevision` stays at the start-up revision.
- **Pins:**
  - `FutRouter.HandleAsync` and `HandleAnonymousAsync`;
  - `FutServices.CreateAsync` (club open and maintenance);
  - Blaze `HandleFrameAsync`, per frame;
  - the panel API request;
  - each market maintenance pass.
- **Panel.** `GET /panel/api/content`, `POST /panel/api/content/reload` (422 and no change on failure) and a
  "Game content" card on the Maintenance tab.

Evidence:

- **Focused tests:** 48/48 passed on Windows, Release, in isolated PostgreSQL clusters. The run covered the new
  `LiveContentTests` (4), 2 new `OperatorPanelTests`, and the existing `OperatorPanelTests`, `ContentCacheTests`,
  `MathGroupTests`, `MarketMaintenanceJobTests`, `RelationalCatalogAndAccountTests`, `RelationalInventoryTests` and
  `LaunchCodeEndpointTests`.
- **The new tests cover:**
  - a reload during a pinned unit of work;
  - a failing reload leaving content unchanged;
  - byte-identical FUT responses after a reload of unchanged content;
  - no unpinned content reads across six FUT requests;
  - a card inserted with SQL that becomes searchable, grantable and renderable after a panel reload.

Still owed before a commit:

- the full solution suite;
- a real browser check of the "Game content" card against a running server;
- a rebuild of the server executable (the running server does not change until it is rebuilt and restarted).

Not measured: the per-read cost of the `AsyncLocal` lookup on hot paths such as hub rendering. The performance
harness can compare it against `main`.

- Build `ContentSnapshot` and `LiveContent` (design §1). Startup creates revision 1 through the new build function.
- Move the held consumers from step 0 onto the holder.
- Add a panel route and button **Reload content** (loopback-only, like the rest of the panel). It rebuilds from the
  database **and re-reads the bundled `rules/*.json`, `sbc.json`, chemistry and stadium files**, validates and swaps.
  Startup uses the same loader.
- **Acceptance:**
  - A card definition inserted with SQL into an isolated database appears in the panel's card search, can be
    granted, renders, and can drop from a pack after Reload, without a restart.
  - A reload that fails validation keeps the old snapshot and reports the error.
  - Requests running during a swap finish on their captured snapshot. Test with a delayed request.
  - Wire bytes for an unchanged catalogue are identical before and after (existing response fixtures).

### Step 2 — Separate club maintenance and Draft from content edits

- **Maintenance.** Find what club maintenance actually depends on (starter players, club rules, initial kits,
  item-id bands) and key `MaintenanceRevision` on those inputs only, not the whole catalogue hash. A card edit must
  not rerun maintenance for every club.
- **Draft.** Decide how a live catalogue change affects Draft runs in progress:
  - **(a)** record the content revision on each run and keep generating from the matching candidate order, or
  - **(b)** accept the change and show "N Draft runs in progress" as a warning on the review page.
  Option (b) is simpler; (a) only matters if deterministic reruns are required.
- **Acceptance:** publishing a card-definition change leaves every club's maintenance flag untouched. The Draft
  behaviour matches the chosen option under test.

**Decided and implemented 2026-09-28 on `feat/live-db-edits`, not yet committed.** The maintainer's decisions:

- **Club maintenance is starter setup for new clubs only.** It no longer reruns for existing clubs, so there is no
  maintenance revision any more:
  - `GameContext.MaintenanceRevision` and `ComputeRelationalMaintenanceRevision` are removed.
  - `ClubMaintenanceService.RunAsync` reads the `relational_club_initialized_v1` flag and sets up only a club
    without it: initial players, a manager, default kits and the first squad (or an empty-squad repair).
  - Every existing club already has that flag, since every earlier run set it, so none is touched again. Old
    `relational_maintenance_revision_v1_*` and starter-consumable flag rows stay in `club_flags`, unused.
  - Removed behaviour: after a deploy or rule change, existing clubs are no longer topped up to the starter
    player count, and do not get a missing manager, kit or empty-squad repair.
- **No starter consumables.** New clubs no longer get chemistry styles or position modifiers; they come from packs.
  The `starterStack` settings are removed from `rules/cards.json` and `CardRules` together.
- **Draft: accept and warn** (option b).
  - Screens already shown are stored and stay as they are.
  - Later screens draw from the reloaded catalogue and Draft rules, and drafted cards show edited stats.
  - `GET /panel/api/content` and the reload answer report `draftRunsInProgress` with a note. The review page (step
    4) should show the same count.

Tests changed with the new contract:

- **`RelationalMaintenanceTests`:**
  - The old "catalogue and rules invalidate the maintenance revision" test is replaced by
    `AnInitializedClubIsNotSetUpAgainAfterACatalogueChangeAndRestart`.
  - The kit, player-state and empty-squad tests now clear the initialized flag instead of a revision flag.
  - Flag counts drop from 4 to 1, with no consumables granted.
- **`OfflineApiTests`:** the consumables checks get their stacks from packs (two modifier packs, and one of each
  chemistry style as a fixture) instead of starter stacks.
- **`RelationalOnboardingTests`:** a new club has no consumables and one flag.
- **`MarketTests`:** the listed-item consumable check gives the club a chemistry style first.

Evidence: focused runs on Windows, Release, isolated PostgreSQL, all passing after the test updates:

- `RelationalMaintenanceTests`, `RelationalOnboardingTests`, `RelationalClubPagingTests`, `MarketTests`,
  `OperatorPanelTests` (63);
- `OfflineApiTests`, `LiveContentTests`, `RelationalRuntimeTests`, `RelationalDraftTests`,
  `RelationalInventoryTests`, `ClubFeatureTests` (in an 81-test run whose two failures were fixed and rerun);
- `ClubLockAndFamilyReadTests` and `RelationalServiceTests` (9).

The full suite has not been run.

**Finding for steps 3 and 5: the catalogue is insert-only.**

- The schema's "Recipe 2: immutable catalog" triggers (`database-setup/schema/003-invariants.sql`,
  `lfv4_r2_catalog`) refuse `UPDATE` and `DELETE` on the catalogue tables, including `item_definitions`,
  `player_card_definitions` and `manager_definitions`. Migrations change catalogue rows by disabling those
  triggers around their statements (for example `migrations/001-catalog-metadata.sql:34-83`).
- Adding a card works today, and step 1's reload test does exactly that. Editing or hiding an existing card from
  the panel does not.
- Before step 5, a migration must let the publish pipeline change catalogue rows in a controlled way. For example,
  the trigger function could allow `UPDATE` only when a transaction-local setting such as
  `SET LOCAL ultimateteam.catalog_publish = 'on'` is present, and still refuse `DELETE`.
- `ALTER TABLE … DISABLE TRIGGER` in the publish transaction is not suitable: it takes an exclusive table lock and
  needs owner rights.

### Step 3 — Changeset schema and the publish pipeline

- Migration 072: the three tables from design §2. Bump `RelationalCatalogMetadataMigration.CurrentVersion` and update
  `RelationalStorageReadiness`'s expected tables.
- `ContentChangesets` service: open or get the draft, add, update or remove a change, validate, publish, discard,
  revert.
- An area-handler interface: validate, apply, and describe the impact.
- Panel API, following the existing `/panel/api/*` style and access rules:

  | Route | Does |
  | --- | --- |
  | `GET /panel/api/content/changeset` | The open changeset with its changes, conflicts and impact |
  | `POST /panel/api/content/changeset/validate` | Dry-run apply; errors, warnings, impact |
  | `POST /panel/api/content/changeset/publish` | `{note, backup}`: apply, commit and swap |
  | `DELETE /panel/api/content/changeset/changes/{id}` | Drop one pending change |
  | `POST /panel/api/content/changeset/discard` | Discard the whole draft |
  | `GET /panel/api/content/history` | Published changesets, newest first |
  | `POST /panel/api/content/history/{id}/revert` | Open a revert changeset |
  | `POST /panel/api/content/reload` | The step 1 reload |

- **Acceptance:**
  - Validate never changes the database or the snapshot.
  - Publish applies all changes or none.
  - A stale `base_revision` or a changed `before_value` is reported as a conflict and refused.
  - A revert restores the before values exactly.
  - Each operation is safe to repeat: a double-clicked publish does not apply twice.

**Implemented 2026-09-28 on `feat/live-db-edits`, not yet committed.** Differences from the design above:

- **No `content_revision` table and no `base_revision`.** The per-row `before_value` check catches every real
  conflict, and the in-memory revision (step 1) numbers what the server runs.
- **`status` is open, published or discarded.** A failed publish rolls back and records nothing.
- **Changeset rows:** a unique partial index enforces one open changeset, and a check constraint ties `before_value`
  and `after_value` to the operation.
- **The catalogue stays insert-only, except inside a publish.** Migration 072 replaces `lfv4_r2_catalog()`: an
  `UPDATE` is allowed only when the transaction set `ultimateteam.content_publish = 'on'` (transaction-local
  `set_config`). The pipeline sets it just for applying its changes. Every other `UPDATE`, and every `DELETE`, is
  still refused.
- **No production content areas yet.** `ContentAreas.Default` is empty; player cards come in step 5. The tests use a
  test-only card-rating area.

What was built:

- **Migration 072** (`database-setup/migrations/072-content-changesets.sql`): `CurrentVersion` 76 after the main merge, a readiness
  check, and both tables in `STORAGE-COVERAGE.json`.
- **`Core/Content/ContentChangesets.cs`:**
  - `IContentArea` (name, apply order, key normalisation, read, validate, apply);
  - `ContentChangesets`: set, remove, discard, validate, publish, history, revert.
- **Validate and publish share one path.** Under an advisory lock, one transaction:
  1. checks every change's before value against the live row (a conflict is a 409 listing the rows);
  2. applies the changes in area order;
  3. loads the content from the same transaction with the start-up loader (`ContentSources.LoadAsync(IUnitOfWork…)`)
     and projects it with `ContentSnapshot.Create`.

  Validate then rolls back. Publish marks the changeset published, commits, publishes the pack rules and swaps the
  snapshot in through `LiveContent.ReloadAsync`.
- **Revert** opens a changeset with the before values (a hide for an inserted row), published through the same
  review. It is refused while other edits are pending.
- **Panel routes** (`OperatorPanel.Content.cs`), all loopback-only like the rest of the panel:

  | Route | Does |
  | --- | --- |
  | `GET content/changeset` | The open changeset, if any |
  | `PUT content/changeset/changes` `{area, key, operation, after, by}` | Adds or replaces one pending change |
  | `DELETE content/changeset/changes/{id}` | Drops one pending change |
  | `POST content/changeset/validate` | Dry run |
  | `POST content/changeset/publish` `{note, by}` | Publishes |
  | `POST content/changeset/discard` | Discards the open changeset |
  | `GET content/history?limit=` | Published changesets |
  | `POST content/history/{id}/revert` | Opens a revert |

Evidence: focused runs on Windows, Release, isolated PostgreSQL.

- **`ContentChangesetTests` (5, new):**
  - a pending edit changes nothing until published;
  - validate rolls back;
  - publish swaps the content and records history;
  - a second publish is a 409;
  - a direct `UPDATE` outside a publish is still refused with 23514;
  - a second edit keeps the first before value, and editing back drops the change;
  - bad values and unknown areas are 400;
  - a changed row is a conflict and nothing publishes;
  - a publish that fails part-way commits none of its changes;
  - a revert publishes the before values and is refused while other edits are pending.
- **`OperatorPanelTests`:** a new routes test covers stage, review, validate, publish, history, revert, remove and
  discard.
- **Existing suites:** `RelationalBootstrapTests` (fresh install and upgrade to version 64), `RelationalMaintenanceTests`
  (coverage inventory and table counts), `LiveContentTests`, `TestDatabaseTemplateTests` and `StartupPreflightTests`.
- **Updated for the two new tables:** the table, primary-key and foreign-key counts in `RelationalBootstrapTests` and
  `RelationalMaintenanceTests`.

The full suite has not been run.

### Step 4 — Review page and history page in the panel

- **Review page:**
  - changes grouped by area, with a field-level before → after diff;
  - per-change remove;
  - the impact lines, for example "owned by 12 clubs", "in 2 store packs", "3 Draft runs in progress";
  - validation results and conflicts;
  - an optional backup before publishing (the existing panel backup request, `state/backup-requests`);
  - a note field, then Publish.
- **Pending indicator:** a count shown on every panel page.
- **History page:** published changesets and their changes, with a Revert button.
- **"Outside change" banner** from design §6. It can land with step 9; until then, Reload covers it.
- **Front end:** the panel is Aurora 16's copied page (`Server/Resources/panel/`). Keep its style, and record the new
  routes in [panel-api-contract.md](../../ultimateteam/panel-api-contract.md).
- **Acceptance:** a direct loopback browser check of the full edit → review → validate → publish → history → revert
  loop against an isolated server.

**Implemented 2026-09-28 on `feat/live-db-edits`, not yet committed.**

What was built:

- **`Resources/panel/js/pages/changes.js`** (new): two pages, registered as `#/changes` and `#/history` and listed in
  the navigation.
  - **Pending changes:** changes grouped by area, with a field-level diff (old value struck through, new value in
    green). It offers:
    - **Remove** per change;
    - **Validate**, which shows ready, errors, or a conflict table with **Keep my value** (restages the edit on the
      live row) and Remove;
    - **Discard all**;
    - a Publish card with a note and a "published by" label, remembered in this browser only.

    A 409 publish shows its conflicts; a 422 shows why nothing was published. After a publish the page shows the new
    revision and the report's warnings and notes.
  - **Change history:** published changesets with note, author, time, a "reverts #n" tag, expandable change details,
    and **Revert** (stages the revert, then opens Pending changes).
- **`index.html`:** the navigation entries, the script, and a "N pending" pill in the side strip.
- **`js/core/panel.js`:** the status refresh fills the pill (one guarded request; an older API keeps it hidden).
- **`css/panel.css`:** diff styles.
- **Docs:** `PROVENANCE.md` and `panel-api-contract.md` (the content routes and their shapes).
- **The backup option:** the page points to Maintenance → "Back up now" before a large publish, rather than offering
  a checkbox. The backup service takes a requested dump asynchronously, so the page cannot guarantee it runs before
  the publish.

Evidence:

- `node --check` passes on `changes.js` and `panel.js`. `OperatorPanelTests` pass 10/10, including that
  `changes.js` is served.
- **Browser check.** Real Windows headless Chrome loaded the real panel files, served by a throwaway Node mock of the
  content API written for this check and not in the repo. A scripted harness passed 15 checks across the whole loop:
  1. the empty state;
  2. staging, with the diff 80 → 85 grouped by area;
  3. the pending pill;
  4. validate;
  5. a conflict (80 vs 77);
  6. Keep my value, which restages 77 → 85;
  7. publish with a note, reaching revision 2 and hiding the pill;
  8. history with note, author and diff;
  9. revert, which stages 85 → 77;
  10. remove;
  11. discard.

  A screenshot confirmed the layout matches the panel's styling.
- **Not verified against the real server.** No production content area exists before step 5, so this is a mock-API
  check. The real routes are covered by `OperatorPanelTests.ContentChangesetsAreStagedReviewedPublishedAndRevertedThroughThePanel`.
  Repeat the browser loop against an isolated server once the card editor exists.

### Step 5 — First content area: player card definitions

**Implemented in the working tree for the current `player_card` contract.** The area stages edits and inserts, validates
them inside the publish transaction, reloads the live catalogue, and retains visible-trait provenance. Migration 074
adds player-head choices and uploaded portraits. The React editor is under `/panel/next/`. Focused server tests cover
the published card value, a new revision card, traits, head selection, edited-card reverts, and the refusal to revert
an inserted card; browser and native-client checks are open.
The original proposal below included tradeability, catalogue order and hiding. Those are not in the current
`player_card` contract: hiding a card from packs uses `grantOnlyResourceIds` in the cards rules, and no card edit
changes tradeability or catalogue order.

- **Area handler** for `player_card_definitions`, its `item_definitions` row and `player_card_traits`.
- **Allowed operations:**
  - edit stats, position, rating, foot, skill moves, work rates, visible traits and the player's head;
  - add a card **based on an existing base card**, as migration 056 does, so the client has art for its asset ids.
- **Validation:**
  - ranges and positions;
  - a unique native resource id within the FIFA 21 revision namespace (`PlayerCatalog.ResourceRevisionSize`);
  - a base asset that exists;
  - no hiding through this area; use the cards rules to make a card grant-only.
- **Panel editor:** extend the existing Cards tab with "Edit" and "New from base card".
- **Acceptance:** an edited card renders with its new values after publish, in isolated runtime tests of the item
  and market responses. A client check in the real game is listed separately (step 10).

### Step 6 — Move the rules into PostgreSQL

**Implemented in the working tree, checked 2026-09-29.** Migration 073,
`RuleRows`, `RulesArea`, the `content/rules` and `content/rules/import` panel routes, and the Rules
page are present in the working tree. `Launch-UltimateTeam.ps1` now passes `--panel-api-port 18448`,
and `ServeCommand` permits that loopback listener in local mode. The focused operator-panel and
live-rules tests pass (13/13, Windows Release, isolated PostgreSQL), and `node --check` passes
for `rules.js` and `pack-manager.js`.
The new `LiveRulesTests` pass 3/3 in isolated PostgreSQL: every bundled file round-trips through
rows, a market-rule edit validates and publishes live, an unknown key is refused, import stages
the bundled value again, and a published removal remains absent after restart. `RuleRows.FillAsync`
now consults the latest published changeset for each rule before adding a missing bundled key.
The supported build script rebuilt the local server executable. `Test-LocalPanel-Isolated.ps1`
then started that exact build with a throwaway managed PostgreSQL cluster, checked the running
build hash and runtime root, fetched the served Rules page and rules API, and completed stage →
validate → publish → history over the loopback panel listener. Headless Chrome clicked the
served Rules page through edit → review → publish → history → revert → publish and showed the
restored value. The check stopped its own server and cluster.

**Still open:** verify a changed rule in a real FUT response. The isolated test proves the
panel and live rules state, not native client behavior.
The step 6 documentation and settings contracts in `AGENTS.md`, the runtime and server READMEs,
and `panel-api-contract.md` have been updated in the working tree.

- **Storage.** Migration 073 uses one `content_rules` row per leaf value, keyed by `(file, path)` with an
  `ordinal` that preserves array and document order. Startup fills a fresh database from the bundled files, then
  uses PostgreSQL as the active source. Newly bundled object keys are added on a later startup unless a published
  `hide` for that path is the latest change. Packs remain in their separate tables from migration 041.
- **Validation.** `GameRules` is built from these rows through the existing `RulesJson.Deserialize` validators.
  Unknown keys and typos still fail, now at Validate time instead of at startup.
- **Area handler.** The `rules` area accepts `{file, path}` keys and stages value edits, inserts or removals.
  The review page shows the before and after values.
- **Keeping repo files useful.** Add an "Import from bundled file" action that turns the difference between the
  bundled file and the database document into a changeset. Developers can still edit the JSON in the repo, then import
  and review it.
- **This is an intentional contract change.** Update in the same step:
  - `AGENTS.md` (Portable settings: "Tunable game content lives in `rules/*.json`");
  - `ultimateteam/README.md` ("Game rules and server data");
  - the rules-loading tests.

  `config.example.json` stays the only source of *server config* defaults. It is not part of this move.
- **Acceptance:** each rules file round-trips unchanged into the database and back to the same `GameRules`. A
  published market-rule change affects the next market request without a restart. A bad key is rejected at Validate.

### Step 7 — Packs onto changesets

**Implemented in the working tree, checked 2026-09-29; not yet committed.** The pack catalogue
and store lineup routes stage `store_pack` and `store_lineup` changes instead of publishing.
The pending changes page validates and publishes them together. The pack page now uses FIFA 21
fields and can show pending packs; preview and simulation read pending definitions. A focused
operator-panel test covers unpublished state, invalid validation, preview, joint publication,
stale revision, hide and revert. The isolated Chrome flow created a pack, staged it for the
store, validated and published both changes, and observed it in the store catalogue. This
does not establish that the real FUT client displayed or opened the pack.

- Change the existing pack catalogue and store-lineup edits (`OperatorPanel.PackCatalog.cs`) to create changes
  instead of publishing directly.
- Keep `PackRevisionAsync` as the pack area's conflict check.
- Remove the direct-publish routes only after the review page is proven, and update the panel contract.
- **Acceptance:** the existing pack tests pass with publishing through a changeset, and simulation or preview still
  works on unpublished changes.

### Step 8 — Remaining areas

One handler and editor at a time, each with its own tests:

1. SBC sets (`sbc.json` into the database, as in step 6)
2. objectives and season progress (details below)
3. Squad Battles, Rivals and Champions rewards
4. managers and consumables
5. customisations: kits, badges, stadium items
6. chemistry and stadiums, only if someone actually needs to edit them

#### Objectives and season progress

**How it works today (observed):**

- **Rules.** Objectives are `rules/objectives.json`. `ObjectivesService` reads them through
  `Game.Rules.Objectives` on each request (`Core/Services/Modes/ObjectivesService.cs:30`), so a published change applies
  to the next objectives request with no extra holder work.
- **Club progress.** It is stored as one counter per stat (`objective_counters`: matches, wins, goals, cleanSheets,
  draftWins, sbcs, packs) plus the claimed objective, group and level ids (`ObjectivesState`, `Core/Data/ClubRecords.cs:30`;
  `Core/Data/RelationalProgressCompatibility.cs:66`). Progress is not stored per objective.

**Editor scope:** campaign name and dates, the welcome group, XP per event, stat labels, the grind groups and their
objectives (name, stat, target, award, XP), group awards, and the Season Progress levels.

**Validation and warnings the area handler must apply:**

- **Refuse reused ids.** An objective or group id that exists in any earlier published revision may not be given to a
  different objective. Clubs that claimed the old one would see the new one as already claimed. Keep a retired-id list
  (from changeset history) and check against it.
- **Only stats the code tracks.** `stat` must be one the server counts from match ends, SBC submissions and pack
  openings. A new stat (for example assists) is a code change, not an edit.
- **Warn about retroactive completion.** A new objective on an existing stat counts each club's current total: a club
  with 300 matches completes a new "play 50 matches" at once. The review page shows how many clubs would already meet
  the target. An objective that counts only from its creation needs new code (a per-objective baseline) and is out of
  scope until then.
- **Warn on target changes.**
  - A lowered target makes clubs that already meet it able to claim at once.
  - A raised target leaves claimed objectives claimed and moves unclaimed ones further away.
  - The review page shows the number of clubs affected either way.
- **Removed objectives** leave their claim rows in place (harmless). The editor hides an objective rather than
  reusing its id later.
- **Awards.** Pack awards must name an existing pack (store, reward or player-pick pack). Coin awards are within the
  balance limits the panel already enforces.
- **Client visibility** of a changed objectives screen without a new login is unconfirmed. Record it in step 10.

**Before step 6:** the step 1 reload re-read `rules/*.json`. With step 6's PostgreSQL rules,
**Reload content** reads database values; use the Rules page and a reviewed publish, or stage an
**Import from files**, to apply an edited bundled `objectives.json`.

### Step 9 — Detect changes made outside the panel

- **Backend built (2026-09-30):** migration 094 records content revisions; the snapshot captures its database revision in the same transaction as catalogue/rules/packs. `GET /content/status` reports later committed table changes without silently reloading. It uses pooled reads, avoiding an always-open `LISTEN` connection.
- **React work remains:** poll status, display affected tables and provide explicit Reload (design §6).
- **Verified:** `OperatorPanelTests.ExternalContentStatusIgnoresRollbacksAndOwnPublishAndClearsAfterReload` passed in an isolated PostgreSQL database. The broader promo backend check passed 31/31; the subsequent publish/reward regression check passed 20/20. A browser banner or real-client result has not been observed.
- **Acceptance:** a SQL edit in an isolated database raises the banner. Reload applies it through validation. A
  publish from the panel does not raise the banner for itself.

### Step 10 — Client visibility checks (live, in-game)

- For each area, record in the real FIFA 21 client whether a published change appears:
  - immediately;
  - after leaving and re-entering a screen;
  - only after a new login.
- Show that information on the review page as the per-area hint.
- These are live results. Report them separately from the build, fixture and isolated-runtime results, and mark each
  as observed or unconfirmed.

### Optional later — Scheduled publish

- A `publish_at` time on a changeset. For example, a Team of the Week changeset prepared ahead and published by the
  server at the set time, through the same pipeline.

## Risks and open questions

- **Scope of the step 1 refactor.** Consumers that capture content in constructors are the main cost; step 0 sizes
  it. Wire output must not change: existing response fixtures guard this.
- **Draft determinism:** decided in step 2: accept and warn.
- **Maintenance inputs:** resolved in step 2: there is no maintenance revision; only new clubs are set up.
- **Client art for new cards.** A new definition only renders correctly when the client has art for its asset ids.
  The editor restricts new cards to "based on an existing base card" until that is investigated.
- **One operator identity.** The panel has no user accounts (loopback is the credential), so `created_by` is a label
  the operator types, not proof of who made a change.
- **Memory.** Two snapshots exist briefly during a swap. The catalogue is about 22,500 definitions; this is expected
  to be small, but should be measured once in step 1.
- **Backups.** Live publishes bypass the deploy's `predeploy` dump. The review page offers a verified manual dump,
  and large changesets should default to taking one.
- **Local single-player users** currently edit `rules/*.json` directly. After step 6 they use the panel or the import
  action. The panel must run in local mode too, or the import must run at startup when a bundled file changed.
  Decision needed.

## Documents to update as steps land

- this file: mark each step done, with the evidence;
- [panel-api-contract.md](../../ultimateteam/panel-api-contract.md): new `/panel/api/content/*` routes;
- `AGENTS.md` and `ultimateteam/README.md`: step 6's rules move;
- [aurora-integration-status.md](../../ultimateteam/aurora-integration-status.md): the panel's new tabs;
- `ultimateteam/server/README.md`: new tests and the reload behaviour.

## Step 0 results: inventory and capture mechanism

Read from source on branch `feat/live-db-edits` (based on `main` at `13f1f2e`), 2026-09-28. Paths are relative to
`ultimateteam/server/src/`. The findings come from reading code; nothing was built or run.

### Summary

- **Almost every consumer reads content at call time, through a `GameContext`.** Services hold a `GameContext` (or
  `FutServices`) and read `Game.Players`, `Game.Cards`, `Game.Rules.X` when a method runs. The rules are mostly exposed
  as property getters, for example `private MarketRules Rules => _fut.Game.Rules.Market;`. Once `GameContext` reads
  through a live holder, these callers follow a publish **without changes**.
- **The real work is a short list of holders** (table B): objects that copy content into their own fields when they
  are constructed, or are bound once at startup.
- **The Server project** reaches content only through Core services (`session.Game`, `Fut.Rivals`) and the panel. The
  one exception is `ServeCommand.cs:278`, which reads the market maintenance interval for a log line.

### A. Content objects and where they come from

| Object | Built in | Source | Shared how |
| --- | --- | --- | --- |
| `PlayerCatalog` | `GameContext.CreateAsync` → `PlayerCatalog.FromRelationalSnapshot` (`Core/GameContext.cs:203`) | PostgreSQL catalogue snapshot | Root context; `ForPlayerAsync` copies the reference into each player context (`GameContext.cs:252`) |
| `Cards` | `GameContext.cs:205` | `PlayerCatalog` + `rules/cards.json` + `rules/market.json` + `Func<PackRules>` | Same |
| `ManagerCatalog`, `KitCatalog` | `GameContext.cs:220-221` | Catalogue snapshot | Same |
| `StadiumCatalog`, `ChemistryModel`, `SbcCatalog` | `GameContext.cs:222-224` | Files under `runtime-source/ultimateteam21/` | Same |
| `GameRules` (14 files) | `GameRules.Load` (`Core/Rules/GameRules.cs:31`) | `rules/*.json` | Same |
| `PackSettings` | `GameContext.cs:204` | PostgreSQL `store_packs` | Same object everywhere; **already live** through `Publish()` |
| `ContentCache` | `GameContext.cs:225` | Holds `PlayerCatalog` + `Cards`; owns downloads and a stop lifecycle | Same |
| `RelationalItemWireMapper` | `GameContext.cs:238` | The catalogue snapshot itself (three dictionaries by definition id) | Bound once into the shared `ClubUnitOfWorkRunner` via `BindRelationalItems` |
| `MaintenanceRevision` | `GameContext.cs:234` | Catalogue hash + every `rules/*.json` + starter config | Copied into each player context |

### B. Holders: the step 1 work list

| # | Holder | What it keeps | Why it would go stale | Proposed fix |
| --- | --- | --- | --- | --- |
| B1 | `GameContext` (`Core/GameContext.cs:69-108`) | `Players`, `Cards`, `Managers`, `Kits`, `Stadiums`, `Chemistry`, `Sbc`, `Rules`, `Content`, `MaintenanceRevision` as `init`-only properties; `ForPlayerAsync` copies them into every player context | Swapping the root does not reach open player contexts | Replace the stored values with reads through one shared `LiveContent` holder (see "Capture mechanism"). Keep the property names so callers do not change. |
| B2 | `Cards` (`Core/Catalog/Cards.cs:17-50`) | `PlayerCatalog`, `CardRules`, `MarketRules`, and dictionaries derived from them (position modifiers, chemistry styles, Aqua Pro ids, rarity groups, face value caches) | Built once from one catalogue | Rebuilt as part of each snapshot. Its caches then belong to one revision, which is correct. `Func<PackRules>` stays. |
| B3 | `ContentCache` (`Core/Catalog/ContentCache.cs:44-65`) | `PlayerCatalog` + `Cards` fields (read at `:114` to choose an asset source) | Constructor-captured | **Do not rebuild it**: it owns in-flight downloads and shutdown. Give it a `Func<ContentSnapshot>` (or the holder) and read players and cards per call. |
| B4 | `RelationalItemWireMapper` (`Core/Items/RelationalItemWireMapper.cs:29-38`) | `_catalog` (the whole `RelationalCatalogSnapshot`) and `_custom`, `_players`, `_consumables` dictionaries. It uses `_catalog.DefinitionsById` ×7, `ResolveDefinition` ×2, `Leagues` ×2, `Definitions`, `Managers`, `Consumables` | Built once; `ClubUnitOfWorkRunner.BindRelationalItems` refuses a second bind (`Core/Data/ClubUnitOfWorkRunner.cs:63-70`) | Keep one bound mapper and store. Move the three dictionaries into the snapshot and read `_catalog` and the dictionaries through the holder per call. This also fixes the "No catalog definition for owned item" throw (`:232`) for new definitions. |
| B5 | `MarketMaintenanceJob` (`Core/Services/Market/MarketMaintenanceJob.cs:18`) | `_interval` from `rules/market.json` `maintenanceIntervalSeconds` | Read once in the constructor | Either document it as needing a restart and have the review page flag it, or re-read it after each tick. Recommend: re-read, validated by the same `ValidateInterval`. |
| B6 | `MathGroups` (`Core/Sessions/MathGroups.cs:21-25`) | `MathGroupRules` from `rules/seasons.json` | Read once in the constructor | Read the rules through the holder per call. Its per-player report state stays in the object. |
| B7 | `SbcService._normalizedNames` (`Core/Services/Modes/SbcService.cs:27`, filled at `:158`) | Card name normalisations by resource id, per open club | A renamed card keeps its old normalised name while the club stays open | Key the cache by snapshot revision too, or move it into the snapshot. |
| B8 | `GameRules.Packs` (`Core/Rules/GameRules.cs:16`, `set` at `GameContext.cs:202`) | A second copy of the pack rules next to `PackSettings` | Never updated by a panel pack publish. **No runtime reader found** (no `Rules.Packs` read outside startup) | Drop the duplicate when the snapshot is built. `PackSettings` (and later the snapshot) is the only pack source. |
| B9 | `MaintenanceRevision` (`GameContext.cs:234`, read at `Core/Services/Club/ClubMaintenanceService.cs:124`) | A hash that includes the whole catalogue and every rules file | Computed at startup | Step 2 decides its inputs. Until then it stays fixed at startup, so live edits do not rerun club maintenance. |

### C. Call-time readers: live once B1 is done

These hold a `GameContext` or `FutServices` and read content when a method runs. They need no change beyond working
inside a pinned snapshot (below).

- **Item building:**
  - `ItemFactory` (`Core/Items/ItemFactory.cs:15`), `NativeItems` (`Core/Items/NativeItems.cs:21`),
    `ResourcePools` (`Core/Services/Store/ResourcePools.cs:14`);
  - the three stores inside `RelationalItemCompatibilityStore`: `RelationalDraftStore`, `RelationalCustomisationStore`,
    `RelationalSquadCompatibilityStore`.
- **`FutServices` members** (`Core/Services/FutServices.cs:15-40`), one set per open club:
  - these reach rules through getters: `ClubPresentationService`, `VanityService`, `SharedMarketService`,
    `RivalsService`, `SbcService`, `ObjectivesService`, `DraftService`, `SeasonsService`, `SquadBattlesService`,
    `WeekendLeagueService`;
  - the rest read `Game.X` inline.
- **Pack opening:** `PlayerPackRoll` (`Core/Services/Store/PackOpeningService.cs:563-590`) takes `PlayerCatalog` and
  `Cards` in its constructor, but is built **per pack opening**. It already captures one consistent copy per
  operation. With a pinned snapshot it reads both from the same revision.
- **Web routes:** `Core/Web/Routes/*` read `Context.Rules` and `Context.Cards` per request.
- **Operator panel:** the `game.Players` and `game.Cards` reads in `Server/Panel/OperatorPanel.Cards.cs` and
  `OperatorPanel.Players.cs`, and the `game.Rules` reads in `OperatorPanel.Modes.cs` and `OperatorPanel.Settings.cs`.

### D. Not content, or unaffected

- **`ServerConfig`** (`config.json`) is per-user server configuration, not game content. Out of scope.
- **Embedded `Resources/`** (JSON templates, Blaze, LSX, QoS documents, the FUT CDN allowlist) are client wire
  contracts, not operator-editable content. Out of scope.
- **Club-data caches** are not derived from the catalogue or rules:
  - `CompetitionCache` (settled weeks, standings with an expiry time; the panel already invalidates it on mode
    resets);
  - `LeaderboardService._cached` (club rankings with a timestamp).

  Standings computed under old rules expire on their normal schedule.
- **Session state:** `NativeSessions`, `PlayerRegistry`, `GameplayRelay`, `MatchmakingRegistry`.

### Capture mechanism (chosen)

The options considered:

- **A. Plain live properties.** `GameContext.Players => holder.Current.Players`, and so on. This is the smallest
  change, but a request can read `Players` from one revision and `Cards` from the next if a swap lands in between. For
  example, a pack opening reads both to build `PlayerPackRoll`. **Rejected** on consistency grounds.
- **B. A pinned snapshot per unit of work (chosen).** `LiveContent` holds `Current` (swapped with `Volatile.Write`).
  An entry point calls `using var pin = content.Pin();`, which stores the current snapshot in an
  `AsyncLocal<ContentSnapshot?>`. `GameContext`'s content properties return `Pinned ?? Current`. Callers keep their
  code, every read in one request sees one revision, and the pin flows through `await`. The codebase already uses
  `AsyncLocal` for the same kind of scoped state (`ClubUnitOfWorkRunner.Active`, `Core/Data/ClubUnitOfWorkRunner.cs:60`).
- **C. Pass the snapshot as a parameter through every service.** This touches 40-plus files and every call chain.
  **Rejected**, because B gives the same consistency without that churn.

Rules for B:

1. **Pin per unit of work, never per connection or at startup.** `Task.Run` and `Timer` copy the creating thread's
   `ExecutionContext`, `AsyncLocal` values included. A pin taken at connection or startup scope would silently freeze
   every task started under it, for example the Blaze poll loop at `Server/Blaze/BlazeConnectionHandler.cs:214`.
2. **Entry points to pin:**

   | Entry point | Location |
   | --- | --- |
   | FUT HTTP request | `Server/Http/FutEndpoint.cs:39` `HandleAsync` |
   | Blaze frame | `Server/Blaze/BlazeConnectionHandler.cs:283` `HandleFrameAsync` (per frame, not in `RunSessionAsync`) |
   | Operator panel request | `Server/Panel/OperatorPanel.cs:71` `HandleAsync` |
   | Market maintenance tick | `Core/Services/Market/MarketMaintenanceJob.cs:66` loop body |
   | Matchmaking background work | `Server/Blaze/Matchmaking/RivalsMatchmaking.cs:766` task body |
   | Club open and maintenance | `PlayerRegistry` open path (`Core/Sessions/PlayerRegistry.cs:214` onward) |

   `ClientApiEndpoint`, EASW, SSF, QoS and the redirector read no content and need no pin. One-shot CLI tools
   (`Server/Tools/ClubCommands.cs`) run against `Current`.
3. **Detect missing pins.** An unpinned read falls back to `Current`, so production keeps working. In test mode it
   records the call site, and a test asserts none occur during the normal request fixtures. That is how a missed entry
   point is found.
4. **Rebuild off the request path.** A new snapshot is built outside any pin, validated, then published with one
   `Volatile.Write`. Requests already pinned finish on their revision. The old snapshot is collected when the last pin
   ends.

### Test impact noted for step 1

- **Construction sites in tests:**
  - `GameContext.CreateAsync` in 3 test files, `ForPlayerAsync` in 5, `CreateRelationalSliceAsync` in 1;
  - `new Cards(` in 1, `PlayerCatalog.FromRelationalSnapshot` in 1.

  Keeping these factory signatures unchanged keeps those tests compiling.
- **New tests step 1 needs:**
  - a swap during a delayed request (the request finishes on its pinned revision);
  - an unpinned-read detector over the existing request fixtures;
  - a reload that fails validation and leaves the old snapshot in place;
  - byte-identical wire output for an unchanged catalogue across a reload.

### Step 1 work list, in order

1. **Snapshot and holder.** `ContentSnapshot` (players, cards, managers, kits, stadiums, chemistry, SBC catalogue,
   rules without the duplicate `Packs`, the mapper's dictionaries, the raw `RelationalCatalogSnapshot`, `Revision`)
   and `LiveContent` (`Current`, `Pin()`, `Publish()`).
2. **Startup builds revision 1 through the same `BuildAsync` a reload uses** (one loader, one validation).
3. **B1:** `GameContext` properties read through `LiveContent`, with `ForPlayerAsync` sharing the holder instead of
   copying values.
4. **B3 and B4:** `ContentCache` and `RelationalItemWireMapper` read through the holder, and the single binding stays.
5. **B5–B8.**
6. **Pins at the entry points above**, plus the test-mode unpinned-read detector.
7. **The panel `POST /panel/api/content/reload` route and button.**
8. **The step 1 acceptance tests.**
