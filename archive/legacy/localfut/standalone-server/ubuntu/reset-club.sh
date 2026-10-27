#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this command with sudo." >&2
  exit 1
fi

if [[ $# -ne 1 || ! $1 =~ ^[0-9]{5,24}$ ]]; then
  echo "Usage: sudo project21-reset-club DISCORD_ID" >&2
  exit 2
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
discord_id=$1
players_root="$root/state/players"
player_root="$players_root/$discord_id"
backup_root="$root/state/club-reset-backups/$discord_id"

# Keep every destructive target beneath the fixed per-player state directory.
case "$player_root" in
  "$players_root"/*) ;;
  *)
    echo "Refusing unsafe player-state path: $player_root" >&2
    exit 1
    ;;
esac

if [[ ! -d $player_root ]]; then
  echo "No club exists for Discord ID $discord_id." >&2
  exit 3
fi

gateway_was_active=false
agent_was_active=false
systemctl is-active --quiet project21-gateway && gateway_was_active=true
systemctl is-active --quiet project21-agent && agent_was_active=true

restart_services() {
  if $gateway_was_active; then
    systemctl start project21-gateway || true
  fi
  if $agent_was_active; then
    systemctl start project21-agent || true
  fi
}
trap restart_services EXIT

# Closing the shared runtime first guarantees no SQLite connection can retain
# or rewrite the club while its state directory is moved into the backup area.
if $agent_was_active; then
  systemctl stop project21-agent
fi
if $gateway_was_active; then
  systemctl stop project21-gateway
fi

# A live auction or high bid links this club database to the shared market.
# Refuse the reset instead of orphaning a real card or escrowed coins. Discord
# IDs are validated as digits-only above, so this read-only query is safe.
market_db="$root/state/shared-transfer-market.sqlite3"
if [[ -f $market_db ]]; then
  linked_market_rows=$(sqlite3 -readonly "$market_db" \
    "SELECT COUNT(*) FROM listings WHERE state IN ('active','settling') AND (seller_id='$discord_id' OR bidder_id='$discord_id');")
  if [[ ${linked_market_rows:-0} -gt 0 ]]; then
    echo "Club reset refused: Discord ID $discord_id has $linked_market_rows active transfer-market listing(s) or bid(s)." >&2
    echo "Let those auctions finish and clear them in-game, then run this command again." >&2
    exit 4
  fi
fi

install -d -o root -g project21 -m 0750 "$root/state/club-reset-backups" "$backup_root"
backup_path="$backup_root/$(date -u +%Y%m%d-%H%M%S)-$$"
mv -T -- "$player_root" "$backup_path"

restart_services
trap - EXIT

if $gateway_was_active && ! systemctl is-active --quiet project21-gateway; then
  echo "The club was backed up, but project21-gateway failed to restart." >&2
  exit 1
fi
if $agent_was_active && ! systemctl is-active --quiet project21-agent; then
  echo "The club was backed up, but project21-agent failed to restart." >&2
  exit 1
fi

echo "Reset club for Discord ID $discord_id."
echo "Backup: $backup_path"
echo "The player can reconnect now; a fresh club will be created automatically."
