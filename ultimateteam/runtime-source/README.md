# Bundled runtime data

This directory holds the catalogue seeds, shared configuration defaults, rules,
content art and original public trust-reference bytes used by the C# server.
It is not a separate runtime. Start the game through
[RUN-FIFA21.cmd](../RUN-FIFA21.cmd); see the [runtime guide](../README.md).

## State ownership

- `ultimateteam21/config.example.json` is the source of server defaults. Personal
  config belongs under `%LOCALAPPDATA%\Aurora21\UltimateTeam`.
- Catalogue and pack files seed fresh PostgreSQL databases. Editing a seed does
  not replace an existing database's published content.
- `ultimateteam21/rules/*.json` seeds PostgreSQL `content_rules` and supplies
  reviewed imports from the panel's Rules page.
- Club state belongs in PostgreSQL, normally under
  `%LOCALAPPDATA%\FIFA21UltimateTeam\postgres`; it is not bundled data.
- Legacy Python runtimes are archived under `archive/legacy/` and are never
  started, built or linked by the supported C# runtime.

Use staged panel edits or registered migrations for content changes. Preserve
owned cards, squads, rewards and history. Do not reset a club as part of an update.

## Trust resources

The launcher applies a build-checked trust patch to the running FIFA process.
It does not modify the game executable on disk. Original reference bytes and
exported public trust anchors are distributable; server CA/leaf private keys are
local or hosted state and must not be committed. For the native findings and
remaining client checks, see [hosting evidence](../native-hosting-investigation.md).
