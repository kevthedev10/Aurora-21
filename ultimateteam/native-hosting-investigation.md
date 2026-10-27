# FIFA 21 native hosting investigation — handoff

## Current integration status (reviewed 2026-10-01)

The dated traces and checklists below preserve investigation history; their old
"current server" and approval-file descriptions refer to earlier builds. The present
hosted profile verifies FUT codes before club access, issues a random `X-UT-SID` for
later requests, and verifies Blaze login codes. The server now has a loopback
`/client/*` API that verifies an Aurora account-service ES256 or dedicated HS256
assertion, checks PostgreSQL/Discord role access, and issues the native game code.
An account may be blocked live; the old approval file is gone. Caddy can publish the
launcher API over public HTTPS, while the panel stays on an SSH-tunnelled loopback
port. The server image contains a versioned Windows client pack with the trust patch,
auth helper and hosted start script. See [integration status](aurora-integration-status.md),
[launcher handoff](aurora15-launcher-handoff.md) and [deployment](deploy/README.md).

**Observed in this repository:** the current code contains these contracts.
Recorded September integration results include an account-service proof, shared
launcher pack download, public HTTPS and tunnel/bot checks; see
[the current summary](aurora-integration-status.md) and its dated report.
Commit `8e5d1ed2` removed the hosted helper's dependency on a local bootstrap;
its current bundled self-test passed on 2026-10-01. React replaced the classic
panel locally, with build and focused route checks recorded in
[game acceptance](panel-web/test/game-acceptance.md).

**Unconfirmed here:** a real updated hosted launch reaching FUT, hosted
certificate validation, current React hub framing and VPS two-player/UDP
acceptance. `/connect/token` still needs a request/response consumer trace.
The numbered checklists below are historical investigation checkpoints; use the
current summary for remaining acceptance rather than treating old unchecked
implementation tasks as current missing features.

**Question:** Can Project 21 give every hosted FIFA 21 player a verified identity and protect game traffic without a local forwarding bridge? Please report how Aurora15 works, then test the corresponding FIFA 21 paths; FIFA 15 behavior alone does not establish FIFA 21 behavior.

## Additional executable hash (2026-10-01)

The maintainer requested allowing executable SHA-256 `05cc56f897201a6ba74d608f971bcf157b1a46c10e9cda335e868c17004fbc71`. The affected player reported product version `21.0.4750997.0` and CardsDLL SHA-256 `37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49`, matching the existing DLL. The bridge now allows that executable alongside the original hash, retaining the file-size, product-version, DLL and live instruction checks. The alternate executable has not been independently inspected here; a complete hosted launch with the updated pack remains unconfirmed. Hash acceptance alone is not evidence of compatible patch locations or successful FUT entry.

## Historical FIFA 21 investigation evidence

- The supported game/auth-helper build is `21.0.4750997.0` (`FIFA21-LocalAuthBridge.cs`). The installed `FIFA21.exe` has SHA-256 `44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494`; the installed `CardsDLL_Win64_retail.dll` used for the Ghidra findings below has SHA-256 `37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49`. Match any offsets to the relevant binary before using them.
- Two local runs on 2026-09-27 (15:08 and 15:21, replacement LSX mode) are summarized in `archive/cleanup-2026-09/hosted-connector.md`. Blaze login carried the auth bridge's code. `POST /connect/token` put its parameters in the query string and received HTTP 400; login continued. `POST /ut/auth` carried `method=authcode` and a 40-character code with the same redacted SHA-256 prefix in both runs. The auth bridge recorded no foreign client ID reaching the function it hooks. These runs do **not** identify the UT code's source, prove that it is the same across machines, or prove that it cannot be validated.
- At the start of this investigation, the local profile served `nucleusConnect` and FUT over loopback HTTP, the redirector advertised `<secure>0</secure>`, and peer-IP selection existed in shared mode. These historical implementation facts are not the present hosted profile or client limitations.
- A read-only Ghidra import of the installed CardsDLL found `nucleusPersonaId` at VA `0x180485ff0` and `{"authcode":"%s", "requestSku":"%s"}` at VA `0x1804860b0`. Both have code references inside `FUN_1802bb1a0`. That function formats an `authcode` JSON block under a `DSENBL` condition; one value comes from an indirect call through an object at `param_1 + 0x3d8`. This separate block has not been matched to a wire request. `FUT::WebSession` has a reference from its constructor at `FUN_1803cbff0`.

### Ghidra follow-up, 2026-09-27

- In the same CardsDLL build, `FUN_1803cd820` looks up a service named `FOS-SERVER` and stores its object at WebSession offset `0x3d8` (slot `0x7b`). `FUN_1802ba490` calls it during `start_flow`; the constructor `FUN_1803cbff0` initializes that slot to null.
- `FUN_1803ccfe0` serializes auth-related fields including `method`, `authcode`, and `authCode`; it obtains a string through virtual method offset `0x28` on the object in WebSession slot `0x7b`. `FUN_1802bb1a0` calls this serializer and, under `DSENBL`, also calls the same getter for the separate `{"authcode":..., "requestSku":...}` JSON block. This establishes the **CardsDLL source path for a FUT auth-code string**. The live comparison below connects this getter to the `/ut/auth` field for one run.
- The CardsDLL serializer or its getter call is therefore a **candidate native hook point** for a player-specific FUT value. It has not been patched or tested, and the server would still need to verify any new value. This DLL finding does not answer whether FUT accepts HTTPS or gameplay traffic can be bound to the same player.
- CardsDLL contains `ut/auth` in a pointer table at VA `0x180441d30`; static analysis has not yet linked that table entry to the serializer's send call. The original game files were not modified, and no live-process inspection was performed.

### Targeted FIFA21.exe trace, 2026-09-27

- A temporary PE containing only selected sections of the installed EXE was imported into Ghidra with automatic analysis disabled; this was targeted decompilation, not full unpacking or full EXE analysis. The addresses below refer to the original EXE hash above. No pre-existing unpacked EXE or Ghidra/Binary Ninja database was found in the project paths checked.
- The original EXE has five disassembled code references to `FOS-SERVER` at `0x144365316`, `0x144365690`, `0x144365a3e`, `0x14483a2d2`, and `0x144d4aeb0`. At `FUN_144d4ae40`, a service lookup stores its result at offset `0x3d8` of an object, matching the CardsDLL WebSession slot. This makes the EXE service lookup concrete, but does not yet identify the returned object's vtable or its method at `+0x28`.
- The shipped helper hooks EXE RVA `0x10290A0` (VA `0x1410290a0`). That function is a wrapper around lower-level `FUN_14102cbf0`. `FifaOnline::FirstPartyAuthTokenRetriever::DoTick` at `FUN_140b79000` also calls `FUN_14102cbf0` directly at `0x140b790db`, passing the client ID stored in a queued job at offset `0x18`. `FUN_140c3d530` creates such a job and copies its client ID from an argument. The game update path calls `DoTick` at `0x140c2b090`.
- **Correction to the 15:21 diagnostic:** `[PASSED] none` covers calls through the hooked wrapper only. It cannot rule out a first-party auth-code request through the direct `DoTick` call. The live test below observed exactly that direct call. The repeated hash across launches remains an observation, not proof that the code is fixed across accounts or machines.

### Live FOS-SERVER trace, 2026-09-27

- In a debugger-assisted launch using the installed build above, the `DoTick` return breakpoint at EXE RVA `0xB790ED` fired with result `0` and a 40-byte output. Its queued job's client ID at job offset `0x18` was `FOS-SERVER`. The read-only probe `Hash-FIFA21-ProcessMemory.ps1` reported full SHA-256 `81ea7138c84a774f193963d3bc5b14612a7ddae046ca019cada0a449378fe769` for that output. Only the hash and length were retained for this finding.
- The CardsDLL getter return breakpoint at DLL RVA `0x3CD1C8` then fired. The probe reported the **same 40-byte length and full SHA-256** for its string. The current server log recorded `POST /ut/auth` at 17:50:29 with `$.identification.authCode=other(len=40,sha256=81ea7138c84a)` and HTTP 200. The server log reports only the first 12 hash characters; their match and the two full native hash matches are strong evidence that this path supplies FUT's wire code in this run.
- The local PowerShell command using `-Tail 500` returned no line because more than 500 log lines followed `/ut/auth`; the full current log and `localfut/runtime/server.log` both contain that timestamped request. After the debugger resumed, the user reported that FIFA reached the FUT hub and remained responsive. This was a debugger-only diagnostic, not a launcher-supported native fix or a match test. The game executable was not persistently edited. The auth helper still hooks only the wrapper at RVA `0x10290A0`; no substitution at the direct path was tested.
- **Controlled substitution, observed at 18:01:28:** at the CardsDLL getter return (`+0x3CD1C8`), the debugger changed only `RAX` to point at the already-mapped, NUL-terminated `FOS-SERVER` string in FIFA21.exe (`0x147073b80` for this loaded base). The first attempt occurred before CardsDLL reloaded and produced no new `/ut/auth` entry. Repeating it at the getter stop after DLL load produced `POST /ut/auth` with `$.identification.authCode=FOS-SERVER`; the server answered HTTP 200, and the user reported reaching the FUT hub. This proves that changing this return value controls the code serialized on the wire for this build and run. It does **not** prove server verification: `BootstrapRoutes.UtAuthAsync` currently returns 200 for every non-DELETE request.
- **Next trace proposed at the time:** determine whether the original `FOS-SERVER` code changes across accounts or sessions. Replace it with a unique, launcher-issued test code, make `/ut/auth` reject missing/wrong/replayed codes in an isolated test profile, and verify that the right player is selected before club access. Identify how subsequent FUT requests carry a session binding, and test HTTPS independently. A matching auth code alone does not authenticate every FUT or UDP request. The current implementation and open gates are summarized at the top of this document.

### Historical server identity audit before session mode

- `FutEndpoint.HandleAsync` acquires a player session **before** routing the HTTP request. In shared mode, `PlayerFor` derives that player from the peer IPv4 address. `BootstrapRoutes.UtAuthAsync` logs redacted evidence and returns a club SID for any non-DELETE `/ut/auth` request; it does not validate the submitted auth code. `X-UT-SID` is sent in responses, but is not used to choose the requesting player.
- The current `X-UT-SID` is deterministically derived from the club SID (`ClubPresentationService.UtSid`). CardsDLL contains the format string `X-UT-SID: %s` at VA `0x180485c5a`, but the Ghidra project has no direct code reference or pointer to that exact address. Whether FIFA sends the header on later requests is **unconfirmed**; a redacted request trace is needed before relying on it for session binding.
- `BlazeConnectionHandler.OnConnectedAsync` also acquires a player session from peer IP **before** reading the first Fire2 frame. It rejects a second active Blaze connection for that resulting player ID. `BlazeResponses` answers local login commands without checking the request's `AUTH` value; the current auth evidence handler only logs it. Two players sharing one public IP therefore collide at the current session-selection step, irrespective of separate FUT persona IDs.
- `MatchmakingRegistry.PairLocked` allocates a two-port `GameplayRelay` using each Blaze client's assigned player ID and peer IP. The relay distinguishes sides by allocated port and checks each datagram's source IP; it does not authenticate datagram payloads to a player. Separate ports can distinguish two participants **after** correct player assignment, but the current IP-based Blaze assignment cannot establish that distinction for two players behind one NAT.
- **Design consequence (inferred from those entrypoints):** a new per-player FUT code needs server validation at `/ut/auth` and a way to bind subsequent FUT requests to the verified player. The Blaze login code needs validation before the connection is bound to a club, or another independently verified binding. HTTPS alone protects transport; it does not change these server identity decisions. Confirm the client's actual follow-up headers and login order in a redacted live trace before selecting a binding mechanism.

## Questions for the Aurora15 developer

1. What is the actual connection path: game directly to VPS, a local proxy/bridge, or a game hook? List FUT HTTP, Blaze TCP, Nucleus/Origin, QoS, and gameplay UDP separately. Which internet hops are encrypted?
2. How does the server verify the Discord OAuth result? What binds each **FUT request**, Blaze connection, and UDP flow to the approved account? Does the design still use source IP, a launcher announcement, a client-supplied persona ID, or a fallback guest club? What happens for two players behind one NAT?
3. If a native hook supplies a player-specific auth code, which game build, module, function/call chain, and client ID does it affect? Is the code sent by both Blaze and `/ut/auth`? Where does the server validate it, and how are expiry, replay, and revocation handled?
4. Did Aurora15 make FUT and Nucleus use HTTPS, and Blaze use secure transport? If so, how was the URL/scheme chosen, what certificate validation did the client perform, and was this tested in game? Does gameplay UDP have separate encryption and identity protection?
5. What was measured in a real two-client match: added latency/jitter, reconnect behavior, and whether matchmaking/gameplay UDP completed? State the test date, build, and network path.

Please return findings with **build identity, module/function or source path, observed request/response or packet evidence, and test result**. Redact credentials and account IDs. Findings from Aurora15 should be treated as leads for FIFA 21, not copied as FIFA 21 offsets or proof.

## Native hosting work and acceptance tests

The goal is: a player completes Discord OAuth in the launcher, FIFA 21 sends a credential for that player, and the hosted server verifies **each channel's** player identity before accessing a club or relaying gameplay. The controlled substitution above proves that the `/ut/auth` value can be changed for one build. It does not yet meet this goal. Complete the gates below in order; record **observed**, **inferred**, and **unconfirmed** results separately. Use synthetic codes and disposable clubs in tests. Keep actual account tokens, full auth codes, and sensitive traffic out of logs and committed fixtures.

### 1. Establish the FIFA 21 wire contract

- [ ] Record the exact EXE and CardsDLL hashes, game version, LSX mode, loaded module bases, and test profile for each live run. Use the RVAs above only after matching both installed binaries. Trace the `FOS-SERVER` direct call, CardsDLL getter, and redacted `/ut/auth` body in one launch. Repeat with another session and, if available, another account or machine. Report whether the *original* code varies; the two earlier identical hashes do not settle this.
- [ ] With request logging that redacts values, capture the `/ut/auth` response and the **next** FUT requests: method/path, header names, presence and hash of any cookie or session header, connection reuse, and reconnect behavior. Determine whether FIFA sends `X-UT-SID` or another server-issued value back. The existing `X-UT-SID` response is not proof that the client uses it. Stop the direct-HTTP identity design if a fresh connection has no client-carried proof and no validated way to add one.
  - **Observed, 2026-09-27 18:16-18:18 (one launcher run, no debugger, 51 logged requests):** every `/ut/game/...`
    request after `/ut/auth` (24 of them, 18 on new connections, including `POST /ut/delete/auth`) carried
    `X-UT-SID` equal to the value the server issued (`club-ut-sid`). No cookies were sent. Static `/fut/...` content
    (item backgrounds, player heads, pack and seasonal config JSON) carried no credential. Before `/ut/auth`,
    `GET /v2/user/accountinfo` carried `nucleus-access-code` equal to the 40-character `FOS-SERVER` code (same hash
    prefix `81ea7138c84a`) and `easw-session-data-nucleus-id` of length 1 (its hash is that of `"0"`). `/ut/auth`
    carried no credential header, and one header named `hash` whose value was not logged. `/connect/token` carried
    none either. **Consequence:** FIFA returns the server-issued `X-UT-SID` on every FUT game request, independent of
    connection reuse, so a random, expiring, server-validated `X-UT-SID` can bind later FUT requests to the player
    that passed `/ut/auth` (gate 3). Unconfirmed: behaviour after a real network drop, and the `hash` header's meaning.
    `X-UT-SID` is a bearer value, so gate 5 (HTTPS) remains required before it crosses the internet.
  - Instrumentation (2026-09-27): `FutEndpoint.NoteFollowUp` logs `AUTH EVIDENCE follow-up` for the
    `/ut/auth` request and the next 30 FUT requests of that player, and for the first request on every new
    connection. Each line has the Kestrel connection id, `new=`, every header name, and the credential-like headers
    (auth, token, session, sid, nucleus, cookie) redacted by `CredentialEvidence`: the club's own `X-UT-SID` shows as
    `club-ut-sid`, anything else as a length and hash prefix. Paths are logged without query strings.
- [ ] Trace the Blaze first login frame and any earlier local Nucleus calls in the same launch, retaining only field names, lengths, and hashes. Confirm the actual order and which code, if any, each request carries. `POST /connect/token` currently receives HTTP 400 when FIFA sends query parameters; establish whether its response is consumed before changing that endpoint.

**Pass evidence:** timestamped, redacted client/server trace showing the code at each source and the exact value *type* FIFA returns on later FUT requests. A hub screen alone does not pass this gate.

### 2. Issue and send a player-specific native code

- [ ] After Discord OAuth succeeds, have the hosted service issue a cryptographically random, short-lived code bound server-side to that player and login attempt. Define expiry, logout/revocation, and retry semantics. Deliver it to the launcher over an authenticated TLS channel, then to the helper without writing it to a config file or log. Do not treat `nucleusPersonaId`, a source IP, or the current fixed local auth constants as proof of identity.
- [ ] Resolve the concrete `FOS-SERVER` getter target, calling convention, ownership, string encoding/length, and lifetime. Choose the smallest guarded **in-memory** hook that supplies the issued code at the direct path or CardsDLL getter. Update `FIFA21-LocalAuthBridge.cs` and its launcher/bootstrap contract together; retain version, binary hash or expected-byte checks and fail closed on mismatch. Do not persistently modify `FIFA21.exe` or require x64dbg for a normal launch.
- [ ] Rebuild the shipped `Project21-LocalAuthBridge.exe` through `Build-FIFA21-Launcher.ps1` when its source changes. In an isolated launcher test, check supported-build success, wrong-build rejection, restart/reload behavior, and that unrelated client IDs still receive their original values. In a live **loopback-only** launch through `RUN-FIFA21.cmd`, verify the issued code's **hash** matches the CardsDLL getter and `/ut/auth` request, and that the hub remains usable. Repeat with two distinct player sessions and verify different codes. Do not send the new credential over Internet HTTP while the TLS gate remains unpassed.

**Pass evidence:** the regular launcher injects a unique issued code for the matched build, and the isolated server sees its redacted hash on `/ut/auth`. The earlier debugger substitution is proof of the hook point only, not this gate.

**Progress, 2026-09-27 (built and self-tested):** the helper now checks the exact installed
EXE and CardsDLL hashes and the 15 original instruction bytes at EXE RVA `0xB790E0`. Its in-memory stub replays the
original `DoTick` pointer/length loads, then substitutes a process-lifetime 40-character code only when the original
call succeeded and the queued client ID is exactly `FOS-SERVER`; other IDs and failures keep FIFA's original result.
This post-call site was chosen because its calling context and wire effect were observed above; it leaves the
lower-level auth call and its ownership side effects intact. The local launcher generates a **synthetic** random code
and passes it to the helper on standard input, never on its command line or in its log. The helper logs only the code
length and SHA-256 prefix for correlation. Its third patch participates in the existing suspended-process installation,
read-back and rollback checks. The official builder produced the helper, and its isolated native self-test passed
exact-ID, longer-ID, wrong-ID, failed-call, return ABI, and existing wrapper/rollback cases without opening FIFA.
**Live loopback result, 2026-09-27 19:03 (one normal launcher run, no debugger):** the helper's `[FUT-CODE]` line
reported `len=40 sha256=a87186b12c6f`. The redacted server log at 19:03:47 showed the same hash prefix in
`nucleus-access-code` on `GET /v2/user/accountinfo`; at 19:03:48 it showed the same prefix in
`identification.authCode` on `POST /ut/auth`. The server returned HTTP 200, later FUT requests carried `X-UT-SID`,
and the user reported reaching the FUT hub. This proves the supported launcher and helper changed the native
`FOS-SERVER` wire value while preserving this local login for the matched build. It is not a two-client or match test.
The code is **synthetic**, generated by the local launcher, not issued by the server or bound to Discord. The local
server used `single` mode and did not validate it in that run. A later change added an opt-in loopback session test
that registers the launcher's synthetic code and verifies Blaze before club selection; one live run reached the hub
with matching redacted code evidence as recorded below.
Gate 2 remains open for authenticated Discord issuance and expiry/revocation across the hosted launcher-to-server
contract. Two fresh loopback launches later produced different synthetic codes and FUT sessions, as recorded below;
that is a restart test for one local player, not issuance to two approved players.

**Server-side handoff added, 2026-09-27 (historical):** the first loopback launch API
checked a short-lived ES256 assertion and an operator allowlist. On 2026-09-28 the
allowlist was replaced by PostgreSQL player access and Discord role checks, and the
shared `/client/*` routes were added. The account-service signer and launcher calls
remain outside this repository; no real Discord account has been admitted through
the hosted path. See `aurora15-launcher-handoff.md` for the current contract.

### 3. Authenticate FUT before selecting club state

- [ ] Change `server/src/Project21.LocalFut.Server/Http/FutEndpoint.cs` so `/ut/auth` is parsed and verified **before** `PlayerRegistry.AcquireAsync` chooses a club. Give necessary pre-auth bootstrap routes stateless handling or an explicit, tested allowlist; they must not load a club. Change `server/src/Project21.LocalFut.Core/Web/Routes/BootstrapRoutes.cs` to use the verified player instead of returning HTTP 200 for any non-DELETE request. Cross-check `nucleusPersonaId` only after authentication; never select a club from that client-supplied number alone.
- [ ] Bind subsequent FUT requests to the verified player using the **observed** client-carried session value from gate 1. If FIFA does not send one on all relevant requests/connections, investigate a guarded native way to supply one, or use a local authenticated bridge. Do not fall back to peer IP for a hosted multi-player server. If `X-UT-SID` is used, replace its deterministic club-derived value with an unpredictable, server-validated session value and define expiry, rotation, and logout behavior.
- [ ] Add isolated server tests: correct code opens only its own club; missing, wrong, expired, revoked, and cross-player reused codes receive non-success responses **before** any club lease or state change; legitimate retries behave according to the defined retry rule. Test two players behind one public IP, spoofed `nucleusPersonaId`, simultaneous requests, new TCP connections, and reconnects. Assert that one player cannot read or change the other's club.

**Pass evidence:** a trace of two same-IP clients retaining separate clubs across `/ut/auth`, later FUT requests, and reconnects, plus negative tests that fail before club access. HTTP 200 on `/ut/auth` alone is insufficient.

**Progress (2026-09-27, server side only; fixture tests, not live):** `serve --player-identity-mode session` (the
`single` and `peer-ip` modes are unchanged).
- `NativeSessions` issues per-player auth codes: 40 lowercase hex characters, valid for `native_auth.code_lifetime_seconds`
  and usable by both `accountinfo` and `/ut/auth`. A verified `/ut/auth` starts a random `X-UT-SID` session
  (`ut.` + 32 hex), one per player: a new login replaces it, it expires after `native_auth.session_idle_seconds`
  idle, and logout (`/ut/delete/auth`, `DELETE /ut/auth`) ends it. Both are held in memory, so a restart ends them.
- `FutEndpoint` verifies the player **before** a club lease:
  - `/ut/auth` by `identification.authCode`;
  - `v2/user/accountinfo` by `nucleus-access-code`;
  - every other request by `X-UT-SID`.

  Replies echo the request's session. A body's `nucleusPersonaId` never selects a club (a mismatch is only logged).
  Without a credential, a request reaches only the local auth endpoints and the shared `/fut/` content routes
  (`FutRouter.HandleAnonymousAsync`); anything else gets 401 `{"code": "401", "reason": ...}` (retail's refusal
  shape is unconfirmed).
- Without the explicit local test setting, replacement LSX is refused in session mode (`PlayerFor` throws).
- The current branch now has a Blaze prelogin allowlist and verifies `Authentication.Login`'s unique, parsed `AUTH`
  before it acquires a club. This has a focused synthetic test and the single-player loopback live run below. Replacement LSX uses
  the configured local player only with the explicit loopback `localSessionTest` setting; it does not authenticate a
  hosted LSX player.
- `NativeSessionTests`: two players behind one address keep their own clubs across `accountinfo`, `/ut/auth` (with a
  spoofed persona) and later requests; replacement, logout and idle expiry; wrong, missing, expired and forged codes
  and sessions refused without opening a club; content and the bootstrap served anonymously.
- Not done: code issuance to a real player (gate 2 and the Discord sign-in), live reconnect behaviour, and two-player separation.

### 4. Authenticate Blaze and align local OAuth

- [ ] Change `server/src/Project21.LocalFut.Server/Blaze/BlazeConnectionHandler.cs` so the first relevant Fire2 `AUTH` frame is verified before binding a player session. The current handler binds from peer IP before reading that frame. Bind it to the same approved player as FUT and preserve the existing Fire2/TDF response contract in `BlazeResponses`. Confirm whether the existing auth bridge supplies that frame in a normal launcher run.
- [ ] Align `server/src/Project21.LocalFut.Core/Services/Auth/LocalAuthService.cs` with the **observed** FIFA 21 `/connect/token` request format and consumer, then test success and failure responses. This local Nucleus emulation is separate from the launcher-to-service Discord OAuth flow; document where the resulting identity is bound. Verify both supported LSX modes without taking EA App's port in `ea-app` mode.
- [ ] Test correct, missing, wrong, expired, and reused Blaze credentials; two same-IP clients; disconnect/reconnect; and an attempted Blaze/FUT identity mismatch. Wrong credentials must not create a club-bound Blaze session or relay allocation.

**Pass evidence:** two same-IP clients establish different verified Blaze and FUT sessions, and a mismatch is rejected before gameplay setup.

**Synthetic connection test, 2026-09-27:** `BlazeSessionConnectionTests` drives the real
`BlazeConnectionHandler.OnConnectedAsync` with two simultaneous in-memory Fire2 clients whose remote endpoints have
the same `203.0.113.7` address. Each sends a distinct code registered in `NativeSessions` and receives a Login
reply with its own persona; both club sessions open. A client-supplied persona naming the other player does not
change the code's identity. The same two codes sent to the real `FutEndpoint` on `/ut/auth` produce different
`X-UT-SID` values, and later `/user` replies identify the corresponding player. The first Blaze player can
disconnect and reconnect without taking the second player's club. Missing, unknown, expired, revoked, and
duplicate Blaze `AUTH` values and a club command before Login are refused without opening a club. The focused
Windows test command (`dotnet.exe test ... --filter FullyQualifiedName~BlazeSessionConnectionTests --no-restore`)
passed 2/2 tests in 33 seconds using throwaway PostgreSQL state. This tests server binding, not two running FIFA
clients, a real match, a hosted issuer, or encrypted transport. A valid code remains a bearer credential: anyone
who possesses player B's valid code will be authenticated as B, so secure issuance and transport remain required.

**Local gate 2/4 test procedure (2026-09-27 implementation and single-player live result):** Set
`"localSessionTest": true` in the per-user `%LOCALAPPDATA%\Project21\LocalFUT\launcher-settings.json`, stop the
previous LocalFUT server normally, then use `RUN-FIFA21.cmd`. The launcher creates one random 40-character code,
passes it to the new loopback `serve --player-identity-mode session --local-session-test` process through its
inherited environment, clears its own environment copy, and sends the same code to the native helper on stdin.
The server registers it in `NativeSessions` for the configured local player and clears its environment copy. No
code-issuing HTTP route exists. The helper's `localfut21-blaze-server` result and direct `FOS-SERVER` FUT result use
that code; its SDK/SSF results keep the local compatibility value. Check only redacted hash prefixes in the helper
and server logs, the `Blaze login verified` event, `/ut/auth` and later `X-UT-SID`, plus actual hub state. A new
launch needs a new server because codes live only in server memory; the launcher refuses to reuse an existing
server in this mode. Set `localSessionTest` back to `false` after the test. This loopback procedure does not supply
Discord OAuth, hosted TLS, or authenticated multi-player LSX/gameplay UDP.

**Observed live loopback run, 2026-09-27 19:29–19:30:** The helper reported a new 40-character code with redacted
SHA-256 prefix `30c622c631ee`. The server logged local session-test registration at 19:29:37, verified Blaze Login
for `single-player` at 19:30:07, and recorded `AUTH` as a 40-character value with the same hash prefix. FIFA sent
that code in `nucleus-access-code` on `accountinfo` at 19:30:10 and in `identification.authCode` on `POST /ut/auth`
at 19:30:12. The server answered `/ut/auth` 200. Later game requests carried one redacted `X-UT-SID` hash across
several new HTTP connections, including `GET /ut/game/fifa21/hub` (200 at 19:30:14). The user reported reaching
the FUT hub. Evidence: `localfut/runtime/auth-bridge.log` and `localfut/runtime/server.log` for this run. This
establishes one player's native code and session binding through the normal launcher. It does not test a second
player, a wrong-code live attempt, a match, Discord issuance, or Internet transport.

**Repeat live loopback run, 2026-09-27 19:32–19:33:** After a normal stop and fresh launch, the helper reported
`len=40 sha256=a791c9d439d6`, different from the first run's `30c622c631ee`. The server registered one local
session-test code at 19:32:54, verified Blaze Login at 19:33:23, and recorded that new hash in Blaze `AUTH`,
`nucleus-access-code` on `accountinfo`, and `identification.authCode` on `/ut/auth` (200). Later FUT requests
carried one new `X-UT-SID` hash (`9d3f453a494d`, versus `bbf236dee1e6` on the first run) across several new
HTTP connections. The hub route returned 200 at 19:33:30, and the user again reported reaching the hub. No
Blaze-login refusal or server error appeared in this run's targeted log search. These runs establish a distinct
synthetic code and FUT session on each fresh launch, for the same configured local player.

### 5. Prove transport for each channel on the actual build

**Loopback HTTPS feasibility probe, 2026-09-27 (live results below):** The server has an opt-in
`--tls-probe-mode off|fut|nucleus|both` listener on a separate loopback port (default `18443`). It reuses the
existing `spring18` redirector leaf and GOS2015 CA without rotating certificates or changing the HTTP bootstrap
used by the launcher/helper. The probe advertises `https://spring18.gosredirector.ea.com:18443/` only for the
selected FUT or nucleusConnect game URLs; normal mode remains HTTP. The launcher setting is `tlsProbeMode`, default
`off`, and it requires a fresh server. `TlsProbeConfigurationTests` passed all four mode cases. In an isolated
temporary Windows profile, `Test-TlsProbe.ps1` started the debug server, made a hostname- and CA-verified TLS 1.2
request to `/localfut/auth/bootstrap`, observed the probe request log, and stopped its own server/database. Windows
curl needed `--ssl-no-revoke` because this synthetic local CA has no published revocation service; CA and hostname
verification remained enabled. This proves the listener and certificate setup, not FIFA's acceptance of HTTPS.

**Reproduction procedure:** Stop FIFA and LocalFUT normally, rebuild with `Build-FIFA21-Launcher.ps1`, set
`"tlsProbeMode": "fut"` in the per-user launcher settings (keeping `localSessionTest: true`), then use
`RUN-FIFA21.cmd` and enter FUT. The server's `FUT TLS PROBE TCP` and `FUT TLS PROBE request` lines must show a
completed TLS connection and game requests on port 18443, and the normal logs must show `/ut/auth`, session
requests and a usable hub. If the game falls back to port 8090 or errors, record that exact behavior. Then test
`nucleus` and `both` separately. Clear the setting to `off` after experiments. The probe leaves the HTTP listener
available for launcher bootstrap and comparison, so it is not a production transport policy.

**Observed live FUT HTTPS run, 2026-09-27 19:55–19:56:** The official builder succeeded, the launcher checks and
251 repository hygiene checks passed, and the running bootstrap's `serverBuildSha256` matched the rebuilt server
(`8c762ead9063` prefix). With `tlsProbeMode: fut` and `localSessionTest: true`, the server logged 20 completed
TLS 1.2 HTTP requests on the probe port, with cipher `TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384`. FIFA sent
`GET /ut/game/fifa21/v2/user/accountinfo`, `POST /ut/auth` (200), later session-bearing requests, and
`GET /ut/game/fifa21/hub` (200) over that listener. The user reached the FUT hub. The server still handled
`POST //connect/token` on HTTP 8090 in this mode, as expected; Nucleus is the next separate mode test. The
TLS handshake feature logged an empty SNI, so this run does not prove exactly how FIFA validates the server
certificate. Two `/fut/` asset requests returned 401; the user still reached the hub. Evidence is the
timestamped `localfut/runtime/server.log` from this run. This establishes native FUT HTTPS to the local
probe on this build, not production Internet transport or encrypted gameplay.

**Observed live nucleusConnect HTTPS run, 2026-09-27 19:58–19:59:** With `tlsProbeMode: nucleus`, the probe
logged a completed TLS 1.2 `POST //connect/token` using the same cipher. The server handled the token request,
then handled `POST /ut/auth` (200) and `GET /ut/game/fifa21/hub` (200) on the normal FUT HTTP listener. The
user reached the hub. This establishes that this client build accepts the advertised HTTPS Nucleus endpoint
in this local setup; it does not yet test both HTTPS routes in one launch. The TLS handshake feature again
logged empty SNI, so the client's certificate validation policy is still unconfirmed.

**Observed combined HTTPS run, 2026-09-27 20:00–20:01:** With `tlsProbeMode: both`, one fresh FIFA launch sent
19 TLS 1.2 HTTP requests to the probe listener, including `POST //connect/token`, `POST /ut/auth`, and
`GET /ut/game/fifa21/hub`. The token route completed; `/ut/auth` and the hub returned 200, and the user
reached the hub. The follow-up hub request carried an `X-UT-SID` field (only its redacted hash was logged).
The launcher setting was restored to `tlsProbeMode: off` after inspection; the already-running test process
continues with the mode it loaded at start. The three live runs prove this build can use native HTTPS for the
advertised FUT and Nucleus routes in the local test, separately and together. They do not establish certificate
validation policy, Internet routing, HTTPS-only enforcement, secure Blaze, gameplay UDP protection, or a
two-player match.

- [x] Test advertised HTTPS FUT and `nucleusConnect` URLs on the matched build in an isolated loopback profile. Separate and combined normal-launcher runs reached the hub, and the server logged TLS 1.2 requests on the probe listener. This proves local client acceptance of these URLs; it does not establish the client's certificate validation policy or a hosted route.

**Hosted auth bridge fix, deployed 2026-09-28 21:50 BST:** Pack `sha-8e5d1ed`
passes explicit `--hosted` to the auth helper to avoid contacting the local bootstrap
profile on a player's PC. Local mode still checks that profile. Game/hash/instruction
checks and the existing issued-code substitution are unchanged; no new server identity
fallback was added. All 589 server tests and the packaged native-helper self-test passed.
The public pack download matched its manifest, server/redirector health passed, and
maintenance/access rules were preserved. A hosted launch to the FUT hub remains
unverified until an early-access player tests it through the shared launcher.

- [ ] With a hosted certificate and hostname in an isolated deployment, record the client's actual certificate validation result and reaction to an untrusted or wrong-host certificate. The probe's empty SNI field cannot answer that question. Require HTTPS for credential-bearing FUT and Nucleus requests on public listeners, verify that advertised redirects and follow-up URLs do not downgrade to HTTP, and keep local bootstrap/test HTTP confined to loopback.
- [x] Test Blaze redirector `<secure>1</secure>` with the matched FIFA 21 build. Record the selected endpoint, TLS handshake, authenticated login, and usable hub. Keep the existing redirector TLS result distinct from Blaze transport; today the redirector advertises `<secure>0</secure>`.
  - **Observed, 2026-09-27 20:20 (normal launcher, no debugger; `blazeTlsProbe: true`, `tlsProbeMode: both`,
    `localSessionTest: true`):**
    - FIFA opened Blaze on `18444` with a TLS 1.2 handshake (`TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384`, empty SNI) at
      20:20:24.
    - Its login arrived over that connection, and the pre-club check logged `Blaze login verified for player
      single-player`. The login's `AUTH` was a 40-character session code (redacted hash prefix `bb40f9c039e6`).
    - FUT and `nucleusConnect` ran over TLS 1.2 on `18443` in the same launch, and `/ut/auth` started a session over
      HTTPS. The hub loaded.
    - FIFA sent **no SNI** on either port, so a hosted server cannot choose its certificate by hostname.
    - `/connect/token` still carried the fixed local code and still received 400.
    - Session mode refused two content requests: `/fut/seasonal/seasonalconfig.json`, and one card-background `.dds`
      that the other modes answer with the empty unknown-endpoint reply. After that 401, FIFA requested no further
      backgrounds. Fixed afterwards (not yet re-run live): the seasonal document joins the anonymous content routes,
      and any other `GET /fut/` asset gets the same empty unknown answer as in the other modes, without a club.
  - Probe details (built earlier the same day): launcher setting `blazeTlsProbe: true` starts
    `serve --blaze-tls-probe-port 18444` (the same Blaze handler behind TLS 1.2 with the redirector's leaf
    certificate, on loopback, beside plaintext 44321) and `redirector --blaze-tls-port 18444`. The redirector then
    answers `<hostname>spring18.gosredirector.ea.com</hostname>`, `<port>18444</port>`, `<secure>1</secure>`. Naming
    the certificate's host instead of `127.0.0.1` is **inferred** to be what FIFA matches the certificate against.
    The server logs `BLAZE TLS PROBE TCP` and `BLAZE TLS PROBE handshake` (protocol, cipher, SNI).
    Tests: redirector document (plain unchanged, secure variant) and option validation.
- [ ] Inventory every other game-facing path (redirector, LSX, QoS, SSF, EASW, content, and callbacks). Record which cross an Internet hop and whether they carry credentials or player data; define and test the required transport and identity binding for each.
  - **Observed, launch to hub (2026-09-27 20:20 and 20:32 runs):**

    | Service | Used on the way to the hub | Transport in the `all` run |
    |---|---|---|
    | Redirector (42230) | yes, one request | TLS (local CA) |
    | Blaze | yes | TLS on 18444; login verified before club selection |
    | FUT, POW, Nucleus (`/ut/auth`, `accountinfo`, `/connect/token`) | yes, 43 requests | TLS on 18443 |
    | EASW | yes, `GET /roster` and `/routing` | TLS on 18445 |
    | SSF | no | TLS listener on 18446 unused |
    | QoS (HTTP and UDP) | no; probably used at matchmaking (unconfirmed) | plaintext |
    | LSX | no connection (replacement mode) | local only |

    - `/connect/token` was plaintext in the 20:20 run and TLS in the 20:32 run. Its URL therefore comes from the client
      config, which `all` rewrites; the exact key is unconfirmed.
    - The only plaintext requests left were the launcher's and helper's own `/localfut/auth/bootstrap` checks.
    - `serve --tls-probe-mode all` (`tlsProbeMode: all`) moves EASW and SSF to TLS on the probe port + 2 and + 3 and
      rewrites every EASW and SSF URL in `OSDK_CORE`.
  - Still loopback in replies, carrying no credentials (the public-addressing step): the QoS ping site (`PSA`),
    telemetry and ticker `ADRS`, the shards response, the scheme-less `FUTDYNAMICMESSAGES_CUSTOMURL`, and the local
    auth profile's `nucleusConnect`. QoS and SSF still need a matchmaking run to observe.
- [ ] Trace gameplay UDP destination selection and the packet path for a real match. `server/src/Project21.LocalFut.Server/Relay/GameplayRelay.cs` currently checks source IP and allocated port, not a player credential. Determine whether the native game protocol can bind and protect UDP; test same-NAT clients, forged/wrong-side datagrams, and reconnects. If direct UDP cannot establish identity and confidentiality, document the remaining need for a tunnel, VPN, or protocol-level change rather than calling the HTTP/Blaze work a complete native solution.

**Pass evidence:** all Internet hops carrying player credentials or gameplay data have the claimed protection, verified by connection traces and a completed two-client match. Report any channel still plain or unauthenticated separately.

#### Idle menu stalls, 2026-10-06

**Observed:** paired PC/VPS header captures show sends on existing FUT and Blaze
connections without corresponding inbound packets at the VPS, followed by prompt
responses on new connections. In the 10:44 UTC recording, the first marked action
lasted 46.77 seconds and contained two old HTTPS connection waits; immediate
repeats took 1.74–2.59 seconds. The second old HTTPS send occurred while the VPS
was demonstrably capturing fresh-flow traffic. VPS capture counters reported zero
kernel drops. This is connection-specific transport evidence, not decoded TLS
request-path evidence or proof of where the network drops traffic.

**Inferred:** idle connection state becoming unusable is a stronger explanation
than slow catalogue processing for these captured stalls. The responsible Windows,
router or upstream component remains **unconfirmed**.

**Experiment prepared, not deployed/accepted:** `tcp_keepalive` enables TCP probes
on hosted game TLS sockets. Hosted Compose now sets a 30-second idle override via
`ULTIMATETEAM_TCP_KEEPALIVE_IDLE_SECONDS`, including for existing config files
that disable probes. The VPS `.env` can set `TCP_KEEPALIVE_IDLE_SECONDS=0` to roll
back after recreating the server container. Config files remain untouched;
standalone server config defaults still disable probes.
**Observed locally on 2026-10-06:** all 21 focused configuration/socket tests pass
on Windows and in an isolated Linux SDK container, including reading enabled
keepalive and 30/10/3 options from an actual Kestrel-accepted socket. The updated
Docker image and isolated `compose.local.yaml` stack pass FUT-without-session 401,
secure Blaze redirector and content 200 checks. All four hosted game TLS listeners
log the 30/10/3 configuration; the persisted config retains idle_seconds=0.
Recreating the server with the environment override set to 0 disables the
configuration on that same state volume. Test certificates/volumes and ports are
separate from any live installation; the test containers were removed afterwards.

VPS probe exchanges and repeated native idle menu actions remain untested.
These socket-option and startup checks do not establish that the idle stall is
fixed. See [the deployment experiment](deploy/README.md#idle-connection-keepalive-experiment)
for activation, acceptance evidence and rollback. This changes no existing native
hosting acceptance gate.

### 6. Release verification and decision

- [ ] After implementation, run narrow helper/server tests, then `dotnet test localfut/server/Project21.LocalFut.sln` and `node localfut/verify-auth-bootstrap.mjs --self-test`. Run `powershell.exe -NoProfile -File localfut/Build-FIFA21-Launcher.ps1`, `Test-StartupScriptPortability.ps1`, `Test-LauncherProcessNames.ps1`, `Test-LauncherSettings.ps1`, and `Test-RepositoryHygiene.ps1` from the repository root (each with the same `powershell.exe -NoProfile -File localfut/` prefix). Run `node localfut/verify-startup-runtime.mjs --server "<absolute Project21-LocalFUT-Server.exe>"` against the built server. Use isolated runtime ports and throwaway PostgreSQL data for server tests; never reset a real club. Verify the running `serverBuildSha256`/profile before a live claim.
- [ ] Use `RUN-FIFA21.cmd` without a debugger for the final live test. Verify one-player login and hub, two players behind one NAT, cross-player rejection, disconnect/reconnect, and a completed two-client match. Measure end-to-end latency, jitter, packet loss, and reconnect time on the native route; use the same conditions for a bridge comparison if one is built. Do not infer capacity from two clients; benchmark server/session/relay load separately if the 1,000-player target is pursued.
- [ ] Hand off separate results for build, fixture, isolated server, and live game tests, with build hashes, timestamps, redacted request evidence, remaining failures, and the next reproducible test. Choose native direct hosting only after FUT, Blaze, Nucleus, and gameplay pass their identity and transport gates. If a gate cannot pass, keep the local bridge proposal in `../archive/cleanup-2026-09/hosted-connector.md` as an alternative to test, not an already proven solution.

**Current state of these gates:** the launcher-driven `FOS-SERVER` hook and synthetic session binding have passed
repeated single-player live FUT hub runs. The matched client build has passed separate and combined loopback
HTTPS runs for FUT and Nucleus. Gate 3 has isolated two-player server tests but no live two-client game test.
Discord-backed code issuance, certificate validation behavior, secure Blaze, authenticated gameplay UDP,
Internet deployment, and a two-client match remain **unconfirmed**.

### Remaining work after the HTTPS feasibility runs

Do these as separate gates; a successful hub screen covers login only. Use the existing loopback session mode
and TLS probe as test fixtures, then replace their synthetic issuer and optional HTTP listener for hosted use.

1. **Complete real account-service issuance and launch.** The hosted server verifies a
   short-lived assertion, checks Discord roles and access, and issues a code; the
   shared proof endpoint and launcher page have recorded integration results, but
   the complete `/client/launch`/client-pack path still needs native acceptance. Test wrong proof, expiry,
   role denial, blocking, restart and two accounts with redacted evidence. The local
   `localSessionTest` is a single-player fixture, not this path.
2. **Finish native TCP transport.** Prove Blaze `<secure>1</secure>` with a real TLS listener and verify the
   already-implemented pre-club `AUTH` check still binds the correct player. Test wrong, missing and expired
   credentials and reconnects. For FUT/Nucleus, test hosted certificate validation and wrong-certificate refusal,
   switch public game URLs to HTTPS as a required policy, and reject or confine plaintext credential routes. Trace
   the `/connect/token` response consumer before changing its local query-form behavior; the HTTPS probe proved
   transport, not successful token exchange. Trace remaining game-facing services before deciding which need TLS
   or a local forwarder.
3. **Bind gameplay and QoS traffic.** Trace a full native match's UDP destinations and relay allocation. The
   present relay's source-IP/port check is insufficient proof of same-NAT identity or confidentiality. Test two
   players behind one NAT, forged/wrong-side packets, NAT rebinding, disconnect and reconnect. If native UDP
   cannot provide the required identity and encryption, prototype the scoped bridge in `../archive/cleanup-2026-09/hosted-connector.md`
   and measure its extra latency in a real match.
4. **Prove the complete hosted path.** From two separate machines through the normal launcher, verify approved
   sign-in, distinct clubs behind the same public IP, cross-player rejection before club access, matchmaking,
   bidirectional UDP, match completion/reporting, persistence and reconnect. Measure latency, jitter, packet
   loss and failures. Record the running build and redacted wire evidence. A one-player hub run does not pass.
5. **Prepare release only after those gates.** Run the deferred full C# suite and isolated startup harness, then
   verify a clean build, upgrade without club reset, packaged certificates/secrets, deployment routing, backup and
   restore, and rollback. Keep build, fixture, isolated server and live-game results separate in the handoff.
