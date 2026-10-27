#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi

source_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
data_dir="/var/lib/project21-control-plane"
target="${data_dir}/media"
staging="${data_dir}/media.new-$(date -u +%Y%m%d-%H%M%S)"
timestamp="$(date -u +%Y%m%d-%H%M%S)"

cd "${source_dir}"
sha256sum --check SHA256SUMS
install -d -o project21 -g project21 -m 0750 "${data_dir}"
cp -a "${source_dir}/media" "${staging}"
chown -R project21:project21 "${staging}"
if [[ -d "${target}" ]]; then
  mv "${target}" "${data_dir}/media.backup-${timestamp}"
fi
mv "${staging}" "${target}"
systemctl restart project21-control-plane.service
curl --fail --silent --show-error http://127.0.0.1:8787/healthz
echo
echo "Local Project 21 media installed. Previous media, if present, was retained as media.backup-${timestamp}."
