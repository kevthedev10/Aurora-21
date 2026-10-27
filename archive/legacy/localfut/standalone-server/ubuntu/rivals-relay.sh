#!/usr/bin/env bash
set -euo pipefail

interface=${PROJECT21_WIREGUARD_INTERFACE:-wg0}
network=10.203.20.0/22
chain=PROJECT21-RIVALS

remove_jump() {
  while iptables -C FORWARD -i "$interface" -o "$interface" -j "$chain" 2>/dev/null; do
    iptables -D FORWARD -i "$interface" -o "$interface" -j "$chain"
  done
}

stop_relay() {
  remove_jump
  iptables -F "$chain" 2>/dev/null || true
  iptables -X "$chain" 2>/dev/null || true
}

start_relay() {
  sysctl -q -w net.ipv4.ip_forward=1
  iptables -N "$chain" 2>/dev/null || true
  remove_jump
  iptables -F "$chain"

  # FIFA 21 initiates peer gameplay toward UDP 3659, but DirtySDK may use the
  # caller's learned ephemeral source port for the return leg.  Accept that
  # conntrack-established return traffic before applying the destination-port
  # admission rule; otherwise the handshake becomes one-way at initialization.
  iptables -A "$chain" -s "$network" -d "$network" \
    -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
  iptables -A "$chain" -s "$network" -d "$network" -p udp --dport 3659 -j ACCEPT
  iptables -A "$chain" -j DROP
  iptables -I FORWARD 1 -i "$interface" -o "$interface" -j "$chain"
}

if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root." >&2
  exit 1
fi

case ${1:-start} in
  start)
    start_relay
    ;;
  stop)
    stop_relay
    ;;
  restart)
    stop_relay
    start_relay
    ;;
  *)
    echo "Usage: $0 {start|stop|restart}" >&2
    exit 2
    ;;
esac
