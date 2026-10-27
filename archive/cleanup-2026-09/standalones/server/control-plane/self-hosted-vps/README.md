# Project 21 VPS control plane

This package replaces the Cloudflare Worker, D1 database, and R2 API dependency with a Node.js service, local SQLite, local media storage, Caddy HTTPS, and the existing Project 21 API contract.

## Before installation

1. Confirm `project21.duckdns.org` resolves to `15.235.81.121`.
2. In the Discord Developer Portal, add this exact OAuth redirect URL:
   `https://project21.duckdns.org/oauth/callback`
3. Open inbound TCP ports 80 and 443 in the VPS provider firewall and UFW.
4. Install Node.js 22+, npm, Caddy, OpenSSL, and (only if importing D1) sqlite3.

Ubuntu prerequisites:

```bash
sudo apt update
sudo apt install -y ca-certificates curl gnupg caddy openssl sqlite3 build-essential
curl -fsSL https://deb.nodesource.com/setup_22.x -o /tmp/nodesource_setup.sh
sudo -E bash /tmp/nodesource_setup.sh
sudo apt install -y nodejs
```

## Install

Upload and extract the ZIP, then run:

```bash
cd /home/ubuntu/Project21-VPS-Control-Plane-20260911
chmod +x install-vps.sh activate-vps.sh import-d1-export.sh
sudo ./install-vps.sh
sudo nano /etc/project21-control-plane.env
sudo ./activate-vps.sh
```

Copy the Discord application client ID and client secret into the environment file. The installer generates the session and host-agent secrets automatically and preserves the environment file on later upgrades.

The first self-hosted start intentionally enables **owner-only mode**. This prevents previously banned or unapproved users from regaining access before the D1 export is imported and checked. Sign in with an administrator account and disable owner-only mode in the panel only after the migration or manual access review is complete.

If UFW is active:

```bash
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
```

## Verification

```bash
sudo systemctl status project21-control-plane.service caddy project21-gateway.service project21-agent.service --no-pager
curl --fail http://127.0.0.1:8787/healthz
curl --fail https://project21.duckdns.org/healthz
curl --fail https://project21.duckdns.org/api/server/status
sudo journalctl -u project21-control-plane.service -n 100 --no-pager
```

`/healthz` checks only the control-plane process. `/api/server/status` also
requires fresh reports from the host agent and game gateway before returning
`"online":true`.

## Optional D1 data migration

Run this on the Windows source machine when Cloudflare D1 is available again:

```powershell
npx wrangler d1 export project21-control --remote --output project21-control-d1-export.sql
scp .\project21-control-d1-export.sql ubuntu@15.235.81.121:/home/ubuntu/
```

Then on the VPS:

```bash
sudo ./import-d1-export.sh /home/ubuntu/project21-control-d1-export.sql
```

The import script validates the dump and backs up the local SQLite database before replacing it.

## Local media migration

The separate `Project21-VPS-Media-Seed-20260911.zip` contains the current 25 music tracks, background video, and a verified local manifest. Upload and extract it on the VPS, then run:

```bash
cd /home/ubuntu/Project21-VPS-Media-Seed-20260911
chmod +x install-media.sh
sudo ./install-media.sh
```

This moves launcher media delivery off R2. It backs up an existing local media directory before installing the seed.

## Backups

Back up `/var/lib/project21-control-plane`. It contains the SQLite database and local media. SQLite WAL files should be included when copying a live database; a safer snapshot is:

```bash
sudo sqlite3 /var/lib/project21-control-plane/control-plane.sqlite3 ".backup '/home/ubuntu/project21-control-plane-backup.sqlite3'"
sudo tar -czf /home/ubuntu/project21-control-plane-media-backup.tar.gz -C /var/lib/project21-control-plane media
```
