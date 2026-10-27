#!/usr/bin/env bash
set -euo pipefail
systemctl --no-pager --full status \
  wg-quick@wg0.service \
  project21-gateway.service project21-agent.service
journalctl \
  -u wg-quick@wg0.service \
  -u project21-gateway.service -u project21-agent.service \
  -n 100 --no-pager
echo
wg show wg0
