#!/usr/bin/env bash
# Runs on the VPS in the deploy folder (the Deploy workflow copies it there with compose.yaml, pg-backup.sh and
# Caddyfile, and logs in to GHCR first):
#   IMAGE=ghcr.io/<owner>/aurora21-ultimateteam-server IMAGE_TAG=sha-1a2b3c4 bash remote-deploy.sh
# The operator's .env keeps the secrets; this script only records IMAGE and IMAGE_TAG in it, after the pull succeeds.
# Migrations run when the new server starts and are not undone by redeploying an older tag, so a "predeploy" database
# dump is taken first whenever the database is running.
set -euo pipefail
cd "$(dirname "$0")"

: "${IMAGE:?set IMAGE}" "${IMAGE_TAG:?set IMAGE_TAG}"
[[ "$IMAGE" =~ ^[a-z0-9./_-]+$ && "$IMAGE_TAG" =~ ^[A-Za-z0-9._-]{1,128}$ ]] || { echo "deploy: invalid image reference" >&2; exit 2; }
[[ -f .env ]] || { echo "deploy: .env is missing here; create it from .env.example first (see README.md)" >&2; exit 1; }
export IMAGE IMAGE_TAG
mkdir -p restart-requests
# Serialize image/config updates with panel restarts. The worker keeps publishing its heartbeat while waiting.
exec 8>restart-requests/deploy.lock
flock 8

log() { echo "deploy: $*"; }

# Replaces or appends KEY=value in .env, leaving every other line alone.
set_env() {
    local key=$1 value=$2
    if grep -q "^${key}=" .env; then
        sed -i "s|^${key}=.*|${key}=${value}|" .env
    else
        printf '%s=%s\n' "$key" "$value" >> .env
    fi
}

previous=$(sed -n 's/^IMAGE_TAG=//p' .env | tail -n 1)
# The app container shares only this host group with the worker, not its Docker access.
RESTART_WORKER_GID=$(id -g)
export RESTART_WORKER_GID
set_env RESTART_WORKER_GID "$RESTART_WORKER_GID"
bash ./restart-worker.sh install
log "deploying $IMAGE:$IMAGE_TAG (previous: ${previous:-none})"
docker compose pull --quiet server redirector
# Recorded now, so the operator commands in README.md (generate-certs, backups) use this image even if a check below stops.
set_env IMAGE "$IMAGE"
set_env IMAGE_TAG "$IMAGE_TAG"
# The caddy service (HTTPS for the launcher API) runs only once LAUNCHER_DOMAIN is set, and the loopback-only
# monitoring stack (Prometheus, exporters, Grafana) only once GRAFANA_ADMIN_PASSWORD is set.
env_value() { sed -n "s/^$1=//p" .env | tail -n 1; }
profiles=()
[[ -n "$(env_value LAUNCHER_DOMAIN)" ]] && profiles+=(launcher)
monitoring=false
if [[ -n "$(env_value GRAFANA_ADMIN_PASSWORD)" ]]; then profiles+=(monitoring); monitoring=true; fi
set_env COMPOSE_PROFILES "$(IFS=,; echo "${profiles[*]}")"

# The certificates are created once by the operator (generate-certs), because players install their trust anchors.
if ! docker compose run --rm --no-deps -T --entrypoint /bin/sh server -c 'test -s /data/certs/certs/spring18-cert.pem'; then
    echo "deploy: the certs volume has no server certificates; run generate-certs once first (see README.md)" >&2
    exit 1
fi

if docker compose ps --status running --services | grep -qx db; then
    log "taking a predeploy database dump"
    docker compose run --rm --no-deps -T backup once predeploy
else
    log "database not running yet; no predeploy dump"
fi

docker compose up -d --no-build --remove-orphans

# Health: FUT answers 401 without a session, the redirector hands out secure Blaze (both TLS on this host), and the
# launcher API answers its health check on loopback.
fut=000 redirect='' launcher=000
for _ in $(seq 1 60); do
    fut=$(curl -sk -m 5 -o /dev/null -w '%{http_code}' https://127.0.0.1:18443/ut/game/fifa21/user/credits || true)
    redirect=$(curl -sk -m 5 -X POST -d '<serverinstancerequest/>' https://127.0.0.1:42230/redirector/getServerInstance || true)
    launcher=$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:18447/client/health || true)
    if [[ "$fut" == 401 && "$redirect" == *'<secure>1</secure>'* && "$launcher" == 200 ]]; then
        log "healthy: FUT 401 without a session, redirector returns secure Blaze, launcher API answers"
        # Monitoring is checked but never fails a game deploy.
        if [[ "$monitoring" == true ]]; then
            # A changed prometheus.yml applies on reload; Grafana re-reads its provisioned dashboards by itself.
            docker compose kill -s HUP prometheus > /dev/null 2>&1 || true
            ready=000 grafana=000
            for _ in $(seq 1 15); do
                ready=$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:9090/-/ready || true)
                grafana=$(curl -s -m 5 -o /dev/null -w '%{http_code}' http://127.0.0.1:3000/api/health || true)
                [[ "$ready" == 200 && "$grafana" == 200 ]] && break
                sleep 2
            done
            if [[ "$ready" == 200 && "$grafana" == 200 ]]; then log "monitoring: Prometheus and Grafana answer on loopback"
            else echo "deploy: WARNING monitoring not ready (Prometheus $ready, Grafana $grafana); see docker compose logs prometheus grafana monitor-role" >&2; fi
        fi
        docker compose ps
        exit 0
    fi
    sleep 2
done

echo "deploy: not healthy after 120s (FUT status $fut, launcher API status $launcher); recent server log:" >&2
docker compose logs --no-color --tail 60 server redirector >&2 || true
echo "deploy: to roll back, redeploy the previous tag (${previous:-none}); a predeploy dump is in the backup folder" >&2
exit 1
