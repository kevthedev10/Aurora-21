# Project 21 server: fresh Ubuntu VPS guide

This guide starts with a new Ubuntu VPS and finishes with a private Project 21
server that Windows players reach through WireGuard. The VPS runs the game
services; Cloudflare runs only the small HTTPS login/admin panel. Cloudflare
Tunnel and WARP are not used.

The current recommended starting VPS is:

- Ubuntu 24.04 LTS, 64-bit x86 (`amd64`)
- 4 vCPU
- 8 GB RAM
- 75 GB or more SSD/NVMe storage
- 1 Gbps network connection
- provider DDoS protection
- one public IPv4 address

Ubuntu Desktop is supported. All setup is done in its Terminal app, so the same
instructions also work on Ubuntu Server. A 4-vCPU/8-GB VPS is a reasonable test
machine for the shared-runtime design, but do not advertise a guaranteed
750-player capacity until the tests near the end pass on the actual VPS.

## 1. Understand what will be public

The intended network path is:

```text
Player FIFA 21 -> player relay -> encrypted WireGuard tunnel
               -> VPS 10.203.21.1 -> shared game gateway -> that Discord user's club
```

Only the WireGuard endpoint, normally UDP port `51820`, is public. The FIFA
service ports bind to the private WireGuard address `10.203.21.1`, not the VPS's
public address. The included WireGuard firewall blocks access to SSH and other
VPS services from player tunnels. Between players, it permits only FIFA 21's
UDP 3659 match traffic inside `10.203.20.0/22`; every other forwarded port is
blocked.

Do not publicly open these private game ports:

```text
TCP 44321, 8090, 10853, 17502, 8081, 42231, 42232
UDP 17502
```

`bindAddress` selects the private server interface. The checked-in Cloudflare
host example uses `10.203.21.1`; `0.0.0.0` remains available for a trusted LAN.
`lsxMode` is `off` because EA App normally owns LSX on each launcher PC.

## Rivals gameplay relay

The checked-in server settings enable a payload-transparent UDP relay on
`31000-31199`. Matchmaking reserves two adjacent ports per match, learns each
client's source endpoint from the first datagram, and forwards subsequent
datagrams byte-for-byte. Relay allocations are bounded by the configured port
range and are released on cancellation, disconnect, game report, timeout, or
server shutdown.

`playerIdentityMode` is `peer-ip` for the trusted LAN/Tailscale path. This gives
each source IPv4 address isolated club state and distinct Blaze identifiers so
two computers can enter the same process-wide matchmaking queue. It is not
suitable when multiple players are hidden behind the same source address.

The relay address and range are host settings:

- `gameplayRelayEnabled`: enables or disables relay allocation.
- `gameplayRelayBindAddress`: `auto` uses `bindAddress`.
- `gameplayRelayAdvertisedAddress`: `auto` uses the local address of each
  accepted Blaze connection.
- `gameplayRelayPortStart` / `gameplayRelayPortEnd`: inclusive UDP range.

Run `Open-Firewall.ps1` again after changing the range. Every player must be
able to route UDP traffic to the advertised address and the full configured
range. UDP `17502` remains QoS only; it is not the gameplay relay.

The design reduces exposure but cannot make a public IP invulnerable. Keep the
VPS patched, use SSH keys, keep provider DDoS protection enabled, and never run
unrelated personal services on this VPS.

## 2. Collect the accounts and files

Before starting, have:

Required client-facing ports are TCP 44321, 8090, 10853, 17502, 8081, 42231,
and 42232; UDP 17502 for QoS; and UDP 31000-31199 for relayed gameplay.
Port-forwarding on an Internet router is neither needed nor recommended.

The optional authenticated gateway still starts one isolated server process per
Discord ID. A process-global matchmaking queue cannot pair across those
processes yet, so the current relay path is for the shared direct server used on
a trusted LAN or authenticated private VPN.

1. A fresh VPS with Ubuntu 24.04 LTS and its public IPv4 address.
2. The provider's browser console or the initial SSH username and password.
3. A free Cloudflare account.
4. A Discord application in the Discord Developer Portal.
5. Node.js 20 or newer installed on the Windows development PC.
6. The complete `standalone-server`, `standalone-launcher`, and
   `cloudflare-control-panel` folders.
7. A password manager for the two new random secrets created later.

The Discord client secret pasted previously must be rotated in the Discord
Developer Portal before production. Do not paste the replacement into source
files, chat, screenshots, or `wrangler.jsonc`. The former WARP service-token
credentials are no longer used and can be revoked/deleted.

## 3. Sign in to the fresh VPS

Use the provider's browser console for the first login. If SSH is already
enabled, Windows PowerShell can also connect with:

```powershell
ssh YOUR_INITIAL_USER@YOUR_VPS_IP
```

Replace both placeholders. Accept the host-key prompt only after checking the
fingerprint shown in the VPS provider's console.

If the provider gave only a root login, create a normal administrator:

```bash
adduser project21admin
usermod -aG sudo project21admin
```

Sign out and back in as `project21admin`. The installer later creates a separate
locked service account named `project21`; do not use that service account for
normal login.

## 4. Update Ubuntu and set the time zone

Run:

```bash
sudo apt update
sudo apt full-upgrade -y
sudo timedatectl set-timezone Europe/London
sudo reboot
```

Wait about a minute, reconnect, and confirm:

```bash
uname -m
lsb_release -ds
timedatectl status
```

`uname -m` should report `x86_64`. If it reports ARM, stop: this server package
has not been release-tested for an ARM VPS.

## 5. Set up an SSH key before disabling passwords

On the Windows PC, create a key if one does not already exist:

```powershell
ssh-keygen -t ed25519 -a 64
```

Press Enter to use the suggested file and choose a passphrase. Copy the public
key to the VPS:

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | ssh project21admin@YOUR_VPS_IP "umask 077; mkdir -p ~/.ssh; cat >> ~/.ssh/authorized_keys"
```

Open a second PowerShell window and prove key login works before changing SSH:

```powershell
ssh project21admin@YOUR_VPS_IP
```

Only after that succeeds, run these commands on the VPS:

```bash
sudo cp /etc/ssh/sshd_config /etc/ssh/sshd_config.before-project21
sudo tee /etc/ssh/sshd_config.d/99-project21.conf >/dev/null <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
EOF
sudo sshd -t
sudo systemctl reload ssh
```

Keep the current session open and test one more new SSH session. If the new
session fails, use the still-open session or provider console to restore the
backup. Never disable password login until key login has been verified.

## 6. Configure the basic firewall

On the VPS, allow SSH and WireGuard, then enable Ubuntu's firewall:

```bash
sudo apt install -y ufw
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow OpenSSH
sudo ufw allow 51820/udp
sudo ufw enable
sudo ufw status verbose
```

In the VPS provider's own firewall/security-group page, add:

- TCP `22` from your own public IP if the provider supports source restrictions.
- UDP `51820` from anywhere (`0.0.0.0/0`). Players can be on any network.

Remove broad rules such as "allow all TCP". Do not remove the SSH rule while
remote. If a different WireGuard port is chosen later, update both firewalls.

## 7. Upload the server folder

From Windows PowerShell, change to the folder that contains
`standalone-server`, then upload it:

```powershell
scp -r .\standalone-server project21admin@YOUR_VPS_IP:/home/project21admin/
```

On the VPS, move it into its permanent location and verify the files:

```bash
sudo mkdir -p /opt/project21
sudo mv /home/project21admin/standalone-server /opt/project21/
cd /opt/project21/standalone-server
ls ubuntu runtime-source
```

You should see `ubuntu/install.sh` and
`runtime-source/authenticated_gateway.py`.

## 8. Install the VPS services

From `/opt/project21/standalone-server`, run:

```bash
sudo bash ubuntu/install.sh
```

This installs Python, WireGuard and required packages; creates the restricted
`project21` service account; creates a Python virtual environment; and installs
the `project21-gateway` and `project21-agent` system services. It does not need
the Python process to be kept open in a terminal.

Do not run `ubuntu/configure.sh` yet. It needs the deployed control-panel URL and
a matching host-agent secret, which the next sections create.

## 9. Create the Discord application settings

Open the [Discord Developer Portal](https://discord.com/developers/applications),
select the Project 21 application, and do the following:

1. Under **OAuth2**, note the Client ID.
2. Reset/rotate the Client Secret because the former value was exposed.
3. Save the replacement only in a password manager.
4. Leave the redirect URL for the moment; the exact Worker URL is obtained below.

The control panel requests only Discord identity for login. Players do not log
in to WireGuard and do not receive VPS credentials.

## 10. Create and initially deploy the Cloudflare control panel

On the Windows development PC, open PowerShell in
`cloudflare-control-panel` and run:

```powershell
npm install
npx wrangler --version
npx wrangler login
npx wrangler whoami
npx wrangler d1 create project21-control
```

The last command prints a D1 `database_id`. Open `wrangler.jsonc` and replace:

```text
00000000-0000-0000-0000-000000000000
```

with that database ID. Also set `ADMIN_DISCORD_IDS` to the Discord IDs allowed
to administer the panel. Multiple IDs are comma-separated with no spaces, for
example:

```json
"ADMIN_DISCORD_IDS": "111111111111111111,222222222222222222"
```

Admin IDs are configuration, not secrets. Do not add quotation marks inside the
value. Leave the WireGuard placeholders temporarily, then run:

```powershell
npm run check
npx wrangler deploy --dry-run
npx wrangler deploy
```

Copy the resulting URL, normally similar to:

```text
https://project21-control-panel.YOUR-SUBDOMAIN.workers.dev
```

In `wrangler.jsonc`, replace `PUBLIC_BASE_URL` with that exact HTTPS URL.

## 11. Finish Discord OAuth

Return to the Discord Developer Portal, open the application's **OAuth2** page,
and add this redirect URL:

```text
https://YOUR-WORKER.workers.dev/oauth/callback
```

Use the exact Worker hostname copied above. It must match
`PUBLIC_BASE_URL`; do not add a trailing slash or use the example text.

## 12. Create the Worker secrets and database tables

Create two unrelated random secrets in PowerShell and store both in a password
manager:

```powershell
$bytes = New-Object byte[] 48
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
$generator.GetBytes($bytes)
[Convert]::ToBase64String($bytes)
$generator.Dispose()
```

Run it twice: one result is `SESSION_SECRET`, and the other is
`HOST_AGENT_SECRET`. The host secret must later be typed into the VPS exactly.

Set the Worker secrets interactively. Wrangler prompts for each value without
putting it in the command line:

```powershell
npx wrangler secret put DISCORD_CLIENT_ID
npx wrangler secret put DISCORD_CLIENT_SECRET
npx wrangler secret put SESSION_SECRET
npx wrangler secret put HOST_AGENT_SECRET
```

Apply all three D1 migrations and redeploy:

```powershell
npx wrangler d1 migrations apply project21-control --remote
npx wrangler deploy
```

Confirm `0001_initial.sql`, `0002_player_approval.sql`, and
`0003_wireguard.sql` are reported as applied. Do not configure the old
`WARP_AUTH_CLIENT_ID` or `WARP_AUTH_CLIENT_SECRET`; this version does not use
them.

## 13. Configure WireGuard and start the VPS

Return to the VPS:

```bash
cd /opt/project21/standalone-server
sudo bash ubuntu/configure.sh
```

Enter:

1. **HTTPS control-panel URL:** the exact `https://...workers.dev` URL.
2. **HOST_AGENT_SECRET:** the host secret saved in the password manager.
3. **VPS public IP or DNS name:** the public IPv4 address players can reach.
4. **WireGuard UDP port:** press Enter for `51820` unless both firewalls were
   deliberately configured for another UDP port.

The script creates the WireGuard private key on the VPS and never prints that
private key. At the end it prints two non-secret values:

```text
WIREGUARD_ENDPOINT=YOUR_VPS_IP:51820
WIREGUARD_SERVER_PUBLIC_KEY=...
```

Copy both lines. If configuration fails, fix the reported error and rerun the
same script; the existing VPS private key is reused.

## 14. Put the WireGuard public details into the Worker

On the Windows development PC, open `cloudflare-control-panel/wrangler.jsonc`.
Replace both placeholders with the values printed by the VPS:

```json
"WIREGUARD_ENDPOINT": "YOUR_VPS_IP:51820",
"WIREGUARD_SERVER_PUBLIC_KEY": "THE_PRINTED_PUBLIC_KEY"
```

Then validate and deploy:

```powershell
npm run check
npx wrangler deploy --dry-run
npx wrangler deploy
```

The public endpoint and WireGuard public key are safe to place in configuration.
Never copy `/etc/wireguard/project21-private.key` off the VPS except into an
encrypted administrator backup.

## 15. Configure the player launcher package

Open `standalone-launcher/control-plane.json` and set:

```json
{
  "enabled": true,
  "baseUrl": "https://YOUR-WORKER.workers.dev"
}
```

Use the same Worker URL as `PUBLIC_BASE_URL`. Keep these files and folders in
the package distributed to players:

- `RUN-FIFA21.cmd`
- `Project21-LocalFUT-Launcher.exe` and its `.dll`/`.json` companions
- `Project21-LocalAuthBridge.exe`
- all PowerShell scripts still present in `standalone-launcher`
- `runtime-source`
- `control-plane.json`, `remote-server.json`, and `wireguard-client.json`

Do not distribute the `standalone-server` folder, Worker secrets, VPS SSH key,
WireGuard server private key, `.secrets.json`, databases, or backups.

## 16. Verify all VPS services

On the VPS run:

```bash
cd /opt/project21/standalone-server
sudo bash ubuntu/status.sh
sudo wg show
sudo ss -lntup
sudo ufw status verbose
```

Expected service state:

- `wg-quick@wg0.service`: active
- `project21-gateway.service`: active
- `project21-agent.service`: active
- `wg0`: address `10.203.21.1/22`, listening on UDP `51820`
- FIFA ports listen only on `10.203.21.1`, not `0.0.0.0` or the public IP

From a computer outside the VPS, the public IP must not accept any of the FIFA
TCP ports listed in section 1. A listening socket alone proves only that the
service started; it does not prove a real FIFA client can log in or play.

## 17. Approve and test the first player

On a separate Windows PC, preferably on a different internet connection:

1. Copy only the configured `standalone-launcher` folder.
2. Double-click `RUN-FIFA21.cmd`.
3. In Settings, select the installed `FIFA21.exe` and save.
4. Choose **Connect Discord** and approve the Discord identity page.
5. Accept the Windows administrator prompt needed to install WireGuard.
6. The launcher will wait if the Discord user has not yet been approved.

As an administrator, open the Worker URL in a browser, sign in with an ID listed
in `ADMIN_DISCORD_IDS`, and choose **Approve** beside that player. Return to the
launcher and choose **Check Again** or **Launch FIFA 21**.

On the VPS, confirm a peer and recent handshake:

```bash
sudo wg show
sudo journalctl -u project21-agent -u project21-gateway -n 100 --no-pager
```

Test login, club loading and at least one real gameplay path. Repeat from a
second external network before giving the launcher to a larger group. Synthetic
tests and HTTP responses are not proof of native gameplay.

## 18. Day-to-day server management

### Quick status

```bash
cd /opt/project21/standalone-server
sudo bash ubuntu/status.sh
sudo wg show
```

### Start, stop or restart everything

```bash
sudo systemctl start wg-quick@wg0 project21-gateway project21-agent
sudo systemctl stop project21-agent project21-gateway wg-quick@wg0
sudo systemctl restart wg-quick@wg0 project21-gateway project21-agent
```

Usually restart only the affected component:

```bash
sudo systemctl restart project21-gateway
sudo systemctl restart project21-agent
sudo systemctl restart wg-quick@wg0
```

To immediately disconnect every active FIFA 21 Ultimate Team session without
deleting clubs, banning players, or removing their WireGuard access, run:

```bash
sudo project21-kick-all
```

The gateway stays online and players can reconnect normally afterward. Use the
emergency shutdown command later in this guide if players must remain offline.

To reset one player's club by Discord ID, run:

```bash
sudo project21-reset-club DISCORD_ID
```

The command briefly stops the gateway and host agent so the live SQLite database
cannot be rewritten during the reset. It moves only that Discord user's club
state into a dated directory under
`/opt/project21/standalone-server/state/club-reset-backups/`, restarts the
services, and lets the server create a fresh club when the player reconnects.
It does not ban the user or remove their WireGuard access. Everyone currently in
FUT is disconnected during the brief restart, so announce it before use.
The reset is refused while that player has an active transfer listing or is the
highest bidder on an active auction. Let those auctions finish and clear them
in-game first; this prevents real cards or escrowed coins being orphaned.

Restarting WireGuard briefly disconnects every player. Restarting the gateway
interrupts active FIFA sessions. Announce maintenance first.

### Read server logs

```bash
sudo journalctl -u project21-gateway -n 200 --no-pager
sudo journalctl -u project21-agent -n 200 --no-pager
sudo journalctl -u wg-quick@wg0 -n 100 --no-pager
```

Follow new gateway messages live with:

```bash
sudo project21-live-logs
```

This temporarily enables the complete safe activity stream: player connections,
disconnects, HTTP requests and responses, Blaze packets in both directions, UDP
traffic and matchmaking events. Authentication tokens and raw private payload
bodies are not printed. Press `Ctrl+C` to stop following; logging returns to its
normal level and the server continues running. Player launchers intentionally do
not expose the VPS logs or administrator tools.

To create a redacted archive that can be sent to a developer or staff member:

```bash
cd /opt/project21/standalone-server
sudo bash ubuntu/collect-support-bundle.sh
```

The command prints the archive path in the administrator's home folder. It
collects the last 24 hours of service logs plus service, socket, memory, disk,
firewall and WireGuard status. It redacts Discord IDs, authentication headers,
URL tokens and WireGuard public keys, and deliberately excludes club databases,
the host secret, WireGuard private keys and peer-state files.

Download the named archive from Windows PowerShell:

```powershell
scp project21admin@YOUR_VPS_IP:/home/project21admin/project21-support-TIMESTAMP.tar.gz .
```

Inspect the archive before sending it. Delete the VPS copy after the recipient
confirms receipt because operational logs can still reveal timings and network
metadata.

### Approve, remove approval, ban or unban

Open the Worker URL, log in with an administrator Discord account, and use the
Players table:

- **Approve** permits the Discord user to finish launcher enrollment.
- **Remove approval** revokes active device access and removes that user's peers.
- **Ban** revokes tokens, closes game access and removes all of that user's peers.
- **Unban** removes the ban, but the user may still need approval and a fresh
  launcher connection.

The host agent polls every five seconds, so revocation is not instantaneous.
Never edit WireGuard peer files manually while the agent is running.

### Give coins, packs or cards and end a session

The same administrator page has a **Grant content** form. Enter the player's
exact Discord ID, choose **Add coins**, **Grant pack**, **Grant card**, or
**Kick session**, and enter the requested amount, pack ID, or card resource ID.
The command is queued for the VPS host agent and its result appears in the
Commands table. Only use IDs verified for this FIFA 21 build; an HTTP success
means the command was queued, not that the item appeared in the native client.

These controls exist only in the Discord-protected administrator panel. They are
deliberately absent from the player launcher. Review the result and ask the
player to reopen the relevant in-game screen before repeating a command.

### Manage the shared online Transfer Market

Transfer Market listings are shared by every Discord-authenticated player on
this VPS. A player listing uses the seller's real club card. Bids reserve the
bidder's coins; an outbid player is refunded; an expired winning bid or Buy Now
moves that exact card into the buyer's Unassigned pile and pays the seller.
Players cannot buy their own auctions, list untradeable cards, or list a card
that is still referenced by a squad. Multiple saved squads are supported.

The shared listing ledger is stored at:

```text
/opt/project21/standalone-server/state/shared-transfer-market.sqlite3
```

It is included automatically by the `state` backup in section 19. Never delete,
replace, or edit this database while `project21-gateway` is running. The default
AI supply target is 500 active listings so the market remains usable before many
players begin selling. To change it, create a systemd override:

```bash
sudo systemctl edit project21-gateway
```

Enter the following, changing `500` to a value from `0` through `5000`:

```ini
[Service]
Environment=LOCALFUT21_MARKET_TARGET=500
```

Then save, reload, and restart only the gateway:

```bash
sudo systemctl daemon-reload
sudo systemctl restart project21-gateway
sudo systemctl is-active project21-gateway
```

Setting the value to `0` disables creation of new AI listings; it does not
delete existing listings or any player auction.

### Monitor CPU, memory, disk and sockets

```bash
top
free -h
df -h
sudo ss -s
sudo systemctl --failed
```

Install `htop` if preferred:

```bash
sudo apt install -y htop
htop
```

Investigate sustained memory above about 80%, heavy swapping, a full filesystem,
or repeated service restarts before admitting more players.

### Apply Ubuntu security updates

```bash
sudo apt update
sudo apt full-upgrade -y
sudo reboot
```

After reboot, run `sudo bash ubuntu/status.sh` and `sudo wg show` again.

## 19. Back up and restore

Club data is under:

```text
/opt/project21/standalone-server/state/players/<discord-id>/data/
```

Configuration is under `config`, while WireGuard server/peer state and the host
secret are protected root files. Create a root-only backup during maintenance:

```bash
sudo install -d -m 0700 /var/backups/project21
sudo systemctl stop project21-agent project21-gateway
sudo tar -czf /var/backups/project21/server-$(date +%Y%m%d-%H%M%S).tar.gz \
  /opt/project21/standalone-server/state \
  /opt/project21/standalone-server/config \
  /opt/project21/standalone-server/control-plane-host.json \
  /opt/project21/standalone-server/gateway-settings.json \
  /etc/wireguard \
  /etc/project21-host-agent-secret \
  /var/lib/project21-wireguard
sudo systemctl start project21-gateway project21-agent
sudo ls -lh /var/backups/project21
```

Copy the archive to encrypted storage that only administrators can access. It
contains club data, a server private key and the host secret.

Back up D1 separately from `cloudflare-control-panel` on the Windows PC:

```powershell
npx wrangler d1 export project21-control --remote --output project21-control-backup.sql
```

Before restoring, stop the agent and gateway, copy the archive to the VPS, list
its contents with `sudo tar -tzf ARCHIVE`, and confirm it contains only the paths
shown above. Then extract from `/`, fix ownership, and restart:

```bash
cd /
sudo tar -xzf /path/to/verified-backup.tar.gz
sudo chown -R project21:project21 /opt/project21/standalone-server/state /opt/project21/standalone-server/config
sudo systemctl restart wg-quick@wg0 project21-gateway project21-agent
```

Always keep the original archive until real player data has been verified.

## 20. Update the Project 21 server files

Schedule a maintenance window and make the backup above first. Upload the new
`standalone-server` folder to `/home/project21admin/standalone-server-update`,
then on the VPS run:

```bash
sudo systemctl stop project21-agent project21-gateway
sudo rsync -a \
  --exclude '.venv/' --exclude 'state/' --exclude 'config/' \
  --exclude 'control-plane-host.json' --exclude 'gateway-settings.json' \
  /home/project21admin/standalone-server-update/ \
  /opt/project21/standalone-server/
cd /opt/project21/standalone-server
sudo bash ubuntu/install.sh
sudo systemctl restart wg-quick@wg0 project21-gateway project21-agent
sudo bash ubuntu/status.sh
```

If `rsync` is missing, install it with `sudo apt install -y rsync`. Never replace
`state`, `config`, the host secret, or WireGuard keys with files from a release
archive.

For a control-panel update, from its Windows folder run:

```powershell
npm install
npm run check
npx wrangler d1 migrations apply project21-control --remote
npx wrangler deploy --dry-run
npx wrangler deploy
```

After any update, repeat the external launcher test. Keep the previous server
archive and use `npx wrangler versions list`/`npx wrangler rollback` if the Worker
must be rolled back.

## 21. Capacity test before a large rollout

These probes use disposable databases and do not modify real clubs:

```bash
cd /opt/project21/standalone-server
sudo -u project21 .venv/bin/python capacity_probe.py --players 750 --workers 4
sudo -u project21 .venv/bin/python protocol_load_probe.py --players 750 --workers 64
```

Run them during a maintenance window, not while players are active. Treat the VPS
as ready for a 750-player rollout only if both probes complete without failures,
the services stay healthy, RAM remains below roughly 80%, and the machine does
not swap heavily. Increase access gradually (for example 25, 100, 250, then 750
approved users) while watching actual load. The tests model connections and FUT
requests; they do not prove 750 simultaneous full matches or guarantee gameplay.

## 22. Troubleshooting checklist

### Player cannot complete Discord connection

1. Confirm `control-plane.json` in the player package has `enabled: true` and the
   exact Worker HTTPS URL.
2. Confirm the Discord redirect URL exactly matches `PUBLIC_BASE_URL` plus
   `/oauth/callback`.
3. Confirm the player appears in the web panel and is approved, not banned.
4. Run `npx wrangler tail` on the development PC while reproducing the error.

### WireGuard installs but never handshakes

1. Run `sudo wg show` on the VPS.
2. Confirm UDP `51820` is open in both UFW and the provider firewall.
3. Confirm the Worker has the correct public endpoint and server public key.
4. Confirm the host agent is active and has no authentication errors.
5. Confirm the player's network does not block outbound WireGuard UDP.

### Gateway will not start

```bash
sudo systemctl status project21-gateway --no-pager --full
sudo journalctl -u project21-gateway -n 200 --no-pager
sudo ss -lntup
```

Look for a port already in use, missing configuration, permissions errors, or a
failed Python dependency. Rerun `sudo bash ubuntu/install.sh` after restoring the
correct server files.

### Host agent reports unauthorized

The `HOST_AGENT_SECRET` stored in the Worker and
`/etc/project21-host-agent-secret` on the VPS do not match. Create a new secret,
update it with `npx wrangler secret put HOST_AGENT_SECRET`, rerun
`sudo bash ubuntu/configure.sh` with the same value, and verify both services.

### A banned player still appears in `wg show`

Wait at least ten seconds, then inspect the agent log. If the agent is stopped,
restart it. A ban revokes access in D1 and queues peer removal; the VPS host agent
must be healthy to apply that removal.

### Emergency shutdown

To immediately stop all player access while preserving data:

```bash
sudo systemctl stop project21-agent project21-gateway wg-quick@wg0
```

To restore service after investigating:

```bash
sudo systemctl start wg-quick@wg0 project21-gateway project21-agent
sudo bash /opt/project21/standalone-server/ubuntu/status.sh
```
