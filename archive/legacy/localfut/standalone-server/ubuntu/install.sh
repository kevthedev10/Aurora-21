#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer as root: sudo bash ubuntu/install.sh" >&2
  exit 1
fi

source /etc/os-release
if [[ ${ID:-} != ubuntu ]]; then
  echo "This package supports Ubuntu only; detected ${PRETTY_NAME:-unknown}." >&2
  exit 1
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# Upload/extraction tools can leave the deployment root accessible only to the
# SSH user. The system services run as project21 and must be able to traverse
# this directory before Python can start. Sensitive state keeps its stricter
# permissions below and in configure.sh.
chmod 0755 "$root"
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
  python3 python3-venv ca-certificates iproute2 iptables sqlite3 sudo wireguard-tools

if ! id project21 >/dev/null 2>&1; then
  useradd --system --home-dir "$root" --shell /usr/sbin/nologin project21
fi

python3 -m venv "$root/.venv"
"$root/.venv/bin/python" -m pip install --upgrade pip
"$root/.venv/bin/python" -m pip install -r "$root/runtime-source/requirements.txt"
install -d -o project21 -g project21 -m 0750 "$root/state" "$root/config"
chown -R project21:project21 "$root/state" "$root/config"

install -o root -g root -m 0755 \
  "$root/runtime-source/wireguard_peer_manager.py" \
  /usr/local/sbin/project21-wireguard-peers
install -o root -g root -m 0755 \
  "$root/ubuntu/rivals-relay.sh" \
  /usr/local/sbin/project21-rivals-relay
cat > /etc/sysctl.d/99-project21-wireguard.conf <<'EOF'
net.ipv4.ip_forward=1
EOF
sysctl -q -p /etc/sysctl.d/99-project21-wireguard.conf
install -d -o root -g root -m 0755 /etc/systemd/system/wg-quick@wg0.service.d
cat > /etc/systemd/system/wg-quick@wg0.service.d/project21-rivals-relay.conf <<'EOF'
[Service]
ExecStartPost=/usr/local/sbin/project21-rivals-relay start
ExecStopPost=/usr/local/sbin/project21-rivals-relay stop
EOF
install -d -o root -g root -m 0700 /var/lib/project21-wireguard
if [[ ! -e /var/lib/project21-wireguard/peers.json ]]; then
  install -o root -g root -m 0600 /dev/null /var/lib/project21-wireguard/peers.json
  printf '{}\n' > /var/lib/project21-wireguard/peers.json
fi
cat > /etc/sudoers.d/project21-wireguard <<'EOF'
project21 ALL=(root) NOPASSWD: /usr/local/sbin/project21-wireguard-peers
EOF
chmod 0440 /etc/sudoers.d/project21-wireguard
visudo -cf /etc/sudoers.d/project21-wireguard >/dev/null

escaped_root=${root//&/\\&}
sed "s|@ROOT@|$escaped_root|g" "$root/ubuntu/project21-gateway.service.in" \
  > /etc/systemd/system/project21-gateway.service
sed "s|@ROOT@|$escaped_root|g" "$root/ubuntu/project21-agent.service.in" \
  > /etc/systemd/system/project21-agent.service
sed "s|@ROOT@|$escaped_root|g" "$root/ubuntu/project21-kick-all.in" \
  > /usr/local/sbin/project21-kick-all
chmod 0755 /usr/local/sbin/project21-kick-all
sed "s|@ROOT@|$escaped_root|g" "$root/ubuntu/project21-reset-club.in" \
    > /usr/local/sbin/project21-reset-club
chmod 0755 /usr/local/sbin/project21-reset-club
sed "s|@ROOT@|$escaped_root|g" "$root/ubuntu/project21-live-logs.in" \
    > /usr/local/sbin/project21-live-logs
chmod 0755 /usr/local/sbin/project21-live-logs
systemctl daemon-reload

echo
echo "Ubuntu dependencies and services are installed."
echo "Next run: sudo bash '$root/ubuntu/configure.sh'"
