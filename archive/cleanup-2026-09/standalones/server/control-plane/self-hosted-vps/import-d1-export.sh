#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 || "$#" -ne 1 ]]; then
  echo "Usage: sudo $0 /path/to/project21-control-d1-export.sql" >&2
  exit 1
fi
if ! command -v sqlite3 >/dev/null 2>&1; then
  echo "Install sqlite3 first: sudo apt install sqlite3" >&2
  exit 1
fi

dump_file="$(readlink -f -- "$1")"
data_dir="/var/lib/project21-control-plane"
database="${data_dir}/control-plane.sqlite3"
temporary="${data_dir}/control-plane.importing.sqlite3"
timestamp="$(date -u +%Y%m%d-%H%M%S)"

[[ -f "${dump_file}" ]] || { echo "Export not found: ${dump_file}" >&2; exit 1; }
rm -f "${temporary}"
sqlite3 "${temporary}" < "${dump_file}"
for table in users devices commands; do
  if [[ "$(sqlite3 "${temporary}" "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='${table}';")" != "1" ]]; then
    rm -f "${temporary}"
    echo "The export is missing required table ${table}; current database was not changed." >&2
    exit 1
  fi
done

sqlite3 "${temporary}" 'CREATE TABLE IF NOT EXISTS _project21_migrations (name TEXT PRIMARY KEY, applied_at INTEGER NOT NULL);'
while IFS= read -r migration; do
  name="$(basename -- "${migration}")"
  sqlite3 "${temporary}" "INSERT OR IGNORE INTO _project21_migrations(name,applied_at) VALUES('${name}',unixepoch());"
done < <(find /opt/project21/control-plane/migrations -maxdepth 1 -type f -name '*.sql' -print | sort)

systemctl stop project21-control-plane.service
if [[ -f "${database}" ]]; then
  cp -a "${database}" "${database}.backup-${timestamp}"
fi
chown project21:project21 "${temporary}"
chmod 0640 "${temporary}"
mv "${temporary}" "${database}"
rm -f "${database}-wal" "${database}-shm"
systemctl start project21-control-plane.service
curl --fail --silent --show-error http://127.0.0.1:8787/healthz
echo
echo "D1 export imported. Previous local database backup: ${database}.backup-${timestamp}"
