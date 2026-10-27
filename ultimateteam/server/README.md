# Aurora 21 UltimateTeam server

This directory contains the active C# UltimateTeam server, its PostgreSQL schema,
tests, and performance harness. Run the commands below from the repository root
in **Windows PowerShell** unless a command says otherwise.

## Requirements

- Windows with the .NET 10 SDK.
- A writable checkout of the complete repository.
- The portable PostgreSQL binaries under
  `ultimateteam\postgres\win-x64`. The supported build installs them when they are
  missing.
- A lawful FIFA 21 PC installation for native game testing.

Confirm the basic development environment from the repository root:

```powershell
$ErrorActionPreference = 'Stop'
dotnet --version
Test-Path .\ultimateteam\server\Aurora21.UltimateTeam.sln
Test-Path .\ultimateteam\postgres\win-x64\bin\postgres.exe
Test-Path .\ultimateteam\postgres\win-x64\bin\initdb.exe
```

If the PostgreSQL checks return `False`, run the supported build:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Build-FIFA21-Launcher.ps1
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
```

The build publishes the self-contained server as
`ultimateteam\Aurora21-UltimateTeam-Server.exe`, builds the launcher and auth bridge,
and installs the pinned PostgreSQL build. Close UltimateTeam processes before a full
build. Do not use `-LauncherOnly` to validate server changes.

## Run UltimateTeam

The supported player entrypoint is:

```powershell
.\ultimateteam\RUN-FIFA21.cmd
```

It invokes `Build-FIFA21-Launcher.ps1 -IfNeeded`, opens the launcher, and then
uses the launcher-selected FIFA 21 executable and LSX mode. Personal settings
are stored under `%LOCALAPPDATA%\Aurora21\UltimateTeam`. Mutable PostgreSQL state is
stored under `%LOCALAPPDATA%\FIFA21UltimateTeam`; neither belongs in the repository.

For command-line help after building:

```powershell
.\ultimateteam\Aurora21-UltimateTeam-Server.exe --help
```

The executable provides `serve`, `redirector`, certificate/trust commands,
offline card grants, relational database checks, club reset, and
`postgres install|status|start|stop`. `reset-club` deletes the selected club.
Relational schema updates are embedded and applied during startup. The retired
SQLite and PostgreSQL public-schema clubs are not imported.

The default managed database listens on `127.0.0.1:5432` with database, user,
and password `ultimateteam`; its schema is `ultimateteam`. Per-user config can change
the managed port and password. `ULTIMATETEAM_POSTGRES_CONNECTION` selects an external
PostgreSQL server and must not be set when running isolated tests.

## Run the full server test suite

The tests create throwaway PostgreSQL clusters under the Windows temporary
directory. They must never point at the real club database.
Each run installs the schema and catalog once into a template database, and most
tests get a copy of it (`PostgresTestCluster.CreateInstalledDatabase`); tests of the
fresh-install path itself use an empty database (`CreateDatabase`).

```powershell
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
dotnet test .\ultimateteam\server\Aurora21.UltimateTeam.sln `
  -c Release `
  --artifacts-path .\outputs\verification\server-full-<fresh-name> `
  --logger 'trx;LogFileName=server-full.trx' `
  --results-directory .\outputs\verification\server-full-results-<fresh-name> `
  --nologo
if ($LASTEXITCODE -ne 0) { throw 'Full server suite failed' }
```

Replace `<fresh-name>` with a new timestamp or label. Use a new artifacts path
for each independent run so loaded DLLs and old reports cannot contaminate the
result. A filtered test run does not prove the full suite passes.

The repository also provides the simpler offline wrapper:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Offline-UltimateTeam.ps1
if ($LASTEXITCODE -ne 0) { throw 'Offline validation failed' }
```

It checks required catalog files and runs the server test project against
temporary state without opening listeners or launching FIFA.

## Verify a published runtime

After server or startup changes, build through the supported builder and run the
isolated startup harness with a **Windows** Node executable:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Build-FIFA21-Launcher.ps1
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
$serverExe = (Resolve-Path .\ultimateteam\Aurora21-UltimateTeam-Server.exe).Path
node.exe .\ultimateteam\verify-startup-runtime.mjs --server $serverExe
if ($LASTEXITCODE -ne 0) { throw 'Isolated startup verification failed' }
```

The harness uses temporary settings, ports, and PostgreSQL state and stops its
own children. A successful isolated run does not prove native FIFA login,
onboarding, or gameplay.

For launcher, packaging, and repository changes, also run:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Test-StartupScriptPortability.ps1
powershell.exe -NoProfile -File .\ultimateteam\Test-LauncherProcessNames.ps1
powershell.exe -NoProfile -File .\ultimateteam\Test-LauncherSettings.ps1
powershell.exe -NoProfile -File .\ultimateteam\Test-RepositoryHygiene.ps1
node.exe .\ultimateteam\verify-auth-bootstrap.mjs --self-test
```

## Run performance tests

First verify the performance harness and its failure reporting:

```powershell
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
dotnet test .\ultimateteam\server\Aurora21.UltimateTeam.sln `
  -c Release `
  --filter 'FullyQualifiedName~PerformanceScenarioTests' `
  --artifacts-path .\outputs\verification\performance-tests-<fresh-name> `
  --nologo
if ($LASTEXITCODE -ne 0) { throw 'Performance harness tests failed' }
```

For a single serial diagnostic run:

```powershell
dotnet run --project .\ultimateteam\server\tools\Aurora21.UltimateTeam.Performance\Aurora21.UltimateTeam.Performance.csproj `
  --configuration Release -- `
  --seconds 0 --samples 50 --peers 100 --pool 250 --inventory 2000 `
  --timings true --waits true --functions true `
  --fail-fast true --revision '<commit>+worktree' `
  --output .\outputs\performance\serial-<fresh-name>
if ($LASTEXITCODE -ne 0) { throw 'Serial performance run failed' }
```

Every output directory must be new. The runner preserves `checkpoint.json` and
`failure.json` when a fail-fast gate stops a run; a completed run writes
`run.json`.

The capacity matrix is the canonical load sequence:

```powershell
powershell.exe -NoProfile `
  -File .\ultimateteam\server\tools\Aurora21.UltimateTeam.Performance\run-capacity-matrix.ps1 `
  -ReportRoot .\outputs\performance\capacity-<fresh-name> `
  -Revision '<commit>+worktree' `
  -Pool 250 `
  -Samples 50
if ($LASTEXITCODE -ne 0) { throw 'Stop and investigate the failed capacity profile' }
```

The script publishes a self-contained .NET 10 server-GC harness (the server's GC mode) and
runs, in order:

1. 100 clubs, 2,000 cards each, 5 minutes.
2. 500 clubs, 2,000 cards each, 5 minutes.
3. 500 clubs, 2,000 cards each, 20 minutes.
4. 1,000 clubs, 2,000 cards each, 5 minutes.
5. 1,000 clubs, 2,000 cards each, 20 minutes.

It stops immediately on an error, invariant failure, timeout, deadlock, malformed
report, or missed latency gate. Ordinary endpoints require p95 below 250 ms;
transfer-market search requires p95 below 500 ms. Do not start a later profile
after a failure. Inspect the profile log, `checkpoint.json`, `failure.json`, or
`run.json`, fix the cause, run the relevant regression checks, and restart with a
new report root.

These benchmarks exercise the in-process FUT router with synthetic clubs and a
disposable database. They do not include native FIFA, Kestrel/TLS, public-network
traffic, or gameplay relay acceptance.

The soak workload includes a 16-card My Club player search from every synthetic
player. Its `soak.club-search` result measures the route under mixed concurrent
traffic; the named `club.search.page` query shows the database page portion.

For a paced run, pass `--target-rps-per-player 2`. Each pair completes 28
measured API requests per round, so this sets a seven-second minimum between
the starts of that pair's rounds and staggers pair starts across the interval.
The report records `measuredRequests` and `achievedRequestsPerPlayerSecond`;
the achieved rate can be lower if requests take longer than the target interval.
This is an average pair workload: individual requests within a round can still
arrive in bursts, and one player makes 13 requests while the other makes 15.

Pass `--auto-prepare <n>` to override `database.max_auto_prepare` for the run; the
report records the value as `maxAutoPrepare`. Compare it against a run without the
option in the same sitting.

The throwaway cluster otherwise runs with PostgreSQL's own defaults, not the local-install
`database.cluster_*` values. Pass `--cluster-config <file>` to start it with a deployment profile:
a JSON fragment with a `database` section of `cluster_*` keys, where unset keys fall back to
`config.example.json`. The report records the file as `clusterConfig` and the values the cluster
actually runs with as `postgresSettings` (`--pool` still sets `max_connections` when it needs more).
[`cluster-profiles/hosted-32gb.json`](tools/Aurora21.UltimateTeam.Performance/cluster-profiles/hosted-32gb.json)
is a starting point for a 32 GB VPS, measured on 2026-09-25 against PostgreSQL defaults at 1,000 clubs (PostgreSQL CPU per request −2.9%, throughput +3.7%; see `issues.md` I-4), shared by the server and PostgreSQL:
8 GB `shared_buffers` (about a quarter of memory), 20 GB `effective_cache_size`, 16 MB `work_mem`,
4 GB `max_wal_size`, and 260 connections to match the benchmarks' 250-connection pool. Before
adopting it, compare it against a run without it in one sitting, judged by PostgreSQL CPU per
request, for example `-CandidateArgs '--cluster-config <absolute path>'` with `run-screen.ps1`.
To use a profile on a hosted server, copy its keys into that server's `config.json`
`database` section; the local-install defaults stay unchanged.

For a quicker screen of one change, run alternating control and candidate runs of
a small paced profile (100 clubs, 2,000 cards, 60 seconds by default):

```powershell
powershell.exe -NoProfile `
  -File .\ultimateteam\server\tools\Aurora21.UltimateTeam.Performance\run-screen.ps1 `
  -ReportRoot .\outputs\performance\screen-<fresh-name> `
  -Revision '<commit>+worktree' `
  -CandidateArgs '--auto-prepare 100' `
  -Pairs 3
```

It judges cost per request: CPU of the run's own throwaway PostgreSQL processes and
harness CPU. Each run also records allocation, but it is not compared: the soak counter is
unreliable under server GC (use `collect-function-profile.ps1`). `screen-summary.json` gives each arm's median and the
spread across control runs; treat a change smaller than that spread as noise. Runs
alternate order in each pair, and the script stops on any error or unexpected
PostgreSQL process layout. This profile does not saturate the host, so it is not a
capacity result; confirm a promising change with the 1,000-club profile.

To compare two builds, publish the control build separately and pass `-ControlHarness <folder>`.
A harness reads rules and catalogs from the nearest `ultimateteam/runtime-source/ultimateteam21` above
its own folder (only the schema is built in), so publish a control build that predates a rules
change inside a `git worktree` of its own revision. The summary records both `dataRoot` values
and the script warns when the two builds share one.

To measure the player-search projection backfill and the extra insert, pile-move,
and delete cost at 1,000 clubs with 2,000 cards each:

```powershell
dotnet run --project .\ultimateteam\server\tools\Aurora21.UltimateTeam.Performance\Aurora21.UltimateTeam.Performance.csproj `
  --configuration Release -- `
  --projection-benchmark true --peers 1000 --inventory 2000 --samples 5 `
  --revision '<commit>+worktree' `
  --output .\outputs\performance\projection-<fresh-name>
```

This creates a separate temporary PostgreSQL database. Bulk generation skips
row-by-row user invariant triggers only while adding synthetic parent and child
rows, then re-enables them and checks row counts. The backfill populates the current
projection schema without rewinding migration history; the output records
`projectionBackfillMs` and database sizes before and after the backfill. Write trials
alternate projection maintenance on and off
in the same database, rolling back each 200-card batch. They isolate database
maintenance cost; they are not full API write benchmarks or a copy of a real club.

## Repository layout

- `src/Aurora21.UltimateTeam.Core` — gameplay, services, persistence, catalogs, and
  response construction.
- `src/Aurora21.UltimateTeam.Protocol` — protocol models and codecs.
- `src/Aurora21.UltimateTeam.Server` — executable hosting and command-line tools.
- `tests/Aurora21.UltimateTeam.Tests` — unit, integration, protocol, database, and
  performance regression tests.
- `database-setup` — schema, seeds, and embedded migrations used by startup.
- `tools/Aurora21.UltimateTeam.Performance` — synthetic benchmark executable and
  fail-fast capacity matrix.
- `tools/Aurora21.UltimateTeam.TraceAnalysis` — analysis utility for synthetic
  performance traces.

Shared runtime catalog seeds, bundled rule defaults, content, and the sole server-config
defaults live under `ultimateteam/runtime-source/ultimateteam21`. PostgreSQL
`content_rules` holds active rules after startup; the operator panel stages reviewed
changes and can import differences from the bundled files. `LiveRulesTests` checks
round-trip, validation, publication, import, and removal across a restart. Preserve
wire response shapes and use
temporary profiles for tests. Building does not update an already running server,
and a listening port or HTTP 200 alone does not establish that the expected build
is active.

Migration 094 adds reviewed `promo`, `promo_event` and `icon_rotation` content areas, reversible
bucket editing and committed table revision tracking. Draft uses saved promo buckets. Panel
availability and seeded pack-odds previews may load the pending changeset in a transaction
that rolls back. Named reward-pool and Match Pack caches follow the content snapshot.
`GET /panel/api/content/status` reports SQL changes that require an explicit reload.
`PromoManagementTests`, `PromoCardSelectorTests`, `PromoGroupsTests` and the promo
cases in `OperatorPanelTests` cover these contracts in isolated databases; see
[the API contract](../panel-api-contract.md) and [the panel guide](../panel-web/README.md).

For an isolated local panel check after building, run
`powershell.exe -NoProfile -File .\ultimateteam\Test-LocalPanel-Isolated.ps1` from the
repository root. It verifies the running build hash and throwaway database, then stages,
validates and publishes one rule through the loopback panel API. If Chrome is installed,
it clicks through rule edit, review, publish, history and revert, then pack creation,
lineup staging and publication in the served panel. It stops only the server and
PostgreSQL cluster it started. It does not check FIFA visibility.
