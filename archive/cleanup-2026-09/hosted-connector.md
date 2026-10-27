# Hosted connector: the local bridge

Alternative design for `plan.md` section 4 (a driver-free client connection path). Status: **design, not built**
(2026-09-27). The matched FIFA 21 build has since reached the FUT hub with native HTTPS for FUT and
`nucleusConnect`, separately and together, plus a synthetic player-specific code verified by Blaze and FUT.
The native results and remaining gates are in `localfut/native-hosting-investigation.md`. Nothing in this bridge
design has been run against a hosted server.

## When to build this alternative

Complete the native gates first: hosted certificate validation, Blaze TLS, HTTPS-only credential routes and a
two-client gameplay UDP trace. If FIFA's native UDP or another required channel cannot establish player identity
and confidentiality, build the smallest bridge that covers that channel and measure latency and jitter in an
actual match. A local hub screen and the loopback TLS probe do not settle that decision. The bridge would still
need Discord approval, a server-issued session, a full two-client match, reconnects and operational tests.

## Decision and why

The proposed hosted connection is a **local bridge**: a small process on the player's PC that FIFA 21 talks to on loopback,
exactly as it talks to the local server today, and that forwards every connection to the VPS over an encrypted,
authenticated channel. It is a tunnel for FIFA's traffic only: no network driver, no VPN adapter, no system-wide routing.

It follows Aurora16's approach in spirit: no WireGuard, and FIFA's own networking carries the traffic. It does not
accept Aurora16's two weak spots. Aurora16 identifies players by IP plus a Discord id the launcher announces
unverified, and it forces every connection to plaintext over the internet. Here, every connection is tied to a
server-verified player and is encrypted end to end between the PC and the VPS (maintainer's decision, 2026-09-27).

A direct hosted route remains unproven. The native `FOS-SERVER` hook now carries a fresh synthetic code in
normal-launcher local tests; Blaze and FUT verify it, and subsequent FUT requests carry a random session value.
The matched game build also accepts HTTPS FUT and Nucleus URLs on loopback. Discord-backed issuance, certificate
validation behavior, secure Blaze and gameplay UDP remain unproven. The evidence table below records the original
bridge rationale at the time of the first two traces; the newer native results supersede its open questions.

## Evidence it rests on

Observed in two local runs (2026-09-27 15:08 and 15:21, replacement LSX mode) with the redacted `AUTH EVIDENCE` log
(`CredentialEvidence`) and the auth bridge's pass-through record:

| Observation | Consequence |
|---|---|
| The Blaze login (`Authentication` 0x0A) sends `AUTH` = the code the auth bridge injects (`local-auth-code`). | Blaze can check a per-player code natively (defence in depth, step 3). |
| FIFA exchanges that code at `POST /connect/token` with the parameters in the **query string** and an empty body; the server answers 400 and login continues. | Trace whether FIFA consumes the response before changing the local token contract. The later HTTPS probe proved this route reaches TLS, not that its OAuth response is used. |
| `POST /ut/auth` sent `method=authcode` with a 40-character code that none of our endpoints had issued in those first runs. The same SHA-256 prefix was observed across them; behavior across accounts and machines was untested. | This was the original identity gap. The later local session mode validates a replacement code; approved hosted issuance and two-player evidence remain open. |
| The auth bridge's wrapper stub was called twice for our client IDs and recorded no foreign ID. In the later live trace, FIFA's first-party retriever called the lower-level auth function directly with client ID `FOS-SERVER`; its output and the CardsDLL getter output had the same full SHA-256, matching the `/ut/auth` log prefix. A debugger substitution at the CardsDLL getter then made `/ut/auth` carry the synthetic string `FOS-SERVER` and still reach the FUT hub. | This was the original lead. The later guarded launcher hook, server code validation and FUT session binding are documented in the native investigation; Discord issuance remains open. |
| FIFA never connected to the local LSX on 3216 in either run. | LSX forwarding is optional (open question 1). |
| In normal mode the redirector serves TLS and advertises cleartext Blaze (`<secure>0</secure>`); FUT (8090) and `nucleusConnect` use HTTP loopback. | The later opt-in loopback probe proved native HTTPS FUT/Nucleus feasibility. It still leaves HTTP available and does not protect Blaze or gameplay. A bridge could encrypt the Internet hop if the native route cannot. |
| Every address FIFA receives in our replies is loopback (`127.0.0.1:44321` Blaze, `127.0.0.1:8090` FUT, QoS, SSF, EASW), except the gameplay relay endpoint. | With the bridge on loopback, game-facing replies do not change, except the relay endpoint (section "Gameplay UDP"). |
| The original shared-server mode selected a player by peer IP before auth. | A later `session` mode verifies FUT and Blaze credentials before club selection; hosted LSX and gameplay identity still need decisions. |

## Goals and non-goals

Goals:

- Every PC-to-VPS byte is encrypted and the server is authenticated (TLS for streams, AEAD for datagrams).
- Every connection, stream and datagram belongs to one server-verified player session. The source IP is never
  identity, so players behind one NAT or a shared proxy work and cannot impersonate each other.
- Revocation takes effect at once: the session's streams close and its datagrams stop being accepted.
- No network driver or adapter, no persistent edits to `FIFA21.exe`, no change to FIFA's wire protocol, and the
  supported launcher path works without a debugger.
- One public TCP port and one public UDP port on the VPS.

Non-goals (handled elsewhere):

- The Discord sign-in, approval and account model (`plan.md` section 3). This design only defines what the bridge
  needs from it: a revocable bridge credential (section "Credentials").
- The admin panel (`plan.md` section 8).
- Protecting against malware running as the same Windows user on the player's PC, which could read the credential
  or FIFA's memory. Out of scope, as it is for any game client.

## Overview

```
 Player PC                                                        VPS
 ---------                                                        ---
 FIFA21.exe                                                       Project21-LocalFUT-Server serve --hosted
   | hosts: spring18.gosredirector.ea.com -> 127.0.0.1              |
   | trust patch + auth bridge (in memory, as today)               | Gateway  :443/tcp (TLS)   :443/udp (AEAD)
   v                                                                |   authenticates the session, then hands
 Bridge (loopback listeners)                                        |   each stream/datagram to the service
   42230 redirector  (served locally, as today)                     |   with the player's identity attached
   44321 Blaze    -+                                                v
   8090  FUT       |  one TLS connection per game TCP connection   Internal services on 127.0.0.1 only:
   8081  FUT-compat|----------------------------------------------> Blaze, FUT HTTP, QoS, SSF, EASW, (LSX)
   17502 QoS tcp   |                                                Gameplay relay (identity-bound)
   10853 SSF       |  one UDP socket, AEAD datagrams               PostgreSQL (unchanged)
   42232 EASW     -+---------------------------------------------->
   17502 QoS udp, gameplay UDP (loopback port per match)
```

The bridge is a subcommand of the existing executable, `Project21-LocalFUT-Server.exe bridge`. That reuses the
redirector, certificate generation and TDF code, and respects the helper-naming rule (no `FIFA21*` prefix). A separate
`Project21-Bridge.exe` is the alternative if the player download should not carry server code (open question 3).

## Player PC

The launcher's steps stay as they are, with the local `serve` replaced by `bridge` in hosted mode:

1. **Sign in.** The launcher gets a bridge credential from the VPS after the server-side Discord sign-in and approval
   (`plan.md` section 3). It is stored per Windows user with DPAPI under `%LOCALAPPDATA%\Project21\LocalFUT`, never in a
   command line or a log.
2. **Health.** `GET https://<host>/p21/health` (version, maintenance, and the minimum bridge and launcher version).
3. **Start the bridge.** The credential is passed on standard input. The bridge opens its control connection
   (below), receives the session, and only then binds its loopback listeners. The launcher waits for its `READY` line,
   the same way it waits for the auth bridge today.
4. **Hosts entry, game launch, trust patch.** Unchanged. The redirector still answers locally with the local CA the
   trust patch installs. Its reply still names `127.0.0.1:44321`, now the bridge.
5. **Auth bridge.** Unchanged mechanism. The code it injects becomes the session's **per-player auth code** (from the
   control connection) instead of `LOCAL-FUT21-AUTH-CODE`. The launcher passes it to the auth bridge on standard input;
   the bootstrap check (`/localfut/auth/bootstrap`) reaches the VPS through the bridge and confirms the same code.
6. **Exit.** When FIFA exits, the launcher stops the bridge, which closes its session. The hosts entry is removed as
   today.

Loopback listeners (same ports as today's local server; see `localfut/README.md`). All are TCP, forwarded over TLS,
unless noted:

| Port | Service | Handling |
|---|---|---|
| 42230 | Redirector (TLS) | **Served by the bridge itself** (static reply, local certificate). Not forwarded. |
| 44321 | Blaze | Forwarded (channel `blaze`). |
| 8090 | FUT HTTP, local auth, `nucleusConnect` | Forwarded (`fut`). |
| 8081 | FUT compatibility | Forwarded (`fut-compat`). |
| 17502 | QoS HTTP | Forwarded (`qos-http`). |
| 17502/udp | QoS probes | Forwarded as datagrams (flow `qos`), so FIFA measures the real path. |
| 10853 | SSF bootstrap | Forwarded (`ssf`). |
| 42232 | EASW | Forwarded (`easw`). |
| 3216 | LSX | EA App mode: untouched (EA App owns it). Replacement mode: open question 1. |
| per match/udp | Gameplay | Forwarded as datagrams (flow `game`); see "Gameplay UDP". |

The bridge binds `127.0.0.1` only, refuses to start if a port is taken (with the owning process named, as the
launcher's LSX check does), and never listens on another interface.

## Protocol

### Transport security

- **Streams:** TLS to the VPS hostname with normal certificate validation (for example a Let's Encrypt
  certificate), plus a pinned SPKI hash shipped with the launcher as a second check. The minimum is TLS 1.2 with
  ECDHE and AEAD suites; TLS 1.3 is preferred. Windows 10's SChannel offers TLS 1.3 to clients only on newer builds,
  so 1.2 must work. ALPN `p21-bridge/1`.
- **Datagrams:** AES-256-GCM with a per-session key from the control connection. The 96-bit nonce is a 32-bit
  direction prefix plus that direction's 64-bit counter, never reused. The receiver keeps a 1024-packet replay window per
  direction. AES-GCM is chosen over ChaCha20-Poly1305 because .NET's `AesGcm` is available on every supported Windows
  and Linux.

### Control connection

The first TLS connection, kept open for the session:

```
bridge -> HELLO   { v: 1, credential, launcherBuild, bridgeBuild, gameBuild: "21.0.4750997.0", windowsBuild }
server -> WELCOME { session, streamSecret, udpKey, udpEndpoint, authCode, persona, heartbeatSeconds, expiresAt }
        | REFUSE  { reason: "revoked" | "not-approved" | "version" | "maintenance" | "full" }
both   -> PING/PONG every heartbeatSeconds; a missing heartbeat for 3 intervals ends the session
server -> CLOSE   { reason }   (revocation, kick, shutdown): the bridge closes everything and exits non-zero
```

Messages are length-prefixed JSON (control traffic is small and rare). `session` is a random 128-bit id.
`streamSecret` (32 bytes) and `udpKey` (32 bytes) exist only for this session and die with it. The long-lived
`credential` crosses the wire once per session, on the control connection only. `windowsBuild` feeds the maths groups
(`MathGroups`), replacing the User-Agent parsing for hosted players.

### Stream connections

One TLS connection per TCP connection FIFA opens. The count is small (one Blaze, a few FUT keep-alive connections), it
keeps FIFA's own flow control and backpressure, and one stuck stream cannot block another. The first bytes:

```
OPEN { session, channel, nonce, mac = HMAC-SHA256(streamSecret, session | channel | nonce) }
```

The gateway checks the MAC and the session's state, and answers one byte (`0` accepted, `1` refused). After that the
connection carries FIFA's bytes unchanged in both directions. Either side's close half-closes the other. TLS session
resumption keeps the per-stream handshake cheap. Limits: at most 16 open streams per session and a 10-second OPEN
deadline.

### Datagrams

One UDP socket from the bridge to the gateway's UDP port (default 443/udp):

```
| version (1) | session (16) | counter (8) | AES-256-GCM( flow (1) | flowId (4) | payload ) | tag (16) |
```

- `flow` is `qos` or `game`. For `game`, `flowId` is the relay allocation (the game id's low 32 bits). The gateway
  hands the payload to the relay as coming from **this session's player**, whatever the datagram's source address.
- The gateway looks up `session`. An unknown session costs one dictionary lookup and a drop; a failed tag costs
  one AES-GCM check and a drop. No reply ever goes to an unauthenticated datagram, so the port cannot be used for
  amplification.
- The bridge sends a keep-alive datagram every 15 seconds while no traffic flows, to hold the NAT mapping. The
  gateway replies to the latest address that sent a valid datagram for the session (a NAT rebinding just moves it).
- Overhead: 46 bytes per datagram (25 header, 5 inner, 16 tag), well within FIFA's gameplay packet sizes (the relay limit is 65,507).

### Gameplay UDP

FIFA binds its gameplay socket to 3659 and sends to the address the matchmaking payload advertises
(`RelayAllocation.EndpointFor`). For a bridged player, the server advertises `127.0.0.1:<bridge game port>` instead of
the VPS address.

- The bridge listens on one loopback UDP port for gameplay (default 31000, in the relay's range; configurable).
- Datagrams FIFA sends there are wrapped as `game` datagrams for the player's current allocation, which the control
  connection announces (`MATCH { flowId, localPort }`).
- Datagrams the relay sends to this player arrive as `game` datagrams and are written to `127.0.0.1:3659` from that
  loopback port, so FIFA sees its peer's traffic come from the advertised address.
- In the relay, `RelayPlayer` gains the session as its identity. The source-address check (`EnforceSourceAddress`) is
  replaced for bridged players by the session match, and the per-player relay port is no longer exposed publicly.
  All players share the gateway's single UDP port.

## Server (VPS)

`serve --hosted` (a new identity mode next to `single` and `peer-ip`):

- **Gateway** in-process: the public TCP and UDP listeners, TLS, the control, stream and datagram handling above,
  session state, limits and logging.
- **Internal services** bind `127.0.0.1` only. The gateway opens a loopback connection to the internal listener for the
  stream's channel and writes an identity preamble first:
  `P21ID <session> <player> <hmac(processKey, session|player|channel)>\n`. `processKey` is random per server start and
  known only in-process, so a local process cannot claim an identity. A Kestrel connection middleware on each internal
  listener reads and checks the preamble and sets an `IPlayerIdentityFeature` on the connection. Connections without
  it are refused in hosted mode.
- **`FutEndpoint.PlayerFor`** takes the identity from that feature in hosted mode. `single` and `peer-ip` stay as they
  are for local and LAN play.
- **Blaze login check:** in hosted mode the login's `AUTH` must equal the session's auth code, otherwise the login
  fails. This keeps a stream that somehow reached Blaze without its session's code from logging in.
- **`/connect/token`:** accepts the parameters in the query string (observed), and checks the code against the
  session's.
- **Relay:** allocations use session identity; advertised endpoints for bridged players are the loopback game port.
  One public UDP port replaces the 31000-31199 range.
- **Maths groups:** the bridge's `windowsBuild` replaces the User-Agent note for hosted players.
- **Presence:** the gateway knows who is connected, which feeds the launcher and panel later.

### Credentials and sessions

| Item | Where | Lifetime |
|---|---|---|
| Bridge credential | Issued by the sign-in service after approval. The launcher keeps it (DPAPI); the server stores only its SHA-256. | 30 days, rolled on use, revocable. Bound to one device id. |
| Session | Created by HELLO, one per running bridge. A second HELLO for the same account ends the first session (one game per account). | Until CLOSE, a missed heartbeat or revocation. |
| `streamSecret`, `udpKey` | In memory on both ends. | The session. |
| Per-player auth code | In memory on the server, sent in WELCOME, injected into FIFA by the auth bridge. | The session. |

New tables (a migration, schema `localfut_v4`): `bridge_credentials(id, account_id, device_id, token_sha256,
created_at, last_used_at, expires_at, revoked_at)` and an audit of session starts and ends. Accounts and approvals
belong to section 3's schema.

## Threats and how they are handled

| Threat | Handling |
|---|---|
| Eavesdropping on the internet path | TLS for streams, AES-GCM for datagrams; FIFA's plaintext stays on loopback. |
| A fake server (DNS or network attack) | Certificate validation plus the pinned SPKI; the credential is only sent after the TLS handshake succeeds. |
| Impersonating another player | Identity comes only from the session. Other players' sessions cannot be guessed (128-bit session id, HMAC per stream, AEAD per datagram). A shared IP means nothing. |
| A stolen credential | Revocation; device binding; one session per account (the real owner's next start ends the thief's session and shows it). |
| Replay | TLS for streams; a counter and replay window for datagrams. |
| Injection into a match | The relay accepts gameplay only from the two sessions of that allocation. |
| Flooding the public ports | No reply before authentication; pre-auth rate limits per source IP on TLS handshakes and HELLO; the relay's existing per-game packet and byte limits. |
| A local process claiming an identity on the VPS | Internal listeners are loopback-only and need the process-keyed preamble. |
| Old or modified bridge | HELLO carries versions; the server refuses unsupported ones. A modified bridge still needs a valid credential and can only act as its own player. |

## Performance

- **One extra hop on the player's PC** (loopback) and one on the VPS (loopback from the gateway to the service), plus
  encryption. Both are expected to be small next to the internet round trip. This is unmeasured and must be measured
  (step 5).
- **Streams:** a few TLS connections per player. At 1,000 players that is about 5,000 to 8,000 long-lived
  connections on the gateway, within Kestrel's normal range. TLS resumption keeps reconnects cheap.
- **Datagrams:** one AES-GCM operation per packet in each direction on the gateway. At 300 matches and FIFA's packet
  rate this needs measuring against the relay's existing limits (2,000 packets per second per game). AES-GCM with
  AES-NI is typically cheap per packet.
- The relay no longer needs a public port pair per match, which removes the 600-port requirement in `plan.md`
  section 5.

## Build order and test gates

Each step ends with its own evidence. None is complete on fixture tests alone.

1. **Streams, same PC.** The `bridge` subcommand with its loopback listeners and local redirector; the gateway with the
   control and stream connections; hosted mode with the identity preamble and `PlayerFor`; a synthetic development
   credential (loopback only, never a real account).
   - Gate: protocol tests (HELLO/REFUSE, OPEN MAC, wrong session, revocation closing streams, limits).
   - Gate: FIFA reaches the FUT hub through the bridge with the server on the same PC.
2. **Datagrams.** QoS and gameplay flows, the relay bound to sessions, the loopback game endpoint.
   - Gate: AEAD, replay and NAT-rebinding tests.
   - Gate: a two-client Rivals match on a LAN through the bridges, compared with the same match on `peer-ip`
     (completion, desyncs, latency).
3. **Per-player code.** WELCOME's auth code injected by the auth bridge; the Blaze login check; `/connect/token`
   behavior established from the native request and response consumer. The guarded local hook and pre-club
   Blaze check already passed single-player loopback tests; hosted issuance and bridge binding are still open.
   - Gate: a login with the wrong code fails; `AUTH EVIDENCE` shows the session's code.
4. **Accounts.** Section 3's Discord sign-in and approval issue real bridge credentials; revocation from an operator
   action.
   - Gate: unapproved, revoked, expired and wrong-device credentials are refused; revocation ends a running session.
5. **Hosted.** The VPS with a real certificate; two players on separate networks through the normal launcher.
   - Gate: login, FUT, a full Rivals match, reconnect after a network drop, and measured latency and packet loss
     against step 2's LAN numbers.

## Open questions

1. **LSX in replacement mode.** FIFA never connected to 3216 in the observed runs. Should the bridge forward LSX
   (channel `lsx`), serve it locally, or leave it out until something needs it?
2. **QoS.** Forwarding the probes makes FIFA measure the real path. Is anything in matchmaking driven by those
   results that could behave differently at internet latency? Check in step 2.
3. **Packaging.** The `bridge` subcommand of `Project21-LocalFUT-Server.exe` (reuses code; the player download already
   carries this executable) or a separate `Project21-Bridge.exe` (smaller, no server code on player PCs)?
4. **VPS operating system.** It affects TLS certificate handling (Let's Encrypt automation) and service management.
   Earlier notes say Ubuntu; Aurora16 runs Windows Server.
5. **Port 443.** One TCP and one UDP port on 443 suits restrictive networks, but it shares 443 with any HTTPS the VPS
   serves (the sign-in service, the panel). Use ALPN on one listener or a separate port for the bridge?
