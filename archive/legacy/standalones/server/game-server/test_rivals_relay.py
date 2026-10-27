from __future__ import annotations

from pathlib import Path


def main() -> int:
    root = Path(__file__).resolve().parent
    relay = (root / "ubuntu" / "rivals-relay.sh").read_text(encoding="utf-8")
    installer = (root / "ubuntu" / "install.sh").read_text(encoding="utf-8")

    required_relay_fragments = (
        "net.ipv4.ip_forward=1",
        '-s "$network" -d "$network" -p udp --dport 3659 -j ACCEPT',
        'iptables -A "$chain" -j DROP',
        '-i "$interface" -o "$interface" -j "$chain"',
    )
    for fragment in required_relay_fragments:
        assert fragment in relay, f"missing restricted relay rule: {fragment}"

    assert "-j ACCEPT" not in relay.replace(
        '-s "$network" -d "$network" -p udp --dport 3659 -j ACCEPT', ""
    ), "relay contains an unexpected broad ACCEPT rule"
    assert "/usr/local/sbin/project21-rivals-relay" in installer
    assert "project21-rivals-relay start" in installer
    assert "project21-rivals-relay stop" in installer
    assert "net.ipv4.ip_forward=1" in installer

    print("PASS restricted WireGuard UDP 3659 relay installation")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
