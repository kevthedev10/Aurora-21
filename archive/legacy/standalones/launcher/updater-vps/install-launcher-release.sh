#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
release_root="/var/www/aurora21/launcher"
caddy_target="/etc/caddy/Caddyfile.d/project21.caddy"
timestamp="$(date -u +%Y%m%d-%H%M%S)"

cd "$source_dir"
sha256sum -c SHA256SUMS

install_caddy_config=0
if test -f "$caddy_target"; then
    if grep -Fq 'handle_path /downloads/launcher/*' "$caddy_target"; then
        install_caddy_config=0
    elif cmp -s "$source_dir/project21.caddy.baseline" "$caddy_target"; then
        install_caddy_config=1
    else
        echo "Refusing to overwrite a customized Caddy configuration: $caddy_target" >&2
        echo "Add the /downloads/launcher route from project21.caddy manually, then run this installer again." >&2
        exit 1
    fi
else
    install_caddy_config=1
fi
package="$(find "$source_dir" -maxdepth 1 -type f -name 'Aurora21-Alpha-v1-Public-Release-win-x64.zip' -print -quit)"
test -n "$package"
test -f "$source_dir/latest.json"
test -f "$source_dir/Aurora21-Updater.exe"

install -d -o root -g caddy -m 0755 "$release_root"
install -o root -g caddy -m 0644 "$package" "$release_root/$(basename "$package").new"
install -o root -g caddy -m 0644 "$source_dir/latest.json" "$release_root/latest.json.new"
install -o root -g caddy -m 0644 "$source_dir/Aurora21-Updater.exe" "$release_root/Aurora21-Updater.exe.new"
mv -f "$release_root/$(basename "$package").new" "$release_root/$(basename "$package")"
mv -f "$release_root/latest.json.new" "$release_root/latest.json"
mv -f "$release_root/Aurora21-Updater.exe.new" "$release_root/Aurora21-Updater.exe"

if test "$install_caddy_config" -eq 1; then
    if test -f "$caddy_target"; then
        cp -a "$caddy_target" "${caddy_target}.backup-${timestamp}"
    fi
    install -o root -g root -m 0644 "$source_dir/project21.caddy" "$caddy_target"
fi
caddy validate --config /etc/caddy/Caddyfile
systemctl reload caddy

curl --fail --silent --show-error https://project21.duckdns.org/downloads/launcher/latest.json
echo
echo "Aurora 21 launcher release published successfully."
