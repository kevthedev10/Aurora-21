#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root: sudo bash ubuntu/collect-support-bundle.sh" >&2
  exit 1
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
stamp=$(date -u +%Y%m%d-%H%M%SZ)
work=$(mktemp -d /tmp/project21-support.XXXXXX)
trap 'rm -rf -- "$work"' EXIT

capture() {
  local name=$1
  shift
  "$@" >"$work/$name" 2>&1 || true
}

capture system-status.txt systemctl --no-pager --full status \
  wg-quick@wg0.service project21-gateway.service project21-agent.service
capture failed-services.txt systemctl --no-pager --failed
capture gateway.log journalctl -u project21-gateway.service --since "24 hours ago" --no-pager
capture agent.log journalctl -u project21-agent.service --since "24 hours ago" --no-pager
capture wireguard-service.log journalctl -u wg-quick@wg0.service --since "24 hours ago" --no-pager
capture wireguard-status.txt wg show
capture sockets.txt ss -lntup
capture memory.txt free -h
capture disk.txt df -h
capture firewall.txt ufw status verbose
capture operating-system.txt bash -c 'uname -a; echo; cat /etc/os-release'

if [[ -f "$root/gateway-settings.json" ]]; then
  cp "$root/gateway-settings.json" "$work/gateway-settings.json"
fi
if [[ -f "$root/control-plane-host.json" ]]; then
  cp "$root/control-plane-host.json" "$work/control-plane-host.json"
fi

# Remove common credentials and stable player identifiers while retaining the
# timing, service, port and error details needed for support.
for file in "$work"/*; do
  [[ -f "$file" ]] || continue
  sed -E -i \
    -e 's/(Authorization:[[:space:]]*(Bearer|Basic))[[:space:]]+[^[:space:]]+/\1 <redacted>/Ig' \
    -e 's/([?&](token|code|secret)=)[^&[:space:]]+/\1<redacted>/Ig' \
    -e 's/(^|[^0-9])[0-9]{17,20}([^0-9]|$)/\1<discord-id>\2/g' \
    -e 's/([A-Za-z0-9+\/]{42})[A-Za-z0-9+\/]=/<wireguard-key>/g' \
    "$file"
done

owner=${SUDO_USER:-root}
if [[ "$owner" == root ]]; then
  destination=/root
else
  destination=$(getent passwd "$owner" | cut -d: -f6)
fi
archive="$destination/project21-support-$stamp.tar.gz"
tar -C "$work" -czf "$archive" .
chmod 0600 "$archive"
if [[ "$owner" != root ]]; then
  chown "$owner":"$owner" "$archive"
fi

echo "Created redacted support bundle: $archive"
echo "It excludes club databases, host secrets, WireGuard private keys and peer-state files."
