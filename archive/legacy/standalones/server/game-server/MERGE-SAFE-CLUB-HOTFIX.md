# Project 21 merge-safe club hotfix

This overlay was built from the current VPS `runtime-source` snapshot. It does
not contain player state, configuration, secrets, logs, backups, or historical
`server.py` copies.

It updates only:

- the VPS-derived `server.py` plus its existing `gameplay_relay.py` dependency;
- the consumable-apply response envelope, using the FIFA 18 `itemData` root
  without speculative fields that can break FIFA 21's fixed-map parser;
- native position-modifier presentation data: base card asset `34`, definition
  ID kept separately, and the native string type such as `STCF` instead of the
  speculative numeric `consumableType=3`/`consumableId` pair;
- a narrow `CONSUMABLE-SCOUT` trace covering the apply request and related
  item/club/squad responses for 15 seconds, so any remaining native crash can
  be diagnosed without logging authentication headers or unrelated traffic;
- the FUT user projection so club names remain editable through the launcher
  without opening the crash-prone native onboarding rename screen;
- club query and rename actions;
- the host command dispatcher;
- the recoverable single/all-club reset helper and its narrow sudo rule.

Extract the archive over `/opt/project21/standalone-server`, run
`sudo bash ubuntu/install.sh`, then restart `project21-gateway` and
`project21-agent`. Club resets are moved under
`state/club-reset-backups`; they are not permanently deleted.
