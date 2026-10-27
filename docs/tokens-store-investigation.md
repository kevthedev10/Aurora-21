# Tokens Store: reuse the native Kits tab

Investigation: 2026-10-04. The requested section contains packs purchased with
Aurora Tokens. The maintainer approved replacing Kits. This document records
static evidence and the implementation below. The mod is compiled and cached;
Tokens Store behaviour has not been tested in game yet.

## Build and evidence

The inspected installed FIFA 21 files have these SHA-256 hashes:

| File | SHA-256 |
| --- | --- |
| FIFA21.exe | `44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494` |
| CardsDLL_Win64_retail.dll | `37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49` |

Read-only FMT extraction reused the existing FIFA21 asset index and extracted
26 Store assets. It checked both hashes before extraction. The local evidence
directory is `outputs/verification/tokens-store-20261004/`: `extraction.log`,
`native/input-assets.json` (individual asset hashes), extracted navigation/UI
assets, StoreFront string inventory, and bounded CardsDLL disassemblies.
FMT reference revision: `67b35957725fc961e4262afb8be08e9cd1df239e`.

No debugger connection was available. Findings below come from the extracted
assets and static disassembly, not a running-client observation. RVAs refer to
the hash above; disassembly uses image base `0x180000000`.

## Observed

- CardsDLL's TabDP construction at RVA `0x7e3b0` explicitly builds My Packs,
  Bronze, Silver, Gold, Special, Kits and Points entries. Kits passes the name
  key `FUT_STORE_CAT_KITS_NAME` at RVA `0x7ea9e`.
- Native category availability (`0x50450`) and panel lookup (`0x50630`) map
  enum 6 to the string `kits`, using the same generic group lookup (`0x4f2a0`)
  as Bronze, Silver, Gold and Special. The panel lookup reads the matching
  group's ID. The Store status provider publishes it as `KITS_CATEGORY_ID`
  (`0x7db90` through `0x7ddd8`). **Enum 6 is not evidence that the JSON wire
  category ID must be 6.**
- `data/ui/nav/fut/store/futStoreFlow.nav` loads the common
  `external/ion_fut/screens/cardShop/StoreFront` screen. The extracted
  StoreFront compiled assets contain the shared `FUT_PACKLIST_DP`,
  `ACTION_GET_PACKLIST`, category selection and ordinary pack purchase paths.
  No separate Kits screen appears in this navigation file.
- CardsDLL's Store XLIFF parser (`0x374990`) recognises `FUT_STORE_CAT_`
  translation units and splits the group/property from the key. Its text
  update path (`0x374470`, `0x374110`) handles `NAME` and `DESC`. Group
  identifiers are held at offset `+0x70` (accessor `0x49ba0`); category
  localisation updates the separate display name at `+0x98` or description
  at `+0xc0`. A server category name therefore need not replace the identifier
  used to find `kits`.
- The tab provider separately supplies a literal localisation key. The static
  trace above does **not** prove that category XLIFF updates the tab caption.
  `FUT_STORE_CAT_KITS_NAME` was not found in the extracted English
  LanguageStrings tables inspected during the currency mod work. The existing
  exact-source replacement mod cannot simply replace a missing row.
- Current C# `Services/Store/StoreService.cs` sends pack tiles through
  `PurchaseGroupsAsync` and `StorePackTile`. The embedded
  `Resources/Templates/store/pack-tile.json` supplies `categoryList` and
  `displayGroup`. The group comes from `store_group`, validated against
  `PackRules.StoreGroups`; unsupported names fall back to the dominant tier.
  Current groups are Bronze, Silver, Gold and Special, so configuring `kits`
  alone will currently fall back.
- Token payments already use the native `points` currency. Packs support
  `allowed_currencies: ["points"]`; the club's `fifaPointsPurchases` rule
  controls whether that currency is offered. `PackDescriptionsXliff` currently
  exports pack names/descriptions, without category translation units.
- The React catalogue/editor currently couples `store_group` to its “Pack art”
  control. StoreArtId also consults that setting. A new section needs its
  placement separated from artwork so Tokens packs keep suitable pack art.

## Inferred implementation path

Reusing Kits is substantially more contained than adding another native tab:
the client already has its visibility check, panel lookup and Store entry.
The common StoreFront assets and generic group lookup make ordinary pack tiles
in that slot plausible. Their presence is not proof that the client will render
and purchase such a tile successfully.

1. Add a Tokens placement choice to the pack editor, keeping artwork independent.
   Internally use the client identifier `kits`; display “Tokens” to operators.
   Update the persisted catalogue/rule path for existing databases as well as
   fresh seeds; changing seed JSON alone will not update active rules.
2. Publish ordinary pack tiles under that group and allow only `points` for
   packs assigned to Tokens. Enforce this in purchase validation too, so a
   forged coin purchase cannot bypass the section's currency policy.
3. Export `FUT_STORE_CAT_KITS_NAME`/`DESC` in Store localisation. Verify the
   group title and tab caption separately. If the tab still requests a missing
   bundled string, extend the existing combined client mod with a verified
   localisation-row addition or a Store UI asset edit. Do not patch the
   installed executable/DLL persistently.
4. Verify the actual wire group ingestion before selecting a category ID;
   preserve existing category IDs and do not substitute the native enum value
   without parser evidence.

## Remaining native gates

A controlled in-game test must establish that a Tokens pack makes the Kits slot
appear, that the tab and title say Tokens, that a normal pack tile and its art
render, and that only Aurora Tokens can purchase it. Check successful payment
and pack opening, insufficient balance, and attempted coin payment, with the
other Store categories and My Packs still behaving normally.

This investigation did not launch/stop FIFA, restart the server, modify the
real club database, or change the active Top25/Aurora Tokens mod. No runtime
tests were needed for this documentation-only result.

## Initial implementation (2026-10-04; panel workflow superseded below)

- Migration 147 adds nullable `store_section` to both pack definition tables,
  with `standard` and `tokens` as its accepted values. It changes no pack
  placements or club state. Null retains existing behaviour.
- The pack editor adds Standard/Tokens placement independently of the existing
  pack art setting (`store_group`). Tokens disables the coin price input and
  requires a positive Aurora Tokens price. Catalogue and Store listing show
  the token price and placement; staged edits retain the artwork.
- Tokens tiles use the native group `kits`, only list the `points` currency,
  and retain their normal art/type/content. The shared purchase validator
  applies that same currency policy: coin requests are refused, insufficient
  tokens create no items or payment, and disabling token purchases hides the
  pack. Owned reward packs still open through My Packs without payment.
- `Templates/store/tokens-section.json` holds the group, title, description and
  category ID. ID **5 is a new server-assigned category ID**, distinct from the
  existing 1–4; it is not claimed as a recovered native enum or historical Kits
  wire ID. Acceptance of this placement remains an in-game gate.
- Store XLIFF exports the Kits group name/description. The combined client UI
  manifest also adds `FUT_STORE_CAT_KITS_NAME` = `TOKENS` to the bundled
  localisation databases, so the tab's literal lookup has a matching entry.
  The generator validates the language hash algorithm against the original
  Store category rows, checks collisions, and preserves unrelated legacy
  hashes. It redistributes sorted rows across the two existing tables because
  the first is already at the 65,535-record limit.
- Roundtrip verification preserved every original row/field except the 45
  existing requested source-text edits, and verified exactly one new caption
  per language. Compiled replacements matched all 21 generated databases byte
  for byte. Original FIFA executable/CardsDLL hashes were checked again.
- Verified profile revision:
  `cdd8160a77c3930754d646c3f06a35f6f4a3106d30a89c6aa1dedb2f6f09814a`.
  It is cached under the initiating user's `client-mods/leaderboards` directory
  and was successfully reused by the normal launcher helper. Older profiles
  remain intact. The normal `RUN-FIFA21.cmd` / Play path selects it through the
  current server manifest; no extra launcher switch is needed.

Validation evidence is under the same local evidence directory:
`panel-tests.log` (31 passed), `runtime-verification.log` (panel production build,
supported launcher/server build, isolated startup and launcher checks),
`build-tokens.log` (native roundtrip/compile), `caption-validation.json`,
`compiled-profile.json`, `install-profile.log`, and `final-backend-tests.log`.
Final backend checks passed **13 tests**, including successful and interrupted
upgrades from migration 140 to 147. The isolated startup smoke test passed
**699 loopback requests** across fresh, custom and restarted test profiles.
The full solution suite was not run; focused backend and supported upgrade
checks use throwaway databases. No real club database or live server was
restarted for these checks.

## Independent lineups (2026-10-04)

The maintainer requested separate coin and token lineups, with the same pack allowed in both. Migration 148 moves existing
listed token prices into independent `token_store_listings` offers. The pack editor no longer changes price, store
placement or sale availability. Each lineup sets its own positive price, On switch and purchase tradeability. Regular
listings advertise/accept only coins; Tokens listings advertise/accept only points (Aurora Tokens). Store Lineup defaults
to Tradeable; Token Store Lineup defaults to Untradeable. Both offer only those two choices, with a muted “(Default)”
beside the appropriate choice. Migration 149 resolves previously inherited tradeability before the global Store rule
is hidden from Rules, preserving existing listings. New purchases use the listing's choice and its lineup's default.

Regular listings keep their current native IDs and use the existing coin-price backing column. Token listings reserve
IDs above all definitions, legacy IDs and existing token offers. They copy contents/art from the original definition;
purchases charge the token offer but generate/record the original pack, preserving specialist generators and player-pick
references. Purchase limits and community stock remain properties of the definition and count both stores together.
Removed token offers remain unlisted ID reservations; readding keeps the same ID and cannot collide with new packs.
Tokens prices from the earlier exclusive-placement workflow migrate without adding those packs to the coin lineup.
No additional native mod rebuild is needed: the Kits/Tokens caption and existing combined mod manifest are unchanged.

Observed validation for the independent-lineup implementation is in `outputs/verification/token-lineups-20261004/`:

- `panel-tests.log`: 32 React pack tests passed; `panel-build.log`: TypeScript and production build passed.
- `backend-tests.log`: 29 focused backend tests passed; one outdated assertion expected zero-price staging to succeed.
  After correcting that expectation, `final-regressions.log` passed all seven final checks, including that test and
  automatic artwork. Together these runs verify 31 distinct store/panel/upgrade tests. Earlier panel regressions were
  updated to set price and availability through lineups, preserving their content, rollback and ownership assertions.
- Upgrade tests preserve the migration-140 club snapshot and existing listed token prices through normal and interrupted
  upgrades. Purchase tests cover coin/token separation, insufficient tokens, owned reward copies, tradeability and shared
  per-club/community counters. Token-only offers also obey the contents-availability publication gate.
- `runtime-verified.log`: the final supported launcher/server build and isolated fresh/custom/restarted profiles passed
  672 loopback requests. `hygiene.log`: 356 checks passed. These are fixture/startup results, not native FIFA acceptance.

The defaults follow-up is verified in `outputs/verification/store-defaults-20261004/`: 43 React tests and the production
panel build passed; 12 focused backend checks and the corrected inherited-choice upgrade fixture passed. Repository
hygiene passed all 356 checks. The freshly compiled Debug server passed the isolated startup smoke test with 610
loopback requests (`runtime-debug.log`). The supported packaging build refused to overwrite the running launcher; no running
process was stopped. The installed server still needs a supported rebuild after the launcher is closed.

The full solution suite was not run. No live server/game or real club database was restarted/reset for this work.

Next user test: start through `ultimateteam/RUN-FIFA21.cmd`; open **Packs / Picks → Token Store Lineup**, add a pack,
set its Aurora Tokens price and tradeability, then stage and publish under Changes. The same pack may remain in
**Store Lineup** at a different coin price and tradeability. Reopen the native Store: check both listings, their prices,
artwork, token purchase and remaining balance. Native acceptance of the reserved offer IDs and Kits placement is still
unconfirmed; fixture tests and a successful build do not prove that in-game result.
