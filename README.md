# Aurora 21

Join our discord : https://discord.gg/aurorafut

Aurora 21 is a FIFA 21 PC Ultimate Team environment with a Windows launcher,
a C# server (.NET 10, PostgreSQL 18), and a React + TypeScript operator panel.
Hosted deployment uses Docker on Ubuntu. Archived Python runtimes are reference
material only.

Aurora 21 works with any version of the game and works against EA.
You’ll need a cracked version of the game, as it will work better than the official version.

## Aurora 21  

Managed by:
- Miz
- sinsehh

Developers:
- Mustafa
- ghoulnotokyo
- Shadow
- Horizzon
- Atlaz

## Documentation

| Document | Purpose |
| --- | --- |
| [Contribution guide](CONTRIBUTION.MD) | Development workflow, testing requirements and contracts to preserve |
| [Panel TODO](todo.md) | Missing content editors and remaining acceptance work |
| [Panel guide](ultimateteam/panel-web/README.md) | Panel setup, local review, assets and frontend structure |
| [Panel API contract](ultimateteam/panel-api-contract.md) | Routes, payloads, publication and access restrictions |
| [Runtime guide](ultimateteam/README.md) | Launcher, local play and settings |
| [Server guide](ultimateteam/server/README.md) | Server builds, isolated tests and performance tools |
| [Deployment guide](ultimateteam/deploy/README.md) | Docker, private panel access and VPS operations |
| [Hosting evidence](ultimateteam/native-hosting-investigation.md) | Native client results and unresolved hosting checks |
| [Native data](ultimateteam/native-data/README.md) | Catalogue provenance |
| [Documentation map](docs/README.md) | Current guides, test records and historical research |
| [Local FIFA acceptance](ultimateteam/panel-web/test/game-acceptance.md) | Completed game checks and known refresh limits |
| [Hosted integration status](ultimateteam/aurora-integration-status.md) | Recorded shared-launcher results and open gates |

The implemented panel plans and old Python/research handoffs are archived.
Use the [documentation map](docs/README.md) for current guides and historical evidence.

## Requirements

- Windows, Windows PowerShell and the .NET 10 SDK for launcher development and
  the documented local server review workflow.
- Node.js 24.x (24.15 or newer) with npm for panel development (compatible with the pinned
  dependency requirements). Node is not required to run the embedded panel.
- Portable PostgreSQL 18 binaries, installed by the supported builder.
- A complete, writable checkout. Keep the runtime catalogues and content assets.

Run the following PowerShell commands from the repository root.

## Run the game locally

```powershell
.\ultimateteam\RUN-FIFA21.cmd
```

Choose the installed `FIFA21.exe` in launcher settings and follow its instructions.
The launcher handles the server, redirector and build-specific auth helper.
For a full rebuild:

```powershell
powershell.exe -NoProfile -File .\ultimateteam\Build-FIFA21-Launcher.ps1
```

The builder installs missing portable PostgreSQL binaries. Respect its
running-process guards; do not use `-LauncherOnly` to refresh server changes.

## Run the panel for local testing

First install and build the frontend:

```powershell
Set-Location .\ultimateteam\panel-web
npm ci
npm run build
Set-Location ..\..
dotnet restore .\ultimateteam\server\Aurora21.UltimateTeam.sln
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\ultimateteam\Start-Panel-Review.ps1
```

Install PostgreSQL through the full builder above if it is missing.
The review script builds the server using the committed frontend bundle and
creates a temporary database and config. It leaves the review server running,
without launching FIFA or a browser.

Open **http://127.0.0.1:18448/panel/** after startup finishes.
The script prints the log directory and saves process information in
`%TEMP%\aurora-panel-review-info.json`. See the [panel guide](ultimateteam/panel-web/README.md#stop-the-review-server)
for stopping that specific review instance.

For frontend development, keep the review backend running and open a second terminal:

```powershell
Set-Location .\ultimateteam\panel-web
npm run dev
```

Open **http://127.0.0.1:5173/panel/**. Vite proxies the API and images to
port 18448. Stop Vite with Ctrl+C. Do not run a Vite production build concurrently
with a C# build: Vite replaces the directory C# embeds.

## Test

### Frontend

From `ultimateteam/panel-web`:

```powershell
npm run typecheck
npm test
npm run build
npm run stamp:check
```

For a focused check, pass a test file: `npm test -- src/pages/promos/promos.test.tsx`.
Commit frontend source and the generated `Resources/panel/` bundle together.

### Backend

From the repository root, use focused tests while developing:

```powershell
Remove-Item Env:ULTIMATETEAM_POSTGRES_CONNECTION -ErrorAction SilentlyContinue
dotnet test .\ultimateteam\server\tests\Aurora21.UltimateTeam.Tests\Aurora21.UltimateTeam.Tests.csproj --filter 'FullyQualifiedName~OperatorPanelTests'
```

Tests use throwaway PostgreSQL clusters. Never supply a real club database.
Before a commit, or when requested, run the full suite (about 20 minutes):

```powershell
dotnet test .\ultimateteam\server\Aurora21.UltimateTeam.sln
```

The [contribution guide](CONTRIBUTION.MD#verification) lists checks for startup,
packaging and deployment changes. A build or fixture result does not establish
native FIFA login, content visibility or playable multiplayer.

## Panel behavior

The current navigation pages exist: Dashboard, Players / Clubs, Cards, Packs / Picks,
Promos, Events, Rules, Changes, Access & maintenance, Matchmaking, Logs and Settings.
Dedicated SBC, Rewards and Objectives editors remain separate work; see
[todo.md](todo.md).

- React is the only panel, served at `/panel/`. Former `/panel/next/` bookmarks
  redirect to it, preserving their hash routes.
- Cards, packs, lineup, rules, promo buckets and events use staged changes.
  Validate and Publish in Changes to apply them.
- Club grants/balances, access and settings apply immediately.
- Promos support browsing and bucket creation, including rating-range seeding.
  Creating promos and adding outside cards to them are excluded from the current UI.
- Store packs support either a per-club purchase allowance or shared community
  stock, never both. Gifts/rewards do not consume store stock.
- Permanent cards do not consume contracts; loans still expire after their matches.
- Browser acceptance, live publication checks and native client tests are separate
  from automated coverage; the existence of a page does not establish acceptance.

## Files and state

| Path | Purpose |
| --- | --- |
| `ultimateteam/panel-web/` | React, TypeScript, Motion and frontend tests |
| `ultimateteam/server/` | Active C# solution, schema, migrations and tests |
| `ultimateteam/runtime-source/ultimateteam21/` | Catalogue seeds, rules and server config defaults |
| `ultimateteam/panel-assets/` | Panel card art, portraits, badges and flags |
| `ultimateteam/deploy/` | Docker and VPS deployment |
| `tools/fifa21-data-miner/` | Native catalogue extraction |
| `docs/README.md`, `archive/` | Documentation map and historical material |

Personal settings live in `%LOCALAPPDATA%\Aurora21\UltimateTeam`;
managed club state lives in `%LOCALAPPDATA%\FIFA21UltimateTeam`.
Updates must preserve both. Bundled JSON seeds do not replace an existing database:
use reviewed panel edits/imports or a migration.

Keep credentials, private certificates, dumps, logs and mutable databases out of Git.
Only public trust anchors are distributable. See [.gitignore](.gitignore) and the
[contribution guide](CONTRIBUTION.MD) before adding files.
