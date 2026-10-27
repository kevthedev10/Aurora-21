# Operator panel page: provenance

These files are the shared Aurora operator panel's front-end, copied with the maintainer's
authorization from the Aurora16 repository, `src/panel/` at commit `027a5c1` (2026-09-25),
for use in Aurora 21. Hub framing still needs a live check because the current response
headers deny it. `OperatorPanel`
(`src/Aurora21.UltimateTeam.Server/Panel/`) serves them and answers their `/panel/api/*`
contract (Aurora16 `docs/PANEL-CONTRACTS.md` section 6) from Aurora 21's data.

Changes from the Aurora16 copy, all for Aurora 21:

- `index.html`, `js/pages/dashboard.js`: "Aurora 16" labels read "Aurora 21";
  the navigation now includes FIFA 21 Modes, and dashboard access/edit labels describe
  the direct loopback panel and immediate PostgreSQL edits.
- The copied FIFA 16 `js/pages/modes.js` and `js/pages/tournaments.js` were removed.
  A new `js/pages/modes.js` reads per-club FIFA 21 Division Rivals, Squad Battles,
  FUT Champions/Weekend League and Draft state and offers audited progress resets.
- `js/pages/pack-manager.js` uses FIFA 21's PostgreSQL store pack fields. Definition
  edits, hides and lineup selections stage in Pending changes for one reviewed publish;
  the copied FIFA 16 pack schema, reward-card assignment editor and ordering controls
  are not used.
- `css/panel.css` gives the Pack catalogue fixed column widths so its Edit and Hide
  controls remain visible at a normal desktop width.
- `js/pages/packs.js` now previews standard FIFA 21 player packs and shows the live
  catalogue and club gifts without the copied Aurora 13 odds and FUT environment labels.
- `js/pages/players.js`: the FIFA 16 seasons and draft/tournament/ban cards removed; an
  access card (block, maintenance bypass, note) added; the list's description says players
  are verified Discord accounts, not addresses.
- `js/pages/maintenance.js`: players are verified Discord accounts, so the Blaze access
  card became "Who may play": Aurora 16's Discord role tools (bot status, role picker,
  "Test a player") now also pick the roles that may play, beside the maintenance bypass
  roles; the address lists became Discord id lists; the maintenance message and the
  Discord server id were added; the panel address list was removed; and a backup is
  requested from the backup service rather than taken by the panel.
- `js/pages/maintenance.js`, `css/panel.css`: the four "Who may play" lists (play roles,
  maintenance bypass roles, bypass accounts, blocked accounts) are removable entries, not
  comma-separated text. Roles show their Discord name once the server's role list loads and
  are added from a picker or by id; accounts are added by Discord user id. The page checks
  ids (5 to 32 digits) and skips duplicates, and Save still sends each changed list whole as
  comma-separated text. The Maintenance switch applies on its own as soon as it is confirmed,
  on or off, shows the current state beside it, and leaves the card's other edits unsaved.
- `js/pages/settings.js`: the maintenance confirmation, the access card (now "Launch
  access") and the environment notes describe Aurora 21: launches are refused except for
  bypass roles and accounts, and start-up options change in the VPS `.env` with a container
  restart, not from the panel.
- `js/pages/maintenance.js`: a "Game content" card (Aurora 21 only, no Aurora16 original)
  shows the content revision this server runs and reloads the catalogue and rules without a
  restart through `GET /panel/api/content` and `POST /panel/api/content/reload`
  (`archive/panel-plans/live-content-editing-plan.md`, step 1).
- `js/pages/changes.js` (Aurora 21 only, no Aurora16 original), `index.html`, `js/core/panel.js`
  and `css/panel.css`: the "Pending changes" and "Change history" pages. They review content
  changes field by field, validate, publish and revert through `/panel/api/content/changeset`
  and `/panel/api/content/history`, with a "pending" pill in the side strip that the status
  refresh fills (`archive/panel-plans/live-content-editing-plan.md`, step 4).
- `js/pages/rules.js` (Aurora 21 only) edits PostgreSQL rule values and imports bundled
  differences through that same changeset review
  (`archive/panel-plans/live-content-editing-plan.md`, step 6).
- `js/core/fut-card.js` (Aurora 21 only), `index.html` and `css/panel.css`: FIFA 21 card art
  drawn from the images the server serves under `/panel/assets/` (downloaded with
  `ultimateteam/download-card-images.py`; current assets live in
  `ultimateteam/panel-assets/`). `js/pages/cards.js` and the
  player inventory in `js/pages/players.js` get a Cards/Table view, and `js/pages/packs.js`
  and `js/pages/pack-manager.js` show drawn cards in their previews. Without images every
  page keeps its text view.

Compare against a newer Aurora16 `src/panel/` to take over its fixes.
