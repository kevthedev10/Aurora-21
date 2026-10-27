# Aurora 21 archive

This directory contains retired implementations, historical tooling and earlier
documentation retained for provenance. Nothing under `archive/` is part of the
supported launcher, C# server build, deployment image, or required test suite.

## Layout

- `documentation-2026-10/` — research, handoffs and status reports retired from
  the active documentation on 2026-10-01;
  [index](documentation-2026-10/README.md).
- `panel-plans/` — implemented React panel, live-editing and promo plans, and the
  removed classic panel's provenance; [index](panel-plans/README.md).
- `cleanup-2026-10/ultimateteam/deploy/launch/` — the launch-day production scripts of 2026-09-29 (club wipe,
  launch store, phase-1 and consumables store). They ran once; migrations 095-097 replaced that store, so they
  cannot run against a current database, and re-running the store scripts would remove the current store packs.
- `cleanup-2026-09/` — files removed in the September 2026 cleanup, kept at their
  original repository-relative paths. This includes the former
  `standalones/server/control-plane/` account/approval source, which is not
  integrated with the C# server.
- `legacy/compat_server/` — the original minimal Python compatibility scaffold.
- `legacy/localfut/runtime-source/` — Python runtime and helper scripts replaced
  by the C# server. Runtime catalogues, rules, certificates, and content assets
  remain active under `ultimateteam/runtime-source/`.
- `legacy/localfut/standalone-*` — older duplicated player/server packages.
- `legacy/localfut/cloudflare-control-panel/` — an older duplicated control-plane copy.
- `legacy/standalones/launcher/` and
  `legacy/standalones/server/game-server/` — the former Python-based public
  launcher/game-server distribution.
- `legacy/tools/` and `legacy/tests/` — Python reverse-engineering utilities and
  their tests. The active C#/Node catalogue miner remains under
  `tools/fifa21-data-miner/`.

Archived files are reference material, not agent instructions or current
contracts. Do not copy them back over active C# files wholesale. If an archived
behavior is needed, compare the exact relevant code and port the smallest
verified contract with tests.

`legacy/` predates the Aurora 21 rename: it preserves the original `localfut/`
and `standalones/` repository-relative paths so historical references can be
resolved mechanically. Current documentation is listed in
[docs/README.md](../docs/README.md).
