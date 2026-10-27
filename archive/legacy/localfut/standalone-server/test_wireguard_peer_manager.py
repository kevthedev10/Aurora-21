from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import wireguard_peer_manager as manager  # noqa: E402


KEY_ONE = "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
KEY_TWO = "AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="


def main() -> int:
    calls: list[tuple[str, ...]] = []

    def fake_wg(*arguments: str, check: bool = True):
        del check
        calls.append(arguments)
        return None

    with tempfile.TemporaryDirectory(prefix="project21-wireguard-peer-") as temporary:
        manager.STATE_PATH = Path(temporary) / "peers.json"
        with patch.object(manager, "wg", side_effect=fake_wg):
            manager.add_peer("123456789012345678", "device-one", KEY_ONE, "10.203.20.2")
            manager.add_peer("223456789012345678", "device-two", KEY_TWO, "10.203.20.3")
            saved = json.loads(manager.STATE_PATH.read_text(encoding="utf-8"))
            assert set(saved) == {"device-one", "device-two"}
            assert ("set", "wg0", "peer", KEY_ONE, "allowed-ips", "10.203.20.2/32") in calls

            calls.clear()
            manager.remove_user("123456789012345678")
            saved = json.loads(manager.STATE_PATH.read_text(encoding="utf-8"))
            assert set(saved) == {"device-two"}
            assert calls == [("set", "wg0", "peer", KEY_ONE, "remove")]

            calls.clear()
            manager.restore()
            assert calls == [("set", "wg0", "peer", KEY_TWO, "allowed-ips", "10.203.20.3/32")]

            calls.clear()
            manager.sync(
                [
                    {
                        "deviceId": "device-three",
                        "discordId": "323456789012345678",
                        "publicKey": KEY_ONE,
                        "address": "10.203.20.4",
                    }
                ]
            )
            saved = json.loads(manager.STATE_PATH.read_text(encoding="utf-8"))
            assert set(saved) == {"device-three"}
            assert ("set", "wg0", "peer", KEY_TWO, "remove") in calls
            assert ("set", "wg0", "peer", KEY_ONE, "allowed-ips", "10.203.20.4/32") in calls

    print("PASS WireGuard peer state, removal and reboot restore")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
