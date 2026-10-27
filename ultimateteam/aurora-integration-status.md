# Hosted integration status

Reviewed 2026-10-01 against the current checkout and recorded evidence. This is
not a fresh probe of the VPS, account service or shared launcher.
[The dated September report](../archive/documentation-2026-10/ultimateteam/aurora-integration-status-2026-09-28.md)
retains release hashes, deployment times, test results and the original handoff.

## Implemented in this repository

- `serve --hosted` requires native session identity, TLS for advertised game
  services and LSX off. FUT and Blaze verify player-bound codes before club access;
  later FUT requests use a random `X-UT-SID`. An IP/persona claim is never identity.
- `/client/*` verifies short-lived ES256 or dedicated HS256 account-service
  assertions, applies live PostgreSQL/Discord access policy, and issues game codes.
- The versioned Windows client pack contains `Start-Aurora21.ps1`, the trust patch
  server executable and `Aurora21-LocalAuthBridge.exe`. Codes travel on stdin.
- The hosted script passes `--hosted`: the helper skips the player's loopback
  bootstrap check on 8090, while preserving code, game-build and patch checks.
  The local launcher still requires its bootstrap profile.
- The React panel is the only UI at `/panel/`, using private `/panel/api/*` routes.
  Existing `/panel/next/` bookmarks redirect. The classic UI has been removed.
- Docker provides server, TLS redirector, PostgreSQL, backups, optional Caddy and an
  optional loopback-only Prometheus/Grafana stack. The checked-in workflows build and
  deploy only on a `main` push.

## Recorded external results

The September report records the account-service proof endpoint and shared
Aurora launcher 1.2.61 FIFA 21 page as implemented. It also records a real
`/client/launch` code issuance, public HTTPS pack downloads, bot/tunnel checks,
and the trust patch on a player's PC. These records do not prove current VPS
health or that a player completed hosted FUT entry.

The first hosted launch with pack `sha-582c029` failed at the helper's local
bootstrap check. Commit `8e5d1ed2` added hosted mode. The report records deployment
of `sha-8e5d1ed` at 21:50 BST on 2026-09-28, with the public pack hash verified.
On 2026-10-01 the current bundled helper self-test passed and printed the hosted
bypass marker; no FIFA process was opened or modified. A real hosted launch
past this fix remains unconfirmed here.

Local panel acceptance and the React replacement checks are recorded in
[game acceptance](panel-web/test/game-acceptance.md). Local game results are not
hosted multiplayer results.

## Remaining acceptance

- [ ] A player launches the updated client pack through the shared launcher and
  reaches the FUT hub, with verified FUT and Blaze identity in redacted logs.
- [ ] Record the real role lookup during that launch, denial/block behavior and
  expired/replayed proof handling through the complete shared-launcher path.
- [ ] Verify hosted certificate validation and refusal of a wrong certificate.
- [ ] Verify the current React panel through the private tunnel and shared hub.
  The server emits `frame-ancestors 'none'` and `X-Frame-Options: DENY`; a
  successful tunnel request alone does not prove iframe integration.
- [ ] Confirm the direct-game Discord display-name update in the native menu.
- [ ] Test a VPS two-player match, gameplay UDP/QoS, compatibility maths groups,
  same-NAT behavior and disconnect/reconnect. A single-PC fixture cannot do this.
- [ ] Trace FIFA's query-form `/connect/token` call and response consumer before
  changing its observed 400 behavior.

Keep maintenance enabled until the agreed in-game gates pass. Do not treat an
old image/pack version in a report as the currently deployed version.

## Contracts and operation

| Area | Guide |
| --- | --- |
| Shared launcher and proof claims | [Launcher contract](aurora15-launcher-handoff.md) |
| Native evidence and open networking gates | [Hosting investigation](native-hosting-investigation.md) |
| Docker, backups and release workflow | [Deployment](deploy/README.md) |
| Manual release fallback | [Manual deployment](../Manual_Deployment.md) |
| React routes, API and content publication | [Panel API](panel-api-contract.md) |
| Local development and verification | [Contribution guide](../CONTRIBUTION.MD) |

Operator configuration belongs in the VPS `.env` and PostgreSQL access settings,
not in these documents. Preserve its certificates, backups and club state across
code updates. Supply a unique `CLIENT_VERSION` when releasing a new pack so the
shared launcher installs it on the next Play.
