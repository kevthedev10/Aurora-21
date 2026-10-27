# Deploying the hosted server with Docker

Work in progress: a real hosted FIFA 21 launch has not been verified. The server-side `/client/*` API,
client pack and private React panel are built. The shared account-service proof
endpoint and launcher page have recorded integration results; real hosted FUT
entry and a two-player VPS test remain unconfirmed. See
[integration status](../aurora-integration-status.md) for dated release evidence. The native gates are in
[the native hosting handoff](../native-hosting-investigation.md).

The optional, loopback-only launch-code API and the Aurora15 account-service assertion
contract are documented in [the launcher handoff](../aurora15-launcher-handoff.md).
The loopback-only panel page and API for clubs, packs, modes and live settings are
documented in [the panel contract](../panel-api-contract.md). The default compose file
enables them on 127.0.0.1:18448 for an SSH tunnel, never on the public launcher proxy.

## What runs

One image (`Dockerfile`, built from `ultimateteam/`) with the server and the bundled game data. It runs as two services:

- **`server`:** `serve --hosted`. This implies session identity (a verified code for FUT and Blaze, never an address),
  TLS for every advertised service, the advertised host name `spring18.gosredirector.ea.com`, and LSX off.
  - TLS listeners: FUT/Nucleus on 18443, Blaze on 18444, EASW on 18445, SSF on 18446.
  - QoS: 17502 TCP and UDP.
  - The plaintext game listeners bind to the container's loopback only.
- **`redirector`:** `redirector --blaze-tls-port 18444`. The TLS redirector on 42230 tells FIFA to use secure Blaze.
- **`db`:** the official `postgres:18` image with its own volume. The server reaches it through
  `ULTIMATETEAM_POSTGRES_CONNECTION`.
- **`backup`:** the same `postgres:18` image running [`pg-backup.sh`](pg-backup.sh): a daily dump of the whole database
  and a dump on panel request (see [Backups](#backups)).
- **Restart worker:** a host-side process running [`restart-worker.sh`](restart-worker.sh). It watches one fixed request
  in `restart-requests/` and runs `docker compose restart server`. The panel exposes the button only while its heartbeat is fresh.
- **`caddy`:** enabled when `LAUNCHER_DOMAIN` is set. It obtains a public HTTPS certificate
  on TCP 80/443 and forwards `/client/*` and `/api/v1/launch/*` to the server's
  loopback-only port 18447. The panel on 18448 stays private.
- **Monitoring:** enabled when `GRAFANA_ADMIN_PASSWORD` is set. Prometheus, node-exporter, postgres-exporter and
  Grafana, all on the VPS's loopback only; see [Monitoring](#monitoring).

Volumes: `certs` (the server's CAs and leaf certificates), `state` (logs, content cache, state files and
`config/config.json`), `db-data`, and with monitoring `prometheus-data` and `grafana-data`. Backups go to a folder on the VPS (`BACKUP_DIR`), not a volume.

## Game-connection ping limit

**Off by default** (`max_ping_ms: 0`). When set, `serve --hosted` on Linux refuses a
new Blaze/game login whose kernel-measured smoothed TCP round-trip time is above the
limit (exactly the limit is allowed). The check runs after the login is identified and
before the player's club opens. A Blaze **resume is never refused**: a player
reconnecting into a live match after a network stall is exactly when the RTT is worst.
It sends no ICMP and does not kick an active game for a later spike. It measures the
direct TCP hop to this VPS, not the opponent or the UDP gameplay path; behind a
TCP-terminating proxy or filter it measures the proxy hop instead.

To turn it on, preserve the other keys in the state volume's `config/config.json` and set
`"connection_limits": { "max_ping_ms": 250 }` (integers 0-60000; 0 = off), then restart.
Non-hosted/local mode never applies it.

Rejected logins log `BLAZE LATENCY REJECT` with the player, the measured and configured values.
An unavailable/unsupported measurement is **allowed**, not treated as high ping, and
logs `BLAZE LATENCY UNAVAILABLE`. Consequently this is a best-effort admission gate,
not a guarantee that every unmeasurable or subsequently congested player is blocked.
Launcher downloads, the launcher API and the operator panel remain accessible.

## Idle-connection keepalive experiment

Hosted Compose deployments enable this automatically, including existing state
volumes: `ULTIMATETEAM_TCP_KEEPALIVE_IDLE_SECONDS` overrides only the configured
idle time, with Compose using 30 seconds unless `.env` sets
`TCP_KEEPALIVE_IDLE_SECONDS`. Interval and retry settings still come from
`config.json` or the bundled defaults (10 seconds and 3 retries). Deploys preserve
the existing config and `.env`. Standalone servers retain their config defaults.

The 2026-10-06 paired menu captures show unanswered sends on old
FUT/Blaze connections after idle, followed by working replacements. They support a
connection-specific transport failure; they do not identify the network component
responsible or establish that keepalive fixes it.

Deploying a build containing the environment override and updated Compose file
is sufficient; no manual VPS config edit is required. For a standalone server
without the deployment override, preserve the other keys in `config/config.json`
and add:

```json
"tcp_keepalive": {
  "idle_seconds": 30,
  "interval_seconds": 10,
  "retry_count": 3
}
```

An operator-approved restart is required. This applies to newly accepted hosted
FUT, Blaze, EASW and SSF TLS sockets, before TLS processing. Local/plaintext,
redirector, panel and launcher API sockets retain their defaults. It sends TCP
probes, not FIFA application messages, and does not extend HTTP, Blaze or auth
session lifetimes. Unanswered probes can cause the OS to close a dead connection.
It does not change request retransmission timeouts when data is already pending.

Startup logs `TCP_KEEPALIVE_CONFIG` per affected listener; `TCP_KEEPALIVE_FAILED`
means a connection could not receive the requested settings. Confirm actual probes
and replies in a paired capture, rather than treating the startup log as proof.
Keep the capture running through at least 45 seconds of idle and the first menu
action. Compare several idle attempts with the disabled baseline. The acceptance
condition is acknowledged idle probes and prompt first actions on usable
connections; a quick action immediately after a restart is insufficient evidence.
Use the same previously troublesome idle duration where it fits the recording.

Local Docker verification on 2026-10-06 passed image build, isolated certificate
setup/startup, FUT without a session returning 401, secure Blaze advertisement,
and content returning 200. The saved config retained idle_seconds=0 while the
environment enabled 30/10/3 on hosted game TLS listeners. Recreating the server
with the override set to 0 disabled it on the same volume. All 21 focused tests
also passed in Linux, including reading the actual accepted-socket options.
VPS idle probe exchanges and in-game stall improvement remain unconfirmed.

Hosted rollback: set `TCP_KEEPALIVE_IDLE_SECONDS=0` in the VPS `.env` and run
`docker compose up -d server` to recreate the container with that environment.
`docker compose restart` alone does not apply changed environment variables.
Standalone rollback: set `tcp_keepalive.idle_seconds` to `0` and restart. No
existing config file is rewritten. Valid ranges are
idle 0–3600 seconds, interval 1–300 seconds, and retry count 1–10.

## GitHub Actions: build and deploy

The checked-in workflow builds and publishes only on a `main` push:
`ghcr.io/<owner>/aurora21-ultimateteam-server:sha-<7>` and `:main`, then calls the
production deploy workflow. Other branches, tags, pull requests and manual runs build
nothing. Both workflows retain a main-push guard for deployment.
No tests run on GitHub; run the server suite locally before merging.

The deploy job:

1. Copies `compose.yaml`, `pg-backup.sh`, `restart-worker.sh`, `remote-deploy.sh`, `Caddyfile`, `.env.example` and the
   `monitoring/` folder to the deploy folder on the VPS (default `/opt/aurora21`). The operator's `.env` stays there and
   is never copied or overwritten. No GitHub secret or variable is needed for monitoring.
2. Logs the VPS in to GHCR with the job's short-lived token, then runs [`remote-deploy.sh`](remote-deploy.sh):
   - installs and starts the restart worker's persistent user service, configures its shared group, and checks a fresh heartbeat;
   - pulls the image and records `IMAGE`/`IMAGE_TAG` in `.env`;
   - stops if the certificates are missing;
   - takes a `predeploy` database dump;
   - runs `docker compose up -d`;
   - waits up to two minutes for FUT to return 401 without a session, the redirector to return secure Blaze,
     and `/client/health` to answer on loopback;
   - with monitoring on, reloads Prometheus and warns (without failing) if Prometheus or Grafana does not answer.
3. Logs the VPS out of GHCR, even when a step fails.

A failed health check prints the server log and fails the run. **Rolling back** means
reverting the change on `main`; that push deploys the reverted code. Migrations
are not undone, which is what the `predeploy` dump is for.

### One-time GitHub setup

In **Settings → Environments**, create `production`:

- Set the deployment branch rule to **Selected branches: `main`**. Optionally add required reviewers.
- Add two secrets:
  - `VPS_SSH_KEY`: the private key of the deploy user.
  - `VPS_KNOWN_HOSTS`: the VPS host key line, from `ssh-keyscan -t ed25519 <host>`. Check its fingerprint against
    `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the VPS.
- Add these variables:
  - `VPS_HOST` and `VPS_USER`;
  - optionally `VPS_PORT` (default 22) and `VPS_DEPLOY_DIR` (default `/opt/aurora21`).

### One-time VPS setup (Ubuntu)

1. Install Docker Engine with the Compose plugin, plus `curl`.
2. Create a deploy user in the `docker` group; that group is effectively root on the host. Add the public half of
   `VPS_SSH_KEY` to the user's `~/.ssh/authorized_keys`, and create the folder:
   `sudo install -d -o <deploy user> /opt/aurora21`.
   Enable its user services after logout and boot once: `sudo loginctl enable-linger <deploy user>`.
3. Firewall: allow TCP 80/443 for Caddy, TCP 42230 and 18443-18446, TCP and UDP 17502,
   and the relay's UDP range (31000-31999 by default, `RELAY_PORT_START`/`RELAY_PORT_END`), plus SSH. Ports 18447/18448,
   PostgreSQL and the monitoring ports (3000, 9090, 9100, 9187) stay on the VPS's loopback only.
4. After the GitHub `production` environment is configured, merge to `main`. The first
   deploy copies the files and stops because `.env` is missing. This is the expected
   first-run gate; it does not start or replace the game services.
5. In `/opt/aurora21`: `cp .env.example .env`, set file mode `600`, then set
   `POSTGRES_PASSWORD` (long and random), `PUBLIC_IPV4`, and `LAUNCHER_DOMAIN` (DNS A
   record to that IPv4). These three values bring the server and public HTTPS
   `/client/*` API online. Leave `LAUNCH_ISSUER`, `LAUNCH_ES256_PUBLIC_KEY`,
   `LAUNCH_HS256_SECRET`, and `DISCORD_BOT_TOKEN` empty until available: health and
   install endpoints work, but launch requests are refused. Do not configure play
   roles without a working Discord bot. Re-run the failed GitHub deploy job; it pulls
   the image and stops because the certificate volume is empty.
6. Create the game-server certificates once (an existing set is kept unless `--force`):
   `docker compose run --rm server generate-certs`
7. Export the public trust anchors for diagnostics (never a private key):
   `docker compose run --rm --user root -v "$PWD/trust-anchors:/export" server export-trust-anchors --out /export`
   The shared launcher fetches the same public anchors through `/client/trust-anchors/manifest.json`.
8. Re-run the failed GitHub deploy job once more. It starts the stack and checks
   FUT, the TLS redirector, and loopback `/client/health`. Check public HTTPS
   `/client/health` separately; the deploy health check does not test Caddy or DNS.

When Aurora15's account-service signer is ready, add `LAUNCH_ISSUER` and exactly one
of `LAUNCH_ES256_PUBLIC_KEY` or `LAUNCH_HS256_SECRET` to the VPS `.env`. Add
`DISCORD_BOT_TOKEN` before enabling play roles. Applying changed environment values
requires a controlled server container restart or a later deploy; editing `.env`
alone does not update an already-running process.

### Panel restart worker

The panel queues only a restart of the `server` service. The worker runs on the VPS host as the deploy user so the
server container never receives the Docker socket. Deployment copies `restart-worker.sh` beside `compose.yaml`,
then runs `bash ./restart-worker.sh install`. This writes and enables
`~/.config/systemd/user/aurora21-restart-worker.service` with the actual deploy path, restarts the worker and requires
a fresh heartbeat. Existing manually created units at that path are replaced. Installation requires the deploy
user's Docker access and a systemd user manager; if enabling lingering is refused, run
`sudo loginctl enable-linger <deploy user>` once and rerun deployment. No passwordless sudo is needed by the worker.

The installer records the deploy user's numeric primary group as `RESTART_WORKER_GID` in `.env`. Compose adds that
supplementary group to the unprivileged `app` user. `restart-requests/` is mode `2770` with that group, and worker
files use umask `0027`, so the server can read the heartbeat and create requests without world access. The previous
`0700` directory and `0077` worker umask prevented this cross-user access. The group is the deploy user's primary
group, never the Docker group. Deploys and restarts share a lock so a queued restart waits until deployment completes.
After an interrupted worker, an already claimed request is marked `interrupted` and cleared rather than replayed.

For a manual checkout deployment, run `bash ./restart-worker.sh install` as the deploy user before `docker compose up`.
On an existing VPS, deploy the updated scripts and Compose file and rerun deployment; merely restarting the old
container does not apply the new supplementary group. For script regression checks without a VPS or Docker daemon,
run `python3 ultimateteam/deploy/test-restart-worker.py` from the repo root (all commands/services are mocked in temp folders).

Check `systemctl --user status aurora21-restart-worker.service`; `GET /panel/api/maintenance` reports
`restart.available: true` when the heartbeat is fresh. A confirmed panel request returns 202, then the worker
waits three seconds and runs `docker compose restart server`. The response does not wait for a healthy server.
This restarts the current image and configuration; it does not deploy a new image. Connected players are dropped.

After startup, tunnel the private panel from the hub to `127.0.0.1:18448/panel/`.
Set the Discord server ID, play roles, bypass roles, maintenance and player cap there.
With no play roles every verified account may launch; a configured role check with no
working bot refuses launches. The React page's frame headers still need a real hub
embedding test. A live Discord role lookup, public HTTPS certificate, client pack launch
on a PC and two-player match remain separate verification gates.

The server and redirector use host networking, because the gameplay relay needs a whole UDP port range. Regenerating
the certificates (`generate-certs --force`) locks every installed launcher out until players get the new trust anchors.

To build on the VPS instead, without GitHub: from a checkout's `ultimateteam/deploy/`, leave `IMAGE` and `IMAGE_TAG`
empty in `.env`, then run `bash ./restart-worker.sh install`, `docker compose build`, the certificate steps above,
and `docker compose up -d`.

## Capacity

`compose.yaml` is sized for the production VPS (12 vCPU, 24 GB, NVMe); the `PG_*` and `PLAYER_MAX_OPEN_CLUBS` values in
`.env` adjust it for another machine.

- **Players online at once:** `--player-max-open-clubs` (`PLAYER_MAX_OPEN_CLUBS`, default 1000). A player's Blaze
  connection keeps their club open for the whole session, so this is a hard cap on concurrent players; the server
  refuses the next one with "player capacity reached". Idle clubs beyond it close least-recently-used first. Memory is
  roughly 6-10 MB per open club (estimated from the 100-club benchmark, not measured on the VPS).
- **PostgreSQL:** `max_connections` 200, `max_locks_per_transaction` 256 (opening a club locks many tables in one
  transaction; 64 runs out), `shared_buffers` 6 GB, `effective_cache_size` 16 GB, `work_mem` 16 MB, a 1 GB `/dev/shm`
  and NVMe planner costs. Changing them restarts PostgreSQL on the next deploy, after the `predeploy` dump.
- **The server's connection pool** is `database.max_pool_size` in `config.json` in the `state` volume, created once from
  the defaults (10) and never overwritten by a deploy. For more than a handful of players, raise it once, then restart
  the server; it refuses to start if the pool does not fit under `max_connections` minus `connection_reserve`:

  ```sh
  cd /opt/aurora21
  docker compose run --rm --no-deps -T --entrypoint sh server -c \
    'sed -i "s/\"max_pool_size\": [0-9]*/\"max_pool_size\": 40/" /data/state/config/config.json && grep max_pool_size /data/state/config/config.json'
  docker compose up -d --force-recreate server
  ```

  The server logs `DATABASE_LIMITS pool=40 serverMax=200 ...` at start-up.
- **Player limit:** until a load test on the VPS has measured real capacity, set a player limit in the panel's
  Maintenance tab (for example 250). 0 means no limit. Watch the Grafana server, PostgreSQL and UDP-error panels during
  the first busy sessions.
- **Logs:** Docker keeps at most five 20 MB files per service, and the server rotates `logs/ultimateteam.log` to
  `.1` at 100 MB (`--log-max-mb`).

## Monitoring

Optional and private. Set two long random values in the VPS `.env`, then deploy (or run `docker compose up -d` after
`remote-deploy.sh` has set `COMPOSE_PROFILES`):

```sh
GRAFANA_ADMIN_PASSWORD=<openssl rand -hex 32>
MONITOR_DB_PASSWORD=<openssl rand -hex 32>
```

The `monitoring` profile then runs, all with host networking and bound to 127.0.0.1:

| Service | Port | What it does |
| --- | --- | --- |
| `prometheus` | 9090 | Stores metrics (`PROMETHEUS_RETENTION`, default 30 days, capped at `PROMETHEUS_RETENTION_SIZE`, default 10 GB) and receives the server's OTLP push. |
| `node-exporter` | 9100 | VPS CPU, memory, disks, network and UDP counters. |
| `monitor-role` | — | One-shot: creates or updates the read-only `aurora_monitor` role (`pg_monitor`) with `MONITOR_DB_PASSWORD`. |
| `postgres-exporter` | 9187 | PostgreSQL statistics as `aurora_monitor`; it cannot read club tables. |
| `grafana` | 3000 | Dashboards, provisioned from [`monitoring/grafana`](monitoring/grafana). |

The server exports its own metrics only while `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` is set; `compose.yaml` sets it
from `GRAFANA_ADMIN_PASSWORD`, so turning monitoring off also stops the export. It sends FUT endpoint latency and
status, database unit-of-work stages, the gameplay relay, the Npgsql pool, Kestrel connections and .NET runtime metrics
every 15 seconds. Labels are route, stage and drop-reason names only; per-query timings are not exported.

The relay counts forwarded and dropped datagrams, stalls (a gap of 200 ms or more in one player's datagrams during
play), shared stalls (both players' datagrams resuming together, which means they were held at the server's end) and
players who went silent mid-game. It also records FIFA's own latency and packet loss to the opponent (from the
clients' ConnectionTelemetry reports) and players whose gameplay arrives from another address of their network than
their Blaze connection (Cloudflare WARP, carrier NAT pools; accepted within the same /16 while the Blaze address sends
nothing). The same events are in the server log as `GAMEPLAY RELAY SHARED STALL`, `STALL` (1 s or longer, and a
player's 1st, 10th and 100th), `PLAYER SILENT`, `SPLIT SOURCE`, and a `quality=` summary on each
`GAMEPLAY RELAY RELEASED` line (per player: stalls, slow seconds of play, `latencyMs=median/max` and `lossPct`).

Open Grafana through an SSH tunnel, as with the panel; never publish it through Caddy:

```sh
ssh -N -L 3000:127.0.0.1:3000 deploy@<vps>
```

Then browse to `http://127.0.0.1:3000` and sign in as `admin`. The **Aurora 21** folder holds three dashboards:
server, host and PostgreSQL. They are read-only in Grafana; to change one, edit it, export its JSON into
`monitoring/grafana/dashboards/`, and deploy. A deploy overwrites the files in place, so delete a removed dashboard on
the VPS as well. `GRAFANA_ADMIN_PASSWORD` applies when the `grafana-data` volume is created; change it later in
Grafana (or `docker compose exec grafana grafana cli admin reset-admin-password <new>`).

## Panel images

The operator panel shows card frames, player and special-card art, club badges, league logos and nation flags. They
live in the repository's `ultimateteam/panel-assets/` folder, outside the Docker
image build context. Update missing images on your PC with
`ultimateteam/download-card-images.py`; it reads the card, club, league and nation ids from your local database
(start Aurora 21 first) and resumes where it stopped. Then copy the folder to the VPS:

```sh
rsync -a "ultimateteam/panel-assets/" deploy@<vps>:/opt/aurora21/panel-assets/
```

Run that copy command from the repository root. `compose.yaml`
mounts `PANEL_ASSETS_DIR` (default `./panel-assets`, next to `compose.yaml`) read-only at `/panel-assets`, and the server
serves it on the private panel port only (`/panel/assets/...`). A copy takes effect without a restart. Repeat the
download and copy when new cards are added; without images the panel shows text.

## Backups

The `backup` service writes a dump every day at `BACKUP_HOUR_UTC` (default 03:00 UTC) into `BACKUP_DIR` (default
`/opt/aurora21/backups`). It keeps the newest `BACKUP_KEEP_DAILY` dumps (default 7, one week).

- **Format:** `pg_dump` custom format of the whole `ultimateteam` database: clubs, operator edits, action receipts
  and migration history. Files are `daily-<UTC time>.dump`, readable only by root (mode 0600).
- **Checks:** each dump must list as a readable archive before it counts. A failed night leaves the previous dumps
  alone, and a restart after a missed night dumps at once.
- **Deploys:** each deploy also writes a `predeploy-<UTC time>.dump`; the newest 5 are kept.
- **Manual dump:** `docker compose run --rm backup once manual`.
- **Panel request:** the backup service watches `state/backup-requests` and writes a
  verified `manual-<UTC time>.dump` when the panel asks. A failed request does not prune
  good dumps.

**Restore** (this replaces the live database; stop the game services first):

```sh
cd /opt/aurora21
docker compose stop server redirector
docker compose run --rm --no-deps -T --entrypoint bash backup -c \
  'pg_restore --clean --if-exists --no-owner --exit-on-error -d ultimateteam /backups/daily-<time>.dump'
docker compose up -d
```

The backups live on the same VPS. They protect against mistakes and bad upgrades, not against losing the VPS; copy
`BACKUP_DIR` off the machine as well if that matters.

## One-PC rehearsal (Docker Desktop on Windows)

`compose.local.yaml` publishes the same services on 127.0.0.1, where the hosts entry already points
`spring18.gosredirector.ea.com`. From `ultimateteam/deploy/`:

1. `docker compose -f compose.local.yaml build`
2. `docker compose -f compose.local.yaml run --rm server generate-certs`
3. `docker compose -f compose.local.yaml up -d`

Add `--profile monitoring` to `up -d` to rehearse monitoring: Grafana on `http://127.0.0.1:3000` (`admin` /
`local-rehearsal-only`) and Prometheus on `http://127.0.0.1:9090`. node-exporter there reports Docker Desktop's VM.

Stop the normal local UltimateTeam first, because it uses the same ports. Docker Desktop translates the game's UDP
addresses, so the gameplay relay cannot be rehearsed here: a paired match is only tested on the VPS. The game can only
use this rehearsal once the launcher has its remote-server mode.
