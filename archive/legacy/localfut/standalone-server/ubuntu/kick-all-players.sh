#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this command with sudo." >&2
  exit 1
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
command_directory="$root/state/session-commands"
install -d -o project21 -g project21 -m 0750 "$command_directory"

temporary=$(mktemp "$command_directory/.kick-all.XXXXXX")
destination="$command_directory/kick-all-$(date -u +%Y%m%d-%H%M%S)-$$.json"
trap 'rm -f -- "$temporary"' EXIT
printf '{"action":"kick_all"}\n' > "$temporary"
chown root:project21 "$temporary"
chmod 0640 "$temporary"
mv -- "$temporary" "$destination"
trap - EXIT

for _ in {1..50}; do
  if [[ ! -e "$destination" ]]; then
    echo "All active FIFA 21 Ultimate Team sessions were disconnected."
    exit 0
  fi
  sleep 0.1
done

echo "The gateway did not accept the kick command. Check: sudo systemctl status project21-gateway" >&2
exit 1
