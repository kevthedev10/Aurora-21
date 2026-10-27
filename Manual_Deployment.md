# Aurora21 Manual VPS Deployment

This guide is the manual deployment fallback for the Aurora 21 hosted server.
The checked-in GitHub Actions workflows deploy only after a `main` push; use
[this automated deployment guide](ultimateteam/deploy/README.md) for that path.
A manual release is an explicit operator action, not a claim about current VPS state.

It assumes:

- The VPS is already prepared.
- Docker Engine and Docker Compose are installed.
- The `deploy` user exists and can use Docker.
- `/opt/aurora21` exists and is writable by `deploy`.
- The required firewall ports are already open.
- The deployment files are located in `ultimateteam/deploy/`.
- The Docker image is built locally and copied to the VPS.

---

## 1. Build the Docker image locally

From the repository root on your PC:

```powershell
$clientVersion = 'sha-' + (git rev-parse --short=7 HEAD)
docker build `
  --build-arg CLIENT_VERSION=$clientVersion `
  -t aurora21-ultimateteam-server:local `
  -f .\ultimateteam\deploy\Dockerfile `
  .\ultimateteam
```

`CLIENT_VERSION` names the client pack the image serves at `/client/install/`. The Aurora launcher reinstalls the pack
only when this version changes. A build without it is always called `local`, so players would keep an old pack, and an
old `Start-Aurora21.ps1`, after a fix. Give every build its commit's `sha-<7>`, as CI does.

Verify that the image exists:

```powershell
docker images aurora21-ultimateteam-server
```

You should see an image with the tag:

```text
aurora21-ultimateteam-server:local
```

---

## 2. Export the Docker image

Save the image to a tar file:

```powershell
docker save -o aurora21-server.tar aurora21-ultimateteam-server:local
```

---

## 3. Copy the image and deployment files to the VPS

From the repository root on your PC:

```powershell
scp -i $HOME\.ssh\aurora21_deploy `
  .\aurora21-server.tar `
  .\ultimateteam\deploy\compose.yaml `
  .\ultimateteam\deploy\pg-backup.sh `
  .\ultimateteam\deploy\remote-deploy.sh `
  .\ultimateteam\deploy\Caddyfile `
  .\ultimateteam\deploy\.env.example `
  deploy@185.172.175.227:/opt/aurora21/
```

Then connect to the VPS:

```powershell
ssh -i $HOME\.ssh\aurora21_deploy deploy@185.172.175.227
```

---

## 4. Load the Docker image on the VPS

On the VPS:

```bash
cd /opt/aurora21
docker load -i aurora21-server.tar
```

The local image is named:

```text
aurora21-ultimateteam-server:local
```

The current Compose setup expects:

```text
aurora21/ultimateteam-server:local
```

Tag the image accordingly:

```bash
docker tag aurora21-ultimateteam-server:local \
  aurora21/ultimateteam-server:local
```

Verify:

```bash
docker images | grep ultimateteam
```

Both names should point to the same image ID.

---

## 5. Create the VPS `.env`

Only do this the first time, or when recreating the environment file:

```bash
cd /opt/aurora21
cp .env.example .env
chmod 600 .env
```

Generate a PostgreSQL password using hexadecimal characters only:

```bash
openssl rand -hex 32
```

Edit the environment file:

```bash
nano .env
```

At minimum, configure:

```env
POSTGRES_PASSWORD=<generated-password>
PUBLIC_IPV4=185.172.175.227
LAUNCHER_DOMAIN=
```

For the initial server deployment, these can remain empty:

```env
LAUNCH_ISSUER=
LAUNCH_ES256_PUBLIC_KEY=
LAUNCH_HS256_SECRET=
DISCORD_BOT_TOKEN=
```

For the manually loaded Docker image, use:

```env
IMAGE=aurora21/ultimateteam-server
IMAGE_TAG=local
```

`LAUNCHER_DOMAIN` can remain empty until public launcher HTTPS/DNS is configured.

After editing:

```bash
chmod 600 .env
```

Validate the Compose configuration:

```bash
docker compose config
```

Do not continue if this reports an interpolation or configuration error.

---

## 6. Generate game-server certificates

This is normally required only once.

Stop any partially running stack:

```bash
docker compose down
```

Generate the certificates:

```bash
docker compose run --rm server generate-certs
```

The certificates are stored in the persistent Docker `certs` volume.

Do **not** use `generate-certs --force` during normal redeployments. Regenerating the certificate authority changes the trust anchors used by installed launchers.

---

## 7. Export public trust anchors

For diagnostics and launcher integration:

```bash
mkdir -p trust-anchors

docker compose run --rm --user root \
  -v "$PWD/trust-anchors:/export" \
  server export-trust-anchors --out /export
```

Only public trust anchors should be exported. Do not export private keys.

---

## 8. Start the stack

Start all configured services:

```bash
docker compose up -d
```

Check container status:

```bash
docker compose ps
```

---

## 9. Check logs

Server:

```bash
docker compose logs --tail=100 server
```

Redirector:

```bash
docker compose logs --tail=100 redirector
```

PostgreSQL:

```bash
docker compose logs --tail=100 db
```

All services should remain running rather than repeatedly restarting.

---

## 10. Check the internal health endpoint

The launcher-facing client API is bound to loopback on the VPS.

Check it with:

```bash
curl -i http://127.0.0.1:18447/client/health
```

Public HTTPS is a separate step and requires `LAUNCHER_DOMAIN` plus working DNS.

---

## 11. Aurora launcher values (live since 2026-09-28)

The Aurora launcher's FIFA 21 page needs the public launcher API, the account service's signing key and the Discord
bot. On this VPS `.env` holds the following. The token itself is never committed.

```env
LAUNCHER_DOMAIN=21.aurorafut.com
COMPOSE_PROFILES=launcher
LAUNCH_ISSUER=https://15.aurorafut.com
LAUNCH_ES256_PUBLIC_KEY=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhgI3f2Nm4TsUeSCdJ3nAjgNHKhQ0J+UrwdHfY12KmSu5xDkNRra2GfzaBu/Ge+A6AdCDeK3F9bNnLqqdZIFeoA==
LAUNCH_HS256_SECRET=
DISCORD_BOT_TOKEN=<the Aurora bot token, the same bot Aurora 13 and 16 use>
```

- `COMPOSE_PROFILES=launcher` is what starts the `caddy` service in a manual `docker compose up -d`. The Actions
  deploy sets it through `remote-deploy.sh`; a manual deploy does not.
- `21.aurorafut.com` is a Cloudflare A record for the VPS, DNS only (not proxied). Caddy gets its Let's Encrypt
  certificate over ports 80 and 443.
- Keep `.env` LF-only. Docker Compose strips a trailing CR, but `grep`, `sed` and scripts do not.
- [`ultimateteam/deploy/set-launcher-env.sh`](ultimateteam/deploy/set-launcher-env.sh) sets these values idempotently,
  with a timestamped backup. It reads the bot token from standard input, so the token never appears on a command line or
  in a log. Copy it next to `.env` and run it as `deploy`.
- A changed `.env` takes effect only after `docker compose up -d` recreates the server, which disconnects players.
- Maintenance, its message, the Discord server, play roles, bypass roles, bypass accounts and blocks live in PostgreSQL.
  They survive restarts and deploys. Change them in the panel's Maintenance tab through the SSH tunnel, not in `.env`.

---

## 12. Panel images (card frames, players, badges, flags)

The operator panel shows card art, from `ultimateteam/panel-assets/` in the checkout. The folder is excluded from
the Docker build context; copy it to `/opt/aurora21/panel-assets`, which `compose.yaml` mounts read-only into the
server. The server serves it only on the private panel port (`/panel/assets/...`).

**First time only:** the mount is in the `compose.yaml` from the live-content-editing update. If the VPS still has an
older `compose.yaml`, copy the new one (step 3) and run `docker compose up -d` once, which recreates the server and
disconnects players.

On your PC, with Aurora 21 running locally (the script reads the card, club, league and nation ids from your local
database) and Python with psycopg installed (`python -m pip install "psycopg[binary]"`):

```powershell
python .\ultimateteam\download-card-images.py --limit 1   # quick trial: one of each category
python .\ultimateteam\download-card-images.py             # everything; resumes if stopped
```

The images go to `ultimateteam/panel-assets/` by default. Many cards have no separate special-card image,
and some card-frame names are reconstructed, so `404` counts in the summary are expected. The script stops by itself
if Futbin answers `403` or `429`; run it again later and it continues where it stopped.

Pack the folder into one archive and copy it:

```powershell
tar -czf panel-assets.tgz -C .\ultimateteam panel-assets
scp -i $HOME\.ssh\aurora21_deploy `
  .\panel-assets.tgz `
  deploy@185.172.175.227:/opt/aurora21/
```

On the VPS, unpack it next to `compose.yaml` and make it readable for the container:

```bash
cd /opt/aurora21
tar -xzf panel-assets.tgz
chmod -R a+rX panel-assets
rm panel-assets.tgz
```

No restart is needed: the server reads each image when the panel asks for it. Check it through the panel port:

```bash
curl -s http://127.0.0.1:18448/panel/api/assets
```

It answers `"available": true` with a count per category. Without images the panel shows text instead of art.

---

# Updating the server later

For normal manual updates, certificates and persistent volumes should remain untouched.

## On your PC

Rebuild, with a new client pack version (see step 1):

```powershell
$clientVersion = 'sha-' + (git rev-parse --short=7 HEAD)
docker build `
  --build-arg CLIENT_VERSION=$clientVersion `
  -t aurora21-ultimateteam-server:local `
  -f .\ultimateteam\deploy\Dockerfile `
  .\ultimateteam
```

Export:

```powershell
docker save -o aurora21-server.tar aurora21-ultimateteam-server:local
```

Copy the new image to the VPS:

```powershell
scp -i $HOME\.ssh\aurora21_deploy `
  .\aurora21-server.tar `
  deploy@185.172.175.227:/opt/aurora21/
```

If deployment files changed as well, copy them too:

```powershell
scp -i $HOME\.ssh\aurora21_deploy `
  .\ultimateteam\deploy\compose.yaml `
  .\ultimateteam\deploy\pg-backup.sh `
  .\ultimateteam\deploy\remote-deploy.sh `
  .\ultimateteam\deploy\Caddyfile `
  .\ultimateteam\deploy\.env.example `
  deploy@185.172.175.227:/opt/aurora21/
```

## On the VPS

Load the updated image:

```bash
cd /opt/aurora21
docker load -i aurora21-server.tar
```

Refresh the Compose-compatible tag:

```bash
docker tag aurora21-ultimateteam-server:local \
  aurora21/ultimateteam-server:local
```

Recreate the services with the updated image:

```bash
docker compose up -d --force-recreate
```

Check status:

```bash
docker compose ps
```

Check logs:

```bash
docker compose logs --tail=100 server
```

## Refreshing the panel images

Only needed when cards were added, or to fill in images a previous run could not get. The server does not need to be
updated or restarted for this. On your PC, run the download again. It only fetches what is missing, then repeat the
pack, copy and unpack commands from step 12:

```powershell
python .\ultimateteam\download-card-images.py
tar -czf panel-assets.tgz -C .\ultimateteam panel-assets
scp -i $HOME\.ssh\aurora21_deploy .\panel-assets.tgz deploy@185.172.175.227:/opt/aurora21/
```

```bash
cd /opt/aurora21 && tar -xzf panel-assets.tgz && chmod -R a+rX panel-assets && rm panel-assets.tgz
```

---


## Building on the VPS instead

With no Docker on your PC, the VPS can build the image; it has the room and the cores. This is how `582c029` was
deployed on 2026-09-28:

```bash
# On your PC: send the commit's ultimateteam folder
git archive <commit> ultimateteam | ssh deploy@185.172.175.227 'd=/opt/aurora21/build/<sha7>; mkdir -p "$d" && tar -x -C "$d"'
# On the VPS
cd /opt/aurora21/build/<sha7>/ultimateteam
docker build --build-arg CLIENT_VERSION=sha-<sha7> -f deploy/Dockerfile -t aurora21/ultimateteam-server:sha-<sha7> .
cd /opt/aurora21
docker tag aurora21/ultimateteam-server:local aurora21/ultimateteam-server:before-<sha7>   # rollback point
docker compose run --rm --no-deps -T backup once predeploy
docker tag aurora21/ultimateteam-server:sha-<sha7> aurora21/ultimateteam-server:local
docker compose up -d --no-build --remove-orphans
```

Then run the health checks `remote-deploy.sh` uses: FUT answers 401 without a session, the redirector returns
`<secure>1</secure>`, and `/client/health` answers 200. To roll back, tag `before-<sha7>` as `:local` again and run
`docker compose up -d`.

---

# Giving another developer deployment access

The `deploy` Linux account is intended to be a shared deployment/service account for Aurora21. It is not tied to one specific developer.

Do **not** share an existing private SSH key between developers. Each person should generate their own SSH keypair and add only their public key to the VPS.

For a small trusted team, using the same `deploy` account keeps the deployment commands and file permissions simple.

## On the new developer's PC

Generate a dedicated key:

```powershell
ssh-keygen -t ed25519 -C "aurora21-deploy-<name>" -f $HOME\.ssh\aurora21_deploy
```

The developer keeps this file private:

```text
~/.ssh/aurora21_deploy
```

Their public key is:

```text
~/.ssh/aurora21_deploy.pub
```

Display it:

```powershell
Get-Content $HOME\.ssh\aurora21_deploy.pub
```

They should send only that `ssh-ed25519 ...` public-key line to an administrator who already has access to the VPS.

## On the VPS

An existing administrator can append the new public key to:

```text
/home/deploy/.ssh/authorized_keys
```

For example:

```bash
echo 'ssh-ed25519 AAAA... aurora21-deploy-alice' >> /home/deploy/.ssh/authorized_keys
```

Then ensure the permissions remain correct:

```bash
chown -R deploy:deploy /home/deploy/.ssh
chmod 700 /home/deploy/.ssh
chmod 600 /home/deploy/.ssh/authorized_keys
```

Each key should preferably have a useful comment at the end, such as:

```text
ssh-ed25519 AAAA... aurora21-deploy-hylke
ssh-ed25519 AAAA... aurora21-deploy-alice
```

This makes it much easier to identify and revoke individual keys later.

## Test access

The new developer can then connect with:

```powershell
ssh -i $HOME\.ssh\aurora21_deploy deploy@185.172.175.227
```

Verify:

```bash
whoami
docker ps
touch /opt/aurora21/test && rm /opt/aurora21/test
```

Expected:

```text
deploy
```

and both Docker access and write access to `/opt/aurora21` should work without `sudo`.

## Revoking one developer

To remove somebody's access, edit:

```bash
nano /home/deploy/.ssh/authorized_keys
```

and delete only that person's public-key line.

There is no need to rotate everybody else's keys.

## Alternative: separate Linux users

If stronger per-user auditing or separation is needed, each developer can instead receive their own Linux account.

However, anyone added to the `docker` group effectively has root-level control of the VPS, so separate usernames do **not** create a strong security boundary if all of them can administer Docker.

For this project, the simpler approach is:

- one shared `deploy` Linux account;
- one unique SSH keypair per developer;
- never share private keys;
- remove individual public keys when access should be revoked.


# Useful commands

Show running containers:

```bash
docker compose ps
```

Show all Docker containers:

```bash
docker ps -a
```

Follow server logs:

```bash
docker compose logs -f server
```

Restart the server container:

```bash
docker compose restart server
```

Stop the stack:

```bash
docker compose down
```

Start the stack:

```bash
docker compose up -d
```

Validate Compose configuration:

```bash
docker compose config
```

List Docker volumes:

```bash
docker volume ls
```

List Aurora21 images:

```bash
docker images | grep -E 'aurora21|ultimateteam'
```

---

# Important notes

- Do not use `docker compose down -v` during normal deployment. That removes persistent volumes, including the database and certificates.
- Keep `/opt/aurora21/.env` on the VPS. Do not copy it into the repository.
- Keep `.env` mode set to `600`.
- `18447`, `18448`, and PostgreSQL should not be exposed publicly.
- `LAUNCHER_DOMAIN`, `COMPOSE_PROFILES`, `LAUNCH_ISSUER`, `LAUNCH_ES256_PUBLIC_KEY` and `DISCORD_BOT_TOKEN` are set on the live VPS (section 11). Keep them when recreating `.env`.
- For production automation, the intended long-term approach is to publish the image to GHCR and let GitHub Actions deploy it to the VPS.
