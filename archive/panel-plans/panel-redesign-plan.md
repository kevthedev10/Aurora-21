# Operator panel redesign (React + TypeScript) — plan

> Archived on 2026-10-01. This is implementation history with historical status
> claims, not the current plan. Use the [repository README](../../README.md),
> [contribution guide](../../CONTRIBUTION.MD) and
> [panel guide](../../ultimateteam/panel-web/README.md) for current instructions.

**Current direction (2026-09-30):** continue the existing React + TypeScript app through the
[module review below](#frontend-module-review-and-motion). Review, fix, animate, and test one module
before moving to the next. Motion is installed; module 1 is done and accepted by the maintainer. The original steps and results
remain implementation history; module 3 passed the maintainer's browser review. Module 2's publishing review remains separate. Making the React panel the default remains **original step 7**.

Status (2026-09-29, `feat/live-db-edits`, uncommitted): **steps 0–3 are done; steps 4, 5.1–5.5 and 6.1–6.5 are built and waiting for the maintainer's check against a real server; step 7 remains separate** (see [Steps](#steps)). Decisions taken with the
maintainer are listed under [Decisions](#decisions):
- rewrite the front end in React and TypeScript;
- a *hybrid* look (console layout, FUT character where the content is FUT: cards, packs, clubs);
- first priorities: **players and clubs**, **content editing**, and **navigation and layout**;
- **build it next to the current panel**, never over it: the new app lives in its own folders at `/panel/next/`,
  and the current plain-script panel at `/panel/` stays untouched and working throughout, as the fallback.

## Frontend module review and Motion

Agreed with the maintainer on 2026-09-30. Build on the working React app in `panel-web/`.
The review order below guides the next frontend work and does not renumber the original implementation
steps. Promo terminology and backend scope follow [promo-groups-plan.md](promo-groups-plan.md);
live editing follows [live-content-editing-plan.md](live-content-editing-plan.md), with routes and
payloads documented in [panel-api-contract.md](../../ultimateteam/panel-api-contract.md).

### Review order

Modules 1 and 3 are **done and accepted**; module 2 is built, with publishing review still pending; the remaining modules are pending. Existing implementation does not mean browser acceptance
is complete; the promo management pages are new work.

| Order | Module | Review and implementation scope | Status |
| --- | --- | --- | --- |
| 1 | Shared UI and navigation | Install Motion; shared animation settings; dialogs, drawers, tabs, notifications, mobile navigation, keyboard and focus behavior | Done; accepted 2026-09-30 |
| 2 | Changes and live editing | Staging, pending previews, validation, publishing, conflicts and errors, history, external database change warning and explicit reload | Built; awaiting browser review |
| 3 | Cards | Search and filters, rendered previews, creation and editing, existing head and dynamic image selection | Done; browser review accepted 2026-09-30 |
| 4 | Promos and buckets | New management pages; promo metadata, card membership, promo and bucket weights, creating explicit memberships from rating ranges | Pending |
| 5 | Promo events and Icon rotations | New scheduling pages; UTC windows, availability preview, validation, current Icon tier and Base fallback | Pending |
| 6 | Packs and player picks | Catalogue and editors, standard pool and promo sources, category odds, pick groups, store lineup, simulations and whole-pack odds preview | Pending |
| 7 | Players and clubs | Player search, club tabs, inventory, gifts, balances, modes, access and sessions | Built; awaiting browser review |
| 8 | Dashboard and operations | Dashboard, settings, rules, logs, matchmaking, access and maintenance | Pending |

### Process for each module

1. Inspect the current page, shared components, API calls, and backend contracts. Identify working
   behavior, issues, missing features, and any backend dependency.
2. Present the proposed changes and agree on the module's scope with the maintainer.
3. Implement the fixes and Motion animations together, reusing the shared components.
4. Run type checks, focused behavior tests, and the production frontend build. Add backend tests
   when an API change is necessary; use isolated data for automated checks.
5. Check the module in the browser with the maintainer, including its real API behavior, loading,
   empty and error states, narrow layouts, keyboard navigation, and reduced motion. Publishing or
   immediate club edits use an explicitly agreed test target.
6. Record changes, automated results, browser results, and remaining limitations here. Fix issues
   within the agreed scope before marking the module accepted and moving to the next.

Automated checks and browser acceptance are recorded separately. A successful build alone does not
complete a module. If a dependency blocks acceptance, record it and agree on the next action.

### Animation approach

Use [Motion for React](https://motion.dev/docs/react-animation), bundled with the app through
`motion/react`. Set shared transition defaults and respect the user's reduced-motion preference.
Start with dialogs and drawers entering and leaving, expanding sections, and feedback for state changes.
Keep animations subtle and responsive; they must preserve focus, editable form state, and click targets.
Avoid animating every row or card in large virtualized lists.

The shared UI review should establish reusable animation patterns that later modules can adopt.
Check animations in the browser, including rapid opening/closing and reduced motion; behavior tests
should verify interactions and state rather than exact animation timing.

### Final rollout

After the module reviews and acceptance checks, proceed to **original step 7 — Make the new panel
the default**. That remains a separate approval and rollout, including final parity checks and a
fallback decision. The eight module review numbers above are not replacement implementation steps.

### Review log

- 2026-09-30: review order and Motion approach agreed; documentation recorded. Next: module 1,
  shared UI and navigation. No Motion dependency or frontend behavior changes made in this update.
- 2026-09-30: module 1 implemented. Motion 13.4.6 is pinned and bundled locally. Shared dialogs,
  drawers and command search have enter/exit transitions, opener focus restoration and interrupted
  exit handling. Navigation and tab indicators animate within separate layout groups; notifications
  animate and cancel their timers when dismissed or unmounted. Reduced-motion changes apply without
  a page reload. Mobile navigation is a modal drawer, closes even on same-page links, and closes when
  resizing to desktop. Added a skip-to-content link and fixed empty-result keyboard selection and
  scrolling in command search. Added a sample drawer to the component kit.
  - Verification: 36 focused checks passed across seven test files: 25 checks in modal, confirmation,
    route, card-editor and pack-editor tests; 11 checks in the final shared-state and app tests.
    The first tab-state fixture used a click-only event; corrected it to the mouse-down event used
    by Radix, then reran that file. Type checks and the production frontend build passed.
  - Browser review checklist: use `/panel/next/#/_kit` to check dialogs, the sample drawer,
    confirmations, notifications and tabs; test Ctrl K, mobile Menu, Escape, focus return, scrolling,
    rapid reopening and reduced motion. See [panel-web/README.md](../../ultimateteam/panel-web/README.md#browser-review).
  - Built embedded assets are updated. An already-running server needs rebuilding/restarting to serve
    those assets; the Vite dev server can preview current source against its API. No live service or
    database changes were made during implementation.
- 2026-09-30: built and started an isolated local test backend for the maintainer's review at
  `http://127.0.0.1:18448/panel/next/`. Verified the current server build, test-mode database isolation,
  panel page, session API and image availability. The maintainer approved the result and requested
  module 1 be marked done. **Module 1 is accepted.** This records the maintainer's acceptance, without
  claiming that every browser checklist item was individually reported. Next: module 2, Changes and
  live editing; it has not started.
- 2026-09-30: module 2 implemented against the existing content API. Added the shared 10-second
  external database change warning, changed-table details, explicit confirmed reload and reload reports.
  Reload keeps pending edits staged. Publish and both reload entrypoints invalidate cached panel data
  while leaving local forms intact. Pending review now filters by area/search and shows nested pack,
  card and promo fields; publishing still includes every pending edit. Validation is tied to the
  reviewed changeset and loaded status, and becomes stale when either changes. Publish/discard
  confirmations close when the reviewed values change. Conflicts show original, live and staged
  values. Shared mutation state prevents overlapping actions while requests/refetches are running.
  History and feedback expand with Motion, respect reduced motion, and remove exiting content from
  keyboard/accessibility interaction.
  - Conflict recovery: Keep my value still uses the API's separate DELETE and PUT transactions.
    Before either request, retain its exact value in this tab's session storage where available.
    Failed restaging shows a retained draft with retry/copy controls and a separately confirmed local
    discard. The recovery survives page remounting; a connection failure still needs a check of Pending
    before retrying. No backend transaction redesign was made in this frontend module.
  - Verification: 32 distinct focused frontend checks passed across five files: 31 in the grouped
    run, followed by the final Changes file run (14 passed) including the added monitoring-failure
    confirmation check. Type checks, production build and source stamp passed. The existing Vite
    warning about a JavaScript chunk over 500 KB remains (~634 KB); code splitting is not part of
    this module. No live service was started and no real database was edited. No backend suite was
    run for this frontend-only implementation.
  - Browser acceptance is **pending**. Review `/panel/next/#/changes` with an explicitly agreed isolated
    test database; checklist in [panel-web/README.md](../../ultimateteam/panel-web/README.md#browser-review).
    Check staging, validation becoming stale, confirmed publish, history/revert, row conflicts,
    external SQL warning/reload, long values, keyboard use, narrow layouts and reduced motion.
    Promo availability and draw preview pages remain in modules 4–6; this module reviews stored pending
    values and validation. Module 3 had not started at this checkpoint.
- 2026-09-30: module 3 implemented; the maintainer agreed to review modules 2 and 3 together.
  Card search preserves focus as URL filters update; invalid rating ranges pause requests, and paging
  labels retained results while fetching. Detail links resolve by resource ID after a rating edit.
  Pending cards link back to their editor from Changes. The editor preserves typed values through
  refetch errors, detects changed live/pending values, confirms form resets and dirty Cancel/Back,
  and shares the content-action lock. New cards retain their allocated ID across saves and can reopen
  from Pending; an old draft address cannot silently recreate a published or removed draft.
  - Portraits: visual keyboard-selectable options include existing same-player dynamic images and
    uploads, including selectable options whose local preview is missing. Uploads fit inside a transparent
    180 × 180 canvas without stretching and become selected immediately. Published custom portraits
    now display in panel cards. Uploading stores an option; staging and publishing apply it to a card.
  - Motion: card detail entry and expandable attribute sections use shared transitions and reduced
    motion. Collapsing a section keeps its draft values. The editor includes a live card preview.
  - Backend additions: optional exact `resourceId` card lookup, editor art metadata, and published
    `customHeadId` art metadata. Existing content payloads and native game contracts are unchanged.
  - Verification: 53 focused frontend checks passed across five files, including the combined
    stage/reopen/validate/publish workflow, upload selection, retry IDs and search focus. Two focused
    backend tests passed against isolated data, covering exact lookup and published portrait metadata.
    Type checks, production build and source stamp passed. The existing large-chunk warning remains
    (~645 KB). The full backend suite and native in-game behavior were not tested.
  - Browser acceptance is **pending**. Use the joint checklist in
    [panel-web/README.md](../../ultimateteam/panel-web/README.md#browser-review) with an agreed isolated database.
    Dirty Cancel/Back is guarded; navigation through the shared rail does not yet have a form guard.
    The server and panel remain off, and no real database was edited. Module 4 has not started.

## Goal

Module 3 follow-up (2026-09-30): stat labels and sections now follow the selected GK/outfield position,
preserving hidden draft fields. Dynamic portrait options require downloaded images, exclude Champions
red copies and the editor's source card, and identify borrowed images by their source card. Browser
acceptance remains pending; rebuilding/restarting the review server is needed to load these fixes.

Further maintainer feedback (2026-09-30): standard new-card types collapse to Rare/Non-rare in a
searchable selector with miniature frames. Rating picks the bronze/silver/gold level, including
published edits to existing standard cards. Overall moved to identity; six stat groups pair face
stats with their detailed attributes. The supplied weighted formulas work in both directions;
reverse edits shift contributors with clamping and rounding, while reactions/composure remain
independent of DRI. Traits expose GK/outfield/shared applicability and the editor filters them by
position. Card art uses short surnames/aliases with the Neymar Jr exception. Supplied Pro Player
and Champions Gold PNGs are embedded asset fallbacks. Navigation now uses a horizontal icon bar
with Motion hover/selection pills, taking direction from Motion's Smooth Tabs example, while mobile
keeps the modal drawer. This revises module 1's previously accepted layout; renewed browser review
is pending. Services were not started or restarted during implementation.
Verification of this follow-up: 49 focused frontend tests passed across five files, nine focused
backend checks passed, and the asset check passed again with real catalogue surname/alias assertions
(Ronaldo, Firmino, Neymar and De Bruyne). Type checks and production build passed; bundled PNGs are
644 × 900 RGBA originals. The JavaScript chunk warning remains (~652 KB). The full suite, browser
appearance and native in-game behavior were not tested in this pass.

Picker/layout correction (2026-09-30): card-type choices no longer disappear during pointer-induced
blur before their selection event. Outside pointer actions and keyboard focus leaving the control
close the picker; Escape restores the opener. A TOTW regression follows the actual pointer/blur/click
sequence and checks the staged type. Face scores are larger highlighted inputs in each section header
and remain editable when detailed attributes are collapsed. Global search sits beside the brand;
Blaze/FUT/online and pending status move to a compact footer. Browser acceptance remains pending.
Verification: 42 distinct focused frontend tests passed (41 in the grouped run, then 11 shell tests
including the added placement check). Type checks, production build and source stamp passed.
No backend changes or service restarts were made for this correction.

A panel that is quick to find things in, makes the difference between an *immediate* club edit and a *staged*
content edit obvious, and shows FIFA 21 content the way the game does (drawn cards and packs),
while the server API, its security model and its wire behaviour stay exactly as they are.

Out of scope: any change to what the API returns (except the few additions named below, each with its own
server test), the hosted/Blaze/FUT paths, and the access model.

## Current state (observed)

- Plain-script front end copied from Aurora16 (`Resources/panel/PROVENANCE.md`), no build step:
  `js/core/{panel,ui,router,fut-card}.js` and 12 page scripts, about 3,600 lines, plus `css/panel.css`
  (30 KB) and self-hosted IBM Plex fonts. Embedded by the server csproj (`Resources\panel\**\*.{html,js,css,woff2}`
  with logical name `panel/...`).
- Served by `OperatorPanel.HandleAsync` (`server/src/Aurora21.UltimateTeam.Server/Panel/OperatorPanel.cs`):
  - `/panel/` and `/panel/index.html` return the shell; `^/panel/([A-Za-z0-9_/-]+\.(js|css|woff2|png))$`
    returns an embedded file; `/panel/assets/<category>/<file>.png` returns a downloaded image (checked first).
  - The HTML carries `Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline';
    img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'`.
  - `Refusal()` runs before every route: loopback remote and local address, no `Forwarded`/`X-Forwarded-*`,
    a loopback `Host`, `Sec-Fetch-Site` same-origin (or a same-site document GET), an `Origin` equal to the
    `Host`, and `Content-Type: application/json` on writes.
- Navigation is 14 flat links. Pages: Dashboard, Players (list and one player's club), Pack catalogue, Store,
  Packs (store packs, unopened club packs, gifts), Cards, Modes, Matchmaking, Logs, Maintenance, Settings, Rules,
  Pending changes, Change history.
- API: about 40 routes under `/panel/api/` (see `panel-api-contract.md` and `live-content-editing-plan.md`).
  Pages guard every field because the static files can be newer than the running API.
- Tests that touch the front end: `OperatorPanelTests` requests `/panel/js/core/panel.js`,
  `/panel/js/pages/{players,modes,changes}.js` (lines 75–78); nothing tests page behaviour.
- The squads API returns `id, name, formation, players` only (`rating` and `chemistry` are always null, and
  there are no slot positions), so a pitch view needs an API addition.

## Side by side with the current panel

| | Current panel (kept as is) | New panel |
| --- | --- | --- |
| Source | `Resources/panel/` (plain scripts) | `ultimateteam/panel-web/` (React + TS) |
| Built files | the source itself | `Resources/panel-next/` (committed build output) |
| Embedded as | `panel/...` | `panel/next/...` |
| URL | `/panel/` | `/panel/next/` |
| Bundles | `/panel/js/...`, `/panel/css/...` | `/panel/next/app/...` |

Both apps call the same `/panel/api/*` and read the same `/panel/assets/` images; neither owns the API. No file
under `Resources/panel/` is edited by this plan (not even to add a link: the new app links back to the old one).
Going back is always "open `/panel/`". Retiring the old panel is a separate, later decision (step 7), not part of
the port.

## Does React work with the loopback model? Yes, unchanged

The built app is ordinary static files served by the same server on the same private port:

- **Same origin.** The page and `/panel/api/*` share `http://127.0.0.1:<panel port>`, so `Sec-Fetch-Site`
  stays `same-origin` and `Origin` matches `Host`. `fetch` sends JSON with `Content-Type: application/json`,
  as today. `Refusal()` does not change.
- **CSP.** A Vite production build emits `<script type="module" src=...>` and `<link rel="stylesheet">`
  with no inline script, and React needs no `eval`, so `default-src 'self'` holds. Inline `style=` attributes
  are already allowed. Fonts stay self-hosted (no Google Fonts: `default-src 'self'` would block them).
- **File names.** Vite's hashed names (`index-B1x_9aQz.js`) fit the existing static-file pattern
  (`/panel/next/app/index-B1x_9aQz.js` resolves to the embedded `panel/next/app/...`). Two build settings keep
  it that way:
  - `base: '/panel/next/'`.
  - `build.assetsDir: 'app'`, so bundles never share the `/panel/assets/` image namespace.

  No `.svg`, `.map` or `.ico` output unless the pattern and content types are extended (icons become inline
  React components instead).
- **Deep links.** A hash router (`#/clubs/123/inventory`) needs no server route; the server only serves the new
  shell at `/panel/next/` (the one new route).
- **Development only:** the Vite dev server (bound to `127.0.0.1`) proxies `/panel/api` and `/panel/assets` to a
  running local panel. The browser's `Origin` would be the dev server's, so the proxy rewrites `Origin` to the
  target and adds no `X-Forwarded-*` headers. This is a dev-tool setting; the server's checks are not relaxed.

## Design

### Direction: hybrid

The frame of the panel is a calm operator console; FUT only appears where the content *is* FUT. The
downloaded card art is the one loud element: everything around it stays quiet so the cards read.

### Tokens (proposal, reviewed on the kit page in step 2)

| Token | Value | Use |
| --- | --- | --- |
| `night` | `#141A24` | page background: blue-slate, a floodlit stadium at night |
| `stand` | `#1C2330` | panels, table rows |
| `tier` | `#262F40` | raised surfaces, hovered rows, inputs |
| `line` | `#33405A` | borders and dividers |
| `chalk` | `#EEF1F6` | primary text |
| `fog` | `#9AA6B8` | secondary text |
| `coin` | `#E9B949` | primary actions and focus: FUT's coin gold |
| `pitch` | `#3BAA6E` | live, online, OK |
| `staged` | `#9A8CF2` | anything pending review (changesets); never used for anything else |
| `flare` | `#F08A3C` | warnings, maintenance |
| `red-card` | `#E5534B` | danger, blocked, errors |

`staged` is deliberately its own colour. A staged content edit and an immediate club edit look different
everywhere: in buttons, toasts, badges and the header.

### Type

- **Display and numbers: Barlow Condensed** (SIL OFL, self-hosted, 600/700). It is used for page titles,
  ratings, coin and point figures, and big stats; FUT's own UI numerals are condensed.
- **Interface and body: IBM Plex Sans** (already bundled).
- **IBM Plex Mono** is kept only for IDs, JSON paths and logs.
- Scale: 13 / 14 / 16 / 20 / 26 / 34 px. Sentence case everywhere, with no all-caps labels. The only
  uppercase is the name bar on a drawn card, as in the game.

### Layout and navigation

```
┌────────────┬──────────────────────────────────────────────────────────────┐
│ AURORA 21  │  [ Search players, cards, packs…  Ctrl K ]   ●Blaze ●FUT 12 on │
│            │                                   ◆ 3 pending  ▸ Review       │
│ Overview   ├──────────────────────────────────────────────────────────────┤
│ Clubs      │  Page title                                     [ actions ]  │
│ Content    │  one-line description                                         │
│  Cards     │  ┌────────────────────────────────────────────────────────┐  │
│  Packs     │  │ content                                                │  │
│  Rules     │  │                                                        │  │
│  Changes ◆3│  └────────────────────────────────────────────────────────┘  │
│ Operations │                                                              │
│  …         │                                                              │
└────────────┴──────────────────────────────────────────────────────────────┘
```

**Navigation (decided 2026-09-29).** The classic panel's 14 flat links become 9 pages in four groups. Pages that
only served one club or one workflow are folded into the page they belong to.

| Group | Menu item | Holds (classic pages) |
| --- | --- | --- |
| Overview | Dashboard | Dashboard |
| Clubs | Players | Players list, plus the club page with tabs: Overview, Inventory, Packs & gifts, Modes, Access & sessions, Log. Classic Modes and one-club gifting move into these tabs. |
| Content | Cards | Cards (and later the card editor, live-content-editing-plan step 5) |
| Content | Packs | Tabs: Catalogue, Store lineup, Simulate, Gifts (gift to every club, recent gifts). Classic Pack catalogue, Store and Pack gifts. |
| Content | Rules | Rules |
| Content | Changes | Tabs: Pending, History. Classic Pending changes and Change history. |
| Operations | Access & maintenance | Maintenance |
| Operations | Matchmaking | Matchmaking |
| Operations | Logs | Logs |
| Operations | Settings | Settings |

Later content areas (objectives, SBCs, seasons and so on, from live-content-editing-plan step 8) are added under
Content as they get editors.

**Routes.** Hash routes, so nothing changes on the server. Classic route names redirect, so links and habits from
the classic panel keep working.

| New route | Page | Classic equivalent (the "Classic panel" link) | Also accepts |
| --- | --- | --- | --- |
| `#/dashboard` | Dashboard | `#/dashboard` | `#/` |
| `#/players` | Players list | `#/players` | |
| `#/players/<persona>/<tab>` | Club page; tab is `overview` (default), `inventory`, `packs`, `modes`, `access`, `log` | `#/players/<persona>` (modes tab: `#/modes`) | `#/modes` → Players |
| `#/cards`, `#/cards/<cardKey>` | Cards, card detail | `#/cards` | |
| `#/packs/<tab>` | Packs; tab is `catalogue` (default), `store`, `simulate`, `gifts` | `#/packcatalog`, `#/store`, `#/packs` | `#/packcatalog`, `#/store` |
| `#/packs/catalogue/<id>` | Pack editor (`new` for a new pack) | `#/packcatalog?edit=<id>` | |
| `#/rules`, `#/rules/<file>` | Rules | `#/rules/<file>` | |
| `#/changes`, `#/changes/history` | Changes | `#/changes`, `#/history` | `#/history` |
| `#/access` | Access & maintenance | `#/maintenance` | `#/maintenance` |
| `#/matchmaking` | Matchmaking | `#/matchmaking` | |
| `#/logs`, `#/logs/<file>` | Logs | `#/logs` | |
| `#/settings` | Settings | `#/settings` | |
| `#/_kit` | Component kit (not in the menu) | | |

One clash: classic `#/packs` is Pack gifts, but in the new app `#/packs` is the Packs page. The redirect is
harmless, since gifts are a tab of it, and the "Classic panel" link maps each Packs tab to the right classic
page.

**Across every page:**
- a top bar with the service status (from the dashboard poll) and online count;
- a **pending-changes button** that opens a review drawer from any page, alongside the full Changes page;
- a **Ctrl K** search that jumps to a player (name, persona, Discord), a card or a pack;
- at narrow widths the side rail becomes a drawer; tables become stacked rows; no horizontal page scroll.

**Club page** (the first priority):

```
┌──────────────────────────────────────────────────────────────────────────┐
│ ● Online   PlayerName  · persona 1234 · Discord …   [Gift] [Grant] [⋯]  │
│ Coins 1,250,000   FIFA Points 3,400   Club items 812   Last seen 2m ago │
├ Overview │ Inventory │ Packs & gifts │ Modes │ Access & sessions │ Log ┤
│                                                                          │
│  Inventory: pile chips · search · Cards/Table                           │
│  ┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐┌──┐                                          │
│  │93││88││85││84││84││83││82││82│  (drawn cards, virtualised)          │
│  └──┘└──┘└──┘└──┘└──┘└──┘└──┘└──┘                                          │
└──────────────────────────────────────────────────────────────────────────┘
```


**Content editors** use a list on the left and an editor on the right with a live preview. For example, the
card editor (live-content-editing-plan step 5) redraws the card as stats change, and the pack editor shows its odds
and a test draw. Saving says **Stage change** and shows the `staged` marker; publishing happens only in Changes.

### Principles

1. Cards are the only loud thing on a page.
2. Every action says where it lands: **Apply** means an immediate club edit; **Stage change** means it
   waits for **Publish**.
3. The API may be older than the page: fields stay optional in the TypeScript types, and a missing
   field shows as "not reported", never as a crash.
4. Keyboard first: visible focus, Ctrl K, Escape closes, and every card is a real button.
5. Motion only answers an action: the pack test draw may reveal its cards; nothing else animates on load.

## Architecture

| Concern | Choice | Why |
| --- | --- | --- |
| Source | `ultimateteam/panel-web/` (Vite, React, TypeScript `strict`) | kept apart from the server's embedded resources |
| Routing | hash router | no server route per page |
| Server data | TanStack Query | caching, polling (status, matchmaking), invalidation after edits |
| Big lists | TanStack Virtual | cards (~16k) and 2,000-item piles |
| Primitives | Radix UI (dialog, tabs, popover, menu), styled by us | accessibility without a component kit's look |
| Styling | one tokens file plus CSS Modules | no runtime CSS-in-JS; works under the CSP |
| API client | typed `api<T>()` over `fetch`, types written from `panel-api-contract.md` | one place for errors (`{error}` bodies, 403 refusals) |
| Tests | Vitest + Testing Library + MSW mocks shaped like the real replies; `tsc --noEmit` | run locally (no GitHub Actions test jobs) |

Dependencies are pinned in a committed lockfile. Nothing is loaded from a CDN at runtime.

### Server changes (small, each with tests)

1. **Embed:** a second csproj item embeds `Resources\panel-next\**` with logical name `panel/next/...`. The
   existing `Resources\panel\**` item is left as it is. The static pattern and content types are extended only if
   the build emits a new file type.
2. **Serve:** `GET /panel/next` redirects to `/panel/next/`; `/panel/next/` and `/panel/next/index.html` return
   `next/index.html` with the same CSP; bundles are already covered by the static pattern. When nothing is
   embedded yet, `/panel/next/` answers 404 and `/panel/` is unaffected.
3. **Tests:** `OperatorPanelTests` keeps its four `js/...` requests (the old panel still ships), and adds:
   the new shell loads with the CSP, every file it references answers 200 with the right type, and the
   `Refusal()` checks apply to `/panel/next/` as to `/panel/`. A stamp test fails when `panel-web/` sources
   changed without a rebuild.
4. **API additions for the new pages** (each optional to the UI and guarded):
   - ~~squad slots with card art~~: dropped on 2026-09-29, because the panel does not show squads;
   - a search endpoint for Ctrl K if listing players/cards client-side is too heavy (measure first).

## Steps

### Step 0 — Parity inventory

List every page's reads, actions (with their API route and body), confirmations, error messages and polling,
from the current JS and `panel-api-contract.md`. Screenshot every current page through
`Test-LocalPanel-Isolated.ps1` (desktop and 390 px). **Done when** the checklist covers all 14 pages and the
maintainer has confirmed the page mapping above.

### Step 1 — Scaffold and the ship path

- Create `panel-web/` with Vite, React, TS, lint, Vitest and MSW.
- Configure the build: `base '/panel/next/'`, `assetsDir 'app'`, output `Resources/panel-next/` (emptied and
  rewritten on each build; the build refuses any other output folder, so it can never write into
  `Resources/panel/`), no source maps, and a stamp file with the source hash.
- Add the server pieces: the embed item, the `/panel/next/` route and the tests above.
- Mark `Resources/panel-next/**` generated in `.gitattributes` and update `Test-RepositoryHygiene.ps1` if it
  needs to know about the folder.
- Add the dev proxy with the Origin rewrite, bound to loopback.
- An empty shell served by a real local server passes `Refusal()` in the browser (sign-in, one GET, one
  JSON POST) under the real CSP.

**Done when** the built shell runs from the server exe at `/panel/next/` with no CSP errors in the console, and
`/panel/` is byte-for-byte the same as before (its files are unchanged in Git).

### Step 2 — Design system and kit page

- Tokens and fonts.
- Components: AppShell, NavRail, TopBar, PageHeader, Tabs, DataTable (sort, filter, paging, virtual), Dialog,
  ConfirmDialog, Toast, Field/Form, Chips, StatusPill, StagedBadge, EmptyState, ErrorPanel, and the `FutCard`
  port. The port keeps the per-frame ink, the shadow for light text, the name fitting and all image fallbacks
  measured in this session.
- A hidden `#/_kit` page shows each component in its states.

**Done when** the maintainer approves screenshots of the kit (desktop and 390 px).

### How each piece is built and checked

Every numbered piece below is one small, reviewable change, and the classic panel keeps working throughout.
- Checked by: `npm run typecheck`, `npm test` (a test per behaviour: rendering from recorded reply shapes, the
  exact API call and body sent, confirmations and error display) and `npm run build`, which rewrites the stamp.
- A piece that touches the server also gets focused `dotnet test --filter` runs.
- A piece is **done** when its step 0 checklist rows are ticked against a real local server. The maintainer checks
  it in their own browser at `/panel/next/`; any automated screenshot run is short, backgrounded and time-capped.
- Nothing merges half a page: a page appears in the new menu only when all of its checklist rows pass. Until then
  its menu item opens the "not built yet" page with a link to the classic one.

### Step 3 — Shell and navigation

3.1 **Routes.**
- A route table (`app/routes.ts`) implements the route map above: parts to page and tab, the classic-route
  redirects, and the "Classic panel" link for every route, including each Packs tab.
- The menu comes from the same table, so the two cannot drift apart.
- Unknown routes show a "No page here" state.
- Tests: every classic route lands on its new page, and every new route's classic link is right.

3.2 **Page frame.**
- A per-page error boundary: a crash in one page shows an error panel with "Try again", and the shell survives.
- Loading and empty states from the kit.
- The page title goes into `document.title` ("Players · Aurora 21").

3.3 **Status bar and pending drawer.**
- The status bar keeps the 10 s poll.
- The pending badge opens a side drawer listing the open changeset (area, key and a short diff), with
  "Review and publish" linking to Changes. It is read-only; publishing stays on the Changes page.

3.4 **Ctrl K search.**
- A command palette that jumps to pages, players (`GET players`, filtered in the browser) and packs
  (`GET packs/catalog`).
- Cards go through the existing server search (`GET cards?q=…&limit=20`), debounced.
- Before adding a search endpoint, measure the `GET players` reply size with a realistic club count
  (a benchmark club set). Add one only if that list is too large to load once.

3.5 **Phones and session.**
- The menu drawer (first cut exists).
- Sign-in on 401 without losing the current route.
- Focus returns to the page after the drawer or palette closes.

**Done when** every classic route has a place, the shell checklist rows pass, and the maintainer has approved
the kit (step 2) and the shell on their browser.

### Step 4 — Players and clubs

4.1 **Players list** (`GET players`).
- Search by name, persona or Discord; filters for online, blocked and bypass.
- Sortable coins, FIFA Points, club items, logins and last seen.
- A row opens the club page.
- Checklist: Players list.

4.2 **Club page frame** (`GET players/<id>`).
- A header with identity, online state, balances as stat tiles and last seen.
- Tabs routed as `#/players/<id>/<tab>`, and the "Classic panel" link to `#/players/<id>`.
- One shared `useClub(id)` query that every edit refreshes from its reply (the PATCH returns the whole detail).

4.3 **Club edits, shared.**
- One `useClubEdit()` for every `PATCH players/<id>`.
- It sends only the ops listed in `editOps` (others show as unavailable, with the reason).
- On `428 needsConfirm` it asks "That player is signed in" and resends with `confirmOnline: true`.
- It toasts the applied result.
- Tests cover the 428 round trip and the `editOps` gating.

4.4 **Overview tab.**
- Balances: set coins and points (0–99,999,999), and add or take away (deltas, non-zero, sent alone), with the
  "game shows it on its next credits read" note.
- The club name is read-only, because the server refuses renames.

4.5 **Access & sessions tab.**
- The access edit (block with confirmation, maintenance bypass, note).
- Sessions, and the identity-collision explanation.

4.6 **Inventory tab** (`GET players/<id>/inventory?pile=`).
- Pile chips with counts, a filter, and the Cards/Table view (virtualised grid for up to 2,000 cards).
- A card popover with notes.
- "As of the last save", the truncation notice, the "worth a look" problems, the newer-file notices, MY PACKS and
  the notes.
- *Remove duplicate specials* is not ported (the server does not apply `dedupeCards`).

4.7 **Squads: dropped** (maintainer, 2026-09-29: the panel does not need to show player squads). There is no
Squads tab and no squad API. For the record:
- `chemistry.json` (FIFA 21's native formations and slot positions) would have given each slot its position.
- Chemistry itself needs each item's loyalty state and the manager, so it would have stayed "not reported".

4.8 **Packs & gifts tab.**
- Unopened club packs.
- Gift packs: the pack picker from `GET packs` with pack details, count 1–50, and `op: grantPacks`, with the
  "cannot be taken back" note.
- Grant a card: search `GET cards`, then `op: grantCard`.

4.9 **Modes tab** (`GET modes/<id>`).
- Division Rivals, Squad Battles, FUT Champions and Draft records.
- Reset per mode (`POST modes/<id>/<mode>/reset {confirm:true}`), with a confirmation naming the mode, and the note
  that settled rewards are kept.

4.10 **Log tab.** The club's panel edits, in readable form (the classic `describe` text for each op).

**Done when** the Players, Pack gifts (one club) and Modes checklist rows pass against a local server with a
seeded test club.

### Step 5 — Content editing

5.1 **Changes** (first: every editor hands off to it).
- Pending: grouped by area and row key, with the field diff and Remove.
- Validate, with conflicts offering "Keep my value" (re-`PUT`) or Remove.
- Publish with a note and "by" (remembered in the browser), and Discard all with a confirmation.
- History: published changesets, and Revert, which stages the old values and opens Pending.
- The step 3.3 drawer reuses the diff component.

5.2 **Cards.**
- The server-paged browser (`GET cards` with q, position, type and rating range; offset/limit): grid or table,
  specials summary (`GET cards/specials`), and the source notice (live FUT list or the `pool.json` fallback).
- Card detail at `#/cards/<cardKey>`: the large card, grouped stat bars, other versions of the same player
  (`GET cards?q=<assetId>`), and Grant to a club (club picker, with the 428 flow).

5.3 **Packs.**
- **Catalogue:** list with pending markers, and an editor (the 19 fields, grouped as store, contents and flags)
  with a live test draw (`POST packs/catalog/test`) drawn as cards. Hide with a confirmation. Every save is
  **Stage change**.
- **Store lineup:** the lineup checklist and Stage, and the notice when a lineup is already pending.
- **Simulate:** rolls of any pack (`POST packs/simulate`), drawn as cards.
- **Gifts:** gift to every club (one PATCH per club, with a per-club result list), recent gifts, and each club's
  unopened packs.

5.4 **Rules.**
- File list, then paged rows with search.
- Edit a value in a JSON editor (the `code` textarea), Remove, and "Edit any location".
- Import from files, one file or all.
- Every edit is staged.

5.5 **Card editor** (live-content-editing-plan step 5).
- Front end: the editor at `#/cards/<cardKey>/edit`, with the card redrawn live as stats change. Save means
  Stage change.
- Depends on that plan's server work: the `player_card` content area with its validation and publish. The front
  end is built here once that area exists.

**Done when** the Changes, Cards, Packs and Rules checklist rows pass. That includes a staged edit, validate,
publish and revert round trip against a throwaway local server.

### Step 6 — Operations

6.1 **Dashboard.**
- Services and supervisor, connected now, clubs, recent logins, the last 14 days, runtime snapshots, the
  Blaze heartbeat, test switches, identity collisions and open drafts.
- Every field is optional, as in the classic page.

6.2 **Access & maintenance.**
- The maintenance switch, player cap, play and bypass roles (with names from `GET maintenance/discord/roles`),
  bypass and blocked accounts, and "Test a player".
- Backups (list, back up now), game content (revision, reload), and a confirmed server restart when the VPS worker is active.

6.3 **Matchmaking.** The live searching / in-match lists, polled.

6.4 **Logs.** The file list, then the tail with a debounced filter and a Follow toggle (3 s).

6.5 **Settings.** The typed settings table (edit) and the read-only environment from `settings/config`. Aurora 21's
server has a fixed set of settings; it refuses unknown keys and removal, so the app explains that limit.

**Done when** their checklist rows pass. At that point every classic page has a new equivalent.

### Step 7 — Make the new panel the default (later, separately approved)

Only after every checklist passes and the maintainer has used `/panel/next/` for real work, and only on their
say-so. `/panel/` then opens the new app, and the classic panel moves to `/panel/classic/` (its files are moved,
not rewritten). Deleting the classic panel is a further, separate decision. At that point:
- update `PROVENANCE.md` (the front end is no longer Aurora16's; the API contract still is);
- update the `AGENTS.md`/README mentions;
- update the tests;
- rebuild the server exe.

Checks: `dotnet test` (focused, then the full suite before the commit), the launcher checks, and the isolated
panel run with screenshots of every page at both widths. **Done when** the maintainer has reviewed those
screenshots.

## Decisions

Taken on 2026-09-29:
1. **Ship path: commit the build output** (`Resources/panel-next/`, marked generated). `dotnet build`, the
   Dockerfile, the launcher builder and the test suite stay Node-free. The stamp test (a hash of the
   `panel-web/` sources and lockfile, written by the build) catches a stale bundle locally.
2. **Aurora16:** nothing relies on the page itself staying shared with Aurora16, so the new front end may
   diverge. The API contract and the access model are unchanged.
3. **Order:** the card editor (live-content-editing-plan step 5) and later content areas (step 8) are built in
   the new app only.
4. **Squads:** not shown in the new panel (changed 2026-09-29; the squad-slots API is not built).
5. **Side by side:** the new app is built next to the classic panel at `/panel/next/`; nothing in
   `Resources/panel/` is changed by the port.
6. **Navigation:** 9 pages in four groups, as in the navigation table.
   - Modes and one-club gifting become club-page tabs.
   - Pack catalogue, Store lineup, Simulate and Gifts become tabs of one Packs page.
   - Pending changes and Change history become one Changes page.
   - Classic route names redirect to their new pages.

## Risks

- **Two front ends during the port:** the classic panel keeps shipping and working, so going back is always
  possible. The cost is that a fix to shared behaviour may be needed in both until step 7. The step 0 checklist,
  not memory, keeps the new app from losing an action.
- **Stale bundle:** covered by the stamp test.
- **Build writing into the classic panel:** the build's output folder is fixed to `Resources/panel-next/`, and
  step 1's check confirms `Resources/panel/` is unchanged in Git after a build.
- **Supply chain:** npm dependencies for a loopback admin tool. Keep the list short, pin it, and review lockfile
  changes.
- **The hub framing path:** it is still open (`frame-ancestors 'none'`); the redesign does not change it and
  must not claim it works.

## Step 0 results: parity checklist

Built on 2026-09-29 from the classic page scripts (`Resources/panel/js/`) and the server routes they call
(`server/src/Aurora21.UltimateTeam.Server/Panel/OperatorPanel*.cs`). Every row is an **observed** call in the
current code. A row is ticked when the new app does the same thing against a real local server, not against
MSW mocks alone. Screenshots of the classic pages are not taken yet: `Test-LocalPanel-Isolated.ps1` runs an empty
throwaway database, so they would show empty pages. They are taken in step 4 from a seeded test club.

### Shell (`core/panel.js`, `core/router.js`)

- [ ] `GET session` at boot; on failure, the sign-in form, which sends `POST login {password}` (loopback mode
      accepts any password; there is no sign-out, and the button is hidden).
- [ ] Any 401 from any call shows sign-in again.
- [ ] Errors carry `status` and `body`. A `{error}` body is the message shown; `428 {code:'needsConfirm'}` asks
      before a club edit on a signed-in player (see Players).
- [ ] Every 10 s: `GET dashboard` feeds the status (Blaze up/down, FUT up/down, online count, a MAINTENANCE
      badge when `services.blazeAccess.maintenance` is true with its bypass count, a TEST MODE / TEST MODE? badge
      from `debug.active` / `debug.unknown`), and `GET content/changeset` feeds the pending count (hidden at 0 or
      when the API has no changesets).
- [ ] Unknown routes fall back to the dashboard; a page load error shows "Could not load: <message>".

### Dashboard (`#/dashboard`)

- [ ] `GET dashboard`, with these sections: Services, Supervisor, Connected now, Clubs, Recent logins, Last 14 days,
      Runtime snapshots, plus the Blaze heartbeat, test switches, identity collisions and open drafts (every field
      guarded).

### Players (`#/players`, `#/players/<persona>`)

- [ ] List: `GET players`, with search, Coins, FIFA Points, Club items, Logins, Last seen and Status columns.
- [ ] Detail: `GET players/<id>`, with tiles (Coins, FIFA Points, …), the Club form, Access, "Add or take away",
      Inventory, Squads, Sessions and Panel edits (a readable history of past edits: coins/points deltas,
      grantPacks, grantCard, dedupeCards, syncCardArt, removeCards).
- [ ] Balances, set: `PATCH players/<id> {coins?, points?}` (0–99,999,999). The server refuses `clubName` and
      `clubAbbr` (clubs are named after their player), so the new app shows the name read-only.
- [ ] Balances, add or take away: `PATCH players/<id> {coinsDelta?, pointsDelta?}` (non-zero, sent on its own),
      with the hint that the game shows the new balance when it next reads credits.
- [ ] Access: `PATCH players/<id> {op:'access', blocked, maintenanceBypass, note}`, with a "Block <name>?"
      confirmation.
- [ ] Gift packs: pick from `GET packs`, then `PATCH players/<id> {op:'grantPacks', packId, count}`, with pack
      details and the note that gifts cannot be taken back from the panel.
- [ ] Grant a card: search `GET cards?q=&limit=`, then `PATCH players/<id> {op:'grantCard', card, count}`.
- [ ] Signed-in player: on `428 needsConfirm` the "That player is signed in" dialog resends with
      `confirmOnline: true`.
- [ ] Which edits are offered comes from `editOps` in the reply. Today that is
      `{ops:['grantPacks','grantCard','access'], deltas:true}`; an op not listed is shown as unavailable, not sent.
- [ ] Inventory: `GET players/<id>/inventory?pile=club|new|transfer|starter`, with pile chips and counts, a
      filter, the Cards/Table view (card art), "as of the last save", the truncation notice, the "worth a look"
      problems list, notices about newer source files, MY PACKS and the notes.
- [x] Squads: not ported, by decision (2026-09-29).

**Not ported:**
- *Remove duplicate specials* sends `op:'dedupeCards'`, which this server does not apply: it is not in `editOps`,
  so the button is always disabled.
- The Aurora16 queued-edit handling (`watchPending`, 202 answers, `409 {code:'pending'}`): `OperatorPanel`
  documents that Aurora 21 applies edits at once and never returns them.

### Packs (`#/packs`)

- [ ] `GET packs`, polled, showing store packs, unopened club packs per club, recent gifts, the pile cap and
      whether FUT is alive and applies `grantPacks`.
- [ ] Gift to one club, or "Gift to every club" (one `PATCH players/<id> {op:'grantPacks'}` per club), with a
      per-club result list kept visible across refreshes.
- [ ] Preview/Roll: `POST packs/simulate {packId, rolls}`, drawn as cards when every item has art.
- [ ] "Customise packs" goes to the Pack catalogue.

### Pack catalogue and Store (`#/packcatalog`, `#/store`)

- [ ] `GET packs/catalog` merged with `GET content/changeset` (store_pack / store_lineup changes shown as
      pending New/Edit/Hide).
- [ ] Create or edit: `POST packs/catalog` / `PUT packs/catalog/<id> {revision, pack}` with the 19 fields
      (name, bio, store group, prices, size, tier weights, rare slots, rating range, specials, chances, flags).
- [ ] Test contents: `POST packs/catalog/test {packId?, pack}`, drawn as cards.
- [ ] Preview: `POST packs/simulate {packId, rolls:1}`.
- [ ] Hide: `DELETE packs/catalog/<id> {revision}`, with a confirmation; locked packs cannot be hidden.
- [ ] Store lineup: checkboxes, then `PUT packs/lineup {revision, publicPackIds}`, and the notice when a lineup is
      already pending.
- [ ] Every stage goes to Pending changes.

### Cards (`#/cards`)

- [ ] `GET cards?q&pos&type&min&max&offset&limit`, with filters (search, position, card type, rating range),
      paging, and the Cards/Table view.
- [ ] `GET cards/specials`: the card types with live counts, and the "What FUT can grant" section.
- [ ] Source notice: live from FUT (pool size, specials mode, snapshot age, grant availability), or the
      `state/pool.json` fallback with no grant.
- [ ] Card detail dialog: a large card with stats, the league logo, and other versions of the same asset
      (`GET cards?q=<assetId>`).
- [ ] Grant to a club: pick from `GET players`, then `PATCH players/<pid> {op:'grantCard'}` (with the
      signed-in confirmation).

### Modes (`#/modes`)

- [ ] `GET modes` (club picker), then `GET modes/<id>`: Division Rivals, Squad Battles, FUT Champions and FUT Draft
      records.
- [ ] Reset one mode: `POST modes/<id>/<mode>/reset {confirm:true}`, with a confirmation. Settled reward history
      is preserved server side.

### Matchmaking (`#/matchmaking`)

- [x] `GET matchmaking`, polled, showing Searching, Pre-match and In-match separately;
      mode/search filters, paged tables, Refresh and Pause. Browser review pending.

### Logs (`#/logs`)

- [x] `GET logs` (searched, sorted and paged file list), then `GET logs/<file>?…`
      with a debounced filter and Pause/Resume follow (every 3 s). Browser review pending.

### Maintenance (`#/maintenance`)

- [ ] `GET maintenance`, with sections Who may play, Database backups, Game content and Restart a server.
- [ ] Maintenance switch: `POST maintenance/blaze-access {maintenance}`; the access lists (play roles, bypass
      roles, bypass and blocked accounts, player cap): `POST maintenance/blaze-access {…}`.
- [ ] Role names: `GET maintenance/discord/roles`; "Test a player":
      `POST maintenance/discord/check {id}`.
- [ ] Back up now: `POST maintenance/backup`.
- [ ] Restart: `POST maintenance/restart {service:'server',confirm:true}`, with a confirmation; enabled only when the VPS worker heartbeat is fresh and no request is pending.
- [ ] Game content: `GET content`, then Reload content: `POST content/reload`.

### Settings (`#/settings`)

- [x] `GET settings` and optional `GET settings/config` (read-only server startup snapshot).
- [x] Edit a declared live value: `POST settings {key, value}`. No setting creation
      or deletion; unused controls removed. Browser review pending.

### Rules (`#/rules`)

- [ ] `GET content/rules` (files), then `GET content/rules/<file>?q&offset&limit` merged with
      `GET content/changeset`, paged.
- [ ] Edit or remove a rule, or "Edit any location": `PUT content/changeset/changes {area:'rules', key, operation,
      after}`.
- [ ] Import from files: `POST content/rules/import {file?}` (one file or All files).

### Pending changes and Change history (`#/changes`, `#/history`)

- [ ] `GET content/changeset`, with the field diff grouped by area and row key; Remove one:
      `DELETE content/changeset/changes/<id>`.
- [ ] Validate: `POST content/changeset/validate`. Conflicts show *Keep my value* (re-`PUT` the change) or
      *Remove*.
- [ ] Publish: `POST content/changeset/publish {note, by}` (the "by" name is remembered in the browser).
- [ ] Discard all: `POST content/changeset/discard`, with a confirmation.
- [ ] History: `GET content/history?limit=50`; Revert: `POST content/history/<id>/revert {by?}`, which stages
      the old values for review.

## Step 1 results

Done on 2026-09-29, all in the uncommitted working tree.

**Added:**
- `ultimateteam/panel-web/` with pinned dependencies:
  - React 19.3 and TanStack Query 5.104;
  - Vite 8.3, Vitest 5 and TypeScript 7.0 (`strict`, plus `noUncheckedIndexedAccess` and
    `exactOptionalPropertyTypes`);
  - separate type checks for the app (DOM) and the Vite config (Node).
- Also in `panel-web/`:
  - a typed API client;
  - a hash router;
  - the grouped navigation with links to the classic panel;
  - the status bar (the 10 s dashboard and changeset poll);
  - sign-in on 401;
  - a "not built yet" page with a classic link;
  - `scripts/stamp.mjs`;
  - a README.
- ESLint is left out for now: `typescript-eslint` support for TS 7 is unconfirmed, and `tsc` is the gate.
- The build writes `server/src/Aurora21.UltimateTeam.Server/Resources/panel-next/` (`index.html`,
  `app/index-*.js|css`, `build-stamp.txt`).
- Server:
  - a second `EmbeddedResource` (`panel/next/...`);
  - `/panel/next` → 302 `/panel/next/`, which serves `next/index.html` with the same CSP. Bundles are served by
    the existing static pattern.
- Tests:
  - `OperatorPanelTests.TheNewPanelIsServedBesideTheClassicOne`: redirect, CSP, no inline script, every
    referenced bundle 200 with its type, the classic shell unchanged, 403 for forwarded and foreign-Host requests,
    and the stamp not served;
  - `PanelNextBuildTests.TheCommittedBuildMatchesItsSources`: the C# twin of `stamp.mjs`.
- Housekeeping:
  - `.gitattributes`: `panel-web/**` LF, `panel-next/**` `linguist-generated`;
  - `Test-RepositoryHygiene.ps1` asserts that `panel-web/node_modules`, `dist` and `coverage` stay ignored.

**Verified (observed):**
- `npm run typecheck` clean; `npm test` 6/6; `npm run build` about 260 KB JS (80 KB gzip).
- `stamp:check` passes, and fails after a one-line source change (then passes once the change is reverted).
- `dotnet test --filter OperatorPanelTests|PanelNextBuildTests` 13/13 on Windows. The C# and Node hashes agree,
  and a WSL build is checked from a Windows checkout.
- `Test-RepositoryHygiene.ps1`: PASS, 289 checks.
- No file under `Resources/panel/` is newer than the build (the classic files' uncommitted changes are the earlier
  card-art work).
- **Live in a browser.** The Debug server exe ran against a throwaway runtime root and cluster
  (`serve --http-only-test-port … --panel-api-port …`), with headless Chrome over DevTools:
  - `/panel/next/` rendered the shell with the real status (Blaze, FUT, 0 online);
  - a page `fetch` GET `dashboard` answered 200, and a JSON POST `login` answered 200 through `Refusal()`;
  - no CSP or script errors (the only console entry was Chrome's automatic `/favicon.ico` request, a 404 the
    classic panel gets too);
  - no `PANEL refused` lines were logged;
  - the classic `/panel/` still rendered;
  - the server and cluster were stopped by exact path and data root.

**Not done yet:** the full solution suite (run it before the commit), and the Vite dev server against a live panel
(the proxy's Origin rewrite is configured but not yet exercised).

## Step 2 results

Built on 2026-09-29 and **waiting for the maintainer's approval of the kit**. Open `#/_kit` in the new panel.

**Added** (`panel-web/src/`):
- Fonts (`fonts/`, with `README.md` and the OFL text):
  - Barlow Condensed 600/700, latin and latin-ext (for names like Szczęsny), from `@fontsource/barlow-condensed`
    5.3.0;
  - IBM Plex Sans and Mono, copied from the classic panel.
- Tokens: `styles/tokens.css`, with the palette, the type scale, spacing and radii.
- `ui/`:
  - `Button` (primary / staged / danger / ghost, and a link variant), `Badge`, `StagedBadge` and `StatusDot`;
  - `Field` (labelled input, select or textarea with hint or error wiring), `Chips`, `Tabs` (Radix) and
    `Dialog` / `ConfirmDialog` (Radix; the action runs inside, and a refusal stays visible);
  - `Toast` (applied, staged, refused), `DataTable` (sort, paging, keyboard rows, and stacked labelled rows on
    phones), and `PageHeader`, `Section`, `Toolbar`, `Stats`/`Stat`, `EmptyState`, `ErrorPanel` and `Loading`;
  - `FutCard`, a port of the classic `fut-card.js` with its per-frame ink, glow, name fitting and fallbacks, plus an
    `xs` table thumbnail;
  - `StatBar` / `StatGroup`: FUT's stat colour scale and grouped detailed stats (a Futbin-style pattern).
- `api/assets.ts` (`useAssets`, `CardArt`) and `ui/format.ts` (`num`, `ago`).
- `pages/Kit.tsx` shows every piece with real FIFA 21 ids, so the downloaded art shows.
- On phones the shell collapses to a brand bar with a Menu button (a first cut of step 3's drawer).

**Inspiration:** Futbin and Futwiz answer headless Chrome with a Cloudflare bot check, so they were not viewed.
Their common patterns (card thumbnails in rows, the stat colour scale, grouped stat bars, a squad pitch) come from
general knowledge, and no code or assets were used.

**Verified (observed):**
- `npm run typecheck` clean; `npm test` 17/17. The tests found and fixed a real bug: missing values sorted first
  on descending sorts.
- `npm run build`: JS 338 KB (106 KB gzip), CSS 20 KB, 6 font files under `/panel/next/app/`.
- `dotnet test --filter OperatorPanelTests|PanelNextBuildTests`: 13/13.
- Isolated server (Debug exe, throwaway runtime and cluster, `--panel-assets-dir` pointing at the downloaded
  images, read-only) with headless Chrome, 1440 px and 390 px screenshots of the kit:
  - fonts and card art load under the CSP;
  - the only console entries are 404s for special-card images the download has not reached yet (each card then
    falls back to the base player image, as designed);
  - no `PANEL refused` lines; the classic panel still renders.
- Fixed from those screenshots:
  - stat groups and form fields stretched to their row's height;
  - the rail background ended at the first screen;
  - notes used the monospace font;
  - there was no gap under the stat tiles;
  - phone padding was too wide.

**Not verified:** the refused confirmation in a browser (the DevTools click script used a top-level `await`, which
`Runtime.evaluate` rejects). Its behaviour is covered by `Dialog.test.tsx`.

## Step 3 results

Built on 2026-09-29 (uncommitted).

**Added or changed** (`panel-web/src/`):
- `app/routes.ts` (replaces `nav.ts`): the route table. It drives the menu (9 pages in four groups), the classic
  redirects (`#/`, `#/packcatalog[?edit=]`, `#/store`, `#/history`, `#/maintenance`, `#/modes`), each route's
  "Classic panel" link (including every Packs tab and pack edit) and the tab titles.
- `app/App.tsx`:
  - redirects use `location.replace`;
  - `document.title` is set per page;
  - each page sits in an error boundary keyed by its address;
  - a "No page here" state;
  - Ctrl/Cmd K;
  - a search button in the status bar;
  - the pending badge opens the drawer;
  - sign-in keeps the address.
- `app/ErrorBoundary.tsx`.
- `app/PendingDrawer.tsx` + `ui/Drawer.tsx`: a read-only list of the open changeset, with "Review and publish"
  linking to Changes. `app/changes.ts` builds the readable summaries (area, row key, and "field: old → new" lines).
- `app/CommandPalette.tsx`: jump to pages, players (`GET players`, filtered in the browser), cards (the server's
  `GET cards?q=&limit=8`, debounced) and packs (`GET packs/catalog`). It works from the keyboard (combobox and
  listbox roles, arrow keys, Enter, Esc) and draws card thumbnails.
- `api/types.ts`: player, card, pack catalogue and changeset reply types.

**Verified (observed):**
- `npm run typecheck` clean.
- `npm test` 26/26, including new shell tests against stubbed replies shaped like the real API: the classic
  redirect, menu and current page, the classic link, the title, the pending drawer's diff, Ctrl K finding a player
  and opening them, and the error boundary with retry.
- `npm run build` rewrote the stamp.

**Open:**
- The `GET players` size measurement (plan 3.4) is not done. The palette filters in the browser; a search
  endpoint is added only if a realistic club count makes that list too big.
- The maintainer's own-browser check of the shell.

## Step 4 results

Built on 2026-09-29 (uncommitted). The Players page is live in the new panel: its menu item opens it instead of the
"not built yet" page. The checklist rows stay unticked until the maintainer has checked them against a real server.

**Found in the server code while porting** (observed in `OperatorPanel.Players.cs`, `OperatorPanel.Modes.cs`), so
not ported:
- the 428 "that player is signed in" confirmation and `confirmOnline` (the server ignores it);
- queued or pending edits (202, `409 pending`, `watchPending`): every edit applies at once in one transaction;
- the shared-address and identity-collision notices (`sharedAddress` and `identityCollision` are always null);
- the starter pile (the server always answers it empty);
- club renames (refused with 400: clubs are named after their player).

**Limits the forms follow** (from the server):
- balances 0–99,999,999; deltas non-zero;
- packs 1–60 per gift;
- cards 1–24 per grant;
- the inventory lists at most 2,000 items per pile.

The server also refuses (409) a gift that would leave a club with more than 100 unopened packs
(`MaximumUnopenedPacks`, the panel's own safeguard). The game itself allows a club more free packs (maintainer,
2026-09-29), so the new panel does not copy that 100. It shows the server's refusal per club instead, and follows the
server if the safeguard changes.

**Added** (`panel-web/src/`):
- `api/players.ts`:
  - `usePlayers`, `useClub`, `useInventory`, and `useClubEdit` (a PATCH whose reply replaces the cached detail);
  - `supports()` (`editOps` gating);
  - `refusedUnchanged()`: a 4xx keeps the form for another try, while anything else clears it and reloads, so an
    uncertain result can't be granted twice;
  - `describeEdit()`: past edits in words, including access and mode resets.
- `pages/players/PlayersList.tsx`: search by name, persona, Discord id or club; the All / Online / Blocked / Bypass
  filters with counts; sortable columns; 50 per page; rows open by click or Enter.
- `pages/players/ClubPage.tsx`: header (online state, persona, Discord, logins, last seen, blocked and bypass
  badges), balance tiles, and tabs routed as `#/players/<persona>/<tab>`.
- The tabs:
  - **Overview:** Set balances (sends only what changed), Add or take away, and what the club holds by kind.
  - **Inventory:** Club / New items / Transfer list, a filter, the Cards/Table view (sharing the classic panel's
    saved choice), an item detail dialog, and the My Packs line. `ui/VirtualCardGrid.tsx` draws only the rows near
    the screen.
  - **Packs & gifts:** unopened packs, Gift packs (the pack's contents and price, count capped by the server and
    by the room left in My Packs) and Grant a card (card search with drawn cards).
  - **Modes:** Division Rivals, Squad Battles, FUT Champions (with kept entries) and the FUT Draft runs, with
    confirmed resets and the "player must leave first" notice.
  - **Access & sessions:** Blocked and Bypass switches and a note. Blocking is confirmed; the other changes save
    directly. The last 50 sign-ins.
  - **Log:** the club's panel edits in words.
- `ui/Switch.tsx`.

**Verified (observed):**
- `npm run typecheck` clean.
- `npm test` 38/38, including new tests against replies shaped like the server's:
  - the list filters and opens a club;
  - the header, tiles and tabs;
  - Set balances PATCHes only `{coins: 1234}`;
  - Add or take away rejects 0 before sending, keeps `-500` after a 400, and clears after a 500;
  - Block asks first, then sends `{op:'access', blocked:true, …}`;
  - a gift above the limit is disabled, then 2 packs send `{op:'grantPacks', packId:3, count:2}`;
  - a Rivals reset POSTs `{confirm:true}` only after confirming;
  - past edits read as words.
- `npm run build` rewrote the stamp.

**Open:**
- The maintainer's check against a real server with a club, which ticks the Players, one-club Pack gifts and
  Modes rows of the step 0 checklist.
- The classic Modes page's club picker is replaced by the club page's tab.
- "Gift to every club" and recent gifts come in step 5.3 (the Packs page, Gifts tab).

## Step 5 progress

Being built on 2026-09-29 (uncommitted).

**5.1 Changes: built.**
- `api/content.ts`: the open changeset shares its cache with the status bar's pending badge, so any action here
  updates it at once. Also validate, publish, discard, history, revert, and "Keep my value" (DELETE the change,
  then PUT the same value on top of the live row). The "published by" name is remembered in the browser.
- `pages/changes/ChangesPage.tsx` + `ChangeList.tsx`, with tabs Pending / History (`#/changes`,
  `#/changes/history`):
  - **Pending:** the changeset header (id, age, author, "reverts #n"); changes grouped by area with the row key, the
    operation and a field-by-field diff (only changed fields; "New" shows the values, "Hide" says owned copies stay);
    Remove; Validate showing ready, errors, or conflicts with Keep my value / Remove; Publish with note and name,
    confirmed, then showing the revision, warnings and notes; Discard all, confirmed.
  - **History:** the published changesets with note, author and age, Show changes, and Revert (confirmed, then
    staged and opened under Pending).
  - A refused publish (409 with conflicts, 422) shows its reasons above the list, and nothing is published.
- Tests (4): remove sends DELETE; validate, then publish sends `{note, by}` only after confirming and remembers the
  name; a refused publish shows why, and Keep my value sends DELETE then the exact PUT; history revert POSTs and
  returns to Pending.

**5.2 Cards: built.**
- Found in the server code, so not ported: Aurora 21 always serves the live card list (`source: 'fut'`, grant
  always available), so the classic `pool.json` fallback and its notice never happen. Card replies carry the six
  face stats only (no detailed attributes), so the detail page shows those as coloured bars.
- `pages/cards/CardsPage.tsx`:
  - filters (search by name, card id or key; position; type: gold/silver/bronze, rare, common, any special, or one
    special type with its count; rating range) live in the address (`#/cards?q=&pos=&type=&min=&max=&page=`);
  - server paging of 60, with Cards/Table views.
- `pages/cards/CardDetail.tsx` (`#/cards/<cardKey>`):
  - the large card, face stats as bars, and the card facts (key, ids, league, grant-only);
  - the player's other versions;
  - Grant to a club, where only players with a club can be picked, 1–24 cards, and the same refusal rules as the
    club page.
- A bug fixed from the tests: without downloaded images "Other versions" drew nothing; it now falls back to
  buttons.
- Tests (3): the filter-to-query mapping, a filter written to the address back at page 1, and the detail page's
  stats, versions and grant PATCH.

**5.3 Packs: built** (`#/packs/<tab>`: Catalogue, Store lineup, Simulate, Gifts; the editor at
`#/packs/catalogue/<id|new>`).
- `api/packs.ts`: `mergeCatalogue()` lays the open changeset's pack and lineup changes over the published
  catalogue (the classic panel's `load()`), plus stage (POST/PUT), hide (DELETE), lineup (PUT), test draw and
  simulate. The catalogue `revision` is an md5 string; every save sends it, and a 409 "catalogue changed" keeps the
  operator's values.
- **Catalogue:** search, a staged/grant-only/in-store/hidden status, Edit, and Hide (confirmed and staged; never
  offered on a locked pack).
- **Editor:** the 19 editable fields, matched to the server's `EditablePackFields` and grouped as In the store,
  Contents and Flags. Stage change sends only the changed fields (all set fields for a new pack) and rejects
  invalid numbers before sending. Test contents draws one pack from the typed values and changes nothing.
- **Store lineup:** switches per sellable pack, Stage lineup, Reset, and a notice when a lineup is already staged.
- **Simulate:** any published pack, 1–20 rolls (the server's limit), drawn as cards.
- **Gifts:** gift to every club (one PATCH per club in turn, with a per-club result list showing the server's own
  refusal), unopened packs by club, and the last 30 gifts.
- Fixed from the tests: the editor would have sent `bio: ""` for a pack without a description (an empty text
  field against a missing value). Text fields are now compared as text.
- My Packs: no hard-coded 100; see the step 4 note on `MaximumUnopenedPacks`.
- Tests (9): `packFields` (edit vs new, invalid values), `mergeCatalogue`, the staged edit with a 409 then success,
  the test draw, Hide confirmed and absent on a locked pack, the lineup PUT, simulate, and gift-all with a refusal
  listed.

**5.4 Rules: built** (`#/rules/<file>?q=&offset=`).
- `api/rules.ts` and `pages/rules/RulesPage.tsx`:
  - a file list with rule counts;
  - debounced search and paging of 100;
  - each rule's path (list positions as `#n`), its value, and a staged marker ("Staged → 6" or "Staged removal");
  - Edit (a JSON dialog that refuses invalid JSON) and Remove (confirmed);
  - Edit any location (path checked as a JSON list of keys and whole-number positions; change, add or remove);
  - Import from files (this file or all, going to Changes when something was staged).
- Every edit is a PUT to the `rules` area.
- Tests (6): path parsing and printing, an edit PUT with the staged marker, invalid JSON refused, removal confirmed,
  any-location path checks and insert, and import.

**5.5 Card editor: built in the working tree.** The server now has the `player_card` content area and migration 074.
The React editor at `#/cards/<cardKey>/edit` loads every editable field, existing pending edits, visible traits and
head options. `#/cards/<cardKey>/new` chooses a card type and asks the server for a free revision id before staging an
insert based on that card. The preview follows rating, position, face stats, card type and selected portrait. Uploading
a portrait stores an option for the same player; choosing it is a separate staged card edit. Both editors send the
complete card value to `PUT content/changeset/changes`, then link to Changes for review and publish.
An edited card can be reverted through History. A newly inserted card cannot be reverted because a club may own it;
History explains this and the server refuses that revert before staging anything. The cards rules can make it grant-only.

Observed checks: `npm run typecheck`, `npm test` (66/66), `npm run build`, `npm run stamp:check`, and the focused
backend card and panel tests pass. An initial build-stamp failure came from running that test concurrently with an
unfinished build; it passed after the final bundle was written. The PNG upload test passes after correcting its
signature bytes.
Still unconfirmed: the editor in a real browser against the isolated server, and the native game response after a
published card edit. The maintainer's own-browser check remains open for all step 5 pages.

### Card editor review follow-up — 2026-09-30

- The whole card-type tile selects its type. Tier variants share one choice; Icon Swaps are excluded.
  Source Icons offer only Icon types and require rating ≥85, with the same restrictions enforced by the backend.
- Existing cards can change type as well as new cards. TOTW stays Inform across silver/gold rating changes,
  uses the matching frame, and becomes Bronze Rare below 65. History snapshots include the type so a revert
  restores it; legacy pending snapshots remain compatible.
- Installed the latest supplied Champions frame and corrected Pro frame. Moved the 301 MB downloaded asset
  folder into `ultimateteam/panel-assets`; downloader, review script and local server defaults use it.
- Observed: 52 focused frontend checks and 16 focused card/changeset/API backend checks passed; frontend build
  and typecheck passed, as did the bundle stamp and the isolated repository asset-path check. Browser acceptance
  remains pending. No live services were restarted or real club data edited.

### Catalogue layout and filters — 2026-09-30

- Centered the shared page with a wider 1840px limit and placed desktop navigation beside the brand.
- Enlarged catalogue cards and included their six face stats. Type choices show miniature card frames.
- Added grouped positions, Attackers, Midfielders and Defenders (including GK), plus searchable nation,
  league and club filters. Choosing a club sets its league; choosing a league clears the club. Clear filters
  resets all filters and pagination. Filter values remain in the address and apply to the full catalogue.
- Table rows include SKI, WF, attacking/defending WR and six face stats, with goalkeeper labels on GK rows.
- Observed: 32 distinct focused frontend checks passed; typecheck, frontend bundle and stamp check passed.
  The isolated backend check passed for combined identity filters, position sets and the table row fields.
  Browser review remains pending. The running review server was left running; it must be restarted to load
  the new backend filters and embedded frontend.

### Catalogue table review corrections — 2026-09-30

- Centered rating, position, SKI/WF/WR and face-stat columns, including headers. Strengthened the shared
  table alignment selectors so the header's default style cannot override the requested alignment.
- Removed the key below the name. Table thumbnails show only their frame and a centered portrait with the
  existing dynamic-image fallback; the rating remains in its own column.
- Nation labels resolve native nation IDs through the relational nation catalogue, rather than depending on
  the absent legacy `nationName` field. The focused backend regression checks the returned names.
- Widened the club filter and constrained long labels. Cards/Table and Clear filters now use larger icon
  buttons in their own action row.
- Observed: 22 focused frontend checks passed, along with typecheck, bundle build and source-stamp check.
  The isolated backend catalogue check passed, including nation display names. Browser acceptance remains
  pending; the running review server was left running.

### Header and toolbar spacing follow-up — 2026-09-30

- Global search sits at the far right inside the desktop header rectangle, outside the navigation landmark.
- Table portraits retain their usual vertical position and move left into the space freed by the rating.
- Compact icon buttons for Cards/Table and Clear filters share one row with the filters on wide screens.
  Club width is capped at 240px. The toolbar uses four/two columns on narrower screens.
- Observed: 25 focused frontend checks passed. Browser review remains pending; the running review server
  was left running and must be restarted to load the new embedded frontend.

### Dashboard spacing and list limits — 2026-09-30

- A dashboard grid now owns the vertical gaps, overriding inherited section margins that produced missing
  gaps between groups and extra margins within the Services/Connected now row.
- Connected players and clubs are paged at 10 rows. Clubs remain all database clubs in ascending club-ID
  order; the backend response is not capped. Recent logins retain the backend's latest-20 cap and show 10 per
  page; a frontend cap also prevents an older response from rendering an unbounded login list. History shows
  at most 14 days and optional runtime snapshots are paged at 10.
- Hide the unused supervisor panel (the current backend reports false and an empty array) and the Blaze
  heartbeat row when the API does not report a heartbeat. Service listener status remains visible.
- Observed: four focused operations checks passed, including dashboard paging and the login cap; typecheck
  and frontend build passed. This is a targeted dashboard follow-up, not completion of module 8. Browser
  spacing review remains pending. No live services or databases were changed.

### Pro portraits and card presentation — 2026-09-30

- Observed cause: the panel read downloaded PNGs, while the six developer Pro portraits shipped as native
  DDS files. Bruno also had a TOTY reference, so it alone showed a dynamic portrait. Added exact RGBA PNG
  decodes alongside all six DDS files and an offline Pillow export tool. The private image route serves the
  sidecars ahead of downloaded art, even when no downloaded asset directory is configured. DDS files and
  card definitions were left unchanged.
- Card art names its bundled preview through `defaultPortraitUrl`. The own-head picker uses this preview or
  the intrinsic dynamic reference; null editable head overrides mean the card's own head, not always a base photo.
- Catalogue pages load 64 cards, filling eight-column rows except the final results page. Large card stat
  columns are centered and show SM/WF stars and attacking/defending WR underneath. Editor previews update
  these details immediately and all attribute groups start expanded, including GK groups.
- Observed: the first 48 focused card/frontend checks passed, then 27 editor checks passed after adding two
  expansion checks and changing the initial state. Two isolated backend portrait/upload checks passed; build
  and typecheck passed. All six PNGs match the decoded DDS pixels exactly. Browser/native acceptance remains
  pending; the review server was left running and real database rows were not edited.

### Card image picker and presentation follow-up — 2026-09-30

- The editor section is now **Card Image**, with **Available images** as selectable tiles.
  Removed the duplicate head dropdown and portrait search. Special cards offer a separate
  base-photo tile; choosing it previews the base image and stages its resource ID.
- Card-type buttons explicitly use the panel's dark colors, including the selected state,
  instead of native white button/fieldset styling. Card detail sections override inherited
  sibling margins so the Card and Face stats sections align.
- Large card footers show only skill/weak-foot stars and attacking/defending work-rate values;
  their labels remain available on hover. Browser appearance and native-game acceptance remain
  pending; the live review server was left running. Observed: 37 focused frontend checks,
  typecheck, build, and source-stamp verification passed.

### Remaining module navigation — 2026-09-30

- The maintainer accepted the card changes in the browser and requested the remaining navigation entries.
  Players and Clubs are adjacent. Clubs reuses the current player list restricted to existing clubs and
  opens the existing club detail tools; its full module review remains pending.
- Content now includes Cards, Packs / Picks, Promos, Events, Rules and Changes. Packs and Player picks
  are tabs within one section. Promos covers promos and buckets; Events covers promo events and Icon
  rotations. Picks, Promos and Events now have their own pages; Events manages timing separately from promo buckets.
- This navigation update does not complete modules 4–7. The shared route table also supplies mobile
  navigation and page-search results. The classic panel remains the existing fallback.
- Observed: 15 focused route/shell checks passed. Updated the pagination fixture to match the existing
  64-card page size. Typecheck and production build passed; the running review server was left running.

### Combined players/clubs and toolbar — 2026-09-30

- Follow-up decision: one **Players / Clubs** navigation entry replaces the separate entries.
  The list includes an **All players / With a club** choice alongside Online, Blocked and Bypass.
  Existing `/clubs` addresses remain reachable, with the combined navigation entry highlighted.
- Player search now has a wider flexible column. All five filter buttons use equal-width columns
  and a 44px height, matching the search field; the grid wraps to two columns on phones.

### Table sorting — 2026-09-30

- Shared table headers reserve a fixed space for the sorting arrow even when inactive, preventing the
  icon appearing/disappearing from changing header widths.
- All named Cards table headers now request catalogue sorting. Sort direction stays in the URL, changing
  it returns to page one, and the backend orders all matching cards before pagination. The image header
  remains without a sorting button. Player tables retain their existing sorting in memory.
- Observed: 29 focused frontend checks passed, then all 14 card checks passed with the added header/URL
  assertions. The isolated backend sorting check passed; typecheck, build and source-stamp verification
  passed. Browser acceptance remains pending; restart the review server to load the new API and frontend.
- Follow-up: Cards now uses fixed table layout with explicit column widths, including a 230px name
  column. Long names truncate with an ellipsis and retain the full name on hover. Narrow desktop tables
  scroll horizontally; the existing mobile row layout remains available. Content length no longer
  changes header positions between sorted pages.
- Follow-up: the temporary updating message now sits outside normal document flow in the toolbar/table
  gap. Previously its paragraph pushed results down during uncached sort requests, then disappeared;
  cached requests did not insert it. The live status announcement remains, without adding layout height.

### Players / Clubs module review — 2026-09-30

- Observed alignment causes: inherited sibling-section margins had higher specificity than the grid
  override, and automatic column counts allowed three/four panels across on wide screens. Fixed the
  shared grid margin rule and gave club Overview, Modes and Access a two-column layout, collapsing to
  one below 900px. Balance descriptions reserve equal space so their input rows align; gift actions no
  longer inherit the toolbar's bottom margin.
- Detail now has Refresh for account/club data, inventory and mode records, a visible club name and
  abbreviation, and a separate New items count. Accounts without a club offer Access and Log; they do
  not request nonexistent inventory or competition data.
- Club edits invalidate inventory queries after success or an uncertain result, so gifts/grants are
  reflected in the open inventory. Pack loads, gift catalogue loads and card searches show failures
  with Retry instead of misleading empty states. Unopened packs are paged at 20; sessions/logs retain
  the backend's last-50 limits. Inventory retains the explicit 2,000-item limit and truncation notice.
- Online players cannot initiate mode resets; the page explains the existing backend restriction.
  Pending records remain visible but do not permanently block an offline reset. Confirmation and
  server refusal handling remain in place. The inventory squad note now means membership in any squad,
  matching the backend query.
- All six planned club tabs are implemented. Squads and duplicate removal remain excluded by prior
  decisions. No extra tab is required. Browser review should cover alignment, Refresh, inventory
  updates after an authorized gift, balance edits, access changes and an offline mode reset on a
  chosen test club. This pass did not change a real club or restart the live server.
- Observed: all 12 player checks passed, including gift-driven inventory refresh, no-club accounts,
  pack-load failures and reset restrictions. The 15 shared table/shell checks passed as well. Typecheck
  and production build passed; browser alignment and real-API acceptance remain pending.

### Packs / Picks: agreed replacement store — 2026-09-30

The maintainer requested replacing the entire store catalogue with these nine enabled packs.
Separate competition/objective reward definitions must remain. Contents must be mixed items exactly
as listed, not player-only approximations. Counts within each category are ranges; their sum must be
the pack's total. The rare count applies to the whole pack, not just its player slots. A dash in the
FIFA Points column means coin-only purchasing.

| Pack | Items | Coins | FIFA Points | Players | Consumables | Stadium items | Staff | Tier | Rare |
| --- | ---: | ---: | ---: | --- | --- | --- | --- | --- | ---: |
| Bronze Pack | 12 | 400 | — | 5–11 | 0 | 2–6 | 0–3 | Bronze | 1 |
| Premium Bronze Pack | 12 | 750 | — | 5–12 | 0 | 3–6 | 0–2 | Bronze | 3 |
| Silver Pack | 12 | 2,500 | 50 | 4–12 | 0 | 0–3 | 0–3 | Silver | 1 |
| Premium Silver Pack | 12 | 3,750 | 75 | 6–12 | 0 | 0–3 | 0–1 | Silver | 3 |
| Gold Pack | 12 | 5,000 | 100 | 3–6 | 5–9 | 0–3 | 0–2 | Gold | 1 |
| Premium Gold Pack | 12 | 7,500 | 150 | 3–5 | 5–8 | 0–4 | 0–2 | Gold | 3 |
| Jumbo Premium Bronze Pack | 24 | 1,500 | 75 | 11–20 | 0 | 3–11 | 1–7 | Bronze | 7 |
| Jumbo Premium Silver Pack | 24 | 7,500 | 150 | 11–24 | 0 | 1–6 | 1–4 | Silver | 7 |
| Jumbo Premium Gold Pack | 24 | 15,000 | 300 | 6–12 | 11–15 | 0–5 | 0–2 | Gold | 7 |

Observed prerequisite: the current native consumable seed only contains position modifiers and
chemistry styles rated 90–95 (`server/database-setup/seed/015-consumable-definitions.sql`). Manager
league cards added by migration 071 also use the configured gold rating. `ItemFactory.BuildConsumable`
and the consumable application route only support those three kinds. There are no bronze/silver
contract or healing definitions in the checked current seeds or archived normalized reference seeds.
The existing pack generator supports player packs and separate fixed-kind generators, not combined
item-category ranges. Native metadata and item behavior must be implemented before these packs can
be enabled honestly. No store packs or live databases have been changed in this investigation.

#### Initial implementation status — mixed-pack configuration (before contract removal)

- Migration 095 replaces the old store catalogue with the nine exact definitions
  above, IDs 70–78, as disabled drafts. It retains retired definitions for
  already-owned packs and preserves competition rewards. It runs on upgrade;
  this development pass has not migrated the maintainer's running review server
  or production database.
- Added a shared mixed-content planner and preview/opening path. Feasible count
  combinations are selected uniformly, then feasible rare allocations uniformly;
  the supplied table does not specify composition odds. Totals and whole-pack
  rare counts are exact. Missing pools produce an error, never substitute another
  tier or artificially mark a common definition rare. Player slots use existing
  promo/bucket odds. Non-player copies can repeat.
- Added the React Mixed items editor, per-category min/max controls, tier selection,
  catalogue availability reasons, and a publication gate for unfillable packs.
- Maintainer correction: Hawk, Engine, Anchor, Hunter, Catalyst and Shadow are rare
  gold chemistry styles. Other styles are common gold; position modifiers remain
  gold rare. Their established IDs, art and effects are preserved. This supplies a
  common gold consumable pool, so the three gold packs can be previewed using the
  currently supported item kinds; they remain disabled pending review.
- Contract and healing candidates are recorded under `native-data/reference/`.
  Their runtime generation/application is **not implemented in this pass**.
  Bronze/silver consumable pools remain unavailable. The maintainer's linked
  `mynameisvasco/futwebapiv2` revision contains no consumable ID/effect table in its
  18 TypeScript source files, so it does not independently verify those candidates.
  Healing candidates are gold only: 5002029, common, all injuries +2; 5002030,
  rare, all injuries +5. Do not seed bronze/silver healing variants from FIFA 17.
- Next native-data gate: establish contract/healing item documents and effects in
  an isolated FIFA 21 profile; implement creation, application, stack decrement,
  persistence and quick sell with that evidence, then enable the remaining packs.
  Browser tests of the new range editor can proceed before that gate.
- Verification: 26 distinct focused backend checks passed across the pack,
  catalogue/cache, store purchase/rendering and panel publication runs. The store
  rendering fixture was updated to explicitly enable supported gold packs rather
  than require the retired testing lineup. Mixed-pack odds now count player cards
  only; a focused check excludes high-rated consumables from the 82+/84+/88+
  metrics. Eleven pack UI tests and the React production build/typecheck passed.
  The full solution suite and native FUT/browser review were not run in this pass.

#### Contract removal — current policy (2026-09-30)

- Permanent players and managers never use contracts. Native projections show
  `cards.permanentContracts` (99) for compatibility, including older owned cards
  whose stored count is zero. Stored contract columns are retained.
- Owned loans lose one remaining match when they participate in an identified,
  settled club match, including a quit/DNF with a game ID. Settlement replays,
  cancelled searches, no-contests and unused substitutes do not consume loans.
  Draft selects its own cards and uses its separate settlement route.
- At zero remaining games, a persisted `expiredLoan` item extension preserves
  loan identity. Native responses keep the loan flag and show zero contracts;
  match creation refuses expired loans. They remain ineligible for market/SBCs
  and have zero quick-sell value.
- Migration 096 moves bronze/silver consumable ranges into player ranges (capped
  at total size). Updated table above is authoritative. Stadium/staff ranges,
  total items, prices and whole-pack rare counts remain the same.
  All nine packs are enabled by migration 096; publication still validates pools.
- Contract consumable creation/application is cancelled. Candidate CSVs are
  retained as reference only. Gold packs retain existing supported consumables;
  healing candidates are not imported by this change.
- No live database migration, server restart or native in-game test is implied
  by these source changes. Review loan expiry and mixed-pack reveal in FIFA 21.

#### Catalogue cleanup — revised scope (2026-10-01)

- Migration 097 removes the old testing store definitions (IDs 1–24 and 42–66)
  from `store_packs`. Keep the genuine reward definitions, pack creation template
  and client display metadata, plus the nine enabled store packs (70–78).
- Genuine reward player packs use promo/bucket selection (`banded_odds=true` is
  the existing database field name). Preserve their tier mix, rating floors,
  guaranteed promos, pick configuration and source restrictions. They remain
  reward-only. Icon Swaps token rewards retain their fixed token generator.
- If an old testing pack/pick is already owned at upgrade, its configuration is
  copied to `owned_legacy_pack_definitions` before deletion. This is compatibility
  data only, absent from the editable catalogue/store. Unowned testing definitions
  are discarded. No club item or owned pack is deleted by the migration.
- Focused verification: 32 backend checks passed for loans, native projection
  parity, all nine pack draws, publication gates, catalogue/cache, store rendering
  and purchases. A separate upgrade check verifies owned testing-pack compatibility.

#### Player picks panel — implementation (2026-10-01)

- Player picks has its own searchable catalogue, create/edit routes and direct
  pick creation defaults. Pack and pick definitions are listed separately.
- Reuse the pick group editor for token count, choices per token, rating range,
  label, specials-only selection, promo sources/category odds and bonus players.
  Preview shows each token's choices separately. Saves stage changes for review
  and publication; simulation/gifts retain their existing routes.
- Include genuine reward picks in the panel catalogue and permit staged edits.
  `rewardOnly` identifies them; they cannot join the store lineup or be hidden
  through the store-pack hide action. Editing preserves their reward kind and
  existing rarity/source restrictions.
- Simulate and Gift use a responsive control grid: labels and inputs align,
  helper/error text sits beneath its own field, and controls stack on small screens.
- Browser review pending. No live server restart or in-game pick claim is implied.
  Verification: 13 focused frontend tests, four backend panel checks and the
  React production build/typecheck passed. Champions previews carry their reward
  source identity; new ordinary picks still cannot request Champions red cards.

#### Promo browser — initial read-only panel (2026-10-01)

- Promos has a searchable overview with large linked tiles, up to three actual
  member cards as artwork, card counts and enabled bucket counts. Motion hover
  respects reduced motion. Definition status is separate from pack availability.
- `#/promos/<code>` reuses the Cards browser, including card/table views, identity
  filters, sorting and pagination. Clear filters and filter changes retain the
  promo route. Individual cards open the existing card inspector.
- `GET cards?promo=<code>` restricts membership before search, exact resource
  lookup, sorting and pagination. Unknown promos return 404. Membership includes
  cards in disabled buckets, since this page displays the collection rather than
  the current pack draw pool. `GET promos` supplies representative member artwork.
- Promo creation, membership changes and bucket editing are deferred. Events and
  Icon rotation management remain a separate module. Browser visual review is
  pending; no live runtime or database was changed for this implementation.

#### Promo buckets — grouped browser and creation (2026-10-01)

- This follow-up supersedes the initial read-only detail layout: each promo now
  displays bucket sections with names, relative weights, counts and independent
  pagination. One Cards/Table switch and shared filters/sort apply to all buckets.
  The main Cards page retains its flat layout and shares the result renderer.
- Create bucket accepts a name and positive relative weight. An optional rating
  range previews cards already in this promo, with representative names/ratings
  from the same snapshot. The range seeds a fixed member list once.
- Moving matching cards requires explicit confirmation and keeps donor buckets,
  including empty donors. Creating an empty named bucket is supported. Disabled
  populated buckets retain their membership and status during edits. Empty buckets
  are ignored by draws; this requires no schema migration.
- Saves merge the existing staged promo definition, refuse a changed preview
  baseline and stage one complete promo update. Published membership remains
  visible until Changes is published. Creating promos or adding outside players
  to a promo remains outside this pass; Events/Icon rotations remain separate.
- Browser visual review is pending. No live server restart or real database edit
  was performed for this implementation.
- Verification: 23 distinct focused frontend checks passed (Cards regression
  checks plus nine promo/bucket checks), five focused backend checks passed,
  and the production build/typecheck and build stamp check passed. Backend
  coverage includes independent bucket pagination, filtered empty sections,
  exact membership restrictions, preview APIs, atomic moves, disabled membership
  preservation, empty buckets and publication/revert in throwaway databases.

#### Promo overview and TOTY consolidation — follow-up (2026-10-01)

- Keep the six standard promos at the top: Bronze Common, Bronze Rare, Silver
  Common, Silver Rare, Gold Common, Gold Rare. Remaining collections sort
  alphabetically by related family, with Icons ordered Base, Mid, Prime, Moments;
  TOTS, POTM, TOTW and other related collections stay together. The count aligns
  with the search input.
- Retain relative-weight editing. Bucket headers also show the normalized share
  among this promo's enabled populated buckets. Empty/disabled buckets show 0%;
  browsing filters do not change the percentage. Pack overrides and eligibility
  filters may produce different actual draw percentages.
- Migration 098 merges TOTY 12th Man (Messi, resource 117598535) into Team of the
  Year and the two Honourable Mentions collections into `toty_honourable_mentions`,
  named TOTY Honourable Mentions. It preserves player definitions, merges default
  buckets, retains curated buckets, remaps event links and stored pack/pick source
  codes, and removes the obsolete promo identities. Migration deletion guards
  are restored after these specific removals; audit history remains unchanged.
- This migration applies on the next server startup; no live database or running
  runtime was changed during implementation. Browser review remains pending.
- Verification: 11 promo frontend tests, four focused migration/catalogue checks,
  the production build/typecheck and source stamp check passed. The migration
  checks cover both a fresh catalogue and an upgrade with old promo memberships,
  event links and configured pack sources, plus the retained legacy-pack upgrade.

### Events page — implemented, browser review pending (2026-10-01)

- `#/events` shows the six always-available Bronze, Silver and Gold common/rare
  pools, linking to their promo buckets. Icons have a separate chronological
  rotation, with the currently published tier reported by `promos/availability`.
- Promo events list all their selected promos and link to each collection. The
  initial TOTW 1–3 event uses the Base Icon window: September 30, 2026 at 19:00
  through January 1, 2027 at 19:00 Amsterdam time (migration 101).
- Create/edit dialogs support names, enabled state, start/end dates, one Icon tier
  or multiple ordinary promos. Standard pools, Icon tiers, Champions reds and
  Developer Cards are excluded from ordinary event selection. Editors stage
  `promo_event` or `icon_rotation` changes; publication stays in Changes. Pending
  inserts can be reopened, pending edits are preserved, and a changed baseline
  refuses saving rather than overwriting another edit. Server validation rejects
  overlapping enabled Icon windows at publish time.
- All schedule dates are displayed and entered in Europe/Amsterdam, independently
  of the browser timezone, with DST-aware UTC conversion. Starts are inclusive;
  ends are exclusive. Invalid DST wall-clock times are refused.
- Migration 100 seeds the initial calendar when no enabled operator rotation
  exists: Base September 30, 2026 → January 1, 2027; Mid → March 1; Prime → May 1;
  Moments → January 1, 2028. Every transition is at 19:00 Amsterdam time. Existing
  enabled operator calendars are preserved. Base remains the backend fallback
  outside enabled windows. The migration applies on the next server startup.
- Motion hover/layout transitions respect reduced-motion preferences. The page
  uses responsive standard-pool cards and schedule rows with aligned controls.
- No running server or live database was changed. Browser review: open Events,
  inspect the three sections, edit an Icon window, and stage a multi-promo event;
  review it in Changes before publishing.
- Verification: five Events UI tests and three focused isolated backend tests
  passed. Production build/typecheck and the source stamp check passed. Browser
  appearance and interaction remain for the maintainer's review.

### Grant tradeability and event date follow-up (2026-10-01)

- Migration 101 sets TOTW 1–3 to September 30, 2026 at 19:00 through January 1,
  2027 at 19:00 Europe/Amsterdam, matching the initial Base Icon window.
- Card grants, club pack gifts and gifts to every club now have an Untradeable
  checkbox. It defaults to unchecked and sends an explicit boolean. The backend
  preserves the selected pack restriction through its stored unopened-pack row;
  older clients omitting the field retain the operator-source rule default.
- Developer Cards have a checked, locked control and are forced untradeable by
  item creation and persistence. Migration 102 updates existing owned copies;
  ownership, stats and the exclusive single-copy rule are retained.
- Verification: five focused UI checks, four grant/developer backend checks, and
  two event-date/upgrade checks passed. The production build/typecheck passed.
  Migrations apply on the next server start; the live server was not restarted.

### Rules module review (2026-10-01)

- Added searchable rule sections with readable titles, descriptions and file
  names; aligned rule rows, value type badges and responsive spacing. Motion
  section links and the advanced editor respect reduced-motion preferences.
- Numbers, text and toggles have dedicated controls. JSON editing and formatting
  remain available for complex values; advanced path editing is collapsed by
  default. Documentation can be shown separately from normal rules.
- Reopening an edit uses its pending value, independent of key property order.
  Pending additions can be edited, and each pending change can be undone. The
  editor checks the current published and pending values before staging to
  detect changes since it opened. This is a client preflight check; publication
  retains the backend's existing conflict validation.
- Search input follows route changes, clear search is available, and out-of-range
  pages return to a valid page. Invalid rule-file routes show an explicit error.
- File imports cannot replace pending edits for affected files. The server checks
  this under the content transaction lock; unrelated file imports remain allowed.
- Browser review: open Rules, switch sections, search, edit a number or toggle,
  reopen its pending value, undo it, and inspect the advanced editor. Changes still
  need review and publication before they affect the game. No running server or
  live database was changed.
- Verification: nine focused Rules UI tests and five isolated backend tests passed.
  Production build/typecheck, source stamp and scoped whitespace checks passed.
  Browser appearance and interaction remain for the maintainer's review.

### Changes module review (2026-10-01)

- The shared page header capped its text width at 72ch. Changes now has a wider
  header and a shorter description, avoiding the isolated “before Publish” line
  on wide screens. Other page headers retain their existing sizing.
- Pending review, filters, content groups, validation and publication have
  consistent spacing. Note and author inputs align side by side on wide screens
  and stack on mobile. Existing tab, dialog and reveal Motion transitions remain.
- Before/After columns align by field. Structured values use readable JSON with
  bounded scrolling, and long keys wrap without extending the page. Removed rules
  are described as removals and their previous values remain available for review.
- Added Clear filters. Filtering changes the review list only; publication still
  includes every pending edit. The existing validation, conflict recovery,
  discard confirmation and retained-draft flows are preserved.
- History states its 50-entry limit and explains why pending edits prevent a
  revert. Revert stays disabled until pending status is available and clear;
  inserted-player-card restrictions remain. Publish confirmation also closes
  when publication becomes unavailable before the request starts.
- Verification: 20 focused Changes UI and change-summary tests passed. Production
  build/typecheck passed. Browser layout and interaction remain for review; no
  running server or live database was changed.

### Access & maintenance module review (2026-10-01)

- Added responsive spacing, aligned access controls and player-test results,
  readable role selectors, bot configuration feedback and inline Discord ID help.
  The shared dialog/reveal animations respect reduced-motion preferences.
- Founder and Co-Founder IDs appear as one Founders group. Developers, Boosters
  and Whitelisters use the supplied IDs. Additional Discord roles appear when
  fetched; manual Extra role ID controls also add roles to either permission list.
  Selected role IDs persist through the existing access API.
- Removed form remounting on connection-count refreshes. Unsaved drafts survive
  refresh errors and external setting changes. Saving checks the latest editable
  settings first; changed settings require resetting/reviewing the form. This is
  a client preflight check, not an atomic concurrency guarantee. Server limits are
  reflected in message, player-cap and role-list validation.
- Player checks use saved settings, distinguish account/role access from an
  actual launch test, and ignore late replies for a different account or policy.
  Backups are paged by ten rows. Backup/restart state refreshes every ten seconds;
  duplicate controls are disabled while actions or requests are pending.
- Migration 103 enables maintenance and grants play access plus maintenance
  bypass only to Founder, Co-Founder and Developer roles. Boosters and Whitelisters
  grant no access by default. Any allowed role admits an account: a Developer
  with Whitelister also passes. An account block still overrides all roles.
  Guild ID, message, cap and account flags are preserved. The initial policy runs
  once; later panel edits survive restarts. Missing database access policy now
  fails loading instead of falling back to unrestricted access.
- No running server or live database was changed. Migration 103 applies on the
  next server start. Browser review: inspect named roles, add an extra role, edit
  without saving and refresh, then test a player after saving. Real Discord role
  checks require the saved server ID and the server bot configuration.
- Verification: nine focused UI checks and eight backend checks passed. The two
  initial-policy cases also passed after adding the explicit Developer +
  Whitelister assertion. Production build/typecheck, source stamp and scoped
  whitespace checks passed. Real Discord calls and browser appearance remain
  unverified.

### Matchmaking module review (2026-10-01)

- Split Searching, Pre-match and In-match into separate counters and tables.
  Initializing, connection verification and pre-game belong to Pre-match;
  only IN_GAME counts as playing. Post-match and unknown-phase games have their
  own sections when present. These are server states, not native gameplay proof.
- The registry captures mode, game ID, phase and search duration together under
  its lock. Matched players retain their mode. The API includes queued/matched
  players without presence and identifies their missing active Blaze session,
  so list membership agrees with counts. Pairing and gameplay logic are unchanged.
- Added player/mode filters, clear filters, ten-row paging, fixed table columns,
  consistent responsive spacing, Pause/Resume and manual Refresh. Failed refreshes
  retain the last snapshot with an error and timestamp; unavailable registries
  and stopped searches are explicitly identified. Summary counts stay unfiltered.
- Verification: three focused UI tests and four backend tests passed, including
  the existing pre-game lifecycle check. Production build/typecheck, source stamp
  and scoped whitespace checks passed. No live runtime or database was changed.
- Browser review: inspect the three sections, filter players, page a long queue
  and pause/resume polling. Actual two-player transitions remain a VPS test.

### Logs and Settings module review (2026-10-01)

- Logs: file search, sorting, sizes and ten-row paging; explicit Pause/Resume
  follow and manual refresh. The file list refreshes every ten seconds and the
  selected tail every three seconds. Searches debounce for 300 ms. Each view
  requests at most 500 matching lines and explains the server's 4 MB search cap.
  Failed refreshes retain the last lines with an error and absolute load time.
  Empty lists and absent filenames no longer leave the tail loading indefinitely.
- Settings: search, aligned responsive controls, per-value Reset and a Motion
  reveal for the optional read-only startup environment. Unsaved values survive
  filtering and refresh failures. Saving checks the current value first; external
  changes require resetting/reviewing it. This preflight is not atomic. The player
  cap now exposes the backend's validation limits. Writes remain immediate.
- Removed obsolete settings from GET and POST: special rarity weights, rating
  thresholds for fallback rarity and all elite weighting controls. Runtime pack,
  mixed-pack and preview entrypoints always pass a promo selector, which uses
  promo buckets. Kept size limits, points-price fallback, tier boundaries and
  reward special-chance default because they retain runtime consumers. Stored
  legacy columns remain for schema compatibility.
- Migration 104 consolidates 94 ST Tévez: keeps definition 21421/resource
  67251865, whose stats are recovered by migration 047 and whose dynamic portrait
  exists in panel assets; removes duplicate 21013/resource 50474649 after moving
  owned-item, squad, pick, draft, market and search-projection references. Adds
  the survivor to Flashbacks and removes its Libertadores Flashback membership.
  Other Libertadores Flashback cards remain. No live database was changed;
  migration 104 applies when the rebuilt server starts.
- Browser review: search/filter logs, pause/resume, inspect timestamps; edit a
  setting, refresh without saving, reset and save; expand Startup environment;
  check that only the dynamic 94 Tévez appears and belongs to Flashbacks.
- Verification: nine focused UI checks and five backend checks passed, including
  fresh/upgrade Tévez ownership preservation and the full catalogue metadata
  regression. The catalogue test's fixed query count now includes the existing
  promo/event/rotation queries; duplicate counts include Tévez. Production
  build/typecheck, source stamp and scoped whitespace checks passed. Browser
  appearance and live migration application remain untested; no runtime restart,
  commit or push was performed.
