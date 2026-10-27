# Project 21 control panel

The administrator Players table includes a live online count. A player is
shown online after FIFA 21 keeps an authenticated Blaze session open for ten
seconds; presence expires after thirty seconds if the VPS stops reporting.

This Cloudflare Worker provides Discord OAuth, launcher device authorization,
administrator actions, bans and an outbound host command queue. D1 stores
identities, device public keys and audit records. Game traffic uses WireGuard
directly between players and the VPS; it does not pass through Cloudflare.

## Public server status

`GET /api/server/status` returns the current authenticated game-gateway status
without exposing player identities. The response includes `online`,
`onlinePlayers`, host-agent and gateway report timestamps, and the 30-second
freshness window. The gateway is reported online only while both the VPS host
agent and the gateway's own presence heartbeat are fresh.

```powershell
Invoke-RestMethod https://YOUR_PANEL_HOST/api/server/status
```

This endpoint verifies the reporting gateway is alive; it does not by itself
prove that a full native FIFA login or match can complete.

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
6. The existing `DISCORD_GUILD_ID` and `DISCORD_AUTO_APPROVE_ROLE_IDS` values in
   `wrangler.jsonc` remain supported as the primary/legacy server. After
   deploying, owners can manage additional servers and roles in the web panel.
7. Add these Worker secrets interactively:

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

## Cloud launcher music and video

The launcher can download approved media from a private R2 bucket after device
authentication. Files are verified against `manifest.json`, cached under
`%LOCALAPPDATA%\Project21\MediaCache`, and played locally. Cached media remains
available during an outage; the launcher's bundled `sources` folder remains the
offline fallback. Cloud music is added to the existing shuffled playlist, while
the first enabled cloud video takes priority over the bundled hero video.

Only upload music and video you own or have permission to distribute. An R2
bucket being private does not replace the required copyright/licensing rights.

Create the bucket once from this directory:

```powershell
npm install
npx wrangler login
npx wrangler r2 bucket create project21-media
```

The `MEDIA` binding in `wrangler.jsonc` points at that bucket. Create the bucket
before deploying the Worker, then deploy the updated Worker:

```powershell
npm run check
npx wrangler deploy --minify
```

To add or replace a licensed MP3, choose a stable ID containing only letters,
numbers, dots, underscores, and hyphens:

```powershell
powershell.exe -NoProfile -File .\scripts\Publish-CloudMedia.ps1 `
  -Type music `
  -Id menu-theme-01 `
  -File "C:\Media\My Licensed Song.mp3"
```

For several songs at once, select the MP3 files in File Explorer and drag them
onto `ADD-MULTIPLE-SONGS.cmd`. The window remains open when it finishes and
reports any failed files. Each cloud ID is generated from its filename; for
example, `FIFA Menu Theme.mp3` becomes `fifa-menu-theme`.

To publish a hero video, use `-Type video` with an MP4, M4V, WMV, AVI, or MOV
file. The helper computes SHA-256 and byte size, uploads the object, updates
`media-manifest.json` without removing other entries, and finally uploads the
manifest. It creates that working manifest when needed;
`media-manifest.example.json` shows its initial shape. Reusing an ID replaces
that manifest entry.

To disable an item, set its `enabled` field to `false` in
`media-manifest.json`, then publish the manifest:

```powershell
npx wrangler r2 object put project21-media/manifest.json `
  --file .\media-manifest.json `
  --content-type application/json `
  --remote
```

Players must have completed **Connect Discord** so the launcher has a valid
device token. On the next launcher start it downloads changed files. If an item
does not appear, inspect Connection details for authorization, HTTP, size, or
integrity errors and confirm the Worker's `MEDIA` binding and R2 object key match
the manifest exactly.

Owners can also upload several MP3 files from the web control panel. Sign in with
an ID listed in `ADMIN_DISCORD_IDS`, open **Launcher music**, select the files,
and choose **Upload selected songs**. The browser calculates each SHA-256 before
uploading; the owner-only Worker endpoint streams the file to R2, asks R2 to
verify that checksum, and updates the launcher manifest. Each MP3 is limited to
80 MB. Deploy the latest Worker before using this panel feature.

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

Discord OAuth requests `identify` and `guilds.members.read`. This exposes only the
signing-in user's membership data for configured servers and does not grant
message access. A matching role or current booster is approved during login; the
matching server and reason are written to the audit log. Existing manual
approvals are never removed when someone no longer matches. A banned user remains
blocked even if they match a role or boost.

## Managing Discord servers

Sign in to the deployed panel with an ID listed in `ADMIN_DISCORD_IDS`. Under
**Discord servers**, enter a friendly name, the Discord server ID, and zero or
more comma/space-separated role IDs. Enable booster approval if current Server
Boosters should qualify, then select **Save server**. The form also edits existing
entries; a server can be disabled temporarily or removed.

To copy IDs, enable Discord **User Settings → Advanced → Developer Mode**, then
right-click the server or role and select **Copy ID**. The OAuth user must belong
to an enabled server and have a configured role or booster status to receive
automatic approval.

The primary server sourced from `wrangler.jsonc` is labelled in the panel.
Editing it creates a D1-managed override. To permanently remove that legacy
entry, remove `DISCORD_GUILD_ID` and `DISCORD_AUTO_APPROVE_ROLE_IDS` from
`wrangler.jsonc` and redeploy.

## Owner-only maintenance mode

Owners can turn on **Game access → Restrict the game service to owner Discord
IDs only** in the control panel. The Worker immediately rejects non-owner device
tokens and returns only owner peers to the VPS WireGuard synchronizer. Existing
player approvals and device registrations are preserved, so turning the switch
off restores normal access without making players sign in again.

Apply migration `0006_owner_only_mode.sql` before deploying this Worker version.
The switch covers the shared game service configured by `LOCALFUT_SERVER_ADDRESS`;
the Discord server entries remain membership/role approval sources.
