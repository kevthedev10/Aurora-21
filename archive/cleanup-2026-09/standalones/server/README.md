# Aurora 21 standalone server

The server snapshot is split into two cooperating components:

- `game-server/` runs the authenticated gateway, per-player FUT runtime, host-control agent, WireGuard peer management, matchmaking, shared market, and current FIFA 21 content implementation.
- `control-plane/` provides Discord authentication, administration, presence reporting, launcher media delivery, SQLite/D1 migrations, and the self-hosted VPS deployment package.

For the current VPS deployment, build the control-plane package with `control-plane/Build-VPS-Release.ps1`, then follow `control-plane/README.md`. Follow `game-server/README.md` for gateway and runtime configuration.

This source snapshot does not include live VPS environment files, private WireGuard keys, mutable club databases, uploaded launcher music/video, or generated runtime state.
