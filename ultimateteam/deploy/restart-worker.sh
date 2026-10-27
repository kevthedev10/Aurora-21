#!/usr/bin/env bash
# Run on the VPS host as the deploy user. The only accepted request restarts this Compose project's server service.
# The panel writes a fixed filename; file contents never become command arguments.
set -euo pipefail
umask 027
cd "$(dirname "$0")"
requests=${RESTART_REQUESTS_DIR:-./restart-requests}

install_worker() {
    [[ -f .env && -f compose.yaml ]] || { echo "restart worker: install beside compose.yaml and .env" >&2; exit 1; }
    [[ "$requests" == ./restart-requests ]] || { echo "restart worker: install uses the Compose restart-requests directory" >&2; exit 1; }
    [[ "$(id -gn)" != docker ]] || { echo "restart worker: use a private primary group; docker must be supplementary" >&2; exit 1; }
    # A user service needs its manager to survive the deploying SSH session and start at boot.
    if [[ "$(loginctl show-user "$(id -un)" -p Linger --value)" != yes ]]; then
        loginctl --no-ask-password enable-linger "$(id -un)" || {
            echo "restart worker: run sudo loginctl enable-linger $(id -un) once, then rerun deployment" >&2
            exit 1
        }
    fi
    export XDG_RUNTIME_DIR="/run/user/$(id -u)"
    export DBUS_SESSION_BUS_ADDRESS="unix:path=$XDG_RUNTIME_DIR/bus"
    systemctl --user show-environment > /dev/null
    docker info > /dev/null

    # setgid makes host and app-created files inherit the deploy user's group. No world access.
    mkdir -p -- "$requests"
    chgrp "$(id -g)" "$requests"
    chmod 2770 "$requests"
    [[ ! -f "$requests/worker.heartbeat" ]] || chmod 640 "$requests/worker.heartbeat"
    local gid
    gid=$(id -g)
    if grep -q '^RESTART_WORKER_GID=' .env; then
        sed -i "s/^RESTART_WORKER_GID=.*/RESTART_WORKER_GID=$gid/" .env
    else
        printf '\nRESTART_WORKER_GID=%s\n' "$gid" >> .env
    fi

    local unit_dir deploy_dir
    unit_dir="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
    deploy_dir=$(pwd -P)
    # Escape quoted systemd paths, including specifier and ExecStart environment expansion.
    deploy_dir=${deploy_dir//\\/\\\\}
    deploy_dir=${deploy_dir//\"/\\\"}
    deploy_dir=${deploy_dir//%/%%}
    deploy_dir=${deploy_dir//\$/\$\$}
    mkdir -p -- "$unit_dir"
    cat > "$unit_dir/aurora21-restart-worker.service" <<EOF
[Unit]
Description=Aurora 21 panel restart worker

[Service]
Type=simple
ExecStart=/usr/bin/bash "$deploy_dir/restart-worker.sh" loop
Restart=always
RestartSec=5
UMask=0027

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload
    systemctl --user enable aurora21-restart-worker.service
    systemctl --user restart aurora21-restart-worker.service
    # Require evidence from the newly started worker, not an old heartbeat.
    local started
    started=$(date +%s)
    for _ in {1..10}; do
        if systemctl --user is-active --quiet aurora21-restart-worker.service &&
           [[ -f "$requests/worker.heartbeat" && "$(stat -c %Y "$requests/worker.heartbeat")" -gt "$started" ]]; then
            echo "restart worker: user service running, heartbeat ready"
            return
        fi
        sleep 1
    done
    echo "restart worker: no fresh heartbeat; see journalctl --user -u aurora21-restart-worker.service" >&2
    exit 1
}

if [[ "${1:-loop}" == install ]]; then install_worker; exit 0; fi
RESTART_WORKER_GID=$(id -g)
export RESTART_WORKER_GID
mkdir -p -- "$requests"
exec 9>"$requests/worker.lock"
flock -n 9 || { echo "restart worker: already running" >&2; exit 1; }
exec 8>"$requests/deploy.lock"
# A stopped worker may have left a request claimed. Do not repeat a potentially completed restart.
if [[ -f "$requests/restart-server.in-progress" ]]; then
    printf 'interrupted %s\n' "$(date -u +%FT%TZ)" > "$requests/worker.result"
    rm -f -- "$requests/restart-server.in-progress"
fi

tick() {
    date -u +%FT%TZ > "$requests/worker.heartbeat"
    if [[ -f "$requests/restart-server.request" && ! -f "$requests/restart-server.in-progress" ]]; then
        # A deploy has priority; leave the request queued until its image and config are settled.
        flock -n 8 || return 0
        mv -- "$requests/restart-server.request" "$requests/restart-server.in-progress"
        # Allow the API's 202 response to finish before its container is stopped.
        sleep 3
        if docker compose restart server; then
            printf 'success %s\n' "$(date -u +%FT%TZ)" > "$requests/worker.result"
        else
            printf 'failed %s\n' "$(date -u +%FT%TZ)" > "$requests/worker.result"
        fi
        rm -f -- "$requests/restart-server.in-progress"
        flock -u 8
    fi
}

case "${1:-loop}" in
    loop) while true; do tick; sleep 5; done ;;
    once) tick ;;
    *) echo "usage: restart-worker.sh [install|loop|once]" >&2; exit 2 ;;
esac
