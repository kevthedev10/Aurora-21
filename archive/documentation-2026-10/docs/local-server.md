> Historical reference, archived on 2026-10-01. Paths, policies, runtime descriptions and test status below describe their original checkpoint. Use the [current guides](../../../README.md) for supported instructions.

# Local compatibility server scaffold

> Historical document: this Python scaffold and its tests now live under
> `archive/legacy/compat_server/` and `archive/legacy/tests/`. They are retained
> for provenance and are not part of the supported runtime or test suite. Use
> `ultimateteam/README.md` for the current C# server.

The repository includes a minimal, independently authored HTTP server that binds only to a literal loopback IP address. It exists so protocol handlers can be added as each native FIFA routing seam is validated. It does not currently emulate an EA or FUT service, alter authentication, or make outbound connections.

## Current behavior

The server exposes `GET /healthz` and `GET /v1/capabilities`. Every unknown route returns a generic `404` response, and `POST` returns `501` without echoing the request body. The process rejects wildcard, LAN, public, and hostname binds; accepted hosts are literal loopback addresses such as `127.0.0.1` and `::1`.

The original executable examples have been removed from the active instructions
because archived code is not a supported server or verification dependency.

The game is **not wired to this port yet**. Connection work must wait for the native FUT base-URL or local service-factory seam so the game can be directed to loopback through a supported configuration path rather than a security bypass.

## Local run options

| Approach | Tradeoffs | Cost | Setup complexity |
| --- | --- | ---: | --- |
| Manual console process | Smallest attack surface and easiest to debug; the user starts it before FIFA 21 and closes it afterward | $0 | Low |
| Windows background service or tray app | Starts automatically and can add status controls, but requires packaging, lifecycle management, and an uninstall path | $0 | Medium |

The manual process is the present scaffold. It can be packaged as a Windows service or tray application after the protocol and port are known; that packaging choice does not change the wire implementation.

## Implementation sequence

1. Trace the copied `OriginPresenceT` into the FIFA online-state field.
2. Identify the condition that loads `CardsDLL_Win64_retail.dll`.
3. Validate the native source of `FUT/SINGLE_BASEURL_%s` or another safe local routing seam.
4. Record the first observed FUT request shape with values redacted.
5. Implement only that route using synthetic local state and deterministic fixtures.
6. Keep the listener on loopback, deny outbound networking, and add rollback tests before connecting the game.
