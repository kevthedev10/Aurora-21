# Fixed Top 25 client captions — shared launcher handoff

As of 2026-10-05, the local mod uses the bundled [leaderboard-client-profile.json](leaderboard-client-profile.json). Both captions are fixed at Top 25, and the launcher makes no leaderboard API request. The profile also includes the current Aurora Tokens text edits and Tokens Store caption. Server APIs and panel size controls remain independent: changing a server size does not update the mod caption.

## Implemented server and panel contract

Both modes default to Top 25. Champions is editable under **Modes → FUT Champions → Settings → Top players**. Stage the number, then validate and publish under Changes. Champions accepts integers from 6 to 200. Its six leaderboard reward slots cannot be deleted, added or manually resized through the content API.

Publishing a Champions size changes the six position ranges in the same database transaction. Rank 1 stays separate; the remaining positions are divided across five slots. For slot `i = 0..4`, the range is `2 + floor((N-1)*i/5)` through `1 + floor((N-1)*(i+1)/5)`. Top 25 gives **1, 2–5, 6–10, 11–15, 16–20, 21–25**; Top 6 gives six single-position slots. Stable tier IDs, reward records and settled rewards are preserved. An older custom catalogue containing fewer than six position slots refuses resizing with 422 instead of discarding its custom rewards.

Squad Battles has backend support only. Its size accepts integers from 1 to 200 and does not resize Squad Battles reward tiers. Migration 129 changes an existing default 200 to 25 while preserving other configured sizes. No Squad Battles panel page is added.

Read the published sizes and edit keys from:

```http
GET /panel/api/content/modes/leaderboards
```

The reply includes `modes[]` with `mode`, `size`, `minimum`, `maximum`, and `key`, plus `clientManifestPath` and `clientManifest`. Stage a Squad Battles change through the existing content changeset route:

```http
PUT /panel/api/content/changeset/changes
Content-Type: application/json

{"area":"rules","key":{"file":"squad_battles","path":["competition","boardSize"]},"operation":"update","after":{"value":25}}
```

Champions uses `file: "seasons"`, `path: ["weekendLeague", "topX"]`. Validate and publish through `POST /panel/api/content/changeset/validate` and `POST /panel/api/content/changeset/publish`, with an empty JSON body. A staged value does not change the public client manifest until publication.

## Optional server manifest (not used by the fixed mod)

```http
GET /client/ui/leaderboards.json
```

This read-only endpoint is served by the existing client API when that listener is enabled. It needs no player identity. GET and HEAD return `Cache-Control: no-store` and an ETag containing the manifest revision. A local operator can also export the identical `clientManifest` from the private panel response above.

The manifest contains:

- `schema: 1`, `game: "FIFA21"`, `requiresGameRestart: true`;
- `profile.gameExecutableSha256` and `profile.cardsLibrarySha256`, identifying the game build investigated here;
- the 21 installed localisation language IDs;
- `captions[]`: the mode, exact `stringId`, `sourceNumber: "200"`, and published `top`;
- `textEdits[]`: 43 Aurora Tokens currency IDs with exact `source` and `target` values under `languages.<language>`; these are combined with the leaderboard edits in each replacement database;
- `revision`: SHA-256 of the compact System.Text.Json serialization of the manifest before adding `revision`.

The exact localisation keys are `FUT_Champions_TopX_upper` for Champions and `FUT_SQUADBATTLE_TOP100_TAB` for Squad Battles. Despite its name, the latter contains a translated Top 200 caption in this installed build. Replace only the numeral in the matching localisation rows, preserving translated wording. Do not replace every occurrence of 200 or change the game executable.

## Required changes in the external shared launcher

The shared updater source is absent from this checkout. The following integration remains to be implemented there:

1. Ship the bundled fixed Top 25 profile with the client pack and compare its revision with the installed mod revision. No leaderboard API request is needed.
2. Obtain a caption package built for that revision and game profile, or generate the asset replacements locally from the installed game. The bundled profile supplies settings, not compiled game assets; package delivery or local generation is still needed.
3. Verify the profile hashes, package integrity, revision and both requested sizes. Keep the installed profile usable when generation or download fails, and report that its captions are stale.
4. With FIFA closed, build/install into a launcher-owned ModData directory. Validate all replacement language databases and the compiled assets before switching the active directory atomically. Preserve other supported mods when composing assets.
5. Save the active revision only after installation succeeds. Require a game restart after a caption change. A server rule publication changes server responses immediately; it cannot change assets already loaded by a running game.
6. Pass the active directory through the normal worker using `-ClientModDataPath`. Preserve the existing authentication, trust-anchor installation, selected game path, initiating user config root and log path across elevation.

The worker already accepts this optional parameter:

```powershell
& ./ultimateteam/Launch-UltimateTeam.ps1 -ClientModDataPath 'C:\Aurora21\ClientMods\ModData'
```

It validates `Data/layout.toc` and `Patch/layout.toc` and carries the path into direct or EA App launch arguments. Calling the worker without `-ClientModDataPath` or `-UpdateLeaderboardClientMods` uses original game data; normal local GUI Play enables the combined mod automatically. The external shared launcher must supply the parameter after completing its update.

The working local prototype used the FIFA21 plugin from [Frostbite Modding Tool](https://github.com/na632/FrostbiteModdingTool), revision `67b35957725fc961e4262afb8be08e9cd1df239e`. Its compiled ModData directory included roughly 40 GB of local game data. That is evidence of a working local asset path, not a finished incremental download format. Do not distribute that entire directory as a launcher update; design and verify incremental delivery or local compilation in the shared launcher.

## Evidence and remaining work

Observed: the user confirmed the Champions English Top 25 leaderboard tab in game. The original prototype's replacement databases were verified across all 21 languages and extracted from compiled ModData byte for byte.

Backend tests cover publication, validation rollback, six protected slots, preserved rewards, history revert, live response cache refresh, independent Squad Battles sizing and public manifest GET/HEAD. Panel tests cover the minimum, staging, locked ranges and range preview.

The reusable generator under `tools/fifa21-leaderboard-ui` has now generated a both-Top-25 `.fbmod` from a manifest fixture. Round-trip comparisons verified exactly the two requested fields in each of the 21 languages (42 caption changes), with every other row and field preserved. See [its build and invocation instructions](../tools/fifa21-leaderboard-ui/README.md). It reads original game assets and writes to a separate output directory, preserving the working client mod. This new both-mode export has passed compiled-ModData verification across all 21 languages; the integrated local launch and Squad Battles caption still need an in-game user test; automatic shared-launcher downloading and installation remain unimplemented.

Earlier leaderboard verification passed: 10 backend tests, 11 panel tests and the production panel build. That build initially refused to replace a running server. The supported release builder subsequently completed on 2026-10-04 for the combined leaderboard/Aurora Tokens update. Start through `RUN-FIFA21.cmd` to test the current build.

After the server is rebuilt and restarted, test the Champions panel with Top 25 and Top 6, validate/publish, and confirm six reward slots with unchanged rewards. Then implement the external launcher integration above and test both in-game tabs in English and another language before distributing it.

## Local launcher integration

Normal GUI launch now passes `-UpdateLeaderboardClientMods`. The worker reads the fixed Top 25 manifest bundled beside its scripts, prepares and verifies a per-user profile through `Client-LeaderboardMods.ps1`, and supplies `-ClientModDataPath`. Services-only starts skip mods; explicit diagnostic mod paths remain supported. The builder packages the reviewed compiler via `Build-LeaderboardClientTool.ps1`. Stop cancels only the owned compiler child before its launch worker, without closing FIFA. The verified both-Top-25 profile is prepared locally; run `RUN-FIFA21.cmd` for the in-game test. External shared-launcher integration remains separate.

As of 2026-10-04, this same path also composes the Aurora Tokens rename into the localisation mod. The revision includes currency text, invalidating the earlier leaderboard-only cache even when both sizes remain 25. The generator checks exact native source strings and preserves placeholders, line breaks and every unrelated database field. Currency machine identifiers (`points`, `fifaPoints`) and game spending behaviour remain unchanged. See [currency evidence and testing](../docs/aurora-tokens-localisation-investigation.md).
