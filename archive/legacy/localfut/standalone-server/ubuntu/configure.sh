#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root: sudo bash ubuntu/configure.sh" >&2
  exit 1
fi

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

read -r -p "HTTPS control-panel URL: " base_url
base_url=${base_url%/}
if [[ ! $base_url =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
  echo "Enter an HTTPS origin only, without a path." >&2
  exit 1
fi
read -r -s -p "HOST_AGENT_SECRET: " host_secret
echo
if (( ${#host_secret} < 32 )); then
  echo "HOST_AGENT_SECRET must contain at least 32 characters." >&2
  exit 1
fi
read -r -p "VPS public IP address or DNS name: " public_endpoint
if [[ ! $public_endpoint =~ ^[A-Za-z0-9.-]+$ ]] || [[ $public_endpoint == .* ]] || [[ $public_endpoint == *. ]]; then
  echo "Enter only the VPS IP address or DNS name." >&2
  exit 1
fi
read -r -p "WireGuard UDP port [51820]: " wireguard_port
wireguard_port=${wireguard_port:-51820}
if [[ ! $wireguard_port =~ ^[0-9]+$ ]] || (( wireguard_port < 1 || wireguard_port > 65535 )); then
  echo "Enter a UDP port from 1 to 65535." >&2
  exit 1
fi
bind_address=10.203.21.1

cat > /etc/sysctl.d/99-project21-wireguard.conf <<'EOF'
net.ipv4.ip_forward=1
EOF
sysctl -q -p /etc/sysctl.d/99-project21-wireguard.conf

install -d -o root -g root -m 0700 /etc/wireguard
if [[ ! -s /etc/wireguard/project21-private.key ]]; then
  umask 077
  wg genkey > /etc/wireguard/project21-private.key
fi
private_key=$(< /etc/wireguard/project21-private.key)
server_public_key=$(printf '%s' "$private_key" | wg pubkey)
cat > /etc/wireguard/wg0.conf <<EOF
[Interface]
Address = ${bind_address}/22
ListenPort = ${wireguard_port}
PrivateKey = ${private_key}
SaveConfig = false
PostUp = iptables -N PROJECT21-WG 2>/dev/null || true; iptables -D INPUT -i %i -j PROJECT21-WG 2>/dev/null || true; iptables -D FORWARD -i %i -j DROP 2>/dev/null || true; iptables -D FORWARD -o %i -j DROP 2>/dev/null || true; iptables -F PROJECT21-WG; iptables -A PROJECT21-WG -p tcp --syn -m hashlimit --hashlimit-above 30/second --hashlimit-burst 60 --hashlimit-mode srcip --hashlimit-name project21-tcp -j DROP; iptables -A PROJECT21-WG -p tcp -m connlimit --connlimit-above 32 --connlimit-mask 32 -j DROP; iptables -A PROJECT21-WG -p udp --dport 17502 -m hashlimit --hashlimit-above 100/second --hashlimit-burst 200 --hashlimit-mode srcip --hashlimit-name project21-udp -j DROP; iptables -A PROJECT21-WG -p udp --dport 31000:31199 -m hashlimit --hashlimit-above 2500/second --hashlimit-burst 5000 --hashlimit-mode srcip --hashlimit-name project21-relay -j DROP; iptables -A PROJECT21-WG -p tcp -m multiport --dports 44321,8090,10853,17502,8081,42231,42232 -j ACCEPT; iptables -A PROJECT21-WG -p udp --dport 17502 -j ACCEPT; iptables -A PROJECT21-WG -p udp --dport 31000:31199 -j ACCEPT; iptables -A PROJECT21-WG -j DROP; iptables -I INPUT 1 -i %i -j PROJECT21-WG; iptables -I FORWARD 1 -i %i -j DROP; iptables -I FORWARD 1 -o %i -j DROP
PreDown = iptables -D INPUT -i %i -j PROJECT21-WG 2>/dev/null || true; iptables -D FORWARD -i %i -j DROP 2>/dev/null || true; iptables -D FORWARD -o %i -j DROP 2>/dev/null || true; iptables -F PROJECT21-WG 2>/dev/null || true; iptables -X PROJECT21-WG 2>/dev/null || true
EOF
chmod 0600 /etc/wireguard/wg0.conf

install -d -o root -g root -m 0755 /etc/systemd/system/wg-quick@wg0.service.d
cat > /etc/systemd/system/wg-quick@wg0.service.d/project21.conf <<'EOF'
[Service]
ExecStartPost=/usr/local/sbin/project21-wireguard-peers restore
EOF
cat > /etc/systemd/system/wg-quick@wg0.service.d/project21-rivals-relay.conf <<'EOF'
[Service]
ExecStartPost=/usr/local/sbin/project21-rivals-relay start
ExecStopPost=/usr/local/sbin/project21-rivals-relay stop
EOF

install -d -o project21 -g project21 -m 0750 "$root/config"
install -o root -g project21 -m 0640 /dev/null /etc/project21-host-agent-secret
printf '%s\n' "$host_secret" > /etc/project21-host-agent-secret
printf '{"enabled":true,"baseUrl":"%s","pollSeconds":5}\n' "$base_url" \
  > "$root/control-plane-host.json"
printf '{"secretFile":"/etc/project21-host-agent-secret"}\n' \
  > "$root/config/control-plane-host-secret.json"
printf '{"bindAddress":"%s","originAddress":"127.0.0.1","singleRuntime":true,"idleContextSeconds":900,"maxCachedPlayers":1000,"contextInitWorkers":4,"revalidateSeconds":900,"logLevel":"WARNING"}\n' \
  "$bind_address" > "$root/gateway-settings.json"
chown project21:project21 "$root/control-plane-host.json" "$root/gateway-settings.json"
chown -R project21:project21 "$root/config"
chmod 0640 "$root/control-plane-host.json" "$root/gateway-settings.json" \
  "$root/config/control-plane-host-secret.json"

systemctl daemon-reload
systemctl disable --now project21-loopback.service 2>/dev/null || true
systemctl disable --now cloudflared.service 2>/dev/null || true
systemctl enable wg-quick@wg0.service
systemctl restart wg-quick@wg0.service
systemctl enable project21-gateway.service project21-agent.service
systemctl restart project21-gateway.service project21-agent.service
sleep 2
systemctl --no-pager --full status project21-gateway.service || true
echo
echo "Configured. Open UDP ${wireguard_port} in the VPS provider firewall."
echo "Put these values in cloudflare-control-panel/wrangler.jsonc and redeploy:"
echo "  WIREGUARD_ENDPOINT=${public_endpoint}:${wireguard_port}"
echo "  WIREGUARD_SERVER_PUBLIC_KEY=${server_public_key}"
echo "The FIFA game ports remain bound only to WireGuard address ${bind_address}."
