# Archived panel plans

Archived on 2026-10-01 after the planned React pages were implemented.
These documents preserve decisions, migration context and reported test history.
Their status summaries describe earlier checkpoints and are not current guidance.

| Original path | Archived document |
| --- | --- |
| `ultimateteam/panel-redesign-plan.md` | [React redesign history](panel-redesign-plan.md) |
| `ultimateteam/live-content-editing-plan.md` | [Live-editing design and history](live-content-editing-plan.md) |
| `ultimateteam/promo-groups-plan.md` | [Promo, bucket and odds design/history](promo-groups-plan.md) |
| `PACK-ODDS-SPEC.md` | [Earlier phase-pool/rating-band odds spec](PACK-ODDS-SPEC.md) |

Historical source comments and migration headers may refer to the original paths
and numbered steps above. Resolve those references here; do not renumber or alter
already-applied migrations to update a documentation reference.

Current entrypoints:

- [Repository README](../../README.md): run, build and test.
- [Contribution guide](../../CONTRIBUTION.MD): workflow and contracts.
- [Panel guide](../../ultimateteam/panel-web/README.md): development and browser review.
- [Panel API contract](../../ultimateteam/panel-api-contract.md): supported behavior.

React replaced the classic panel on 2026-10-01. It is served at `/panel/`;
former `/panel/next/` bookmarks redirect. The original classic-panel attribution
is preserved in [classic-panel-provenance.md](classic-panel-provenance.md).
Completed local game tests are in [game acceptance](../../ultimateteam/panel-web/test/game-acceptance.md).
Missing content editors and remaining acceptance work are tracked in
[the current panel TODO](../../todo.md).
