#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this activation script with sudo." >&2
  exit 1
fi

env_file="/etc/project21-control-plane.env"
server_root="/opt/project21/standalone-server"
base_url="https://project21.duckdns.org"

if [[ ! -f "${env_file}" ]]; then
  echo "Missing ${env_file}; run install-vps.sh first." >&2
  exit 1
fi
if grep -q '=CHANGE_ME$' "${env_file}"; then
  echo "Replace every CHANGE_ME value in ${env_file} first." >&2
  exit 1
fi

set -a
source "${env_file}"
set +a
if [[ "${PUBLIC_BASE_URL}" != "${base_url}" ]]; then
  echo "PUBLIC_BASE_URL must be ${base_url}." >&2
  exit 1
fi

if [[ -d "${server_root}" ]]; then
  timestamp="$(date -u +%Y%m%d-%H%M%S)"
  cp -a "${server_root}/control-plane-host.json" "${server_root}/control-plane-host.json.backup-${timestamp}" 2>/dev/null || true
  cp -a "${server_root}/config/control-plane-host-secret.json" "${server_root}/config/control-plane-host-secret.json.backup-${timestamp}" 2>/dev/null || true
  install -o project21 -g project21 -m 0600 /dev/null "${server_root}/config/control-plane-host.secret"
  printf '%s' "${HOST_AGENT_SECRET}" > "${server_root}/config/control-plane-host.secret"
  cat > "${server_root}/config/control-plane-host-secret.json" <<'JSON'
{"secretFile":"config/control-plane-host.secret"}
JSON
  cat > "${server_root}/control-plane-host.json" <<JSON
{"enabled":true,"baseUrl":"${base_url}","pollSeconds":15}
JSON
  chown project21:project21 "${server_root}/config/control-plane-host-secret.json" "${server_root}/control-plane-host.json"
  chmod 0600 "${server_root}/config/control-plane-host-secret.json"
fi

systemctl enable project21-control-plane.service
systemctl restart project21-control-plane.service
systemctl enable --now caddy
systemctl reload caddy

if systemctl list-unit-files project21-gateway.service >/dev/null 2>&1; then
  systemctl restart project21-gateway.service
fi
if systemctl list-unit-files project21-agent.service >/dev/null 2>&1; then
  systemctl restart project21-agent.service
fi

curl --fail --silent --show-error http://127.0.0.1:8787/healthz
echo
echo "Local control plane is healthy. Caddy may need a minute to obtain its HTTPS certificate."
echo "Check public HTTPS with: curl --fail --show-error ${base_url}/healthz"
