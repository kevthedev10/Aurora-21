#!/usr/bin/env bash
# PostgreSQL backups for the Aurora 21 stack. Runs in the `backup` service (the postgres:18 image, so pg_dump matches
# the server), which mounts the backup folder at /backups and reaches the database through PGHOST/PGUSER/PGPASSWORD.
#   pg-backup.sh loop           one dump a day at BACKUP_HOUR_UTC; keeps the newest BACKUP_KEEP_DAILY (default 7); also a
#                               "manual" dump whenever the operator panel drops a *.request file in BACKUP_REQUESTS
#   pg-backup.sh once [label]   one dump now (deploys use "predeploy"); keeps the newest BACKUP_KEEP_OTHER per label
# Only complete, readable dumps count toward retention, so failing nights never delete the last good backups.
set -uo pipefail
umask 077

dir=/backups
keep_daily=${BACKUP_KEEP_DAILY:-7}
keep_other=${BACKUP_KEEP_OTHER:-5}
hour=${BACKUP_HOUR_UTC:-3}
requests=${BACKUP_REQUESTS:-}

log() { echo "backup: $(date -u +%FT%TZ) $*"; }

dump() {
    local label=$1 file
    file="$dir/$label-$(date -u +%Y%m%dT%H%M%SZ).dump"
    if ! pg_dump --format=custom --compress=6 --file="$file.partial"; then
        rm -f -- "$file.partial"; log "FAILED: pg_dump for $label"; return 1
    fi
    # A truncated or corrupt archive fails to list; never keep one as a backup.
    if ! pg_restore --list "$file.partial" > /dev/null; then
        rm -f -- "$file.partial"; log "FAILED: $label dump is not a readable archive"; return 1
    fi
    mv -- "$file.partial" "$file"
    log "wrote $file ($(du -h -- "$file" | cut -f1))"
}

prune() {
    local label=$1 keep=$2 old
    # Names sort by their UTC timestamp, so the oldest come first.
    find "$dir" -maxdepth 1 -name "$label-*.dump" -printf '%f\n' | sort | head -n "-$keep" | while read -r old; do
        rm -f -- "$dir/$old"; log "removed $old"
    done
}

# Takes one dump per request the panel left; the request is removed first, so a failing dump is not retried forever.
handle_requests() {
    [[ -n "$requests" && -d "$requests" ]] || return 0
    local request
    for request in "$requests"/*.request; do
        [[ -e "$request" ]] || return 0
        rm -f -- "$request"
        log "the operator panel asked for a backup"
        dump manual && prune manual "$keep_other"
    done
}

seconds_until_next_run() {
    local now next
    now=$(date -u +%s)
    next=$(date -u -d "$(date -u +%F) $(printf '%02d' "$hour"):00" +%s)
    if (( next <= now )); then next=$(( next + 86400 )); fi
    echo $(( next - now ))
}

[[ "$keep_daily" =~ ^[1-9][0-9]*$ && "$keep_other" =~ ^[1-9][0-9]*$ ]] || { log "FAILED: retention counts must be positive integers"; exit 2; }
[[ "$hour" =~ ^([01]?[0-9]|2[0-3])$ ]] || { log "FAILED: BACKUP_HOUR_UTC must be 0-23"; exit 2; }
[[ -d "$dir" && -w "$dir" ]] || { log "FAILED: $dir is not a writable folder"; exit 2; }
rm -f -- "$dir"/*.partial

case "${1:-loop}" in
    once)
        label=${2:-manual}
        [[ "$label" =~ ^[a-z][a-z0-9-]*$ && "$label" != daily ]] || { log "FAILED: label must be lowercase and not 'daily'"; exit 2; }
        dump "$label" && prune "$label" "$keep_other"
        ;;
    loop)
        trap 'log "stopping"; exit 0' TERM INT
        log "daily dumps at $(printf '%02d' "$hour"):00 UTC, keeping $keep_daily"
        # Catch up after downtime: dump now when the newest daily dump is older than 23 hours.
        if [[ -z "$(find "$dir" -maxdepth 1 -name 'daily-*.dump' -mmin -1380 -print -quit)" ]]; then
            dump daily && prune daily "$keep_daily"
        fi
        next=$(( $(date -u +%s) + $(seconds_until_next_run) ))
        while true; do
            sleep 15 & wait $!
            handle_requests
            if (( $(date -u +%s) >= next )); then
                dump daily && prune daily "$keep_daily"
                next=$(( $(date -u +%s) + $(seconds_until_next_run) ))
            fi
        done
        ;;
    *)
        echo "usage: pg-backup.sh loop | once [label]" >&2; exit 2
        ;;
esac
