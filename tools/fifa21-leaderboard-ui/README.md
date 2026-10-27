# FIFA 21 leaderboard and currency localisation generator

The current bundled profile also includes 12 reviewed FIFA Points texture edits
in 11 native assets, plus the Aurora Champions logo replacements. It composes
those with the localisation changes and verifies every compiled artwork asset
byte for byte. See
[the artwork inventory and preparation utility](../fifa21-currency-art/README.md).
The Champions image targets, text inventory and source artwork are documented in
[the Champions art utility](../fifa21-champions-art/README.md).

This Windows development tool reads the bundled `ultimateteam/leaderboard-client-profile.json`, verifies the installed game profile, and produces one combined `.fbmod` containing leaderboard captions, Aurora Tokens text across 21 languages and the bundled currency artwork. It checks round-tripped database fields and native artwork before exporting. The package filename and launcher switch remain unchanged. The launcher does not fetch a leaderboard manifest from the server.

`textEdits` supplies exact source and replacement text per language for 43 currency IDs, covering the global header, store labels, pack confirmations, Draft costs/messages, accessibility and purchase-limit text. It preserves translated surrounding sentences, capitalisation, placeholders and line breaks. Historical points-product descriptions, trial/transfer notices and the legacy Microsoft Points key are outside this rename. The header uses `Aurora\nTokens` where the original wraps. The bundled `artwork` edits also replace the reviewed FIFA Points graphics with the maintainer's supplied artwork.

`textAdditions` supplies the missing `FUT_STORE_CAT_KITS_NAME` = `TOKENS` caption
for the Tokens Store placement. The generator checks its hash against the native
Store category hash convention, rejects existing keys/hash collisions, and
preserves every original field on roundtrip. It repartitions sorted rows across
the two existing tables to respect their 65,535-record limit. This addition and
the 45 existing text edits passed roundtrip and compiled-asset checks in all 21
languages on 2026-10-04; the Tokens Store's native rendering still needs testing.

The generator was verified with Top 25 for both modes: all 21 language databases contained exactly two requested field changes and no other row or field changes. It anchors FMT's relative SDK lookup to its installation directory and resolves caller paths first. Both-mode compiled ModData has passed byte-for-byte verification of all 21 localisation databases. The integrated local launch and Squad Battles caption still need an in-game user test.

The combined Aurora Tokens/Top 25 build was subsequently verified on 2026-10-04:
all 21 databases had exactly 45 requested source-text changes and no other field
changes, and all compiled databases matched the generated replacements byte for
byte. The verified profile is installed in the local launcher cache. Its visual
fit and normal in-game launch still require the user's test.

Dependencies come from the separately reviewed Frostbite Modding Tool checkout (revision `67b35957725fc961e4262afb8be08e9cd1df239e`), including its built FIFA21 plugin. They are not vendored into this repository. Build with the Windows .NET SDK:

```powershell
powershell.exe -NoProfile -File ultimateteam/Build-LeaderboardClientTool.ps1 -FmtRoot 'C:\Tools\FrostbiteModdingTool'
```

The executable is written into that checkout's `FrostbiteModdingUI/bin/Debug/net7.0-windows` directory, beside its plugin and SDK. Its three arguments are the game directory, bundled profile JSON and output directory:

```powershell
& 'C:\Tools\FrostbiteModdingTool\FrostbiteModdingUI\bin\Debug\net7.0-windows\Aurora21-LeaderboardUiTool.exe' 'C:\Games\FIFA 21' 'C:\Aurora21\leaderboard-client-profile.json' 'C:\Aurora21\GeneratedCaptions'
```

Add `--compile` as the fourth argument to build isolated ModData and verify all compiled localisation and artwork replacements. FIFA must be closed. Compilation copies about 40 GB of game data and writes `compiled-profile.json` only after verification succeeds. External shared-launcher downloading remains separate work. Run against original game data, not an already edited localisation profile. It refuses mismatched game hashes, unexpected language sets or keys, and unrelated database changes.

See [the launcher handoff](../../ultimateteam/leaderboard-client-launcher.md) for the backend contract, native evidence and remaining integration steps.

The local builder invokes `ultimateteam/Build-LeaderboardClientTool.ps1` to package the compiler and dependencies into the ignored `ultimateteam/client-mod-tools` directory. Set `AURORA21_FMT_ROOT` to the reviewed FMT checkout when it is elsewhere. Distributable local builds need this generated package beside the launcher; third-party build outputs are not committed.

Normal **Play** through `ultimateteam/RUN-FIFA21.cmd` enables the combined mod automatically. Its revision includes the fixed Top 25 captions and all currency edits, so a leaderboard-only profile cannot be reused for this manifest. Compilation finishes and verifies the replacement assets before the per-user profile is installed. No currency identifiers, balances, prices or database migrations change.

Packaging also runs `--self-test-localisation`, which rejects unexpected source text, changed placeholders/line breaks and unrelated replacements. Native database and compiled asset verification remain separate from this synthetic check; visual fit and active screen usage require an in-game test.

The builder imports this repository's `ZipDependency.targets` into the FMT build
graph and replaces DotNetZip 1.15.0 with ProDotNetZip 1.20.0 before NuGet restore.
The reviewed FMT revision and its source files stay unchanged. ProDotNetZip keeps
the `Ionic.Zip` API and includes the fix for CVE-2024-48510. The package excludes
the old `DotNetZip.dll` and removes it from an existing generated installation.
The source digest includes the override and self-test, so `-IfNeeded` rebuilds an
older package. Direct compiler builds refuse the vulnerable dependency; when
using `dotnet build` manually, pass both `-p:FmtRoot=<checkout>` and
`-p:CustomAfterMicrosoftCommonTargets=<absolute path to ZipDependency.targets>`.

Every packaging build runs `Aurora21-LeaderboardUiTool.exe --self-test-zip` against
synthetic temporary files: it checks the loaded patched assembly, cache round
trip, ZIP interoperability and rejection of absolute archive path traversal.
It does not read game data or start FIFA. This check does not establish in-game
caption or compiled ModData compatibility; those remain separate native checks.

The build also applies reviewed FMT warning fixes through generated source
overlays. [The guarded manifest](FmtWarningFixes.json) and
[overlay generator](Prepare-FmtWarningFixes.ps1) check the pinned source before producing the
replacement compilation inputs; the dependency checkout remains unchanged.
An unexpected source version fails the build instead of applying a loose text
replacement. The overlays correct 64-bit varint shifting and EBX property lookup
across all reflected classes, preserve existing checksum results and zlib stream
layout, and make platform, member hiding, handle lifetime and exception handling
explicit. These are compilation inputs for this tool's dependency graph, not an
upstream FMT update or a global warning suppression.

`Aurora21-LeaderboardUiTool.exe --self-test-fmt` checks the loaded dependency
assemblies using synthetic inputs: values above 32 bits and signed extremes,
legacy checksums across odd lengths and block boundaries, a property in a later
EBX class, and native zlib stream size and field offsets. It does not index or
compile game assets, call native zlib, start FIFA, or establish native mod
compatibility. Run the supported builder before invoking this check so it tests
the generated overlays rather than stale dependency DLLs.

The bundled profile is a reviewed snapshot of the server client resources (`leaderboard-ui.json`, `aurora-tokens.json`, and `tokens-store.json`), with both `captions[].top` values set to 25 and the local `artwork` inventory composed into its revision. Keep its game hashes and localisation edits intact. When updating it, recompute `revision` as SHA-256 of the UTF-8 .NET `JsonObject.ToJsonString()` output with `revision` removed, matching the compiler validation. Ship the profile beside `Client-LeaderboardMods.ps1`. Server APIs and panel controls remain available independently.
