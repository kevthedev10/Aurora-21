# Project 21 control panel

This Cloudflare Worker provides Discord OAuth, launcher device authorization,
administrator actions, bans and an outbound host command queue. D1 stores
identities, device public keys and audit records. Game traffic uses WireGuard
directly between players and the VPS; it does not pass through Cloudflare.

## Security model

- Discord IDs are accepted only after Discord OAuth.
- Browser sessions use HttpOnly, Secure, SameSite cookies.
- Launcher tokens are signed, device-specific and checked against D1.
- Each launcher generates its WireGuard private key locally. D1 stores only the
  corresponding public key and private-network address.
- A ban or approval removal revokes devices, disconnects active game sockets and
  queues removal of that Discord user's WireGuard peers on the VPS.
- The host agent makes outbound HTTPS requests and authenticates with its own
  host secret.

## Initial setup

1. Create a Discord OAuth2 application and add this redirect URI exactly:
   `https://YOUR_PANEL_HOST/oauth/callback`.
2. Create a D1 database named `project21-control`:
   `npx wrangler d1 create project21-control`.
3. Put its database ID and the deployed HTTPS Worker URL in `wrangler.jsonc`.
4. Run `standalone-server/ubuntu/configure.sh` on the VPS. Copy the printed
   WireGuard endpoint and server public key into `wrangler.jsonc`.
5. Set `ADMIN_DISCORD_IDS` to a comma-separated list of Discord user IDs.
6. Add these Worker secrets interactively:

```powershell
npx wrangler secret put DISCORD_CLIENT_ID
npx wrangler secret put DISCORD_CLIENT_SECRET
npx wrangler secret put SESSION_SECRET
npx wrangler secret put HOST_AGENT_SECRET
```

Use independent random values of at least 32 characters for `SESSION_SECRET` and
`HOST_AGENT_SECRET`. The host-agent value must match the value entered during
Ubuntu configuration.

Apply all migrations and deploy:

```powershell
npm install
npm run catalog:build
npx wrangler d1 migrations apply project21-control --remote
npx wrangler deploy --minify
```

The former `WARP_AUTH_CLIENT_ID` and `WARP_AUTH_CLIENT_SECRET` secrets are not
used by this version and may be removed after deployment.

## Player flow

The player opens `RUN-FIFA21.cmd` and chooses **Connect Discord**. First use opens
Discord once and installs WireGuard with a Windows administrator prompt. The
launcher generates a key, registers only the public key, receives an assigned
address and installs a persistent Project 21 WireGuard tunnel. WireGuard itself
has no user login.

Each approved Discord ID owns a separate LocalFUT club database on the VPS.
Admin coin, pack and card grants are applied only to the selected Discord ID.
The card grant form searches the bundled FIFA 21 catalogue by player name,
team, rarity or resource ID and lets the administrator choose the exact card
version from a dropdown. Re-run `npm run catalog:build` after updating either
the server's `players.json` catalogue or its `custom-players.json` catalogue.
