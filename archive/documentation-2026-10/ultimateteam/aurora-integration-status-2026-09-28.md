> Historical integration/deployment report, archived 2026-10-01. This records earlier releases, not current VPS state. See the [current summary](../../../ultimateteam/aurora-integration-status.md).

# Aurora 21 in the shared Aurora launcher and panel: status

Branch `feat/aurora15-integration` is at `77334fc` and tracks
`origin/feat/aurora15-integration` at that commit. The workflow re-enable described
below is currently an uncommitted working-tree change. Updated 2026-09-28.

The goal: Aurora 21 plugs into the Aurora launcher and the shared operator panel (the ones Aurora 13/15/16 use), so the
Aurora15 developer mostly fills in values instead of building Aurora 21-specific features.

## What was built

### Who may play: Discord roles

- Players are verified Discord accounts: the Aurora account service signs who launches, and Aurora 21 never trusts an
  address or a launcher-supplied id.
- A player may launch when their account holds one of the **play roles** in the Aurora Discord server. The server's bot
  answers this (`DiscordRoles`, Discord REST API). With no play roles set, every verified account may play.
- **Maintenance** lets in **bypass roles** and listed **bypass accounts**. A **block** always wins and ends that
  player's codes, FUT session and Blaze connection at once. The **player cap** applies to everyone else.
- **Role answers:** cached for 5 minutes. During a Discord outage, an answer from the last 7 days is used. An account
  with no remembered answer is refused ("could not check your roles"), never let in unchecked.
- **Storage:** everything lives in PostgreSQL (migration 063: `player_access`, `server_access`, `player_sessions`,
  `panel_edits`). It's edited live from the panel and survives restarts and deploys.
- **Removed:** the earlier approval file (`--launch-approved-ids`) and manual approve/disapprove.

### Launcher API, `/client/*` (same shapes as Aurora 16's)

On `127.0.0.1:18447`, published over HTTPS by Caddy:

| Route | Does |
| --- | --- |
| `GET /client/health?discord=&name=` | Server up, player count, maintenance and refusal reason (`maintenance`, `full`, `blocked`, `no_role`, `roles_unavailable`, `not_configured`), `gameAddress`, install version |
| `POST /client/launch {discordId, discordName, launcher, assertion}` | Verifies the assertion, checks roles and access, records the launch, returns `{ok, code, expiresInSeconds, personaId, gameAddress}` |
| `GET\|POST /client/presence` | Which Discord accounts are online, searching or in a match (friends list) |
| `GET /client/trust-anchors/manifest.json` and files | This server's public trust anchors (never a private key) |
| `GET /client/install/manifest.json` and files | The client pack, validated manifest, resumable downloads |

Other launcher-API details:
- **Launch assertions:** ES256 (preferred: Aurora 21 holds only a public key) or HS256 with a secret used for Aurora 21
  only. The configured key fixes the algorithm.
- **Claims:** required are `iss`, `aud = aurora21-launch`, `sub` (the Discord id), `iat`, `exp` (at most 120 s) and
  `jti`. Optional are `nbf`, `scope` and `name`.
- **Club names:** the launch records the player's name, which `main`'s club naming uses.
- **Limits:** 20 launches per address per 10 minutes. All messages are configurable in `config.example.json`
  (`launcher_api`).

### The shared operator panel

- **The page:** Aurora 16's panel front-end, copied with your OK from Aurora16 `src/panel/` at `027a5c1` into
  `server/src/Aurora21.UltimateTeam.Server/Resources/panel/`. It's served on `127.0.0.1:18448/panel/`, reachable
  through an SSH tunnel. Hub framing still needs a live check because the current response headers deny framing.
- **The API:** `Panel/OperatorPanel*.cs` answers Aurora 16's `/panel/api/*` contract from Aurora 21's data.
- **Access model:** Aurora 16's, where a direct loopback connection is the only credential. Forwarded requests, foreign
  Host headers, cross-site requests and non-JSON writes are refused.
- **Working tabs:**
  - **Dashboard:** services, online players, totals, recent logins, clubs, 14 days.
  - **Players:** list and detail, balances (set, add, take away), gift packs, grant cards, inventory per pile, sessions,
    the panel edit history, and a new Access card (block, maintenance bypass, note).
  - **Cards:** search and specials.
  - **Packs, Pack catalogue and Store:** overview and gifting; create, edit, preview and hide store pack definitions;
    publish the store lineup live. Previews cover standard player packs; specialist item generators return 422.
  - **Settings:** live pack rules and rarity weights, maintenance and player cap.
  - **Maintenance:** who may play: the play and bypass roles (picked from the Discord server's roles or entered by
    id) and the bypass and blocked accounts, each a list with add and remove, saved with Save; "Test a player";
    maintenance message and cap; a Maintenance switch that applies on its own once confirmed; backups.
  - **Modes:** per-club Division Rivals, Squad Battles, FUT Champions/Weekend League and Draft state with confirmed,
    audited resets. Current weekly/event state can be reset while settled rewards and earlier records remain.
  - **Logs** and **Matchmaking** (live).
- **Removed:** FIFA 16 Tournaments (seasons and cups). The earlier token-based `/api/v1/panel` API and
  `PanelEndpoint` are gone.
- **Edits apply at once** in PostgreSQL and are recorded in `panel_edits`.
- **Not possible on Aurora 21, and the panel says so:** club name and abbreviation (derived from the player's name),
  restarting services from the panel (use Docker), and address-based lists.
- **Page changes:** every change to the copied page is listed in `Resources/panel/PROVENANCE.md`.

### Client pack (the Install button)

- `client-pack/Start-Aurora21.ps1` runs elevated with the game code on standard input. It does the local launcher's
  sequence for a hosted server:
  1. Refuse the known malformed FIFA21.exe.
  2. Start FIFA 21 the way this copy needs (`-LaunchMethod auto`, the default, decides):
     - **EA app install** (its `__Installer` data, or EA's registry entry naming this folder): through the EA app's
       `origin2` handler.
     - **Steam copy** (under `steamapps\common`): through Steam.
     - **Anything else**, including an emulated copy (anadius files or a `_crack` folder): `FIFA21.exe` directly and
       suspended. Such a copy is never sent to the EA app, even when the EA app is installed.
  3. Run the in-memory trust patch with this server's trust anchors. A direct launch also bypasses the Origin client
     bootstrap check, as the local launcher's replacement mode does, and then resumes the game.
  4. Feed the code to the auth bridge (stdin, never a file).
  5. Point `spring18.gosredirector.ea.com` at the server's IPv4 and verify it resolves.

  It prints `[READY]` or `[FAILED] reason`. `-Cleanup` removes the hosts entry afterwards. `-DetectOnly` prints the
  method it would use and changes nothing, and `-LaunchMethod ea-app|steam|direct` overrides the detection.
  Observed on 2026-09-28 with `-DetectOnly`: the InsaneRamZes copy resolves to direct (anadius), and synthetic EA,
  Steam and plain folders resolve to ea-app, steam and direct. The first hosted launch (2026-09-28 20:40 BST; method not
  recorded) got through the trust patch and stopped at the auth bridge; see issue #15.
- `client-pack/runtime.json` describes those steps as data for the launcher: command, stdin code, placeholders, ready
  line, cleanup, and where the trust anchors come from.
- The Docker build publishes the Windows server executable (for `trust-patch`). It zips the script, the auth bridge
  and EA's original CA moduli, and writes the manifest. The pack ships inside the image at `/app/client-install`, so
  every deploy serves a matching pack. CI versions it `sha-<7>`.

### Deployment

- **`compose.yaml`:** the server runs the launcher API, the client pack and the panel. It gets the backup folder
  read-only and the secrets as environment variables. There's a new **`caddy`** service (HTTPS for the launcher API
  only, with a Let's Encrypt certificate) that starts once `LAUNCHER_DOMAIN` is set.
- **Backups:** the backup service also takes a dump when the panel asks for one.
- **`.env.example`:** lists every value (below).
- **`remote-deploy.sh`:** keeps the Caddy profile in step and also health-checks the launcher API. The Deploy workflow
  copies the `Caddyfile`.

## What the Aurora15 developer does

This is the smallest set of work the current design allows. Some of it is code, but code that copies their FIFA 13 and
16 features:

1. **Account service:** a `fifa21/launch-proof` endpoint copied from `api/v1/client/fifa13/launch-proof` (same request
   and response). The `assertion` is a JWT signed with ES256 (preferred) or an HS256 secret used for Aurora 21 only,
   carrying `iss`, `aud: aurora21-launch`, `sub: <discord id>`, `iat`, `exp` (at most 120 s) and a fresh `jti`, and
   optionally `name`. It gives the Aurora 21 operator the issuer string and the public key or secret.
2. **Launcher, a FIFA 21 page** following the FIFA 13/16 pages:
   - Poll `https://<LAUNCHER_DOMAIN>/client/health?discord=&name=`.
   - Install the pack from `/client/install/manifest.json`.
   - Fetch `/client/trust-anchors/manifest.json` and its files into `{install}\trust-anchors`.
   - On Play, get the proof, `POST /client/launch`, and run `runtime.json`'s `command` elevated, with the returned
     `code` on stdin and `{gameAddress}` from the reply. Wait for `[READY]`, then run `cleanup` after FIFA exits.
   - Use `/client/presence` for the friends list.
3. **Panel hub:** add Aurora 21 as another panel, the tunnelled `127.0.0.1:18448`, like Aurora 16's.

### Aurora15 side and the live VPS, 2026-09-28

Each item says whether it was observed or is still inferred.

- **Account service (observed in tests; live since 2026-09-28 20:13 BST as Aurora 15 build `49501d2a`; the public route answers 401 to a bad session).** `POST https://15.aurorafut.com/api/v1/client/fifa21/launch-proof`
  with `{client, sessionToken}` returns `{assertion, expiresAtUtc, discordId}`, with the same checks as the FIFA 13 and
  FIFA 16 proof routes. The assertion is an ES256 compact JWT: `iss`, `sub`, `name`, `aud: aurora21-launch`,
  `scope: aurora21.launch`, `iat`, `exp = iat + 120` and a 32-character base64url `jti`. The signature is 64 bytes,
  IEEE P1363. Aurora15 commit `d89a57fa`. The private key was created on the Aurora 15 host and never leaves it.
- **The VPS `.env` (observed; set 2026-09-28 18:43 UTC).** The launcher values are in place, set with
  [`deploy/set-launcher-env.sh`](../../../ultimateteam/deploy/set-launcher-env.sh), which reads the bot token from standard input. The token is
  the Aurora bot that Aurora 13 and 16 use, copied host to host and never committed. The backup on the VPS is
  `.env.before-launcher-20260928T184253Z`. The file had CRLF line endings and is now LF-only. Docker Compose strips a
  trailing CR, which was checked against the running database password, so no value changed.

  ```env
  LAUNCHER_DOMAIN=21.aurorafut.com
  COMPOSE_PROFILES=launcher
  LAUNCH_ISSUER=https://15.aurorafut.com
  LAUNCH_ES256_PUBLIC_KEY=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhgI3f2Nm4TsUeSCdJ3nAjgNHKhQ0J+UrwdHfY12KmSu5xDkNRra2GfzaBu/Ge+A6AdCDeK3F9bNnLqqdZIFeoA==
  LAUNCH_HS256_SECRET=
  DISCORD_BOT_TOKEN=<the Aurora bot token>
  ```

  `21.aurorafut.com` is a Cloudflare DNS-only A record for 185.172.175.227. Caddy obtained its certificate on first start.
- **Access (observed).** Maintenance is on with the message "Aurora 21 is in maintenance while it is tested. Players
  with early access can play." The Discord server is 1530323539192713407 (Aurora). The bypass role is
  1546860858326515732 (Whitelisted (FIFA 21)) and the bypass account is 171646583736107008. There are no play roles
  and nobody is blocked. Checks: anonymous `/client/health` answers reason `maintenance`; the bypass account gets
  `bypass: true`; the bot lists the server's roles and "Test a player" resolves them; public
  `https://21.aurorafut.com/client/health` answers 200; `/panel/` answers 404 publicly and port 18448 is closed from
  outside.
- **Launcher (observed in a live probe; no FIFA 21 launch yet).** Aurora launcher 1.2.61 was released on 2026-09-28 at
  19:48 BST as the minimum version (Aurora15 commits `d3c06d6b` and `e72c42d7`). Its own code was run against the VPS:
  health for an anonymous and a bypass account, the 50 MB `local` pack downloaded and verified, four trust anchors
  verified, and `runtime.json` accepted. What it enforces is in
  [the launcher handoff](../../../ultimateteam/aurora15-launcher-handoff.md#what-the-aurora-launcher-enforces-1261). The first real launch
  through the launcher (2026-09-28 20:40 BST, Discord 896404375218565180) got a code and passed the trust patch, then
  the auth bridge stopped it: `[ERROR] Server lacks local auth profile v1`. The bridge checks the local launcher's auth
  profile on `127.0.0.1:8090` before it installs, which no hosted PC has. See issue #15.
- **Deployed build (observed).** Image `aurora21/ultimateteam-server:sha-582c029` has been live since 2026-09-28 20:36
  BST. It was built on the VPS from `582c029`, which includes the panel editor (#12) and the launch-method detection
  (#13). It serves client pack `sha-582c029`. A predeploy dump `predeploy-20260928T193551Z.dump` was taken first, and the
  previous image is tagged `before-582c029`. Maintenance and the access lists carried over. Checks: FUT 401, secure
  redirector, launcher API 200, and the served panel has the new editor. The published pack's `-DetectOnly` resolves the
  InsaneRamZes copy to direct.
- **Panel hub (observed).** The shared hub's tunnel as `deploy` reaches this panel: the title matches and the API answers
  200 through the tunnel. Its gateway removes `X-Frame-Options` and replaces `frame-ancestors` with the hub origin, so the
  page's frame-deny headers need no change. Only the `aurora21_panel` cookie is forwarded. Aurora15 commit `49501d2a`.
## What the Aurora 21 operator fills in

- **VPS `.env`:**
  - `POSTGRES_PASSWORD`, `PUBLIC_IPV4`;
  - `LAUNCHER_DOMAIN` (DNS pointing at the VPS);
  - `LAUNCH_ISSUER` and `LAUNCH_ES256_PUBLIC_KEY` or `LAUNCH_HS256_SECRET`, from the Aurora15 dev;
  - `DISCORD_BOT_TOKEN`, the Aurora bot, which must be in the Aurora Discord server.
- **Firewall:** also open TCP 80 and 443, for Caddy.
- **In the panel's Maintenance tab:** the Discord server id, the play roles and the bypass roles.

## How it was verified

| Check | Result |
| --- | --- |
| Server and tests build (Windows, Debug) | Succeeded as part of the focused panel test run on 2026-09-28 |
| Focused operator-panel tests, including live pack catalogue edits/publication and all four mode resets, in isolated test databases | 7 passed on 2026-09-28 |
| Local Windows panel smoke test | Hosted profile on `127.0.0.1:18448`, with a freshly initialized default PostgreSQL cluster; Dashboard, Pack catalogue, Store and Modes rendered in Edge; main read-only APIs returned 200; a player detail and 12-item pack preview returned 200; forwarded-address request returned 403; running `serverBuildSha256` matched the rebuilt executable |
| Earlier focused launcher, access, hosted-profile, migration and deploy checks | See prior branch work; not rerun for this pack/mode update |
| `node --check` on the edited Packs, Pack catalogue and Modes scripts; `git diff --check` | Passed on 2026-09-28 |
| Maintenance card list editors, immediate Maintenance switch and Settings maintenance wording | Headless Chrome against a throwaway local mock of the panel API, not the real server: scripted list adds and removals, id checks, a failing or unavailable Discord role list, switch on/off/cancel/failure and the Save request bodies all behaved as intended, with no page errors; `node --check` and `git diff --check` passed (2026-09-28) |

**Not verified:**
- the full test suite;
- the Maintenance card's list editors and switch against a running server (only a local mock API);
- `Start-Aurora21.ps1` on a real PC past the trust patch: its auth bridge step fails on every hosted PC (issue #15);
- Caddy and a real Let's Encrypt certificate;
- the Discord bot against real Discord;
- the hub framing the page;
- any deploy to the VPS.

The local browser check confirms direct loopback use of the panel. It did not test a
shared-hub frame, a Discord-backed player or operator writes against a hosted VPS.

The local Docker rehearsal cannot reach the launcher API or panel, because both bind the container's loopback on
purpose.

## Hosted auth bridge continuation (deployed 2026-09-28 21:50 BST)

- The hosted pack now passes `--hosted` to `Aurora21-LocalAuthBridge.exe`. Only the
  local `127.0.0.1:8090` bootstrap-profile check is skipped in this explicit mode.
  The local launcher still requires its profile. Launch-code validation, supported
  executable and CardsDLL hashes, expected native bytes and atomic patch checks
  are unchanged. No native code layout or compatibility-code values changed.
- The shared Aurora launcher needs no interface change: `runtime.json`, stdin code
  handling and the final `[READY] ... (PID n)` output stay the same.
- Rebuild the shipped helper with `Build-FIFA21-Launcher.ps1 -AuthBridgeOnly` and
  run its `--self-test`. This build mode requires the Windows .NET Framework C#
  compiler only; it does not start a server or generate local certificates/data.
- Verification: the helper-only rebuild and native adapter self-test passed. Hosted
  CLI checks rejected missing/invalid codes and still rejected an unsupported game
  path with a syntactically valid code, without echoing the code. Startup portability
  (42 checks), helper process names (34), repository hygiene (283) and local bootstrap
  self-tests (23 groups) passed. An independent source review found no blocking issue.
  The full build and launcher-settings checks also passed with a task-local .NET
  10.0.401 SDK after the default .NET 8 SDK rejected their target framework. The
  full server suite passed: 589 tests, zero failures or skips, in 25m35s against
  isolated temporary PostgreSQL state. Its test artifacts must stay inside the
  checkout so the fixture can locate the bundled player catalogue. The live game
  launch has not been run for this patch.
- Release `8e5d1ed` is on Project-21-V2 `main`. The VPS now runs
  `aurora21/ultimateteam-server:sha-8e5d1ed` through its existing `:local` alias;
  both server and redirector report image
  `sha256:6df82bcb37a939c217efc5f1ae75a1a3652d0adb717181f4abfd4483fdde61a4`.
  The public install manifest serves `sha-8e5d1ed`, replacing the blocked
  `sha-582c029` pack recorded above. Its 50,158,299-byte ZIP has SHA-256
  `6a683a4b9e26cfcfa793f718d2e3e5442bb4acc0145b9e34b01f8bb18121992c`.
  A public HTTPS download matched this hash. The packaged helper matches the
  reviewed build (`53810f6abd0c130f99fb8af5411659942cf23aba90d482008a4b75c0bdd345f2`)
  and passed its native self-test; runtime version and hosted arguments were checked.
- Activation at 20:50:36 UTC recreated only server and redirector, with zero players
  or matches. Database, backup and Caddy container identities/start times, `.env`,
  maintenance and access-policy digest were unchanged. Maintenance remains ON.
  Anonymous FUT returned 401, the redirector advertised secure Blaze, and local/public
  client health returned 200. A readable predeploy dump was saved as
  `/opt/aurora21/backups/predeploy-20260928T205033Z.dump`; rollback image tag is
  `aurora21/ultimateteam-server:before-sha-8e5d1ed` (the actual prior running image).
  Retag that image as `:local` and recreate only server/redirector to roll back code;
  an image rollback does not undo database migrations.
- Isolated startup smoke checks passed for fresh/custom settings (604 loopback
  requests). These are not hosted FUT gameplay results. The next gate is an
  early-access player reaching the FUT hub through the shared launcher, then a
  two-player match. Gameplay UDP and maths-bridge support remain unverified.

## Discord main-menu identity update (2026-09-28)

- Code `4ecb555` is on Project-21 `main`. The shared Aurora launcher `1.2.62`
  (Aurora15 `db71c7e6`) is published. Client pack `sha-4ecb555` is built and verified;
  activation is waiting for the remaining online FIFA 21 session to close.
- The launcher now passes the verified `/client/launch` response `account` as a
  child-only `AURORA21_DISCORD_USERNAME` environment value. The pack consumes and
  clears it before starting game/helper children. No username is added to arguments
  or logs; the signed proof, launch code, numeric player identity and runtime schema
  are unchanged.
- For a direct copy with `anadius.cfg`, the pack updates only its single quoted
  `Username` value before game start. It retains the original as
  `anadius.cfg.aurora21.bak`, preserves unrelated bytes, and refuses malformed or
  ambiguous fields. EA/Steam, already-running games and copies without this file
  are unchanged. A fresh launch is required to see the name.
- Verification: 45 focused shared-launcher tests, the published NativeAOT launcher
  self-test, hosted username fixtures and all 42 startup portability checks passed.
  The packaged start script matches reviewed source; the native helper hash remains
  `53810F6ABD0C130F99FB8AF5411659942CF23ABA90D482008A4B75C0BDD345F2`.
  Server logic is unchanged from the release with 589 passing server tests.
- Pack: `aurora21-client-sha-4ecb555.zip`, 50,159,223 bytes, SHA-256
  `6940436ed6983bb36c97c79cd6c0c21b5cea4a7a4aa65bdf4c592f04920b513c`.
  The in-game displayed name still needs confirmation after an updated launch.

## Still to do

Plan steps 1 (pack catalogue, store lineup and preview), 2 (the integration handoff,
panel contract, deploy guide, hosted working agreement and native hosting notes) and 4
(FIFA 21 Modes tab and resets) are implemented on this branch.

1. **Remaining live tests after the 21:50 BST release:**
   - an early-access player launches the new `sha-8e5d1ed` pack through the shared
     launcher and reaches the FUT hub; this confirms whether issue #15 is resolved in game;
   - the Discord role check with the real bot during that launch (configuration and
     preserved policy alone do not prove the member lookup);
   - the hub framing the panel (the copied page sends `frame-ancestors 'none'` and
     `X-Frame-Options: DENY`, so verify the integrated hub rather than the tunnel alone);
   - the two-player match and gameplay UDP.
2. **Release process:** the full suite passed before `8e5d1ed` was pushed to `main`.
   This checkout keeps GitHub Actions under `.github/disabled-workflows`, so the
   current VPS release is manual. Use [Manual_Deployment.md](../../../Manual_Deployment.md)
   for a uniquely versioned pack, predeploy backup, service health checks and rollback.
   Keep maintenance enabled until the in-game gates pass.
