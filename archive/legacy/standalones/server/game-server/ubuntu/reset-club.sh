#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this command with sudo." >&2
  exit 1
fi

if [[ $# -ne 1 ]]; then
  echo "Usage: sudo project21-reset-club DISCORD_ID|--all" >&2
  exit 2
fi
if [[ $1 != "--all" && ! $1 =~ ^[0-9]{5,24}$ ]]; then
  echo "Usage: sudo project21-reset-club DISCORD_ID|--all" >&2
  exit 2
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
players_root="$root/state/players"
backup_root="$root/state/club-reset-backups"
mode=$1

gateway_was_active=false
systemctl is-active --quiet project21-gateway && gateway_was_active=true

restart_services() {
  if $gateway_was_active; then
    systemctl start project21-gateway || true
  fi
}
trap restart_services EXIT

# Close the shared gateway so no cached SQLite connection can rewrite a club.
# Keep the control-plane agent alive: it may be the caller of this root helper.
if $gateway_was_active; then
  systemctl stop project21-gateway
fi

# Refuse resets that would orphan an active listing or escrowed high bid.
market_db="$root/state/shared-transfer-market.sqlite3"
if [[ -f $market_db ]]; then
  if [[ $mode == "--all" ]]; then
    linked_market_rows=$(sqlite3 -readonly "$market_db" \
      "SELECT COUNT(*) FROM listings WHERE state IN ('active','settling');")
  else
    linked_market_rows=$(sqlite3 -readonly "$market_db" \
      "SELECT COUNT(*) FROM listings WHERE state IN ('active','settling') AND (seller_id='$mode' OR bidder_id='$mode');")
  fi
  if [[ ${linked_market_rows:-0} -gt 0 ]]; then
    echo "Club reset refused: $linked_market_rows active transfer-market listing(s) or bid(s) are linked to the requested club set." >&2
    echo "Let those auctions finish and clear them in-game, then run this command again." >&2
    exit 4
  fi
fi

stamp=$(date -u +%Y%m%d-%H%M%S)-$$
install -d -o root -g project21 -m 0750 "$backup_root"
if [[ $mode == "--all" ]]; then
  backup_path="$backup_root/all-$stamp"
  install -d -o root -g project21 -m 0750 "$backup_path"
  reset_count=0
  shopt -s nullglob
  for player_root in "$players_root"/*; do
    [[ -d $player_root ]] || continue
    discord_id=${player_root##*/}
    [[ $discord_id =~ ^[0-9]{5,24}$ ]] || continue
    mv -T -- "$player_root" "$backup_path/$discord_id"
    reset_count=$((reset_count + 1))
  done
else
  discord_id=$mode
  player_root="$players_root/$discord_id"
  case "$player_root" in "$players_root"/*) ;; *) exit 1 ;; esac
  if [[ ! -d $player_root ]]; then
    echo "No club exists for Discord ID $discord_id." >&2
    exit 3
  fi
  install -d -o root -g project21 -m 0750 "$backup_root/$discord_id"
  backup_path="$backup_root/$discord_id/$stamp"
  mv -T -- "$player_root" "$backup_path"
  reset_count=1
fi

restart_services
trap - EXIT

if $gateway_was_active && ! systemctl is-active --quiet project21-gateway; then
  echo "The club was backed up, but project21-gateway failed to restart." >&2
  exit 1
fi
echo "Reset $reset_count club(s)."
echo "Backup: $backup_path"
echo "Affected players can reconnect now; fresh clubs will be created automatically."
