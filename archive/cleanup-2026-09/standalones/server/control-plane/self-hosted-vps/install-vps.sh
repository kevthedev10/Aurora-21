#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
target_dir="/opt/project21/control-plane"
data_dir="/var/lib/project21-control-plane"
env_file="/etc/project21-control-plane.env"
timestamp="$(date -u +%Y%m%d-%H%M%S)"

for command in node npm caddy openssl make g++; do
  if ! command -v "${command}" >/dev/null 2>&1; then
    echo "Missing required command: ${command}. Install the Ubuntu build-essential package and retry." >&2
    exit 1
  fi
done

node_major="$(node -p 'Number(process.versions.node.split(".")[0])')"
if [[ "${node_major}" -lt 22 ]]; then
  echo "Node.js 22 or newer is required; found $(node --version)." >&2
  exit 1
fi

if ! id project21 >/dev/null 2>&1; then
  useradd --system --home-dir /opt/project21 --shell /usr/sbin/nologin project21
fi

install -d -o project21 -g project21 -m 0750 "${data_dir}" "${data_dir}/media"
staging="/opt/project21/control-plane.new-${timestamp}"
install -d -o project21 -g project21 -m 0755 "${staging}"
cp -a "${source_dir}/dist-selfhost" "${source_dir}/migrations" "${source_dir}/package.json" "${source_dir}/package-lock.json" "${staging}/"
chown -R project21:project21 "${staging}"
install -d -o project21 -g project21 -m 0700 "/tmp/project21-npm-cache-${timestamp}"
sudo -u project21 env npm_config_cache="/tmp/project21-npm-cache-${timestamp}" npm --prefix "${staging}" ci --omit=dev
rm -rf "/tmp/project21-npm-cache-${timestamp}"

if [[ -d "${target_dir}" ]]; then
  mv "${target_dir}" "/opt/project21/control-plane.backup-${timestamp}"
fi
mv "${staging}" "${target_dir}"

if [[ ! -f "${env_file}" ]]; then
  install -m 0600 "${source_dir}/project21-control-plane.env.example" "${env_file}"
  session_secret="$(openssl rand -hex 48)"
  host_secret="$(openssl rand -hex 48)"
  sed -i "0,/^SESSION_SECRET=CHANGE_ME$/s//SESSION_SECRET=${session_secret}/" "${env_file}"
  sed -i "0,/^HOST_AGENT_SECRET=CHANGE_ME$/s//HOST_AGENT_SECRET=${host_secret}/" "${env_file}"
  echo "Created ${env_file}. Add your Discord client ID and client secret before starting the service."
fi

install -m 0644 "${source_dir}/project21-control-plane.service" /etc/systemd/system/project21-control-plane.service
install -d -m 0755 /etc/caddy/Caddyfile.d
install -m 0644 "${source_dir}/project21.caddy" /etc/caddy/Caddyfile.d/project21.caddy
if ! grep -Fqx 'import /etc/caddy/Caddyfile.d/*' /etc/caddy/Caddyfile; then
  cp -a /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.backup-${timestamp}"
  printf '\nimport /etc/caddy/Caddyfile.d/*\n' >> /etc/caddy/Caddyfile
fi
caddy validate --config /etc/caddy/Caddyfile
systemctl daemon-reload

if grep -q '^DISCORD_CLIENT_.*=CHANGE_ME$' "${env_file}"; then
  echo
  echo "Installation staged but not started because Discord credentials are still placeholders."
  echo "Edit ${env_file}, then run: sudo ${source_dir}/activate-vps.sh"
  exit 0
fi

"${source_dir}/activate-vps.sh"
