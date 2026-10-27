> Historical reference, archived on 2026-10-01. Paths, policies, runtime descriptions and test status below describe their original checkpoint. Use the [current guides](../../README.md) for supported instructions.

# Aurora 21 integration handoff

Aurora 21's hosted C# server has the `/client/*` launcher API, Discord role access, a private
`/panel/` page and API, a downloadable Windows client pack, and the Docker/Caddy deployment
configuration. The shared Aurora launcher, account service and panel hub are separate projects;
their FIFA 21 integration and a live hosted launch are still unverified. See the current
[integration status](../../ultimateteam/aurora-integration-status.md) and
[native hosting evidence](../../ultimateteam/native-hosting-investigation.md).

## Aurora account service and launcher

Done on the Aurora15 side on 2026-09-28; the evidence and the live values are in
[aurora-integration-status.md](../../ultimateteam/aurora-integration-status.md).

1. `POST https://15.aurorafut.com/api/v1/client/fifa21/launch-proof` signs the ES256 assertion
   (issuer `https://15.aurorafut.com`, audience `aurora21-launch`, 120 s). The VPS `.env` holds
   the issuer and the public key. The claim contract stays in
   [aurora15-launcher-handoff.md](../../ultimateteam/aurora15-launcher-handoff.md).
2. Aurora launcher 1.2.61 has the FIFA 21 page: `GET /client/health`, the pack from
   `/client/install/manifest.json`, the public trust anchors, the proof, `POST /client/launch`,
   then the `runtime.json` command with the code on standard input and its cleanup. What it
   refuses is in [the launcher handoff](../../ultimateteam/aurora15-launcher-handoff.md#what-the-aurora-launcher-enforces-1261).
   `/client/presence` is not used yet.
3. **Open:** the launch stops at the pack's auth bridge, which checks the local launcher's auth
   profile on `127.0.0.1:8090` before it installs (issue #15). Until the helper has a hosted mode,
   no player gets past that step. The maths bridge is not in the hosted pack either (same issue).

Aurora 21 verifies the assertion itself, checks live Discord role and access rules, and binds
the code to that player for FUT and Blaze. It does not accept a launcher-supplied Discord ID
as proof. The old approval file and `--launch-approved-ids` option are gone.
## Panel hub and operator

The panel page and `/panel/api/*` are served from `127.0.0.1:18448`. The shared panel hub opens it
through an SSH tunnel as `deploy` (Aurora15 `tools/aurora-admin-hub`, the FIFA 21 tile). Direct
loopback access is the panel credential; do not expose this listener through Caddy. The
[panel contract](../../ultimateteam/panel-api-contract.md) lists its routes and limits. The old
`/api/v1/panel` token API is gone.

The VPS `.env` holds `POSTGRES_PASSWORD`, `PUBLIC_IPV4`, `LAUNCHER_DOMAIN`, `COMPOSE_PROFILES=launcher`,
`LAUNCH_ISSUER`, `LAUNCH_ES256_PUBLIC_KEY` and `DISCORD_BOT_TOKEN`, set on 2026-09-28;
[deploy/set-launcher-env.sh](../../ultimateteam/deploy/set-launcher-env.sh) keeps them. Maintenance, its
message, the Discord server, play roles, bypass roles, bypass accounts and blocks live in PostgreSQL
and are changed in the panel's Maintenance tab. On 2026-09-28 maintenance is on, with the
Whitelisted (FIFA 21) role and one account as bypass. GitHub Actions deploys are disabled; deploys
are manual per [Manual_Deployment.md](../../Manual_Deployment.md), built on the VPS with a
`CLIENT_VERSION` per commit.
## Verification boundary

Observed on 2026-09-28: the account-service proof (a code was issued for a real launch), the
client pack download and its hash, the Discord bot against the real server (role list, "Test a
player"), the public HTTPS certificate on `21.aurorafut.com`, the panel through the hub's tunnel,
and the trust patch on a player's PC. Not yet observed: the auth bridge in hosted mode (issue
#15), a real FIFA 21 hosted launch through to the FUT hub, the hub framing the panel page, and
two-player gameplay. A successful server build or isolated route test does not establish native
playability.