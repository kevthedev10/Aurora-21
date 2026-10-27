# Aurora 21 (Alpha v1) player package notes

Give players the complete configured `standalone-launcher` folder and direct
them to `README.md`. They should start it only with `RUN-FIFA21.cmd`.

Before distribution, the administrator must set `control-plane.json` to:

```json
{
  "enabled": true,
  "baseUrl": "https://YOUR-WORKER.workers.dev"
}
```

The player-facing interface contains only:

- Discord/WireGuard connection setup and status
- automatic Python 3.12 installation and dependency verification
- FIFA 21 executable selection
- Launch FIFA 21
- Stop the player's local support processes

It intentionally has no remote-server controls, server logs, card or coin
grants, club-name editing, reset tools, content catalogues, or administrator
functions. The small bundled relay, certificate material, trust helper and
WireGuard scripts are still required for FIFA 21 to reach the VPS and must not
be removed from the distributed folder.

The official WireGuard installer is accepted only when its WireGuard LLC
Authenticode signature is valid. Each player's WireGuard private key is
generated on that player's PC; only the public key is registered. Discord bans
and approval removal revoke launcher tokens and cause the VPS agent to remove
that Discord user's WireGuard peers.

The distributable ZIP also omits the bundled development certificate private
keys. Its Python bootstrap generates unique local connection certificates
and replacement public moduli on the player's PC before the first launch.
