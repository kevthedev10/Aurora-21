# FIFA Points → Aurora Tokens investigation

Investigated and implemented 2026-10-04. The original investigation was read-only;
the user then authorised generation, installation and normal launcher enablement.
No game was launched during implementation.

## Finding

Observed: FIFA Points labels and purchase/Draft messages are present in FIFA 21's
`data/loc/<language>.db` assets. The current rules control balances, rewards,
purchase availability and prices, but contain no global currency display-name
setting. A localisation mod is the supported candidate for the in-game rename.
Which keys each live screen actually uses remains unconfirmed in this investigation.

Observed server contracts:

- [Credits template](../ultimateteam/server/src/Aurora21.UltimateTeam.Core/Resources/Templates/club/credits.json)
  sends currency identifiers `coins` and `points` plus balances.
- [Store service](../ultimateteam/server/src/Aurora21.UltimateTeam.Core/Services/Store/StoreService.cs)
  uses points for purchase eligibility and deducts the stored balance.
- [Draft entry template](../ultimateteam/server/src/Aurora21.UltimateTeam.Core/Resources/Templates/store/draft-entry-tile.json)
  supplies prices with the same currency identifiers.
- [POW catalogue](../ultimateteam/server/src/Aurora21.UltimateTeam.Core/Resources/Templates/bootstrap/pow-catalogs.json)
  also has `name: "FIFAPoints"`. There is no evidence that changing that value
  would rename the global header or the native purchase messages.
- Operator-panel labels are React text, for example
  [completion rewards](../ultimateteam/panel-web/src/pages/modes/CompletionReward.tsx).
  Those can be renamed separately without changing the game localisation.

## Native evidence

Reused the original extracts from
`outputs/verification/champions-top25-20261003/localisation-all/tables/`.
English source database SHA-256:
`39d30555c612f70855566130bed58457e8d6aedc6223abc9bbb551e679ace8fe`.
The existing native English database matches the export's recorded input hash.
The original investigation reused the earlier build evidence. During the
authorised implementation, the generator freshly verified the installed game
executable and CardsDLL against the manifest, and the extracted English source
database again matched the hash above.

The English tables contain **131 rows** whose source text mentions FIFA Points,
including the escaped line-break spelling. They span labels, tutorials,
accessibility, purchase limits, trial/transfer notices and mobile/older store
descriptions; presence does not establish that a row is active in FIFA 21's PC UI.
Those 131 IDs, plus the abbreviated `STORE_PC_PURCHASE_CONFIRMATION` entry, are
present in all **21** extracted language databases.

Representative exact IDs and English values (screen roles inferred from IDs):

| ID | Extracted text |
| --- | --- |
| `FUT_GH_FIFAPOINTS_0` | `FIFA\nPoints` |
| `FUT_FIFA_POINTS` | `FIFA POINTS` |
| `FUT_FIFA_PTS` | `FIFA POINTS` |
| `FUT_FUTMoney` | `FIFA Points` |
| `MarketplaceCurrency` | `FIFA Points` |
| `FUT_STORE_PURCHASE_POPUP_POINT_COST` | `%1s (FIFA POINTS)` |
| `FUT_DRAFT_POINTS_COST` | `%1d FIFA Points` |

Full multilingual inventory and source hashes are saved locally in
`outputs/verification/aurora-tokens-20261004/localisation-inventory.json`.

## Implementation approach

Implemented: the server embeds `Resources/client/aurora-tokens.json` and includes
its 43 exact per-language text edits in the existing leaderboard manifest before
calculating the revision. IDs were selected from the native inventory for header,
currency labels, store/pack/Draft messages, tutorials, accessibility and purchase
limits. Historical points-product descriptions, trial/transfer notices, the legacy
Microsoft Points key and a tutorial whose translations omit the currency name
are excluded. Surrounding translated sentences remain intact.

The generator now composes those edits and both leaderboard captions into each
localisation database. It requires the expected source text, preserves formatting
placeholders and line breaks, then compares every table field after saving. The
normal `RUN-FIFA21.cmd` → GUI Play → launch worker path enables this combined mod
through the existing `-UpdateLeaderboardClientMods` option. The full supported
builder refreshed the tool, launcher, server and helpers successfully.

Observed native generation: all 21 installed language databases passed round-trip
verification with exactly **45** changed source-text fields each (43 currency
edits and two Top 25 captions), with all other rows/fields preserved. The
generator freshly checks installed executable and CardsDLL hashes against the
manifest, instead of relying only on the earlier extract.

Observed compiled verification: all 21 databases extracted from compiled ModData
matched the generated replacements byte for byte. Both installed game profile
hashes remained unchanged. The launcher helper atomically installed and then
successfully reused the new per-user cache profile, revision
`ebccf2927766aa4976a8d76fc34031243bb61a5721e5273aed890169b0cf9efc`.
The previous profile was preserved. Both leaderboard sizes in this prepared
profile are 25; normal Play still follows the database's published sizes.

Checks passed: six focused backend tests; tool ZIP and localisation self-tests;
client-mod cache/entrypoint tests; startup portability, process-name, settings
and repository hygiene checks; 23 auth-bootstrap self-test groups; and the rebuilt
server's isolated startup smoke test (680 loopback requests across fresh/custom
temporary profiles). The full solution suite was not rerun. Reports are local
under `outputs/verification/aurora-tokens-20261004/`.

Unconfirmed: visual fit, which keys each active screen uses, and the normal
combined-mod launch in game. Next user test: start `ultimateteam/RUN-FIFA21.cmd`,
press Play, and check the global header, Store/pack purchase confirmation, Draft
entry cost/confirmation and insufficient-points messages, plus both Top 25 tabs.
Currency icons remain unchanged; the external shared launcher is outside this
local installation.

The design steps below describe the implemented composition and remaining
in-game checks:

1. Choose explicit currency label, store, Draft and accessibility IDs from the
   inventory. Use `Aurora Tokens`, `AURORA TOKENS`, and `Aurora\nTokens` where the
   respective capitalisation/wrapping is required. Review sentence text and
   translated wording separately. Preserve placeholders, IDs, hashes and escapes.
2. Extend the existing
   [caption generator](../tools/fifa21-leaderboard-ui/CaptionMod.cs) with an explicit
   currency edit manifest. It currently accepts exactly two leaderboard keys and
   replaces only the numeral `200`, so it cannot perform this rename unchanged.
3. Compose the currency and leaderboard edits into the same language database
   replacements. Separate mods editing the same localisation asset may overwrite
   one another; combined asset validation is required.
4. Retain the generator's database round-trip checks and unrelated-field checks,
   then verify compiled ModData and test the header, pack confirmation, Draft
   entry and insufficient-points messages in game. Check label width/wrapping.

Keep the existing `points`, `fifaPoints`, database fields and spending mechanics.
The rename needs no new currency or database migration. Do not globally replace
`Points`: Champions Points, Squad Battles scores and Draft entry tokens have
different meanings. A currency icon/logo replacement requires a separate asset
investigation; the relevant texture and active UI binding are unconfirmed.
