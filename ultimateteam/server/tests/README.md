# C# database tests

The supported upgrade baseline is migration **140**, the maintainer's deployed
schema as of 2026-10-04. Older migrations remain part of fresh installation, but
tests no longer maintain separate upgrade paths from versions below 140.

- Current behavior tests use `TestRuntime.Create()` or
  `PostgresTestCluster.CreateInstalledDatabase()`. Test observable behavior,
  persisted state, validation and restart preservation on the current schema.
- Supported upgrade tests use `Migration140Database.InstallAsync()` on an empty
  throwaway database. It applies the embedded base package and migrations 001–140
  forward, then loads historical affected rules from `Fixtures/Database140`.
  `SupportedDatabaseUpgradeTests` checks preserved balances, owned players and
  their search rows, unopened packs and rolled contents, objective XP/claims,
  settlements and stored/pending stadium
  items. It also checks transactional rollback, retry and ordinary restart.
  `SbcLiveTests` checks conversion of the old hidden-set settings and panel edits.
- Bootstrap tests keep fresh install, package validation, cancellation, rollback,
  concurrent installers, schema classification and repeated-install coverage.
  Failure injection covers schema creation, seeding and the dynamically selected
  latest migration, including cancellation, rather than one case per old migration.
  The template tests compare every current table's rows with a fresh install.
  `RelationalCatalogAndAccountTests` retains the full source catalogue comparison
  for player identities, metadata and resource migrations.

Do not recreate an old database by deleting migration ledger rows or putting
removed columns back on the current schema. This can replay incompatible SQL,
mask missing upgrade steps and overwrite unrelated settings. Add a supported
forward-built fixture when an upgrade needs coverage.

Keep migration-version assertions in infrastructure tests. Use the production
`CurrentVersion` for the destination and the fixed fixture version for the source;
ordinary feature tests should not require updating when another migration is added.
Use current bundled data or independent behavior expectations where appropriate,
not cumulative arithmetic for how every historical migration altered seed counts.

The projection performance probe also uses the current schema. It bulk-seeds
synthetic players and measures projection backfill and trigger costs without
rewinding migration history.

Run focused tests for changed areas while working. Run the full solution suite
before committing. All database tests use disposable PostgreSQL databases; never
supply the real local or VPS database connection.
