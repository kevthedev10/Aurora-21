# Aurora 21 client mods — launcher developer handoff

This replaces the earlier leaderboard-only handoff. Extract the ZIP into a
writable folder. Start with the included preparation command; the compiled tool
and its dependencies are included, so building the tool is optional.

The 2026-10-07 refresh includes reviewed compiler warning fixes and their source
manifest/generator. `-ValidateOnly` runs four checks: ZIP dependency,
localisation, currency artwork and FMT warning corrections (`--self-test-fmt`).
The ready mod and all build evidence retain their originally verified bytes;
this refresh does not recompile the game mod or establish new in-game acceptance.

## What the client mod contains

- Fixed Top 25 captions for FUT Champions and Squad Battles.
- Aurora Tokens currency text across all 21 supported languages: 43 currency
  edits plus the two leaderboard edits, and a `TOKENS` store caption addition.
- The approved Aurora Tokens graphics: 12 texture replacements in 11 native
  assets, including header, store icons, pack preview, purchase controls, Draft,
  live message, points products and tutorial.

Profile revision:
`9d33649141c8188a1e6f1012fe354f4c9bd0805932a2983efa6c9fd4a8bb4e24`.

The fixed profile is bundled; do not fetch leaderboard sizes from the server.
Changing server leaderboard rules does not change these fixed client captions.
The existing filename and switch still say "leaderboard" but cover the combined
mod. Balances, prices, pack rules and currency machine identifiers are unchanged.

## One preparation command

Prerequisite: Windows x64 with the .NET 10 Desktop Runtime. The tool targets
`net10.0-windows` and requires both Microsoft.NETCore.App and
Microsoft.WindowsDesktop.App 10.0. Extract all files and keep FIFA closed during
preparation. No service, authentication helper or game is started by this command.

From the extracted folder, first check the package without touching game data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Prepare-ClientMods.ps1 -ValidateOnly
```

Then prepare using the selected game executable (substitute its actual path):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Prepare-ClientMods.ps1 -GamePath 'C:\Games\FIFA 21\FIFA21.exe' -ResultPath '.\launcher-result.json'
```

The first build compiles locally and copies roughly 40 GB of game data. Provide
enough free disk space and display preparation progress in the launcher. Later
runs reuse the verified revision for that game installation. The default cache is
`%LOCALAPPDATA%\Aurora21\UltimateTeam\client-mods\leaderboards\<revision>`.
Pass `-UserConfigRoot` explicitly when your launcher already has a settings root,
and preserve the initiating user's path across any elevation.

The process exits nonzero on failure. Read the result JSON only after successful
exit: it contains `revision`, `modDataPath`, `gameArguments`,
`verifiedLanguages: 21`, and `verifiedArtworkAssets: 11`. Standard output is
progress text, not a JSON API. Use a fresh result path for each attempt to avoid
reading an older successful result after a failure.

## Wire it into Play

1. Ship the extracted package under a launcher-owned writable tools directory.
2. Before Play, run the preparation command using the selected `FIFA21.exe`,
   initiating user's config root and a fresh result file. Wait for successful
   exit and read that file. Preparation validates the original EXE/DLL hashes;
   unsupported game builds fail closed.
3. Launch the selected original `FIFA21.exe` with the result's `gameArguments`:
   `-dataPath "<modDataPath>"`. Keep your existing authentication, trust patch,
   hosted login and launch flow intact. If your launcher calls the existing
   UltimateTeam worker, pass `-ClientModDataPath '<modDataPath>'` instead; the
   worker carries the argument through direct and EA App launch paths.
4. Save the active revision only after success. Preparation stages a new profile
   then renames it into place after compiled-asset verification. Failed builds
   leave previous profiles intact. Decide explicitly how your UI handles a
   preparation failure; never mark a failed revision installed.
5. When composing other client mods, compile them together using your reviewed
   mod manager. This helper compiles this combined UI mod only; pointing at its
   ModData alone does not preserve separately compiled mods automatically.

Do not modify FIFA21.exe or CardsDLL_Win64_retail.dll. Do not ship another user's
40 GB ModData or reuse their `compiled-profile.json` as an installation marker.
The included evidence is a record of this checkout's verified build, not a cache
marker to copy into a player's installation.

## Included files

- `ready-mod/aurora21-leaderboards-<revision>.fbmod`: the already generated
  combined package, useful for an existing FMT integration. It is not ModData;
  import and compile it with the reviewed FIFA21 plugin before launching.
- `ultimateteam/client-mod-tools/`: packaged compiler, FIFA21 SDK/plugin and
  native dependencies. Generated caches, logs and other game helpers are omitted.
- `ultimateteam/Client-LeaderboardMods.ps1` and bundled JSON profile: existing
  preparation/cache helper, called by the wrapper.
- `ultimateteam/assets/currency/`: reviewed DDS payloads and their inventory.
  The prebuilt compiler also embeds these payloads.
- `tools/fifa21-leaderboard-ui/`, `Build-LeaderboardClientTool.ps1` and artwork
  preparation sources: optional rebuild route. See their READMEs. Rebuilding
  requires the separate reviewed FrostbiteModdingTool checkout at commit
  `67b35957725fc961e4262afb8be08e9cd1df239e`; its source is not included.
- `reference/Launch-UltimateTeam.ps1`: caller reference only; this is not a
  standalone worker installation. Do not overwrite an external launcher's whole
  worker with this file.
- `evidence/`: native asset validation reports and visual previews.
- `checksums.json`: SHA-256 of every payload file, checked by Verify-Package.ps1.

The packaged ZIP dependency is ProDotNetZip 1.20.0; retain the build override and
ZIP self-test when rebuilding. Do not restore the older DotNetZip.dll.

## Custom portraits: server assets

`server-assets/custom-playerheads/` contains all six current per-card DDS/PNG
pairs, including the final Dost portrait shifted 26 pixels left from the first
repair. Copy this folder into the server runtime's `custom-playerheads` folder
(normally `ultimateteam/runtime-source/ultimateteam21/custom-playerheads` in this
checkout) and deploy the updated server image. Do not add these portraits to the
launcher FBMOD: FIFA requests them from `/fut/playerheads/g4/single/p<id>.dds`.
Restart FIFA after deployment because the client caches player heads. The server
log should show `Serving custom player head resource=<id> file=p<id>.dds`.

The PNGs are exact decodes of the DDS files for the panel. Only Dost changed in
the portrait work; the other five are included so the developer has the complete
folder. His latest position still needs an in-game review after deployment.

## Verification and acceptance

Observed in this checkout: the combined native package compiled successfully;
all 21 language databases and 11 artwork assets matched the generated replacements
byte for byte. The user approved the artwork previews. Earlier Champions Top 25
was confirmed in game. Dost's first larger framing was seen in game; subsequent
left shifts were requested by the user and verified as DDS/PNG assets.

Before release, verify your own launcher on a clean cache and a reused cache;
check both leaderboard tabs, header/store/pack/Draft graphics, Tokens text and
an additional language in FIFA. Confirm the original game hashes remain unchanged.
The external launcher integration and final artwork/Dost position have not been
accepted in game by these offline checks.
