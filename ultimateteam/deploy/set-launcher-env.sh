#!/usr/bin/env bash
# Sets the Aurora launcher values in the .env beside this script (the VPS deploy folder, /opt/aurora21):
# LAUNCHER_DOMAIN, COMPOSE_PROFILES=launcher (starts the caddy service in a manual deploy), the account service's
# LAUNCH_ISSUER and ES256 public key, and, when one arrives on standard input, DISCORD_BOT_TOKEN.
#
# The token is read from the first line of standard input and handled with bash builtins only, so it never appears on a
# command line, in a log or in this script's output. Example, from a machine that holds the token:
#   ssh <token host> '<print the token>' | ssh deploy@185.172.175.227 'bash /opt/aurora21/set-launcher-env.sh'
# Run with standard input empty (< /dev/null) to leave DISCORD_BOT_TOKEN as it is.
#
# Idempotent. Takes a timestamped backup first, leaves the file LF-only and mode 600, and refuses to add the ES256 key
# beside an HS256 secret (the server takes exactly one). Apply the result with: docker compose up -d
set -euo pipefail
cd "$(dirname "$0")"
[ -f .env ] || { echo "[FAILED] no .env beside this script"; exit 2; }
token=""
IFS= read -r token || true
token=${token%$'\r'}
token=${token#$'\xef\xbb\xbf'}
if [ -n "$token" ]; then
  case "$token" in *[!A-Za-z0-9._-]*) echo "[FAILED] the bot token has unexpected characters; nothing changed"; exit 3;; esac
  if [ "${#token}" -lt 50 ]; then echo "[FAILED] the bot token is too short; nothing changed"; exit 3; fi
fi
declare -A want=(
  [LAUNCH_ISSUER]='https://15.aurorafut.com'
  [LAUNCH_ES256_PUBLIC_KEY]='MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEhgI3f2Nm4TsUeSCdJ3nAjgNHKhQ0J+UrwdHfY12KmSu5xDkNRra2GfzaBu/Ge+A6AdCDeK3F9bNnLqqdZIFeoA=='
  [LAUNCHER_DOMAIN]='21.aurorafut.com'
  [COMPOSE_PROFILES]='launcher'
)
if [ -n "$token" ]; then want[DISCORD_BOT_TOKEN]="$token"; fi
stamp=$(date -u +%Y%m%dT%H%M%SZ)
cp -p .env ".env.before-launcher-$stamp"
tmp=$(mktemp .env.new.XXXXXX)
chmod 600 "$tmp"
declare -A seen=()
while IFS= read -r line || [ -n "$line" ]; do
  line=${line%$'\r'}
  key=${line%%=*}
  if [[ "$line" == *=* && -n "${want[$key]+x}" ]]; then
    printf '%s=%s\n' "$key" "${want[$key]}"
    seen[$key]=1
  else
    printf '%s\n' "$line"
  fi
done < .env > "$tmp"
for key in "${!want[@]}"; do
  if [ -z "${seen[$key]+x}" ]; then printf '%s=%s\n' "$key" "${want[$key]}" >> "$tmp"; fi
done
hs=$(grep -E '^LAUNCH_HS256_SECRET=' "$tmp" | head -1 | cut -d= -f2- || true)
if [ -n "$hs" ]; then rm -f "$tmp"; echo "[FAILED] LAUNCH_HS256_SECRET is set beside the ES256 key; nothing changed"; exit 4; fi
mv "$tmp" .env
chmod 600 .env
echo "[READY] .env updated (backup .env.before-launcher-$stamp): ${!want[*]}"