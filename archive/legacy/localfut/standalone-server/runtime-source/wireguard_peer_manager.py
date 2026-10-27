#!/usr/bin/python3
from __future__ import annotations

import argparse
import base64
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


STATE_PATH = Path("/var/lib/project21-wireguard/peers.json")
INTERFACE = "wg0"
NETWORK = ipaddress.ip_network("10.203.20.0/22")
SERVER_ADDRESS = ipaddress.ip_address("10.203.21.1")
KEY_PATTERN = re.compile(r"^[A-Za-z0-9+/]{43}=$")
IDENTITY_PATTERN = re.compile(r"^[0-9]{5,32}$")
DEVICE_PATTERN = re.compile(r"^[A-Za-z0-9-]{8,80}$")


def validate_key(value: str) -> str:
    if not KEY_PATTERN.fullmatch(value):
        raise ValueError("invalid WireGuard public key")
    decoded = base64.b64decode(value, validate=True)
    if len(decoded) != 32 or not any(decoded):
        raise ValueError("invalid WireGuard public key")
    return value


def validate_address(value: str) -> str:
    address = ipaddress.ip_address(value)
    if (
        address.version != 4
        or address not in NETWORK
        or address in {NETWORK.network_address, NETWORK.broadcast_address, SERVER_ADDRESS}
    ):
        raise ValueError("client address is outside the Project 21 WireGuard pool")
    return str(address)


def load_state() -> dict[str, dict[str, str]]:
    if not STATE_PATH.exists():
        return {}
    data = json.loads(STATE_PATH.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError("invalid saved WireGuard peer state")
    return data


def save_state(peers: dict[str, dict[str, str]]) -> None:
    STATE_PATH.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".project21-peers-", dir=STATE_PATH.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(peers, output, sort_keys=True, separators=(",", ":"))
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, 0o600)
        temporary.replace(STATE_PATH)
    finally:
        temporary.unlink(missing_ok=True)


def wg(*arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["/usr/bin/wg", *arguments],
        check=check,
        text=True,
        capture_output=True,
    )


def apply_peer(public_key: str, address: str) -> None:
    wg("set", INTERFACE, "peer", public_key, "allowed-ips", f"{address}/32")


def remove_peer(public_key: str) -> None:
    wg("set", INTERFACE, "peer", public_key, "remove", check=False)


def add_peer(discord_id: str, device_id: str, public_key: str, address: str) -> None:
    if not IDENTITY_PATTERN.fullmatch(discord_id):
        raise ValueError("invalid Discord ID")
    if not DEVICE_PATTERN.fullmatch(device_id):
        raise ValueError("invalid device ID")
    public_key = validate_key(public_key)
    address = validate_address(address)
    peers = load_state()
    for existing_device, peer in list(peers.items()):
        if existing_device == device_id:
            if peer.get("publicKey") != public_key:
                remove_peer(validate_key(peer["publicKey"]))
            continue
        if peer.get("publicKey") == public_key or peer.get("address") == address:
            remove_peer(validate_key(peer["publicKey"]))
            peers.pop(existing_device)
    peers[device_id] = {
        "discordId": discord_id,
        "publicKey": public_key,
        "address": address,
    }
    apply_peer(public_key, address)
    save_state(peers)


def remove_user(discord_id: str) -> None:
    if not IDENTITY_PATTERN.fullmatch(discord_id):
        raise ValueError("invalid Discord ID")
    peers = load_state()
    for device_id, peer in list(peers.items()):
        if peer.get("discordId") == discord_id:
            remove_peer(validate_key(peer["publicKey"]))
            peers.pop(device_id)
    save_state(peers)


def sync(peers_input: object) -> None:
    if not isinstance(peers_input, list) or len(peers_input) > 1100:
        raise ValueError("invalid WireGuard peer list")
    desired: dict[str, dict[str, str]] = {}
    public_keys: set[str] = set()
    addresses: set[str] = set()
    for item in peers_input:
        if not isinstance(item, dict):
            raise ValueError("invalid WireGuard peer")
        device_id = str(item.get("deviceId", ""))
        discord_id = str(item.get("discordId", ""))
        public_key = validate_key(str(item.get("publicKey", "")))
        address = validate_address(str(item.get("address", "")))
        if not DEVICE_PATTERN.fullmatch(device_id) or not IDENTITY_PATTERN.fullmatch(discord_id):
            raise ValueError("invalid WireGuard peer identity")
        if device_id in desired or public_key in public_keys or address in addresses:
            raise ValueError("duplicate WireGuard peer")
        public_keys.add(public_key)
        addresses.add(address)
        desired[device_id] = {
            "discordId": discord_id,
            "publicKey": public_key,
            "address": address,
        }

    current = load_state()
    desired_keys = {peer["publicKey"] for peer in desired.values()}
    for peer in current.values():
        public_key = validate_key(peer["publicKey"])
        if public_key not in desired_keys:
            remove_peer(public_key)
    for device_id, peer in desired.items():
        if current.get(device_id) != peer:
            apply_peer(peer["publicKey"], peer["address"])
    save_state(desired)


def restore() -> None:
    for device_id, peer in load_state().items():
        if not DEVICE_PATTERN.fullmatch(device_id):
            raise ValueError("invalid saved device ID")
        if not IDENTITY_PATTERN.fullmatch(str(peer.get("discordId", ""))):
            raise ValueError("invalid saved Discord ID")
        apply_peer(validate_key(str(peer.get("publicKey", ""))), validate_address(str(peer.get("address", ""))))


def main() -> int:
    if os.geteuid() != 0:
        raise PermissionError("WireGuard peer manager must run as root")
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="action", required=True)
    add = subparsers.add_parser("add")
    add.add_argument("--discord-id", required=True)
    add.add_argument("--device-id", required=True)
    add.add_argument("--public-key", required=True)
    add.add_argument("--address", required=True)
    remove = subparsers.add_parser("remove-user")
    remove.add_argument("--discord-id", required=True)
    subparsers.add_parser("sync")
    subparsers.add_parser("restore")
    args = parser.parse_args()
    if args.action == "add":
        add_peer(args.discord_id, args.device_id, args.public_key, args.address)
    elif args.action == "remove-user":
        remove_user(args.discord_id)
    elif args.action == "sync":
        sync(json.load(sys.stdin))
    else:
        restore()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
