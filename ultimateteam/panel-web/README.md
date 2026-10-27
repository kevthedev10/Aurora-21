# Operator panel

React + TypeScript with Motion. See the root [README](../../README.md) for setup
and test commands and the [contribution guide](../../CONTRIBUTION.MD) for development
rules. [The API contract](../panel-api-contract.md) documents routes and payloads.

## Run and build

From this directory:

```sh
npm ci
npm run dev
npm test
npm run typecheck
npm run build
npm run stamp:check
```

Vite serves `http://127.0.0.1:5173/panel/` and needs a running backend.
It proxies API, image and uploaded-head requests to
`AURORA21_PANEL_URL` (default `http://127.0.0.1:18448`). The proxy rewrites
Origin for that backend without adding forwarded headers; production access
checks remain unchanged.

For an isolated backend, run `ultimateteam/Start-Panel-Review.ps1` in Windows
PowerShell from the repo root after installing PostgreSQL and restoring .NET
dependencies. Follow the [root quick start](../../README.md#run-the-panel-for-local-testing).
The script builds the server using the existing frontend bundle; it does not
run npm or launch a game/browser. Ports 18448 and 18549 must be free.
It creates temporary config/database state, clears an external database connection
and the Discord bot token, and leaves the server running for manual review.
Real Discord calls and VPS backup/restart workers are not part of this review.

The production build is committed in
`../server/src/Aurora21.UltimateTeam.Server/Resources/panel/` and embedded by C#.
Commit it with frontend source. `stamp:check` detects stale sources;
`PanelBuildTests` checks the embedded build. Build frontend and C# sequentially.
A running server needs a rebuild/restart to serve a changed embedded bundle;
the dev server serves current frontend source.

React is the only panel, served at `/panel/`. Existing `/panel/next/` bookmarks
redirect to `/panel/`; the browser keeps their hash routes.

## Stop the review server

Stop Vite with Ctrl+C. For the backend, use the information written by the review
script. This PowerShell example verifies the exact executable and test arguments
before stopping it, then selects its temporary runtime for the database stop:

```powershell
$review = Get-Content "$env:TEMP\aurora-panel-review-info.json" -Raw | ConvertFrom-Json
$reviewProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $($review.processId)"
if ($reviewProcess) {
    if ($reviewProcess.ExecutablePath -ne $review.executable -or
        $reviewProcess.CommandLine -notmatch '--http-only-test-port\s+18549' -or
        $reviewProcess.CommandLine -notmatch '--panel-api-port\s+18448') {
        throw 'Process does not match the recorded review server'
    }
    Stop-Process -Id $review.processId
}
$env:ULTIMATETEAM_RUNTIME_ROOT = $review.runtimeRoot
$env:ULTIMATETEAM21_USER_CONFIG_ROOT = $review.configRoot
$env:ULTIMATETEAM_POSTGRES_ROOT = (Resolve-Path ..\postgres).Path
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
& $review.executable postgres stop
if ($LASTEXITCODE -ne 0) { throw 'Review database stop failed' }
```

Run this from `ultimateteam/panel-web` in a separate PowerShell session and close
that session afterwards, so the temporary environment overrides do not affect
later game launches. Keep the review folder/logs if you need to inspect a failure.
Do not stop processes by name or use the normal game stop script for this test instance.

## Pages and source

Desktop navigation keeps Dashboard, Players / Clubs, Content, Operations and
search in one row beside the logo. Content and Operations open animated mega
menus; click a group or press Arrow Down to open it, and Escape closes it and
returns focus to its button. Phones keep the grouped navigation drawer.
Objectives is linked from Content and currently shows a Coming soon placeholder.

| Area | Source |
| --- | --- |
| Navigation, search, pending drawer and external-change warning | `src/app/` |
| Typed API helpers and responses | `src/api/` |
| Shared controls, dialogs, tables and Motion | `src/ui/` |
| Cards and card editor | `src/pages/cards/` |
| Players, clubs, inventory, modes and grants | `src/pages/players/` |
| Packs/picks with their use (Store, Rewards, Unassigned), store lineup, simulation, gifts and the event odds step | `src/pages/packs/` |
| Promo browser, buckets and rating-range creation | `src/pages/promos/` |
| Standard pool, promo events and Icon rotations | `src/pages/events/` |
| Modes: settings and reward tables per mode (FUT Champions so far) | `src/pages/modes/`, `src/api/modes.ts` |
| Shared reward editor (coins, FC Points, XP, packs with count and tradeability) | `src/ui/RewardEditor.tsx` |
| Rules and pending/history review | `src/pages/rules/`, `src/pages/changes/` |
| Dashboard, access, matchmaking, logs and settings | `src/pages/operations/` |

Modes owns competitive scoring, ranking, entry fees and rewards. Objectives owns
the objective catalogue and Season Pass; SBCs owns its catalogue and settings.
Rules lists only the remaining active gameplay settings.
Draft's active pick controls are under Modes → Draft, grouped into Basics,
Weights (normal and weak rounds side by side), and Special rounds. Entry costs
remain above the separate Rewards and Pick settings views. Structural Draft
defaults have no panel editor.
Its `view=rules` API requests filter duplicates and unused controls before search
and pagination. Local startup defaults are omitted; shared club naming rules remain.
Promos can be created and populated with existing native card versions.
Add or remove members, edit their buckets and preserve per-card reductions.
Remaining acceptance work is tracked in [the panel TODO](../../todo.md). Completed native checks are in
[game acceptance](test/game-acceptance.md); store-limit wire/display evidence is
in [store limits](test/store-purchase-limits.md).

## Images and static constraints

Panel images live in [../panel-assets/](../panel-assets/README.md), not AppData.
The backend prefers that directory when present; the review script passes it
explicitly. Hosted installations may override it with `--panel-assets-dir`.
Uploaded card images use private `/panel/heads/` URLs.

Bundles go under `/panel/app/`. Preserve the server's static-file allowlist
and CSP: no inline scripts, data fonts, or unsupported output extensions
(`.map`, `.ico`) without updating the server contract.

## Browser review

Use the temporary review database for writes. Check:

- All pages at desktop and narrow widths, long labels, loading/empty/error states.
- Keyboard navigation, Escape, opener focus return, Ctrl+K and reduced motion.
  `#/_kit` is the shared component playground.
- Cards/table sorting, paging, filters, GK/outfield stats and image selection.
- Players → Overview → Reset club: offline-only confirmation, cancellation,
  failure without clearing state, refreshed empty balances/inventory and the audit
  log. Use a disposable review club; verify onboarding in FIFA separately.
- Pack/pick editors (unassigned creation, pack art, complete odds tables with the upgraded row, view-only packs), the
  usage filters, the event odds step after staging an event, simulations,
  grant tradeability and retained unsaved values.
- Promo bucket shares, independent bucket paging, rating-range preview and moves.
  In a promo's bucket editor, mark a member **Desirable**, then choose its reduction
  (default 5×), stage and publish. Unchecking restores ordinary weight. Marks apply
  only to that promo and follow cards moved to another bucket. Gold Common has no
  desirability controls. Check preview, publication and History revert; see the
  [migration 120 profile](../../docs/gold-and-base-icon-bucket-balance.md).
- Standalone TOTW/Icon windows in Schedule → Rotations, UK dates and overlap errors.
- Modes → FUT Champions: edit a rank's coins, FC Points and packs (count, tradeable or
  untradeable), add and remove a rank, the incomplete-table warning (uncovered wins, more
  than 12 win ranks, bands not running on from position 1), a setting edit, Undo staged
  change, publish and History revert. Then check the prize tiers and a claim in FIFA.
- Stage → Validate → Publish → History → Revert. Filters do not limit publication.
- Stale validation, row conflicts, failed restaging and externally changed content.
  The shared warning polls every ten seconds; explicit reload preserves pending edits.
- Access/settings drafts after refresh, immediate-save confirmations and failures.
- Matchmaking phase sections and bounded logs with Pause/Resume.

Conflict recovery uses separate DELETE/PUT requests; check Pending before retrying
an uncertain request. Newly inserted cards cannot be reverted through History.
Shared navigation does not yet guard every unsaved editor draft.

Automated tests use synthetic API replies or disposable databases. Browser
acceptance and native FIFA visibility must be recorded separately; a successful
build does not establish either.

Mode tabs expose a match completion reward: flat coins, one optional catalogue pack,
and its tradeability. Draft has separate online and single-player controls. These
stage `seasons.matchCompletionRewards.<mode>` as one rules change and publish through
Changes. Coins do not depend on result, match statistics or CPU difficulty. The
existing quit, minimum-duration, desync and opponent-left rules still apply. One
selected pack is granted per eligible match, without a shared daily cap. Previously
earned packs retain their stored tradeability. Weekly/rank and Draft run rewards remain separate.

## Scheduled content

Open **Schedule** to create a campaign with start and end dates, then **Add drop**
inside that campaign. Each drop has its own start and end dates within the
campaign window. All dates use 19:00 in `Europe/London`, including the winter GMT change. Month/week calendars
and the daily agenda show drafts, scheduled drops, live content and failures.
The campaign browser shows one campaign at a time, initially the active campaign
(or the next upcoming campaign). Previous/next, a picker and **Go to active campaign**
change the displayed campaign. Each campaign shows five drops per page.
The panel uses the existing Events time conversion to send UTC timestamps for
the selected UK dates; the server receives those timestamps.

Normal card, promo, pack, SBC, objective and rules edits save live immediately.
Each save validates and commits independently, keeps History, and preserves other
pending edits and drop drafts. New special cards enter the catalogue; assigning
their promo to a drop controls when they become available in the general pool.

Campaigns contain promo drops. Assign one or more promos, store packs, SBCs or
objective groups in the drop editor, then choose **Schedule drop**.
The content picker is available while creating a drop: **Add to drop** saves its
name and dates automatically and keeps the editor open for further additions.
**Save drop** includes the currently selected content and keeps the editor open; neither action schedules the drop.
**Schedule drop** saves remaining name/date edits and the selected content before queuing the release.
Saving and scheduling show progress feedback while their requests run.
TOTW, Icons and Standard pools are separate under Schedule → **Rotations**.
New TOTW/Icon rotations use date-only fields at 7pm London; existing windows
retain their exact timestamps until their dates change. The Standard pool remains
always available. Old Events bookmarks redirect to this section; Events is no
longer a navigation item. Legacy ordinary events remain editable for migration.

New drops initially use the campaign dates; save each drop's own dates. Advanced item overrides can end earlier or
persist. Expiry changes availability while preserving definitions, owned packs
and objective progress. **End now** closes a live drop. **Cancel** stops an
unpublished drop. Editing queued content returns affected dependent drops to
Draft for review and rescheduling. Draft validation and timed previews project
earlier scheduled drops without publishing them.

The server catches up overdue releases before accepting gameplay requests and
applies expired windows without exposing them. Failed drops are shown for
review; independent releases continue. Server checks use temporary PostgreSQL
clusters. Native client acceptance still needs a separate in-game review.

Squad Foundations use simple Rivals/FUT Champions conditions: score in 10 separate
matches (at least one goal per match), score 10 goals in total, and the existing
play/win targets. They no longer require particular players or goal/assist types.
Migration 169 updates existing imported definitions without changing reward IDs,
claims, live state or objective counters. Foundations remain off until enabled.
Normal objective groups expose an **In-game description** field: `{n}` follows the
target, and clearing the field restores the stat's standard description.

Store Lineup, Token Store Lineup, Live SBCs and Live Objectives show separate
**Live** and **Scheduled** sections. Their add buttons open **Schedule content**:
choose the content, start date and required end date (plus price/tradeability for
store offers), then **Save release**. These create standalone drops in the same
schedule. Future windows are scheduled; already-open windows publish immediately
while retaining their original start. Every window expires automatically.
New availability cannot be activated with the old switches or by directly saving
an enabled lineup through the panel API. Existing live content retains its state,
and price/tradeability, definition edits, reordering and removal remain available.
New objective groups save to the catalogue without automatically entering Live.

Adding a promo to a drop opens the existing pack-odds step for general-pool packs
missing that promo's odds row. Choose a percentage and the packs receiving it;
unticked packs receive an explicit 0%. Existing percentages remain unchanged.
Odds save immediately, while general-pool eligibility still waits for the promo
window. Scheduling an existing draft also checks this step; skipping keeps it a draft.
**Delete campaign** is available only outside its live date window and with no live
drops. Confirmation removes the selected campaign and its unpublished drops;
completed release history and content definitions remain. Individual drops retain
Cancel drop; there is no campaign-wide cancellation.

SBC editors offer Once, Unlimited and Daily repeatability. Daily resets at 7pm London;
finish all challenges within that content day for its group reward. Requirements support
minimum, maximum and exact squad/group/player counts, including bronze/silver/gold counts
and searchable native league, nation and club selections. Existing named-league rules
remain valid; new requirements use the generic selector types. Migration 170 adds daily
claims without deleting lifetime SBC completions and adds the new selector definitions
for existing databases.
