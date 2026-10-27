# Shared Aurora launcher → Aurora 21

Aurora 21 implements the hosted server side. The shared account-service proof
endpoint and launcher FIFA 21 page were recorded as implemented on 2026-09-28;
see [integration status](aurora-integration-status.md) for results and open gates.
This document defines their interface, rather than current deployment state. The running server identifies a player only after verifying a
signed account-service assertion; an address or launcher-supplied Discord ID is never proof.

## Account service: FIFA 21 launch proof

The account service owns the authenticated `fifa21/launch-proof` action and its
response shape. After checking its own
Discord session, issue a fresh compact JWT (JWS) for the session's verified account:

| Part | Required value |
| --- | --- |
| `alg` | ES256 with P-256 (preferred), or HS256 with a secret dedicated to Aurora 21 |
| `iss` | Exact issuer configured on the Aurora 21 server |
| `aud` | `aurora21-launch` |
| `sub` | The verified Discord ID, 5–32 decimal digits |
| `iat`, `exp` | Integer Unix seconds; `exp` at most 120 seconds after `iat` |
| `jti` | Fresh base64url identifier, 22–128 characters |
| `nbf`, `scope`, `name` | Optional; `scope` must be `aurora21.launch` when present |

ES256 uses the 64-byte IEEE P1363 `(r || s)` signature. Give us the exact issuer and SPKI
public key; the private key remains on the account service. If ES256 is unavailable,
transfer a new Aurora 21-only HS256 secret of at least 32 UTF-8 bytes privately. Aurora 21
pins the configured algorithm, rejects expired and replayed assertions, and accepts an `iat` no more
than 120 seconds old or 30 seconds ahead. The in-memory `jti` replay record clears on a
server restart, so assertions stay short-lived and travel over HTTPS.

## Launcher: FIFA 21 page

1. Poll `https://<LAUNCHER_DOMAIN>/client/health?discord=<id>&name=<name>` for server status,
   player count, maintenance/refusal reason, game address and install version.
2. Install the versioned ZIP named by `/client/install/manifest.json`, verifying its SHA-256.
   Fetch `/client/trust-anchors/manifest.json` and its public files into
   `{install}\trust-anchors`; keep private server keys off the client.
3. Request a fresh proof from the account service and send
   `POST /client/launch` with JSON
   `{ "discordId": "<id>", "discordName": "<name>", "launcher": "<version>", "assertion": "<JWS>" }`.
   Only a `200` response with `ok`, `code`, `expiresInSeconds`, `personaId` and
   `gameAddress` permits Play. `401` means invalid proof; `403`, `429` or `503` are refusals.
4. Run the downloaded `runtime.json` command elevated, passing the returned 40-character
   game code on standard input and `gameAddress` as the server IPv4. Wait for `[READY]`,
   surface `[FAILED]`, then run `cleanup` after FIFA exits. The script starts FIFA the way
   the copy needs: through the EA app for an EA app install, through Steam for a Steam copy,
   and directly and suspended for anything else, including an emulated copy. It then patches
   trust in memory, bypassing the Origin client bootstrap on a direct launch. Last, it calls the
   auth bridge with `--hosted` and manages the hosts entry. Hosted mode skips only
   the local bootstrap-profile check; launch-code and game-build checks remain.
   Optionally add `"friends": ["<discordId>", ...]` (the signed-in account's launcher friends, at most 200).
   The server keeps the list from the last verified launch. Two players whose launchers list each other are paired
   with each other first in FUT online Friendlies (Play Online, a shared house-rule game type), and a search is
   kept from strangers for 20 seconds while a friend is online. FIFA's own friends list and invites come from the
   EA app and cannot show launcher friends.
5. Use `GET|POST /client/presence` for friends. If available, send the PC's
   `X-Aurora21-Windows` build/revision and `X-Aurora21-Math` report on launch so online
   searches pair compatible Windows maths groups.

`POST /api/v1/launch/code` remains a minimal assertion-to-code route on the same loopback
listener; the shared launcher should use `/client/launch`. Keep assertions, codes, tokens and
keys out of command lines, URLs, settings and logs. The local launcher's synthetic test
code is not a hosted credential.

## Values to exchange and tests

Aurora15 supplies the exact issuer, ES256 public key (or the dedicated HS256 secret),
launch-proof endpoint contract and a launcher test build. If Aurora15 operates the
shared Discord bot, arrange private transfer of its token. Confirm the Discord
server ID and intended play/bypass role IDs. We supply the launcher API hostname,
database password, VPS access and GitHub deployment variables. See the
[integration status](aurora-integration-status.md) for recorded coordination.

`serve --hosted --launch-api-port 18447` binds HTTP to loopback. The default Docker stack
also enables the private panel on 18448 and serves the client pack. Caddy publishes only
`/client/*` and `/api/v1/launch/*` over the configured `LAUNCHER_DOMAIN` with a public HTTPS
certificate. Put `LAUNCH_ISSUER` and exactly one of `LAUNCH_ES256_PUBLIC_KEY` or
`LAUNCH_HS256_SECRET` in the VPS `.env`; set `DISCORD_BOT_TOKEN` for role decisions. The
operator sets Discord server ID and play/bypass roles in the panel. There is no approval
file. Launches require the configured issuer and one matching signing key.

First test a signed launch, a wrong signature, replay, expiry, role denial and a block with
synthetic accounts; then test the real account-service proof and client pack on a PC, and
finally two players and gameplay UDP on the VPS. No real hosted FIFA launch has been
verified. See [integration status](aurora-integration-status.md) for the current gates.

## What the Aurora launcher enforces (1.2.61)

The shared launcher's FIFA 21 page (Aurora15 repository, `client/Aurora15Connector/Fifa21Session.cs` and
`Fifa21Runtime.cs`) implements the steps above. It refuses anything outside the rules below, so keep changes to the
client pack, `runtime.json` and the `/client/*` replies inside them, or change the launcher first. These are observed in
its unit tests and in a live probe against `21.aurorafut.com` on 2026-09-28; a real FIFA 21 launch through it is not
yet observed.

- **Server.** `https://21.aurorafut.com`. A loopback `host:port` setting is plain HTTP, for local tests only.
- **Health.** Polled about every 20 s with `discord` and `name`. Play is offered only when `maintenance.reason` is empty,
  `maintenance.enabled` is false or `maintenance.bypass` is true, `blaze`, `redirector` and `ut` are true, and
  `trustAnchors` is true. `gameAddress` must be an IPv4 address. `maintenance.message` is shown as one line of at most 300
  characters.
- **Install.** Manifest `schema` 1. `version` matches `^[A-Za-z0-9._-]{1,64}$`. The launcher uses the first file that
  ends in `.zip` with `kind` `runtime` or no `kind`. Its name must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$`, its size
  must be at most 1 GiB, and its `sha256` must be 64 hex characters. The zip is streamed to disk, its size and SHA-256 are
  checked, and it is unpacked into `fifa21\runtime-<version>`. Entries escaping the folder are refused. `runtime.json` must
  sit at the zip root. The manifest is checked again before every Play, and a new `version` reinstalls.
- **Trust anchors.** Every manifest file must be `.bin`, `.pem`, `.crt` or `.cer`, at most 64 KiB, with a unique name.
  `local-gos2015-modulus.bin` and `local-gs2019-modulus.bin` must be present. Each file is checked against its SHA-256.
  A file containing `PRIVATE KEY` is refused, and files no longer listed are removed from `{install}\trust-anchors`.
  `runtime.json`'s `trustAnchors` block is not read; the launcher uses the fixed `/client/trust-anchors/` paths.
- **Proof and launch.** The launcher fetches the account service's proof immediately before `POST /client/launch`, then
  sends `{discordId, discordName, launcher: "<launcher version>", assertion}` with `X-Aurora21-Windows: <build>.<UBR>`.
  `X-Aurora21-Math` is not sent. A 200 must carry `ok: true` and a `code` matching `^[0-9a-f]{40}$`. `gameAddress` must be
  IPv4; the launcher falls back to the health reply's address. A refusal shows the reply's `message` (one line, at most 240
  characters), or a line chosen from the `error` word. A 429 honours `Retry-After`, from 1 s to 1 h, 60 s by default.
- **`runtime.json`.** `schema` must be 1.
  - `command.file` must be `powershell.exe`, which runs from `System32\WindowsPowerShell\v1.0`, with `-File` naming a
    script inside the pack. The launcher refuses `-Command`, `-c`, `-EncodedCommand`, `-enc`, `-ec` and `-e`. The only
    other allowed `file` is a program inside the pack.
  - Placeholders are `{install}`, `{game}` (the full path of `FIFA21.exe`), `{gameAddress}` and `{logs}` (the launcher's
    `logs\fifa21` folder). Any other `{name}` is refused.
  - `stdin` must be empty or `code`. The code and a newline are written, then standard input closes. The code never goes
    on a command line, in a file or in the log.
  - `ready` and `failed` default to `[READY]` and `[FAILED]`. `readySeconds` is clamped to 30 to 900; cleanup defaults to
    60. Past the limit the script is ended and cleanup runs.
  - `[INFO] text` lines are shown as progress. The game's pid is read from `(PID n)` in the ready line.
  - After ready, the launcher watches `FIFA21` and `FIFA21_Trial`. It runs `cleanup` when they exit, when the command
    fails, and when the launcher closes.
  - The launcher itself runs elevated. The steps run as its children, with no window.
- **Not used yet.** `/client/presence` (the friends list) and `X-Aurora21-Math`. The launcher needs neither for the
  maths bridge: the client pack's `Aurora21-MathBridge.exe`, run by `Start-Aurora21.ps1`, sends `X-Aurora21-Math` itself
  to `POST /client/launch/math` with the launch code (the code is not used up by FIFA's login).