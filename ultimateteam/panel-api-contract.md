# Operator panel API contract

The Aurora 21 server embeds the React panel under `/panel/`. It uses the API
described here. See the
[panel guide](panel-web/README.md) for running and reviewing the frontend.
The server answers `/panel/api/*` from PostgreSQL and live session state.
The default hosted stack enables it
on `127.0.0.1:18448`, for a direct SSH tunnel from the panel hub. Loopback access is the
credential: the server refuses forwarded requests, foreign Host headers, cross-site requests
and non-JSON writes. Keep port 18448 private. The old token-authenticated
`/api/v1/panel/*` endpoint is removed.

The page and API are Aurora 16-shaped where useful, but game mode and pack fields come from
FIFA 21. Club routes use the derived **persona ID** in the path. `discordId` in a player
response is known only after a verified account launch; an address never identifies a player.

| Route | Purpose |
| --- | --- |
| `POST /panel/api/login`, `POST /logout`, `GET /session` | Loopback panel session handshake |
| `GET /dashboard` | Services, online players, clubs, recent logins, 14-day activity |
| `GET /players`, `GET /players/{personaId}` | Player list and detail, including balances, access, sessions and edit history |
| `GET /players/{personaId}/inventory?pile=` | Inventory per pile |
| `PATCH /players/{personaId}` | Set or adjust balances, grant cards/items/packs/Season XP, remove player cards, reset club contents, or edit that player's block/bypass/note |
| `GET /cards`, `GET /cards/specials` | Native card picker and special rarity list |
| `GET /cards/filters` | Catalogue nation/league names and clubs with their card-associated leagues |
| `GET /cards/items?tab=` | Grantable consumables, club customisation items or managers (list only, never edited) |
| `GET /packs` | Store and owned pack overview, grantable packs and recent gifts |
| `GET /packs/catalog` | FIFA 21 store and reward definitions, revision, categories and published pack IDs |
| `POST /packs/catalog`, `PUT /packs/catalog/{id}` | Stage a new pack (player, mixed-item or player-pick), unassigned until the lineup sells it or a reward names it, or an edit, for review |
| `DELETE /packs/catalog/{id}` | Stage a pack hide; its definition and owned copies remain after publication |
| `GET /packs/catalog/{id}/delete-check` | What still depends on a store pack: blockers, whether Delete or Force delete is possible, unopened copy and club counts |
| `POST /packs/catalog/{id}/delete` | Stage deleting a store pack definition (`store_pack_delete`), or with `force` also its unopened copies (`store_pack_force_delete`) |
| `POST /packs/catalog/test` | Preview player/mixed pack contents or player-pick choices without creating items or spending currency |
| `POST /packs/simulate` | Draw 1–20 player/mixed pack previews without changing a club |
| `POST /packs/odds` | Seeded whole-pack probability estimates with sample counts and 95% intervals; supports pending content |
| `GET /promos`, `GET /promos/{code}` | Published promo list and full editable definition with weighted buckets and explicit card lists |
| `GET /promo-events`, `GET /promo-events/{code}` | Ordinary promo windows and their memberships |
| `GET /icon-rotations`, `GET /icon-rotations/{code}` | Independent windows replacing the standard Icon tier |
| `GET /promos/availability?at=&pending=1` | General eligibility, active promos and selected Icon tier at a UTC instant |
| `POST /promos/{code}/buckets/from-ratings` | Propose explicit bucket membership from rating ranges; does not stage or publish |
| `POST /promos/distribution` | Conditional card probabilities within a category, adding overlapping promo routes |
| `PUT /packs/lineup` | Stage the non-grant-only packs the store sells; required built-ins stay enabled |
| `GET /maintenance`, `POST /maintenance/*` | Discord guild/roles, player access, the new-match pause, backup and server-restart requests |
| `GET /modes`, `GET /modes/{personaId}` | Clubs and per-club Division Rivals, Squad Battles, Weekend League and Draft state |
| `POST /modes/{personaId}/{mode}/reset` | Reset current progress for `rivals`, `squad-battles`, `champions` or `draft` |
| `GET /logs`, `GET /logs/{name}`, `GET /matchmaking` | Server logs and live searches/matches |
| `GET /content`, `POST /content/reload` | Content revision and counts; reload the PostgreSQL catalogue and rules without a restart |
| `GET /content/status` | Committed database changes outside the loaded revision, affected tables and whether explicit reload is needed |
| `GET /content/changeset`, `PUT /content/changeset/changes`, `DELETE /content/changeset/changes/{id}` | The pending content changeset; stage or drop one change |
| `POST /content/changeset/validate`, `POST /content/changeset/publish`, `POST /content/changeset/discard` | Dry run, publish or discard the pending changes |
| `GET /content/history`, `POST /content/history/{id}/revert` | Published changesets; stage a revert |
| `GET /content/rules`, `GET /content/rules/{file}` | List rules files and page or search one file's database rows |
| `GET /sbc` | SBC sets (bundled and panel-added, with hidden/edited state) plus the editor's tabs, badges, requirement types and packs |
| `POST /content/rules/import` | Stage differences from bundled rules files for review |
| `GET /assets` | Whether panel images are installed, with a count per category |

The Settings page and its `GET/POST /settings` and `GET /settings/config` API were removed (2026-10-04). The pack
limits it edited (largest store and kit pack, gold and silver tier boundaries) stay in the database at their current
values; maintenance and the player cap are edited on Access & maintenance. Old `#/settings` links open Access. Coins
per FIFA Point went too (migration 153): a coin listing sells for coins only and a Token Store listing for its own
Aurora Tokens price, so nothing converted between them any more.

`GET /matchmaking` is a read-only lobby snapshot. `snapshot.available` reports
whether the registry exists, `current` also requires player presence, and
`accepting` indicates whether new searches are allowed. Its players include the
union of active Blaze presence and queued/matched players; `connected: false`
marks a registry entry without an active Blaze session. Matched rows retain their
mode and expose `gameId`, `gameState` and `phase`: states 1/3/130 are `pre_match`,
131 is `in_match`, and 4 is `post_match`. An unknown state has no phase. Searching
rows expose `queuedForMs`, measured using monotonic elapsed time. `counts.match`
still counts paired players; `preMatch`, `inGame` and `postMatch` count players in
each phase, while `games` counts distinct paired games. This does not prove that
native clients completed setup or playable networking.

All paths in the table after the first segment are under `/panel/api`. Panel images are served outside the API at
`GET /panel/assets/{players|dynamic|clubs|leagues|nations|cards/hd}/{file}.png`, from the folder
`download-card-images.py` fills (`ultimateteam/panel-assets` in a checkout). The server prefers that folder beside
`runtime-source` when present, otherwise uses the runtime's `panel-assets`; `--panel-assets-dir` overrides both. Images use the same private port;
a missing image answers `404` and the page shows text instead. JSON errors include
`error`; validation and stale catalogue revisions return 4xx, not raw database exceptions.
The page is embedded in the server assembly, so its routes and API are deployed together.
Bundled custom native heads have PNG sidecars beside their DDS files. Their previews use the same private
`dynamic/p<resourceId>.png` route, ahead of downloaded images, and are included in the head choices.
Card art includes `defaultPortraitUrl` when such a preview exists, plus skill moves, weak foot and work rates.

Portrait uploads in the create/edit card form fit the visible subject using the PromoFit v4 defaults,
with size, horizontal/vertical position and optional lower-edge fade controls. `POST heads/preview`
accepts base64 `rgba` (180 × 180) and returns a base64 PNG decoded from the generated DXT5 DDS;
it creates no head and changes no card. The UI previews these game pixels before `Use portrait`
uploads the prepared image. Saving the card applies the selected head. Uploaded PNG previews,
including older uploads, are decoded from the stored DDS; their URLs use `?texture=2` to avoid
cached previews from the old conversion. The browser card layout approximates FIFA's rendering.
Colour compression uses portable C# alpha-weighted palette refinement based on the reviewed
Dost repair utility; Python and Windows texconv are not server runtime dependencies.


Card list filters include `pos` (one position or a comma-separated set), `nation`, `league` and `team`
(positive native identity IDs), alongside the existing search, type, rating and pagination parameters.
`cards/filters` returns `nations[]` and `leagues[]` with `id`/`name`, plus `clubs[]` with `id`, `name`,
`leagueId` and `leagueName`. Club/league pairs come from the current card catalogue; selecting one sets both
filters. A club with cards in multiple leagues can therefore have multiple labelled choices. Card list rows
also expose `skillMoves`, `weakFoot`, `workRateAttack` and `workRateDefense` for the table view.
Their `nation` display name is resolved from the relational nation catalogue by the card's native nation ID;
it does not require the legacy `nationName` compatibility field.

Card list sorting uses `sort=name|rating|pos|skill|weak|work|PAC|SHO|PAS|DRI|DEF|PHY|type|nation|packs`
and `direction=asc|desc`. Sorting applies to all matching definitions before offset/limit pagination.
The default remains rating descending, then name and resource ID for stable ordering. Face-stat columns
use the matching face slot for goalkeepers as well. Unknown sort keys or directions return 400.

## Pack publication

There is one kind of pack since migration 119 ([packs and pack odds v3](../docs/pack-odds-v3.md)). A pack is
used by the **Store** while the published lineup sells it and by **Rewards** while a published rule names it; a new pack
is **Unassigned**. The former reward-only rows are ordinary packs outside the lineup.

Store placement, prices and purchase tradeability are managed in two independent lineups (migration 148):
**Store Lineup** sells for coins; **Token Store Lineup** sells for Aurora Tokens. The same definition can appear in both.
Regular offers use the definition's native ID and the existing `store_packs.price` backing column; token offers reserve
separate native IDs in `token_store_listings` and map purchases to the original definition. Removed token offers retain
their reserved IDs so an old client cannot buy a different pack under a reused ID. Token prices previously configured on
listed packs are migrated into token offers, and regular listings no longer advertise or accept token payment.
The old `store_section` column is retained for migration/history compatibility; it is no longer a pack editor setting.

`GET /packs/catalog` returns `revision`, `groups` (the artwork categories in art order, then legacy aliases),
`publicPackIds` (the packs for sale), `listing[]` and `tokenListing[]` (each `{ packId, price, enabled, untradeable }`), `packs[]` (each
with `inStore`, sold, and `listed`), `promos[]` (`code`, `name`, `category`), `pickLimits` (`minChoices`, `maxChoices`,
`maxTokensPerGroup`) and `oddsTemplate` (`icon`, `pickTotw`: the values the editor fills into a new odds table, from
`rules/pack_odds.json`) and `vanityTypes[]` (`subtype`, `name`, `items`: the My Stadium item types a My Stadium pack slot
can draw and how many items a store pack may give). Each pack has `id`, `description`, `blurb`, `group`, prices, contents summary, `locked`,
`grantOnly`, `inStore` (sold by either published lineup), `usedBy[]` (the published rules paths that name it, `file.path`, found as
for Delete below except that `tradeability` paths do not count), `art` (the store category whose tile art it shows),
`viewOnlyReason` and its stored `config`. `viewOnlyReason` is set when what the pack draws comes from somewhere the editor
does not control: a specialist generator (club items, consumables, managers, stadiums, swap tokens; not My Stadium packs, which are edited below), a fixed card pool
(`resource_pool`) or the red-pick ids 550–553. Such a pack can only change
`name`, `bio`, `store_group`, `purchase_limit`, `community_limit` and `admin_only` (`400`
otherwise).

The retired Match Pack (544) is omitted from the catalogue and cannot be selected as a completion reward. Its definition and stored opening path remain for previously earned packs.

Mode completion rewards are edited as one `rules` change at `seasons.matchCompletionRewards.<mode>`: `{ coins, packId, untradeable }`, where `packId: null` disables the pack. Each eligible match pays the flat coins and one selected pack, independent of result, statistics and difficulty, without the retired daily cap.

Create and edit bodies are `{ "revision": "<current>", "pack": { ...fields } }`. A new pack takes the next id after every
pack, owned legacy definition and reserved token offer and starts out of the lineup. Its art is its
`store_group` (the editor defaults to `special`): a pack draws the art of its store category whenever it names one, in the
store, in My Packs and on SBC reward tiles alike, and only a pack without one draws by id. Pack changes staged or published
before migration 119 may still name `special_chance`, `banded_odds` or `odds_table`; those fields are ignored, so such a
change can still be published or reverted with its other fields.
The editable definition fields are `name`, `bio`, `store_group`, `size`,
`purchase_limit`, `community_limit`, `gold_weight`, `silver_weight`, `bronze_weight`,
`rare_slots`, `min_rating`, `max_rating`,
`guaranteed_specials`, `manager_chance`, `chem_style_chance`, `promo_only`,
`admin_only`, `allow_unweighted_specials`, `include_general_pool`, `promo_codes`, `category_chances`,
`player_picks`, `player_pick_count`, `bonus_player_count`, `bonus_min_rating`, and `bonus_promo_only`.
`allow_unweighted_specials` is retained legacy metadata; it does not control player draws. Advanced built-in rules remain stored when
these fields change. An invalid size, rating range or rare/special count rolls back.

`PUT /packs/lineup` takes `{ "revision": "<current>", "listing": [{ "packId": 1, "price": 7500, "enabled": true, "untradeable": false }] }`:
the whole coin store listing. Price is a positive whole coin amount. `PUT /packs/tokens-lineup` takes the same shape, with price in Aurora Tokens. A pack is sold only when it is listed and `enabled`; `untradeable` is what a
purchase opens with. Store Lineup defaults to tradeable and Token Store Lineup defaults to untradeable; the panel offers only those two explicit choices and marks the appropriate default. A missing/null choice uses that lineup's default. Migration 149 resolves existing inherited choices before retiring the global Store rule from the Rules page, preserving their effective tradeability.
Grant-only packs cannot be listed; a required built-in pack must stay listed and enabled; an enabled pack needs a price.
An older client's `{ "revision", "publicPackIds": [...] }` lists those packs enabled, keeping their existing price and tradeability. Historical three-field listings also retain their existing coin prices.
The catalogue and lineup routes stage `store_pack`, `store_lineup` and `token_store_lineup` changes; their replies
include `applies: "after-review"`. The Pending changes page validates and publishes both in
one transaction. Publication changes PostgreSQL and the running immutable pack snapshot;
the next FUT store request reads it. FIFA 21 displays packs in pack ID
order. A stale revision returns `409`; refresh before retrying. Hidden coin listings keep their independent token offers. Definitions remain
grantable and already owned packs can still open. Hide switches off the coin listing; reverting a pack's creation does
the same. The lineup refuses a grant-only pack and, at publish, an enabled pack without a positive price in that lineup’s currency. New catalogue writes reject `price`, `points_price`, `store_section` and `enabled`; those are listing settings. `POST /packs/catalog/test` takes a
`pack` of editable fields and optional `packId` to inherit an existing definition. It
previews supported player, mixed-item and pick packs, including pending edits,
without publishing; an unsupported specialist generator returns `422`. `POST /packs/simulate` takes `{ "packId": 1, "rolls": 2 }`
and also includes pending edits.

### Deleting a pack

Hide (`DELETE /packs/catalog/{id}`) only stops selling a pack. Delete removes the store-pack definition itself and is
staged and published through the same content changeset as other pack edits: it shows in the pending banner, is
checked by `POST /content/changeset/validate` and appears in content history. The checks are repeated inside the publish
transaction, so a pack that gained a dependant after staging is refused at publish.

`GET /packs/catalog/{id}/delete-check` (read-only; `404` for an unknown id) answers
`{ id, name, kind, revision, blockers[], canDelete, canForceDelete, unopened: { copies, clubs } }`. Each blocker is
`{ code, message, overridable }`. `POST /packs/catalog/{id}/delete` takes `{ "revision": "<current>" }` and stages the
deletion (`{ id, force, pending, applies: "after-review", unopened }`). It is refused with `409` and the blocker message
while any of these holds (none of them can be overridden except the last):

- `not_pack`: the row is the custom-pack template (0) or a display-only reward name (500), not a pack. Any pack may be
  deleted once nothing below uses it; the server's start-up checks no longer require particular pack rows.
- `built_in`: the pack is `always_present`.
- `in_lineup`: the pack has an enabled coin listing or is present in the token lineup (including a switched-off token listing). Hide it (or take it out of
  the lineup), publish that, then delete it. Hidden and grant-only packs may be deleted.
- `referenced`: a rules document names the pack id (any `packId`/`pack`/`packs`/`rewardPackId`/`neverPacks`-style
  property, a pack-id keyed map such as `tradeability.packs`, or a `playerPickPacks` value), a mode reward table pays it,
  or an SBC reward pays it (any catalogue set, live or not: "SBC Bronze Upgrade (UPG_BRONZE)"). The message lists where.
  References are read from the published content, so remove them and publish that first.
- `held_picks`: a club still holds a player pick item whose `source_pack_id` is this pack.
- `owned_copies`: a club owns an unopened copy (`unopened_packs`). This is the only overridable blocker.

Publishing a delete removes the `store_packs` row and its purchase-counter rows (`club_pack_purchases`,
`community_pack_purchases`). The counters go with the pack because the next new pack can reuse a freed id and must not
inherit another pack's purchase history or community stock. Items already opened from the pack are untouched.

**Force delete.** `POST /packs/catalog/{id}/delete` with `{ "revision", "force": true, "copies": n, "clubs": m }`, where
`copies` and `clubs` are the counts the operator was shown (`unopened` from the check). A mismatch returns `409` with the
current `unopened` counts, and a missing count returns `400`. Every other blocker still applies. The change is staged in the
`store_pack_force_delete` area and records the counts as its before value. Publish deletes every unopened copy of the pack
from every club and then the definition in one transaction (all or nothing), under a table lock on `unopened_packs` so no
copy is granted meanwhile. A copy granted or opened after review changes the recorded counts, so validate and publish
report a conflict and the operator must stage it again with the new counts. Coins and FIFA Points are not refunded. Each
force delete writes a `panel_action_receipts` row (`operation = pack.force_delete`, a request id
`pack-force-delete-{packId}-{guid}`) whose `result` holds `packId`, `name`, `copies`, `clubs`, `perClub[]`, `changesetId`,
`stagedBy` and `source`, plus a `PANEL` log line when it is staged. The content history shows the change under its changeset.

The `store_pack_delete` and `store_pack_force_delete` areas use `{ "packId": 1 }` as a key; their only operation is
`hide`, which in these areas means "remove the definition", with no value. Their before value is the pack row (plus
`unopened` for force). They apply after `store_pack` and `store_lineup`. A published delete cannot be reverted from
history (the definition is gone): create the pack again instead.

`include_general_pool` defaults to true (including a stored null). It includes ordinary cards, the current Icon tier
and active event promos. `promo_codes` adds specific active catalogue promos independently of event dates; false
with an empty list is invalid. Champions reds and Developer Cards cannot be selected as general sources.
`category_chances` is the pack's odds table per rare slot (per choice for picks): special odds category codes (`totw`,
`icon`, other promos by code) and `upgraded` (specials rated above a silver or bronze slot whose base card, the same asset
id, is in that tier) mapped to probabilities from 0 to 1, summing to at most 1. There is no fallback: a category the table
leaves out has no chance, and the remainder draws standard cards. A slot chooses its tier, then a row, then a saved bucket
of that promo, then an eligible card weighted by its inverse draw reduction; a row with no eligible card (an Icon in a silver slot, a promo with nothing in
the pack's rating range) falls back to a standard rare. Icons drop only in gold slots and upgraded specials only in silver
and bronze slots. Forced specials (`guaranteed_specials`, `promo_only`) use the special rows rescaled; with no rows,
eligible special categories have equal weight. A specials-only pack or pick with a table must total exactly 1 (`400`).

The React editor shows the odds table complete: every category the sources offer needs a value (0% leaves it out), and the
`upgraded` row appears once a pack has silver or bronze slots. An empty table starts from `oddsTemplate`. Totals above 100%,
missing rows and specials-only totals other than 100% block staging and preview. Saved odds for unavailable categories
remain visible as warnings. Its one special-player requirement control maps to the stored `guaranteed_specials` and
`promo_only` fields. Player-only packs clear legacy manager/chemistry replacement chances when staged. Publishing an event
from the Events page offers to stage odds for its promos in every pack that draws the current event but has no row for
them: ticked packs get the entered chance, unticked packs 0%.

### Store purchase limits

`purchase_limit` is a lifetime allowance per club/pack; `community_limit` is
shared stock across clubs. Zero means unlimited. They are mutually exclusive:
the editor clears the opposing field and the API/database reject both positive,
including partial updates against an existing opposing limit. Community totals
are capped at 999,999 for native display.

Successful store purchases increment persistent counters transactionally;
community purchases lock the shared counter to prevent overselling. Failed
payment/draws roll back stock and coins. Gifts, rewards and owned-pack openings
do not consume store allowances. Counts start at migrations 105 (per-club) and
107 (community); changing modes does not reset them. Increase the configured
total to add allowance, or use a new pack ID for separate stock.

Per-club tiles send their remaining `quantity` and no finite global stock;
community tiles send shared remaining `quantity` and `globalAvailableQuantity`,
with no per-club allowance. Both exclusive modes display **Packs Remaining:** in
the maintainer's FIFA test. Detailed evidence and remaining shared-stock live
checks are in [store limits](panel-web/test/store-purchase-limits.md).

`player_picks` is an array of groups, for example
`[{"count":2,"choice_count":3,"min_rating":82,"max_rating":99,"promo_only":true,"label":"82+ special pick"}]`.
Groups may override `include_general_pool`, `promo_codes`, and `category_chances`; otherwise they inherit the pack.
They may also retain explicit rarity, team, nation and named reward-pool filters. A **fixed** group lists `choices` (2 to the
pick limit of different player card resource ids, `choice_count` equal to their number): its tokens offer exactly those
cards, in that order, and the rating, special and source filters do not apply (owner, 2026-10-04: for example two
versions of one player). A group without `choices` is a **random** pick. `size` must equal the number of
tokens plus `bonus_player_count`; `player_pick_count` must be zero when using groups. The old single-pick setting
remains readable and can be converted to groups by the editor. New packs start hidden unless enabled or included
in a staged lineup. Store picks cannot name Champions red sources.

New tokens capture their draw configuration. Availability and saved bucket weights are evaluated on first reveal;
the resulting choices are saved and never rerolled. A forced pick whose event has expired fails and keeps its token.
Pick previews return sample player choices with `pickIndex` and `choiceCount`; bonus players omit `pickIndex`.
The preview list counts offered choices, not the number of awarded tokens.

## Objectives

The Objectives page edits `rules/objectives.json` as ordinary `rules` changes of file `objectives` (whole `grindGroups`,
`gifts` or `welcomeGroup` values, the `live` list, the whole `seasonProgress`, `campaign.name` and the whole `seasons`
list), published under Changes; the
Rules page no longer lists that file. The welcome group, the grind groups and the gifts are the catalogue. `live` is the
ordered list of `{group, enabled}` the game shows (FIFA's priority follows it); without a stored list every group is shown,
welcome first. A listed group that is not in the catalogue is refused when the publish loads the content (a group and its
listing may be staged together); a group listed twice is refused when staged. Only live groups are served and claimable.

A group counts from when it first went live (migration 143): the publish (or start-up, for a fresh database) that first lists
a group live records it in `objective_group_starts` and stores each club's current counters for its stats in
`objective_baselines`; progress is the counter minus that. Groups the rules held when migration 143 ran keep counting from
the club's creation. A reward (objectives, groups, gifts, Season Pass levels) pays `coins`, `fifaPoints` and `packs`
(`[{packId, count 1-100, untradeable}]`, or the older single `pack`/`untradeable`), plus the My Stadium extras (`vanity`,
`stadiumTier`, `unlockSlots`), and must pay something; every pack must exist when the publish loads. Award tiles are sent for
coins, each pack line and the My Stadium extra; FIFA Points have no known FIFA 21 tile and are paid without one (unconfirmed).
A Season Pass level pays one reward, which may be a choice: `{"options": [reward, …]}` (2 to 5, nothing else beside them;
only levels may have options). Each option is one `rewardOptionList` entry (`optionId` 0, 1, …); the claim
`/scmp/campaign/level/{level}/reward/{optionId}` pays that option only, and the club's pick is stored (claim scope
`level_option`, migration 144) and served back as the claimed level's `chosenOption`.
Season XP per completed match is each mode's `seasons` `matchCompletionRewards.<mode>.xp` (Modes), the same whatever the
result, and claimed objectives add their own `xp`; SBCs and opened packs earn none (migration 145 moved `xp.match` into
every mode and retired `xp.win`; migration 146 removed the objectives `xp` rules).

Seasons (migration 166): `seasons` lists custom seasons `{id, name, startsAt, endsAt, levelCount, xpPerLevel,
rewardLevelCap, levelRewards}` (ISO 8601 times with a UTC offset; the start is inclusive, the end exclusive). The season
whose window holds now is in force: `/scmp/campaign/active` sends its id, name, start and end and serves its levels, and
Season XP and level claims go to it. Outside every season the default season runs: `campaign` (id, name, rolling window)
with `seasonProgress`. A club's Season XP and Season Pass claims (`level`, `level_option`) are kept per season id:
`objectives.season_id` names the season the stored progress belongs to, and the club's first save under another season
moves it to `objective_season_history` and takes up that season's row from there (none = 0 XP, nothing claimed). Objective
and group claims and stat counters are not per season. Season ids are unique, never the campaign id, and seasons never
overlap (refused when staged).
`GET objectives` returns `settingsFile`, the published `rules`, `liveGroupIds`, `stats`, `nextGroupId` and `nextObjectiveId`
(past every id in the file and every id a club has claimed, so retired ids are never reused), `started`, `packs` and
`seasons`: `{now, current, defaultId, nextId, played: [{season, clubs, xp}]}` (the server time, the season in force, the
default season's id, the next free season id past every id in the file or with club progress, and clubs and total XP per
season).

## Mode resets

The reset body is `{ "confirm": true }`. A player with an active Blaze session, search or
match must leave first (`409`). The reset is a per-club transaction recorded in
`panel_edits`. Rivals clears its progress and pending match; Squad Battles clears the
current week's progress, set and pending match; Champions clears its current match and
current Weekend League event record; Draft removes its runs and their child picks. Earlier
weekly/event records and settled reward results remain, as do Weekend League entry redemptions and registration, so a reset
does not refund entry points or duplicate prizes. A Draft reset abandons an unfinished
paid run and pending coin reward; the page confirms this explicitly.

## Content changes

Content edits are staged, validated and published through Changes. The React
button validates first and opens the publication confirmation only on success;
changed pending content invalidates that result. Publication validates again
inside the server transaction. Pending changes are visible in the page banner
and status drawer.
`GET /content` returns `revision`, `loadedAt`, `catalogSha256`, `counts`,
`draftRunsInProgress`, `warnings` and `notes`. `POST /content/reload` reads the PostgreSQL
catalogue, store packs and PostgreSQL rules again with the start-up validation. It
answers the same report, plus `previousRevision` and `catalogChanged`. A load that fails
answers `422`, and the old content stays.

`PUT /content/changeset/changes` takes `{ "area", "key": {…}, "operation": "insert" | "update" | "hide",
"after": {…} | null, "by" }`. The first edit of a row records its live value as `before`,
and an edit back to that value drops the change. The changeset replies are
`{ areas, open, pending, changeset }`. A changeset has `id`, `status`, `note`, `createdBy`,
`createdAt`, `publishedAt`, `revertsChangesetId` and `changes[]`. Each change has `id`,
`area`, `key`, `operation`, `before`, `after` and `updatedAt`.

`POST /content/changeset/validate` applies the changes, loads the content and rolls back:
`{ ok, changes, conflicts[], errors[] }`. `POST /content/changeset/publish` takes
`{ note, by }`. It applies every change or none, and answers `{ published, content }`, where
`content` is the `GET /content` report of the new revision. A conflict is `409` with
`conflicts[]` (`changeId`, `area`, `key`, `expected`, `live`), and any other failure is `422`.
`POST /content/history/{id}/revert` stages the before values of a published changeset as the
open changeset (a hidden row for an inserted one). It is refused (`409`) while other edits are
pending. It also refuses a changeset that inserted a player card: clubs may already own that
definition, so the card cannot be hidden. Use the cards rules to make it grant-only instead.

Only the areas the server lists in `areas` accept changes. The `rules` area uses
`{ "file": "market", "path": ["maintenanceIntervalSeconds"] }` as a key and its
new value is `{ "value": 30 }`. A path contains object keys and zero-based list positions.
`GET /content/rules` lists the files and rule counts; `GET /content/rules/{file}` supports
`q`, `offset` and `limit`. `comments=0` excludes documentation paths whose first key
starts with `_` before searching and paging; other requests include them. Passing
`path` as an encoded JSON path instead returns `{file, definition: {value}}`, or a
null definition for an absent path. Invalid paths return `400`.
The Rules page adds `view=rules` to both list endpoints. This presentation filters
settings with dedicated editors, obsolete controls and reference metadata before
search, counts and pagination; files without visible rows are omitted. Exact
`path` reads remain unfiltered so the dedicated mode editors work as before.
Stored documents, validation and imports retain the required legacy values.
Local startup configuration (including the placeholder club name `Local FC`) is
not part of this hosted rules editor. `club.clubNames` remains available because
it controls the names derived for hosted players.
`draft` is omitted entirely from the Rules browser. `GET /content/modes/draft`
returns `pickSettings` (the active pick controls only) and `pickOptions` (catalogue
category codes and enabled Gold Rare group codes). Modes → Draft separates Rewards
and Pick settings; pick controls are grouped into Basics, Weights and Special
rounds, with normal/weak weights in paired columns. Edits remain `rules` changes
at `["picks", key]`, and publish through Changes. Formations, aliases and item IDs
stay in the stored rules as maintained defaults; legacy rating curves and the
unused `scheduledPool` switch are not exposed.
`POST /content/rules/import` accepts an optional `{ "file": "market" }` and stages
differences from the bundled file; it does not publish them. It returns `409`
without staging anything when a file with import differences has pending rule
edits. Pending edits in other files are preserved.
The `store_pack` area uses `{ "packId": 1 }` as a key and stores a catalogue revision
plus complete pack row. The `store_lineup` area uses `{ "scope": "public" }` and a
revision plus the complete `listing` (an older change holds `publicPackIds`, applied as above). Pack definitions apply
before the lineup.
The `player_card` area uses `{ "resourceId": 158023 }` (a card's native resource ID) as a key. An update's
value is `{ "card": {…}, "cardTypeId": id }` (the type is optional for older clients), with every editable
`player_card_definitions` column under its column name: `rating`,
`position`, `preferred_foot`, `weak_foot`, `skill_moves`, `work_rate_attack`, `work_rate_defense`, `face_slot_0`–`5`
and the 34 detailed attributes. It also carries the in-game head: `head_resource_id` (another card of the same player,
whose head it shows) and `custom_head_id` (an uploaded head of the same player), each null for the card's own head.
`traits` is the list of the card's visible trait IDs; hidden traits are left alone, and a trait added here is stored
with source player 0 (an operator edit). Text values must already be used by some card; numbers are 1–99 (weak foot and
skill moves 1–5). An insert adds a card based on an existing one:
`{ "base": resourceId, "cardTypeId": id, "card": {…} }`, keyed by the new resource ID. That ID is the player's asset
ID plus a whole number (at least 1) of 16,777,216, and the base card's definition, card and trait rows are copied.
Player cards apply before packs and rules. Hiding a card is refused: a card leaves packs through
`grantOnlyResourceIds` in the `cards` rules. `GET /cards/editor/{resourceId}` returns `resourceId`, `assetId`,
`name`, `cardTypeId`, `grantOnly`, `art`, `card` (the editable fields) and `options` (`positions`, `feet`,
`workRates`, `cardTypes[]` with `id`, `name`, `level` and `rareflag`, and `traits[]` with `id`, `name`
and `appliesTo` (`goalkeeper`, `outfield` or `both`, based on the native trait catalogue)).
Standard rare/non-rare types normalize to the rating's bronze (≤64), silver (65–74) or gold (≥75)
level when an insertion or edit is published. TOTW similarly follows silver/gold, but becomes Bronze Rare below 65.
Other special types keep their selected identity. Source Icons can choose only Icon types (native rareflags 12/84)
and must have rating ≥85; ordinary players cannot become Icons. Icon Swaps are unavailable as a new type.
The editor groups tier variants into one choice and makes the whole tile selectable. New card snapshots include
`cardTypeId`, so History can restore the type as well as the attributes. Older pending snapshots without that field
remain valid; restaging adds the type to their baseline.
`GET /cards/images/{assetId}` lists the heads a card of that player can show: `basePhoto`, `cards[]` (downloaded
dynamic portraits of the player's cards: `resourceId`, `name`, `rating`, `rarity` and `url`) and
`uploads[]` (`headId`, `label`, `createdAt`, `createdBy` and `url`). `POST /heads` takes `{ assetId, label, png, rgba, by }`.
Dynamic choices exclude FUT Champions red copies and deduplicate borrowed images by their source card.
The editor also excludes its own source card from these choices; the default head remains a separate option.
That option previews the card's actual own portrait, including a bundled native head or intrinsic dynamic reference;
null editable head overrides do not necessarily mean the base player photograph.
`png` is the preview as base64 (at most 1 MB) and `rgba` is the same picture as 180 × 180 RGBA pixels in base64. The
server writes the game's DXT5 head from `rgba`, stores both as an option for that player, and answers
`{ headId, assetId, label, createdAt, url }`. This route alone accepts a body of up to 2 MB. `GET /panel/heads/{headId}.png`
serves an upload's preview on the same private port. An upload changes no card until an edit that picks it is published. `POST /cards/new` takes
`{ "baseResourceId" }` and returns `{ resourceId }`: the lowest free revision of that player, skipping cards that
are published or staged.
`GET /cards?resourceId={id}` optionally filters by one positive native resource ID, independently of
the card key's rating/rarity suffix; invalid IDs return 400 and a missing ID returns an empty list.
Card name searches (`q`) ignore case and Latin accents, so `Eusebio` matches `Eusébio`;
the displayed name and native resource IDs are unchanged.
This lets detail pages resolve the same card after its rating changes. Published card art (including
editor `art`) carries nullable `customHeadId`; the panel renders it through the private
`/panel/heads/{headId}.png` route before falling back to dynamic or base-player art.
Art also carries `cardName`, a short card label derived from footballer surname or common-name alias
(including Neymar without Jr); the existing full `name` remains available to other consumers.
`GET /assets` includes the bundled `10_pro_player.png` and `18_champions_gold.png` frames. Their private
asset routes fall back to embedded PNGs if the downloaded asset directory lacks those files.
The page shows the pending changes, with a pending count on every page, and the history.

## Modes and rewards

Mode rewards use one shape (migration 126, `todo.md` section 2): `{ coins, fcPoints, xp, lines[] }`. A line is
`{ packId | cardResourceId, count, untradeable, source? }`: exactly one of a pack (or pick) or a player card's native
resource ID, a count from 1 to `maxLineCount` (100), and the line's own untradeable choice, which is what the club
receives (rules/tradeability.json does not change it). `source` names a grant source; null is the mode's own.

`GET /content/modes/champions` returns the published FUT Champions settings and reward ranks:
`{ area, settingsFile, seasonsFile, settings, maxWins, leaderboardSize, leaderboardMinimum, leaderboardMaximum, clientWinTiers, clientTopBands, maxTierLevel, maxLineCount, ranks[] }`.
`leaderboardSize` is `seasons.weekendLeague.topX` (6–200). The Settings → Top players button stages this rule.
Publishing resizes the six position slots atomically: Rank 1 stays separate and the remaining positions are divided across
five slots, preserving tier IDs and rewards. Position slots cannot be added, removed, converted to wins or manually resized;
their rewards remain editable. Reverting the Top rule resizes the same six slots back.

`GET /content/modes/leaderboards` returns `{ area, modes[], clientManifestPath, clientManifest }`. Each mode includes
`mode`, `size`, `minimum`, `maximum` and its rule `key`. Champions accepts 6–200 and Squad Battles 1–200, both defaulting
to 25. Squad Battles uses `squad_battles.competition.boardSize`; changing it preserves its reward tiers. The public client
manifest at `/client/ui/leaderboards.json` reflects only published values. See
[the shared launcher handoff](leaderboard-client-launcher.md) for client caption updates; publication alone cannot change loaded game assets.
`maxWins` is `seasons.json` `weekendLeague.maxMatches` (edited as the rule in `seasonsFile`); the win ranks must cover 0 to it,
so a change to both publishes together. `settings` holds
`minMatchesToRank`, `minWinsForTop` (the wins a leaderboard band place needs; fewer take their wins rank), `minWinsForTopFromEvent` (the first Weekend League it applies to) and `fcPointsCap`. These settings stay rules in `settingsFile` (`champions_rewards`) and are
edited through the `rules` area. Each rank has `tierLevel` (its stable ID, which settled results name),
`clientTier` (FIFA's tier number, best first: win ranks 1-12 by wins from Elite 1, Top 200 bands 1-6 by position),
`fifaName` (what FIFA shows: "Elite 1" … "Bronze 3", or "Band 2"), `name` (the panel's own label), either
`wins` or `positions` as `[low, high]`, and `reward`. Pending edits are read from the changeset.

The `champions_rank` area uses `{ "tierLevel": 16 }` (1-999) as a key; its value is the whole rank without the key:
`{ name, wins | positions, reward }`. Update replaces the rank, insert adds a win tierLevel and hide removes a win rank;
settled results keep the reward they stored. Staging checks the rank alone: a 1-64 character name, one valid
range (wins from 0, positions from 1, low not above high), a valid reward with no XP, no card lines and no
`source` (Champions packs are granted from `weekend_league`). Publishing checks that each pack exists (packs staged
in the same changeset apply first) and loads the whole table as start-up does: the win ranks must cover 0 to
`maxWins` exactly once, at most `clientWinTiers` of them, and at most `clientTopBands` (6) Top 200 bands running on from
position 1. The page edits existing ranks only; it does not add ranks.
A failure is `422` and nothing is published. Rules edits to `champions_rewards` `ranks` or `eventOverrides`
(moved and removed) are refused with `409`.

`GET /content/modes/rivals` returns the published Division Rivals settings and rewards: `{ area, promotionArea,
settingsFile, settings, maxOptions, maxLineCount, divisions[], promotion[] }`. `settings` holds `win`, `draw`, `loss`,
`perGoal`, `goalCap`, `matchCap`, `weekendLeagueCounts`, `weekendLeagueMatch`, `rankShares` (six percentages, Rank I
first, adding up to 100; staged as one list) and `poolBelowClubs`, all rules of `rivals_rewards` (`weeklyScore.*` for the
score values). Each division has `division` (1-10) and `ranks[]` of `{ rank (1-6), name ("I"…"VI"), row }`, where `row`
is the `rivals_rank` value; `promotion[]` holds `{ division, reward }`.

The `rivals_rank` area uses `{ "division": 1-10, "rank": 1-6 }` as a key; its value is `{ "options": [reward, …] }` with
1 to `maxOptions` (4) rewards in the order FIFA shows them. With several options the first is granted from
`rivals_option_a`, the second `_b` and later ones `_c`; a single one from `rivals_fixed`; a line may name its own `source`.
The `rivals_promotion` area uses `{ "division": 1-10 }` with `{ "reward": … }`. Both are update-only (every rank and
division always has rewards); rewards pay no XP and no player cards yet; a missing pack is refused at publish. Rules edits
to `rivals_rewards` `divisions` or `promotion` are refused with `409`.

`GET /content/modes/squad-battles` returns `{ area, settingsFile, boardFile, settings, boardSize, boardMinimum,
boardMaximum, maxLineCount, tiers[] }`. `settings` holds `mode` (percentile or points), `minPointsToQualify`,
`rankedEnabled` and `rankedMinParticipants` (rules of `sqbt_rewards`); `boardSize` is the Top players number
(`squad_battles` `competition.boardSize`, 6-200). Each tier has `tierId` (stable; settled results name it), `name`
("Rank 2-5" for a Top slot, FIFA's "Elite 1" for a points tier) and the `squad_battles_tier` value.

The `squad_battles_tier` area uses `{ "tierId": "elite1" }` as a key; its value is the whole tier
`{ kind, rankStart, rankEnd, tierLevel, percentStart, percentEnd, minPoints, reward }`. Update only. There are six Top slots
(kind `ranked`) whose places follow the Top players number (Rank 1, then the rest split over five slots) and cannot be
edited, and twelve points tiers (tierLevel 1-12, fixed) whose percent band and minimum points can. Rewards pay coins and packs
only (no FC Points, XP, player cards or line source). Publishing a new Top players number moves the six slots and keeps their
rewards; Rules edits to `sqbt_rewards` `tiers` or `leaderboardBands` are refused with `409`.

A pack named by a reward line cannot be deleted: the pack catalogue lists it in `usedBy` (for example
`Champions: Rank 1`, `Rivals: Division 3 Rank II`, `Rivals promotion: Division 1`), the delete check reports it as
`referenced`, and the database refuses the delete.

## State and access

Role access, blocks, maintenance, cap, player sessions and panel edits live in PostgreSQL
(migration 063). Club, access and settings edits apply immediately; content edits
require publication. A block revokes codes and FUT sessions
and closes that player's Blaze connection. The panel cannot rename a club derived from
the verified account name or manage address-based lists.
Migration 103 enables maintenance and replaces the initial play/bypass role lists
with Founder (`1531102023108395169`), Co-Founder (`1546887158424670238`) and Developer
(`1546861326037549056`). Boosters and Whitelisters are named picker choices without
default access. Either Founder ID is displayed under Founders. Role checks use
any matching allowed role, so Developer + Whitelister is still admitted; account
blocks override every role. The migration preserves guild ID, message, cap and
account flags, runs once on fresh install/upgrade, and leaves later operator edits
intact. The saved guild ID and server Discord bot must be configured for live role
verification. A missing persistent access-settings row refuses loading instead of
falling back to open access.
Backups are requested from the separate backup service, which writes verified dumps. A confirmed
`POST /maintenance/blaze-access` also takes `newMatches` (boolean; migration 108), and `GET /maintenance` reports it
under `blazeAccess`. `false` pauses every new match at once and persists across restarts. Online searches (Rivals,
Champions, online Draft and friendlies) still get FIFA's usual StartMatchmaking reply but are neither queued nor paired,
so players keep searching (no new Blaze bytes). Squad Battles and single-player Draft CreateMatch are refused with the
existing match-refusal shape (400, code 471); how FIFA displays that refusal is not yet observed. Offline friendlies have
no known marker and are not paused. Matches already being played finish and settle normally.

`POST /maintenance/restart {"service":"server","confirm":true}` returns 202 and queues a fixed server-only request
when the VPS restart worker's heartbeat is fresh. `GET /maintenance` reports `restart.available` and `restart.pending`.
The worker runs `docker compose restart server` on the host after the response has finished; it disconnects players.
Without the worker, restart returns 409 and remains unavailable in the page.

The React page serves `frame-ancestors 'none'` and `X-Frame-Options: DENY`.
The panel hub's real framing path still needs validation. Historical tunnel,
Discord and VPS results do not establish current iframe or native-game acceptance; see
[integration status](aurora-integration-status.md).

## Promos, buckets, events and Icon rotations

Stage definitions through the existing changeset route with a stable `{ "code": "…" }` key:

- `area: "promo"`: `{ "name", "odds_category", "draw_weight", "status": "active" | "inactive", "buckets": [{ "code", "name", "draw_weight", "enabled": true, "cards": [nativeResourceId, …] }] }`. Bucket `enabled` defaults to true when omitted. Empty card lists are allowed for buckets awaiting membership.
- `area: "promo_event"`: `{ "name", "enabled", "starts_at", "ends_at", "promos": [code, …] }`.
- `area: "icon_rotation"`: `{ "name", "enabled", "starts_at", "ends_at", "promo": "mid_icons" }`.

Codes are lowercase identifiers and remain unchanged when names change. Weights are positive relative weights,
with at most six decimal places. Cards draw within a bucket with weight `1 / draw_reduction`; unmarked cards use 1.
The optional promo field `card_reductions` maps native resource ID strings to reduction factors, for example
`{ "225100": 5 }`. Factors are decimal numbers from 1 to 999999 with at most six decimal places, and keys must
name members of this promo. Omission preserves existing marks; an explicit map replaces all marks, so `{}` clears
them. Reads return the map with factors greater than 1. Marks follow cards when moved between buckets within
the same promo, and do not affect other promo memberships or versions. Gold Common disallows reductions greater
than 1. Eligible card weights are renormalised after pack filters and duplicate exclusion; a factor of 5 means
one fifth the chance of an unmarked eligible card in the same bucket, not one fifth its previous absolute chance.
A card may have several promo memberships
but only one bucket per promo. Edit the complete promo definition to move or remove members atomically; removed
buckets are disabled. Hide makes a promo inactive or a timed definition disabled. It never deletes a player or an
owned card. Standard promos cannot be hidden or change category, and Icon bucket edits retain the tier's curated
membership. Enabled events and pack sources cannot refer to hidden promos. New cards, promos, events and packs
can be staged in that order and published together.

`GET /promos/<code>` includes enabled buckets and disabled buckets that still contain cards;
empty disabled buckets are omitted. Editors must retain those disabled memberships when
adding a bucket. Empty buckets are ignored by card draws.

`GET /cards?promo=<code>&group=buckets` returns `buckets` sections, each with `code`, `name`,
`weight`, `enabled`, unfiltered `count`, filtered `total`, `offset`, `limit` and `cards`.
Search, identity filters and sorting apply before each section is paged. The shared `limit`
applies per bucket; `bucketPage.<code>=2` advances only that bucket. The top-level `cards`
array is empty in this mode. Without grouping the existing flat response is retained.
Unknown promos return 404; grouping without a promo returns 400. The collection view
includes disabled memberships rather than claiming every displayed card is packable.
The panel shows each bucket's relative weight plus `100 * weight / sum(weights)`
over enabled populated buckets, using unfiltered membership counts. Empty and disabled
buckets show 0%. These percentages are conditional on selecting this promo and precede
pack-specific weight overrides and eligibility restrictions.

Dates require explicit UTC offsets; windows include their start and exclude their end. Ordinary events may overlap.
They cannot contain standard promos, Icons, Champions reds or Developer Cards. Enabled Icon rotations cannot overlap;
the selected tier replaces Base Icons within its window, with Base returning outside all rotation windows. Use adjacent
windows for successive Mid → Prime → Moments periods.

The rating shortcut accepts `{ "pending": false, "definition": { "ranges": [{ "code", "name", "draw_weight", "min_rating", "max_rating" }] } }`.
It partitions the selected promo's existing cards into explicit lists, omits empty ranges and returns `unassigned` IDs and up to 12 `samples` cards from the same preview snapshot.
It does not stage changes. Ranges cannot overlap. Subsequent rating edits do not automatically move cards.

`POST /packs/odds` accepts `{ "packId": 400, "samples": 500, "seed": "review", "at": "2026-10-01T12:00:00Z", "pending": true }`.
Samples are bounded to 1–1000. It uses the actual player-pack/pick draw and returns whole-pack occurrence estimates
for rare/special 82+, 84+, 88+ and each supported category, with hit counts and Wilson 95% intervals. Outcomes overlap.
Pick metrics concern offered choices and bonus players; the player's eventual selection is not simulated. Non-player
generators return 422. A zero observed count is not proof of zero odds, particularly for the 0.05% Icon slot.

`POST /promos/distribution` accepts `{ "category": "totw", "filters": { "min_rating": 82, "promo_codes": ["totw_1"] }, "pending": true, "at": "…", "limit": 100 }`.
It returns exact conditional probabilities after selecting the category, adding overlapping promo routes and filtering
before normalization. This is before previous slots remove a footballer's other versions; it is not a whole-pack rate.
It returns up to 1000 cards and the omitted probability mass. Both preview APIs, and availability previews with
`pending=1`, validate the entire changeset in a rolled-back transaction and use its immutable snapshot. They create
no club items and never publish.

`GET /content/status` returns `{ revision, loadedDatabaseRevision, externalChanges, tables: [{ table, revision, changedAt }], reloadRequired, message }`.
Poll it to show an outside-change banner. Committed SQL edits and another process's publishes are detected; own
publishes and rolled-back edits are not. Reload is explicit and validated. The React panel polls this endpoint
every 10 seconds and shows a shared warning with table details and a confirmed reload. Pending edits remain
staged; publish and reload refresh cached panel data. The React promo page browses collections
and bucket sections. Each bucket has an Edit button for its name, relative weight,
enabled state and membership. New and existing buckets can be filled once from a
rating range or by selecting individual cards already in the promo. Renaming keeps
the bucket's stable code. Deselected cards require another destination bucket;
card moves require confirmation and preserve the promo's full membership. Editors
merge earlier pending promo edits and refuse stale previews. Changes take effect
only after validation and publication. Promo creation and adding cards from outside
the promo remain deferred.

### My Stadium pack contents

Pack create/edit accepts `vanity_pack` (migration 123; the store's My Stadium Pack is 83). An empty object switches back to
the ordinary player/pick generator. Otherwise it is `{ "slots": [ { "subtypes": [500], "pools": [ { "weight": 70,
"min": 6800000, "max": 6809999 }, { "weight": 30, "max": 6799999 } ] } ] }`: 1 to 12 slots, each one item from its
catalogued `subtypes` (FIFA's card subtypes, `rules/club.json` `vanity.catalogue`), optionally split into 1 to 10 weighted
`pools` of asset id ranges (`weight` above 0, `min`/`max` optional). Every slot and pool must hold an item a store pack may
give (not in `vanity.rewardOnly`), and `size` must equal the slot count (`400` otherwise, at staging or validation). Opening
never gives an item the club owns or one drawn earlier in the pack, and skips a slot with nothing left. Test contents and
Simulate draw for a club that owns none of the items; each item is `{ itemType: "vanity", name, subtype, assetId }`.

### Mixed pack contents

Pack create/edit accepts `mixed_contents` as an object. An empty object switches
back to the ordinary player/pick generator. Otherwise it has this shape:

```json
{
  "tier": "gold",
  "players": { "min": 3, "max": 6 },
  "consumables": { "min": 5, "max": 9 },
  "stadium": { "min": 0, "max": 3 },
  "staff": { "min": 0, "max": 2 }
}
```

`size` is the exact total and `rare_slots` counts the pack's rare players, so the
players maximum must be at least `rare_slots`. Every other item is drawn from the
non-player drop table (rules `pack_odds.nonPlayerDrops`): a pool by its weight, then an
item, rare items at `rareWeight` of a common one's chance; its rarity never takes a rare
slot. Ranges must admit a total of `size`; counts must be integers and
bounded by the pack size. Player/pick, fixed-kind and replacement-slot generators
cannot be combined with mixed contents. Player draws retain promo sources and
bucket weights. Staff currently means managers. Stadium items include kits,
badges, balls and stadium cards. Non-player copies may repeat within a pack.

Catalogue entries include nullable `unavailableReason`. Disabled definitions can
be edited while awaiting item metadata. Preview refuses an unfillable definition;
validation/publication refuses a lineup enabling one, rolling back the entire
publish. A successful preview is a backend draw, not native rendering evidence.

Migration 095 defines packs 70–78 from the agreed nine-pack table, initially
disabled. Old store definitions become non-sale reward-kind rows so already-owned
packs still open; existing competition reward rows keep their configuration. (Migration 119 later makes every reward-kind
row an ordinary pack outside the lineup.)

Migration 096 removes consumable slots from the six bronze/silver packs, adding
their min/max counts to the player range (maximum capped at pack size). Total
size, rare count and other ranges are unchanged. Gold consumables remain supported.
It enables packs 70–78 as the requested store lineup.
Permanent player/manager contract consumption is removed; native card responses
show `cards.permanentContracts`. Loans still lose one match when used, expire at
zero, and cannot be played, listed or submitted to SBCs after expiry.

Migration 097 removes retired testing packs from the active catalogue. Only
genuine rewards and the nine new store packs remain as pack definitions (the
creation template and client display labels are also retained). An already-owned
testing pack/pick may resolve through `owned_legacy_pack_definitions`; that table
is compatibility data, not an editable or purchasable catalogue. Genuine reward
player packs use bucket selection and preserve their filters and guarantees.

The panel catalogue lists every pack, including the former reward packs and picks, with the same staged edit and
preview endpoints. The catalogue revision covers every pack row, so concurrent edits participate in the same
review/conflict checks.

### Club reset

`PATCH /players/{personaId}` accepts `{ "op": "resetClub", "confirm": true, "confirmPersonaId": <personaId> }`.
The confirmation must name the exact target persona. Missing or mismatched confirmation returns 400;
an online player, matchmaking activity, active club lease, concurrent open or reset returns 409.
`editOps.ops` advertises `resetClub` and the Overview tab offers the confirmation button.

The reset immediately clears that club's items, packs, coins, FIFA Points, squads, objectives,
competition progress and settled reward claims, onboarding/client data, and pack purchase counts.
It withdraws market listings and bids through the existing market transaction, refunding other bidders
when needed. The club row and player identity/access remain, as do operator/session audit history
and market history. All clearing and the reset audit entry commit together; failure rolls them back.
This is a full club reset, unlike a mode reset that preserves settled reward claims.

The empty club is marked for native creation, including in local profiles. Maintenance leaves it empty;
account info reports no club and `userMassInfo` returns 465 until FIFA completes the country/CreateUser
flow. Existing idle contexts receive the rotated account values before new leases are admitted.
The response is refreshed player detail. Browser and native FIFA acceptance must be recorded separately
from the isolated API/onboarding tests.

### Season Progress XP grants

`PATCH /players/{personaId}` accepts `{ "op": "grantSeasonXp", "amount": 1500 }`.
The amount must be a whole number from 1 to 99,999,999. It adds to that club's
Season Progress XP immediately and records the grant in the player edit history.
The returned player detail includes `club.seasonXp`; `editOps.ops` advertises
`grantSeasonXp`. Existing objective counters and reward claims are preserved.
This grants XP only: the player still claims unlocked level rewards in FIFA.

### Consumables, club customisation and managers

The Cards page has four tabs: Players (the card catalogue), Consumables, Club customisation and Managers
(`#/cards/consumables`, `#/cards/customisation`, `#/cards/managers`). The last three are plain tables with no
card art, and nothing on them is edited or created; a row opens Grant to a club.

`GET /cards/items?tab=consumables|customisation|managers` answers `{ tab, types[], items[], total }`. Any other
tab is a 400. `types[]` holds `value`, `label` and `count` for the type filter. Each item has `key`, `type`,
`typeName`, `name`, `rating`, `rareflag`, `resourceId`, `team`, `league`, `nation`, `forcedUntradeable` and
`countMax`, plus `nameKey` (a localisation key) for a My Stadium item. Rows come from the non-player drop table,
so the panel offers exactly what packs build:

- Consumables: chemistry styles (not Basic or GK Basic), position changes and manager league cards.
- Club customisation: kits, badges, balls and stadium cards, plus every My Stadium item except the starter items.
- Managers: the managers with a league.

The item key is `<pool>:<resourceId>` (`chemistry_style`, `position_modifier`, `manager_league`, `kit`, `badge`,
`ball`, `stadium`, `manager`) or `vanity:<subtype>:<asset>` for a My Stadium item.

`PATCH /players/{personaId}` accepts `{ "op": "grantItem", "item": "<key>", "count": 1, "untradeable": false }`;
`editOps.ops` advertises `grantItem`. Consumables take a count from 1 to 24. They land in New items as
separate items, as packs give them, and the 100-item New items limit applies. A manager, club customisation
item or My Stadium item takes a count of 1 only. It is refused with 409 when the club already has it: the same
manager or customisation item in any pile, one of the club's default items, or a starter or granted My Stadium
item. A club that FIFA did not create is given the best-rated manager when it is first opened, so that manager
may already be held. My Stadium
items are club unlocks (`club_vanity_items`), so they are always untradeable and `untradeable` is ignored.
Grants carry the `operator` reward source. An unknown key, extra field or out-of-range count is a 400 and
changes nothing.

### Club inventory kinds and removing player cards

`GET /players/{personaId}/inventory` rows also carry:
- `typeName`: what a non-player item is ("Chemistry style", "Kit", "My Stadium: Tifo").
- `equipped`: the item fills one of the club's slots.
- `listed`: it is on the transfer market.
- `removable`: the item is a player card that is not on the market.

Non-player rows are named from the drop table. The club and New items piles also list the club's granted My
Stadium items (kept, or still waiting), as untradeable `clubItem` rows that cannot be removed; the inventory
counts include them. The club page splits each pile into the Cards page's tabs: Players, Consumables, Club
customisation and Managers.

`PATCH /players/{personaId}` accepts `{ "op": "removeItems", "items": [<native item id>, ...] }` with 1 to 100 ids;
`editOps.ops` advertises `removeItems`. It deletes those player cards from the club, New items or transfer pile
and pays nothing. A card in a squad leaves its squads first, as a quick sell does. Consumables, managers and club
customisation are never removed, so a kit or badge slot is never left empty. Any of them, a card listed on the
market or an unknown id refuses the whole edit with 409, and nothing changes.

### Tradeability of panel grants

`PATCH /players/{personaId}` accepts optional `untradeable: boolean` for
`grantCard`, `grantItem` and `grantPacks`. `grantCard` gives one copy: a `count` other than 1 is a 400, and the
card list's `grant.countMax` is 1. Explicit true/false selects the grant's restriction;
omitting it uses the existing `tradeability` operator-source rules. Each gifted
pack persists its choice in `unopened_packs.untradeable`; opening applies that
restriction to its contents. Existing intrinsic restrictions (such as loans and
player-pick rules) still apply. Card grants carry the `operator` reward source.

Developer/Pro cards are always untradeable, irrespective of the requested flag.
Card list rows report `forcedUntradeable` for the locked grant control. The item
factory and persistence mapper enforce the restriction; migration 102 also
marks existing owned Developer Cards untradeable without changing ownership or
stats. All card and pack gift forms expose an Untradeable checkbox; unchecked
requests a tradeable grant, and Developer Cards show a checked, disabled control.

## SBCs

SBC repeatability is Once (`repeatable:false`), Unlimited (`repeatable:true`) or Daily
(`repeatable:true, repeatableDaily:true`). Daily SBCs reset at 19:00 Europe/London (GMT/BST).
Each challenge pays once per content day; all challenges must finish in the same day for
that day's group reward. Daily claims (migration 170) are separate from lifetime completion
counts, preserving settled rewards and objective progress. Claims, consumption and rewards
share the locked club transaction; concurrent submissions cannot pay twice.

Numeric squad and player-count requirements accept `min`, `max` or `exact`; a supplied
minimum/maximum range enforces both bounds. Zero is supported. The generic `LEAGUE_PLAYERS`,
`NATION_PLAYERS`, `CLUB_PLAYERS` and `QUALITY_PLAYERS` rules take `attributes.leagueId`,
`nationId`, `clubId` or `quality` respectively, plus their count/comparison. `selectionName`
is descriptive text. The panel uses the native catalogue from `GET cards/filters`, and
publication validates the selected IDs. The older named league rules remain compatible.
Requirement definitions expose `selector` in `GET sbc`. The game receives eligibility
key, selected value, player count and comparison; the server independently enforces them.

The SBC page has two parts, like Packs / Picks: the catalogue of every set, and the live list of the sets the game shows.

The catalogue is the `sbc_set` content area (migration 136, table `sbc_sets`). A change is keyed `{ "setKey": "UPG_BRONZE" }`
and its value is `{ "definition": {…} }`: the set as `sbc.json` writes it (`name`, `description`, `tabId`, `repeatable`,
`badge`, `challenges` with requirement `rules` and reward `entries`, set `rewards`). For two or more challenges, the group
reward must contain at least one valid entry; preview, staging and publication enforce this. The panel shows its group
reward editor and summary only for these sets. Single-challenge sets do not require a group reward. A row replaces the bundled set
with that key or adds a new set. `hide` removes a set added in the panel (refused while it is on the live list or any club has
a completion or saved squad in it) or reverts a bundled set's edit; a bundled set cannot be removed. Bundled sets keep their
client set id; added sets get `900000 + (1000 + ordinal) * 100`. A challenge's id is its set id plus its `number`, which the
server writes on publish (a challenge keeps its number by key, a new one gets the next number no challenge or saved squad of
the set has used), so reordering or removing challenges moves no saved squad. `badge` names an image in the SBC art folder
(served at `/panel/sbc-badges/{name}.png`). A PACK reward names its pack by `attributes.packId` (an existing pack); the old
pack names and `rules/sbc.json` `rewardPacks`/`legacyRewardPacks`/`defaultRewardPack` were converted to ids at start-up
(`SbcRewardPackConversion`, 2026-10-04) and removed. Challenge ids must be unique across the catalogue; publication refuses a
duplicate between live sets. The pack catalogue lists SBC rewards under a pack's `usedBy`.

The live list is the `sbc_live` content area (migration 142, table `sbc_live`), which replaced `rules/sbc.json` `hiddenSets`
and the per-set hide. One change keyed `{ "scope": "live" }` holds the whole list as `{ "listing": [{ "setKey", "enabled" }] }`.
The game shows a set only while it is listed and switched on; a set that is not listed keeps its definition and clubs keep
their progress. The list applies after catalogue edits, so a set added in the same publish can be listed. `GET sbc` returns
each set's `listed` and `enabled`, and the published list as `live`.

SBCs → Settings edits `rules/sbc.json` as ordinary `rules` changes of file `sbc` (one value per change, published under
Changes); the Rules page no longer lists that file. `GET sbc` adds `settingsFile`,
`settings` (`formation`, `repeatableRewardsUntradeable`, `playerPickRewardName`, `ignoredRequirements`, `text`, `images`,
`squadSize`, `brickItemIdBase`), `requirementRules` (every type with its fixed client check: `eligibilityKey`,
`eligibilityValue`, `scope`, `playerCount`, `qualities`; and its editable `label` and `exact.label`) and `formations`.
Staging refuses an empty label or game text, an unknown type in `ignoredRequirements` and a negative `images.cacheSeconds`;
publishing refuses a default `formation` missing from `chemistry.json` and an `images.fallbackTabId` that is not an SBC tab.
"Restore shipped settings" is `POST content/rules/import {"file": "sbc"}`.

The set editor stages the whole set. A new set's key is made from its name and challenge ids are assigned by the editor
(`<KEY>_<n>`, never reused); the server refuses an update that gives a numbered challenge a different id, since clubs'
completions are stored by it. A challenge may name a `formation` (from `chemistry.json`) and `bricks`: `freeIndices`
(the slots the player fills, 1 to squad size - 1 of them), `players` (slot to a card's resource id) and `dummies` (slot to
`{league, nation}`, a placeholder the server fills with such a card); locked slots are the others, and the PLAYERS rule's
`max` must equal the open slots. Rewards are `PACK` (`packId`, `count` 1-10), `COINS`/`XP` (`value`), `PLAYER` (a card's
`resourceId`: with no `name` it pays exactly that card; authored entries with a name are matched by name, rating and
position) and `MANAGER_LEAGUE` (`league` with a manager league card). A repeatable set may carry `rewardsTradeable`. `GET sbc`
also marks each pack `pick` (a player pick pack) and `fixed` (a fixed pick), and returns `formations` as `[{id, slots}]` (slot position ids, slot 0 the goalkeeper), `positions`, `squadSize` and
`managerLeagues`. `POST sbc/preview {setKey, definition}` answers `{problem, description, challenges: [{description,
players}]}`: why staging would be refused (or null) and the text FIFA would show, from the server's own code (`SbcText`).

## Content schedule

`GET /content/schedule` returns `now`, versioned `campaigns`, versioned `drops` with
change items and inherited/overridden ends, and existing native windows in
`legacy`. All dates are ISO timestamps with a UTC offset. The panel presents
`Europe/London` and defaults new drops to 19:00.

| Route | Purpose |
| --- | --- |
| `POST /content/campaigns`, `PUT /content/campaigns/{id}` | Save name and optional default `endsAt`; updates require `version` |
| `POST /content/drops`, `PUT /content/drops/{id}` | Save optional `campaignId` (null for standalone), name, `startsAt`, optional `endsAt`, `inheritEnd`; updates require `version` |
| `POST /content/drops/{id}/validate` | Validate the draft against the queued timeline using `version` |
| `POST /content/drops/{id}/schedule` | Explicitly queue validated content using `version` |
| `POST /content/drops/{id}/publish` | Publish now using `version` |
| `POST /content/drops/{id}/cancel`, `POST /content/drops/{id}/end` | Cancel an unpublished drop or end a live one using `version` |
| `PUT /content/drops/{id}/items/{changeId}/end` | Set `endsAt` (null means persistent), or `inheritEnd:true` to restore the default; requires `version` |
| `GET /content/schedule/preview?at=` | Project queued content at a timestamp without changing live state |

Content edits accept `dropId` on changeset, promo and pack catalogue/lineup
requests, including pack tests and simulation. Catalogue GETs return the
baseline projected through earlier queued drops; the selected changeset supplies
its own pending edits. Pack revisions remain live concurrency tokens. Without
`dropId`, the existing immediate review workflow applies. Saving to a queued
drop returns it and affected dependent drops to draft. Finalized drop edits are
rejected. Destructive pack deletion and operations/gameplay rules cannot be
scheduled. Revert can stage eligible content into a selected draft.

`content_window` changes use operation `update`, key `{kind,target}` (`store`,
`token`, `sbc`, `objective`) and after `{enabled}`. They control one reusable
item's availability. Enabling one requires `dropId` with a start and end date;
item end overrides must follow the drop start. Future availability windows
cannot be manually published early. Direct lineup/live-list edits that activate
new content return 422 and instruct the operator to choose release dates.
Existing live metadata edits, ordering and removal remain supported.
Required built-in packs cannot receive expiry windows.
Native promo/Icon windows and custom seasons retain their definitions when
ended; disabled seasons have `enabled:false`, preserving reward history.

Promo groups use the existing `promo` content area. `GET /cards?resourceIds=`
accepts up to 200 comma-separated native resource IDs for resolving members
without depending on picker pagination.

### Campaign date windows

The panel uses date-only start/end fields for campaigns and their drops, fixing
both times at 19:00 London time. It reuses the Events conversion helper to send
UTC `startsAt`/`endsAt` timestamps, including BST/GMT changes. The server uses the
existing offset-qualified timestamp contract and UTC comparisons.

Campaign writes send `name`, `startsAt`, `endsAt` and update `version`. Drops send
`campaignId`, `name`, `startsAt`, `endsAt`, `inheritEnd:false` and update `version`.
Each drop has explicit dates contained within the campaign window. Campaign
edits cannot exclude existing drops. Schedule validates automatically. New dated
campaign drops publish through their schedule; normal editor saves apply live.

Migration 168 preserves older campaigns with `startsAt:null` until an operator
chooses their date window. Older timestamp payloads remain compatible with those
records. The new panel requires campaign dates before adding drops.

### Immediate editor saves and standalone rotations

Normal panel definition saves send `apply=1` on `PUT content/changeset/changes`
and pack catalogue/lineup mutations. With no positive `dropId`, this validates
and publishes one isolated change atomically, swaps the live snapshot after
commit, and records published History. Other open changes and campaign drafts
are preserved. The response retains the published changeset for editor baselines
and reports `pending:0`; pack responses say `applies:"live"`. A rejected save
changes neither catalogue rows nor the live snapshot. Calls without `apply=1`
retain the legacy staged API for recovery and older clients.

Explicit `dropId` writes remain draft availability assignments. The panel no
longer offers a global content destination: campaign promo drops assign existing
promos/packs/SBCs/objectives, while their definitions save independently.
Dated campaigns reject TOTW and Icon timing assignments. TOTW is identified by
native `totw_N`/`silver_totw` codes or the catalogue `totw` odds category.

Schedule has Campaigns & drops and Rotations sections. TOTW and Icons reuse the
existing timed definition APIs and immediate save path outside campaigns.
The existing Standard pool stays always available; no fabricated date window is
stored for it. Schedule legacy promo entries include `rotationType:"totw"` or
`"promo"` for calendar filtering. Old `#/events` bookmarks redirect to
`#/schedule?section=rotations`; the Events navigation item and links are removed.

`GET promo-events?standalone=1` excludes events owned by drops, using recorded
changeset ownership, so campaign availability does not appear again as a legacy
standalone rotation.

`DELETE /panel/api/content/campaigns/{id}` requires `{version}`. It returns
`{deleted:id}` for a non-live campaign, 409 for a stale version, an active campaign
window or any live child drop, and 404 for a missing campaign. Unpublished child
drops are removed and their changesets discarded atomically. Completed releases
retain publication/expiry history with no campaign association. Definitions and
player state are unchanged. No campaign cancellation endpoint is provided.
