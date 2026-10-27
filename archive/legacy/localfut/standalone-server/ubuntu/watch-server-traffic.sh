#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
    echo "Run this command with sudo." >&2
    exit 1
fi

service=project21-gateway.service
if ! systemctl is-active --quiet "$service"; then
    echo "Project 21 gateway is not running." >&2
    echo "Check it with: sudo systemctl status project21-gateway --no-pager --full" >&2
    exit 1
fi

restore_normal_logging() {
    if [[ -n ${signal_helper:-} ]]; then
        kill "$signal_helper" 2>/dev/null || true
        wait "$signal_helper" 2>/dev/null || true
    fi
    systemctl kill --kill-who=main --signal=SIGUSR2 "$service" 2>/dev/null || true
}
trap restore_normal_logging EXIT INT TERM

echo "Project 21 live player traffic"
echo "Shows connections, requests, responses, Blaze packets, UDP traffic and matchmaking."
echo "Authentication tokens and raw private request bodies are not printed."
echo "Press Ctrl+C to stop. The server will keep running."
echo

# Reapply detailed logging if systemd automatically restarts the gateway while
# this viewer remains open.
(
    while true; do
        systemctl kill --kill-who=main --signal=SIGUSR1 "$service" 2>/dev/null || true
        sleep 5
    done
) &
signal_helper=$!

journalctl \
    --unit=project21-gateway.service \
    --follow \
    --lines=50 \
    --output=short-iso \
    --no-pager
