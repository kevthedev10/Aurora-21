# Contributing to Aurora 21

Use [README.md](README.md) for setup and run commands. This guide covers changes
to the active C# server, Windows launcher and React operator panel.
[AGENTS.md](AGENTS.md) contains the repository's detailed agent working agreement.

## Workflow

1. Read the current source, callers, configuration and tests for the area you change.
   Check the branch and existing edits; preserve unrelated work.
2. Make a small coherent change. Update affected callers, defaults, resources,
   tests and documentation together.
3. Run focused checks, then the verification required for the scope below.
4. Describe the change, its reason, actual test results and unresolved limits.
5. Review the files being committed. Use Conventional Commits
   (`feat:`, `fix:`, `docs:`, etc.). Agents commit or push only when requested.

Run tests locally. A push to `main` builds and deploys the hosted server; nothing
else builds an image. Do not add build or deployment triggers for pull requests,
tags or other branches, or test jobs to GitHub Actions.

## Panel changes

- Source: `ultimateteam/panel-web/`. React + TypeScript with Motion.
- Embedded output: `ultimateteam/server/src/Aurora21.UltimateTeam.Server/Resources/panel/`.
- Backend routes: `ultimateteam/server/src/Aurora21.UltimateTeam.Server/Panel/`.
- Contract: [panel-api-contract.md](ultimateteam/panel-api-contract.md).

Reuse shared controls, API helpers, notifications and Motion settings. Preserve
keyboard navigation, focus return, reduced motion and unsaved form values.
Check long text, narrow layouts, loading, empty and failed-refresh states.
Tables need stable widths and sorting; large lists need bounded paging.

Content edits are staged and published through Changes. Club operations,
settings and access changes are immediate. Make that distinction visible.
Validation/publication use the server's conflict checks; client preflight checks
do not make a multi-request operation atomic. Keep the full changeset visible
before publishing: filters must not silently reduce what gets published.

Promos contain weighted buckets with explicit card membership. Bucket selection
uses relative weights; eligible cards within a bucket draw with weight
`1 / draw_reduction` (default 1). Gold Common uses rating buckets only and does
not expose desirability controls. Displayed bucket percentages are conditional
on selecting the promo, not whole-pack odds.
Rating ranges seed membership once; later rating edits do not move members.
Ordinary promo events add availability. Icon rotations replace the standard Icon
tier and must not overlap. Dates in the UI use Europe/Amsterdam; API timestamps
carry an explicit UTC offset.

After frontend source changes, run `npm run build` and `npm run stamp:check`.
Include generated embedded output with the source. Never hand-edit bundles or
run Vite build concurrently with C# compilation. Rebuilding does not update a
running server; an authorized test restart must use the rebuilt executable.

The panel stays private on loopback, normally reached through SSH. Its password
form is not authentication; loopback access is the credential. Do not expose
port 18448 through Caddy, add forwarded-request exceptions or relax Origin/Host
checks to fix a development issue. Use the existing Vite proxy.

## Backend and database changes

The C# server is the only supported runtime. Archived Python and other imported
implementations are reference material. Port verified behavior into C# with
tests; do not restore retired runtimes or add compatibility shims by default.
Python is welcome for diagnostics, analysis, binary mining, quick tests and
development utilities. Using it for tooling does not require making it a game
server dependency.

- Preserve native JSON shapes, status codes and Fire2/TDF bytes.
- Verify native item IDs and metadata from source data or client evidence.
  Record provenance and distinguish observed facts from hypotheses.
- Add migrations after the latest registered migration. Register embedded
  resources and update the schema version and migration list together.
- Never rewrite a migration already applied to production to introduce a change.
- Verify both a fresh database and an upgrade with existing ownership/references.
  Content cleanup must preserve owned cards, squads, picks, market rows and history.
- PostgreSQL `content_rules` holds active rules; bundled files seed fresh installs
  and supply reviewed imports. Keep config defaults in `config.example.json`.
- Content requests use a pinned immutable snapshot. Preserve atomic publication,
  validation rollback and explicit reload of externally changed content.
- Never point fixtures at real club state, reset/reseed a club without explicit
  authorization, or kill all PostgreSQL processes.

Hosted identity must come from a verified launch assertion/code and UT session,
never an IP address or a supplied persona/Discord ID. Keep credential evidence
redacted. Do not change the identity hash salts during renames.

## Launcher and process safety

Use `ultimateteam/RUN-FIFA21.cmd` and the supported builder. Preserve:

| Interface | Required arguments |
| --- | --- |
| Entrypoint → builder | `-IfNeeded` |
| GUI → launch worker | `-UserConfigRoot`, `-LauncherLogPath` |
| GUI → stop worker | `-LauncherLogPath` |
| Elevation → worker | Initiating user's config root and log path |

Resolve resources relative to the script/executable; support spaces and
non-ASCII paths. Preserve existing personal settings across upgrades.
Ship the launcher and auth helper with matching source/resources; keep their
Aurora names because FIFA Config checks process prefixes.

Before stopping or reusing a process, verify its resolved executable/script
path and association with the checkout. Match the specific server subcommand
and database directory. Launch scripts can alter services, hosts and trust;
do not run them for a documentation or read-only inspection task.
Keep build-specific native byte/hash checks and fail closed on mismatches.

## Verification

Use the narrowest meaningful checks while working. Do not add tests that merely
mirror a reversible CSS change; check the layout in the browser. Backend changes
need behavior tests. Do not weaken assertions to hide a regression.

| Scope | Required checks |
| --- | --- |
| Documentation only | Paths, links, commands against current source, whitespace |
| Frontend behavior | Focused Vitest checks, typecheck, production build and stamp |
| Panel API/content | Focused C# API/content tests, frontend checks for affected consumers |
| Migration | Fresh install and upgrade preserving existing state |
| Server/bootstrap | Full server suite and isolated startup verification |
| Launcher/packaging | Supported full build and Windows checks below |
| Deployment | Docker build, local Compose rehearsal and documented endpoint probes |

For code changes, run the full server suite before a commit or when requested,
not after every edit. Documentation-only changes need the checks in the table.
Capture output and record failures as well as passes. Tests must use disposable
PostgreSQL clusters, with `ULTIMATETEAM_POSTGRES_CONNECTION` unset.
See the [server guide](ultimateteam/server/README.md) for isolated artifact paths
and benchmark commands.

For launcher/startup/release changes, from the repository root:

```powershell
powershell.exe -NoProfile -File ultimateteam/Build-FIFA21-Launcher.ps1
powershell.exe -NoProfile -File ultimateteam/Test-StartupScriptPortability.ps1
powershell.exe -NoProfile -File ultimateteam/Test-LauncherProcessNames.ps1
powershell.exe -NoProfile -File ultimateteam/Test-LauncherSettings.ps1
powershell.exe -NoProfile -File ultimateteam/Test-RepositoryHygiene.ps1
node ultimateteam/verify-auth-bootstrap.mjs --self-test
```

After a server/bootstrap build, use **Windows Node** for:

```powershell
$serverExe = (Resolve-Path .\ultimateteam\Aurora21-UltimateTeam-Server.exe).Path
node.exe .\ultimateteam\verify-startup-runtime.mjs --server $serverExe
```

WSL Node supplies Linux temporary paths that the Windows server rejects.
Do not claim the harness passed if it was not run. Report builds, fixtures,
isolated runtime checks, browser checks and native game results separately.
Two-player gameplay, UDP relay and QoS acceptance require a VPS test.

## Files, secrets and evidence

Review [.gitignore](.gitignore) and
[Test-RepositoryHygiene.ps1](ultimateteam/Test-RepositoryHygiene.ps1) together.

Keep personal settings, mutable databases, backups, logs, captures, debugger
databases, `.env` files, tokens and private certificate material local.
Do not commit game executables/DLLs or proprietary material without established
rights. Keep reviewed catalogues, provenance, shared defaults and project content
assets; do not blanket-ignore all JSON or databases.

The committed panel bundle is an intentional generated artifact. The two shipped
helper EXEs are also intentional; the server EXE and downloaded PostgreSQL
binaries are build outputs. Do not broadly allow EXEs/DLLs.

In a handoff, include the exact test names/results, relevant paths and what
remains unconfirmed. Never treat a listening port, HTTP 200 or successful fixture
as proof of native FUT login or playable multiplayer.
