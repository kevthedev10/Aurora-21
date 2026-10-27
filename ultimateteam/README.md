# Aurora 21 runtime

The supported runtime is the C# server under [server/](server/README.md), with the
Windows launcher and native auth helper. The React panel is served at `/panel/`.
See the [repository README](../README.md) for panel development and testing,
[CONTRIBUTION.MD](../CONTRIBUTION.MD) for verification requirements, and
[deployment](deploy/README.md) for hosted operation.

## Local launch

From the repository root in Windows PowerShell:

```powershell
.\ultimateteam\RUN-FIFA21.cmd
```

The entrypoint invokes [Build-FIFA21-Launcher.ps1](Build-FIFA21-Launcher.ps1) with
`-IfNeeded`, then opens the launcher. Choose your installed `FIFA21.exe` in
Settings. Do not rename the game or use old `FIFA21-*` helper shortcuts: FIFA
Config checks process prefixes, so helpers are named `Aurora21-*`.

The launcher starts the server and redirector, prepares routing and the
build-specific in-memory trust/auth patches, and launches the game according to
its saved settings. The helper supports build `21.0.4750997.0` with matching
executable and CardsDLL hashes; unsupported files fail before patching.

### Launch modes

- `lsxMode: "auto"` checks the owner of loopback port 3216.
- `ea-app` leaves EA App's LSX listener alone. With `launchViaEAApp: true`, FIFA
  starts through its registered `origin2` handler.
- For an intentional direct local launch, set `lsxMode: "replacement"` and
  `launchViaEAApp: false`. The local service must own port 3216; close EA App/Origin
  first. The two modes cannot own that port together.

Local launches temporarily replace the four FIFA redirector hosts mappings with
loopback, saving the previous mappings in `local-routing.json` under the user
config folder. **Stop** and failed-launch cleanup restore those mappings while
preserving unrelated hosts entries. Stop local testing and close FIFA before
switching to the hosted launcher: FIFA can retain resolved addresses in memory.
If another launcher changes routing during a local session, cleanup preserves
its changes and reports the retained recovery backup instead of overwriting them.

The launcher **Stop** action cancels this checkout's pending launch workers and
stops its services. It does not close FIFA. Use that action for normal cleanup;
do not stop every server or PostgreSQL process by name.

### Building and updating

Close this checkout's launcher/server before a full rebuild:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Build-FIFA21-Launcher.ps1
```

The builder produces the launcher, auth helper and self-contained Windows server
and installs pinned portable PostgreSQL binaries when missing. `-LauncherOnly`
does not rebuild the server; `-AuthBridgeOnly` rebuilds only the native helper.
Use the complete checkout and matching scripts/resources when distributing an
update. Existing settings and clubs must survive upgrades.

After frontend changes, first run `npm run build` in `panel-web/`, then rebuild
the server. `RUN-FIFA21.cmd` does not run npm. Do not build Vite and C# concurrently:
Vite replaces the generated directory C# embeds. A running server continues using
its loaded bundle/content until it is explicitly refreshed or restarted.

## Settings and logs

| Location | Ownership |
| --- | --- |
| `launcher-settings.example.json` | Shared launcher defaults |
| `runtime-source/ultimateteam21/config.example.json` | Server defaults |
| `%LOCALAPPDATA%\Aurora21\UltimateTeam` | Personal settings and launcher logs |
| `%LOCALAPPDATA%\FIFA21UltimateTeam\postgres` | Managed club database |
| `panel-assets/` | Repository card art, portraits, flags and badges |

Choose the game path per user. Empty `sourcePath` selects bundled runtime data.
`ULTIMATETEAM21_USER_CONFIG_ROOT` overrides personal settings and is preserved
through elevation. `ULTIMATETEAM_RUNTIME_ROOT` is a separate runtime/state override.
Keep personal paths, logs, credentials and mutable database files out of Git.

The initial build creates local certificates only when absent. Preserve an
existing CA; never regenerate it merely to update the code. Hosted certificates
belong to the server volume, and only exported public trust anchors reach clients.

## PostgreSQL and game content

The managed PostgreSQL 18 cluster normally listens on loopback port 5432 using
the `ultimateteam` database/user/schema. The default managed password is
`ultimateteam`; personal config can change the managed port and password.
The builder installs its checksum-verified binaries under `postgres/win-x64/`.
Use the server's `postgres install|status|start|stop` commands to manage the exact
cluster. A legacy SQLite club remains untouched and is not imported.

`ULTIMATETEAM_POSTGRES_CONNECTION` selects an external PostgreSQL server. Keep it
server-side and unset it for isolated tests. Tests create throwaway databases;
never point them at your real club. Schema migrations run at startup without
resetting club ownership, saved squads or reward history.

FUT Champions defaults to a fixed Top 25 leaderboard and six reward bands: 1,
2–5, 6–10, 11–15, 16–20 and 21–25. Each band keeps its corresponding previous
reward; players outside the bands receive their win-based rank reward. Leaderboard
reward bands still require the configured minimum matches/wins. Migration 127
upgrades the previous six default bands and their scaling settings without changing
club state or settled rewards. Migration 128 also upgrades already-populated
tables with the original four default bands, copying the third/fourth rewards
into separate records for the two added bands. Operator-defined position ranges
are preserved.
Edit reward ranks under Modes → FUT Champions. Settings → Top players accepts
6–200 and automatically resizes the six permanent leaderboard reward slots when
published, preserving their rewards. Squad Battles also defaults to Top 25;
its backend rule is `squad_battles.competition.boardSize` (1–200), ready for later
panel wiring. The Champions English leaderboard tab was confirmed as Top 25
using the client asset mod. Normal local launcher starts read the bundled `leaderboard-client-profile.json`, with both captions fixed at Top 25, generate and verify the client profile, and load it automatically. No leaderboard API call is needed for the mod. Server sizes remain editable, so changing them does not change these fixed captions. Profiles are cached under the initiating user’s settings root at `client-mods/leaderboards`. First-time compilation copies about 40 GB of game data; unchanged sizes reuse the verified profile. The builder packages the compiler from the reviewed FMT checkout (`AURORA21_FMT_ROOT` selects another location). Automatic external shared-launcher updates remain to be integrated; see
[the launcher handoff](leaderboard-client-launcher.md). All screens and the Squad
Battles caption have not yet been verified in game.
`CHAMPIONS-UI` lines in `runtime/server.log` record the running
server build hash, event IDs, returned prize-tier ranges and leaderboard entry
count after the relevant responses are written. They omit player details,
credentials, rewards and request query strings. To collect evidence, launch with
`RUN-FIFA21.cmd`, open Champions rewards and the leaderboard, then close normally.

Active game rules live in PostgreSQL `content_rules`. Bundled `rules/*.json`
seed fresh databases and provide reviewed imports from the panel. Pack rows and
promo membership also live in PostgreSQL. Changing JSON seed files alone does not
update an existing database. For direct SQL changes, explicitly reload content
through the panel or restart; requests use the published immutable snapshot.

Migration 112 removes obsolete fixed-value discard rules so startup can fill the
current rating-based definitions from `rules/cards.json`. Existing multiplier
settings and unrelated rules are preserved; old fixed-value in-form bands are
replaced with the current bundled bands. No club reset or manual JSON edit is needed.

Migration 119 ([packs and pack odds v3](../docs/pack-odds-v3.md)) makes every pack one kind: the former reward-only
packs stay out of the store lineup, rewards keep naming them by id, and owned copies still open. Every pack gets its own
complete odds table with the agreed values, and the old fallback chances and `banded_odds`/`odds_table` columns are
removed. Clubs, owned packs and reward history are untouched.

### Content areas

| Content | Source / editor |
| --- | --- |
| Store packs and player picks | Relational catalogue; Packs / Picks |
| Promo membership and weighted buckets | Relational promos; Promos |
| Ordinary promo availability | Promo events; Events |
| Standard Icon tier | Icon rotations; Events |
| Draft, market and match rules | Bundled `rules/*.json`, then PostgreSQL; Rules |
| Draft, Champs, Rivals and Squad Battles rewards | Bundled reward rules, then PostgreSQL; Rules today, dedicated Rewards section requested |
| Objectives and Season Pass (Season Progress XP/rewards) | Bundled objective rules, then PostgreSQL; Rules today, dedicated section requested |
| SBC catalogue | Bundled `sbc.json`; dedicated SBCs section and persistent catalogue editing requested |
| Card stats, traits, rarity and image selection | Card definitions; Cards |
| New-club policy | `rules/onboarding.json`; [onboarding notes](onboarding.md) |

The requested SBCs, Rewards and Objectives/Season Pass sections are still to be
built. Their scope and validation requirements are in the [panel TODO](../todo.md).

The standard pool contains bronze, silver and gold common/rare cards plus the
current Icon tier. Promo events add active promos; explicit pack sources can name
specific promos. Promo buckets hold relative weights. Within a bucket, each eligible
card has weight 1, divided by its configurable desirable-card reduction (for example 5).
Marks belong to a promo membership; Gold Common stays rating based. See the
[migration 120 balance profile](../docs/gold-and-base-icon-bucket-balance.md).
Rating ranges seed membership once; later stat edits do not
move cards between buckets. Champions red picks use their named reward pool,
not ordinary pack availability. See the [panel contract](panel-api-contract.md)
for precise source/odds behavior.

Players → Overview → **Reset club** clears that player's possessions, balances
and progress after confirmation while they are offline. Their access and audit
history remain, and FIFA must run onboarding again, including in local profiles.
See the [reset contract](panel-api-contract.md#club-reset); use a disposable club
for browser acceptance.

Permanent players/managers do not consume contracts. Loans still consume matches
and expire. Contract and fitness consumables are intentionally removed; healing,
chemistry styles and position modifiers remain supported. Developer Cards are
forced untradeable. Published squad-card edits and image changes may require
leaving and re-entering Ultimate Team; the [acceptance record](panel-web/test/game-acceptance.md)
describes what was observed.

Repeatable SBCs retain completion counts and reopen after every complete set round;
repeating a request without submitting new players grants no extra reward. Bronze,
Silver and Gold Upgrade rewards contain two standard rare players of their stated
tier, excluding Icons and other specials.

AI transfer-market filling is disabled by default (`rules/market.json` →
`aiListings.target: 0`). Migration 111 also disables the existing database rule and
removes unsold AI listings without bids. Player listings, completed sales and AI
auctions with escrowed bids remain so settlement can finish. An explicit
`ULTIMATETEAM21_MARKET_TARGET` override can enable AI filling for diagnostics.

## Hosted play

Use `serve --hosted` through the [Docker stack](deploy/README.md). It requires
verified account-service launch assertions, player-bound codes and native FUT
sessions. Identity is never inferred from an IP address or a supplied persona ID.
No VPN or per-player forwarding server is part of this path.

The shared launcher downloads a versioned client pack, obtains a fresh assertion,
requests `/client/launch`, then runs `client-pack/Start-Aurora21.ps1`. That script
passes `--hosted` to the auth helper: hosted clients do not require a local
bootstrap service on port 8090. Local launch still checks its bootstrap profile.
The [launcher contract](aurora15-launcher-handoff.md) describes the interface;
[integration status](aurora-integration-status.md) records the observed releases.

The hosted profile enforces TLS for advertised game services and keeps plaintext
listeners on loopback. `/client/*` uses public HTTPS through Caddy; `/panel/` and
`/panel/api/*` remain private on loopback port 18448 for an SSH tunnel.
A tunnel working does not establish hub iframe support: the server still sends
frame-denying headers. Real hosted FUT entry, certificate validation and
multiplayer/UDP acceptance remain subject to the recorded
[hosting gates](native-hosting-investigation.md).

The capacity target of 1,000+ concurrent players is an engineering target, not a
measured live capacity claim. Pool sizing, shutdown behavior and benchmark
commands are documented in the [server guide](server/README.md).

## Listener reference

Defaults below are for orientation; CLI/configuration and the hosted profile
choose the actual listeners.

| Port | Local purpose | Hosted exposure |
| --- | --- | --- |
| 42230 | TLS GOS redirector | TLS redirector advertising secure Blaze |
| 44321 | Blaze | Loopback plaintext only; public TLS uses 18444 |
| 8090 | FUT/Nucleus HTTP and local auth bootstrap | Loopback plaintext only; public TLS uses 18443 |
| 42232 | EASW | Public TLS uses 18445 |
| 10853 | SSF | Public TLS uses 18446 |
| 17502 | QoS TCP/UDP | QoS |
| 3216 | LSX; exactly one owner | Disabled in hosted server |
| 18447 | Launcher API when enabled | Loopback, proxied by Caddy |
| 18448 | Operator panel when enabled | Private loopback, SSH tunnel |
| 5432 | Managed PostgreSQL, configurable | Private database listener |

## Diagnostics

Use the [contribution guide](../CONTRIBUTION.MD#verification) for build/startup
checks and the [server guide](server/README.md) for focused tests and isolated
runtime verification. Windows Node is required when testing the Windows server.
Python is allowed for diagnostics and quick tests; the game runtime stays C#.
Do not launch services or reset a club just to inspect source/documentation.

For a read-only check of a running local runtime:

```powershell
node.exe .\ultimateteam\verify-auth-bootstrap.mjs
```

This checks local auth/build evidence; it does not prove playable FUT or a
multiplayer match. Keep [Hash-FIFA21-ProcessMemory.ps1](Hash-FIFA21-ProcessMemory.ps1)
and [Test-TlsProbe.ps1](Test-TlsProbe.ps1) as maintained diagnostic tools.
Prefer the panel for normal grants. Offline grant scripts remain available for
explicit maintenance; inspect their process guards and arguments before use.
