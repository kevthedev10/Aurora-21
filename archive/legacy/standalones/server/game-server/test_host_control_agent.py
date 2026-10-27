from __future__ import annotations

import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import host_control_agent as agent  # noqa: E402


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-host-agent-") as temporary:
        agent.ROOT = Path(temporary)
        agent.signal_session_command(
            {
                "id": 42,
                "action": "ban_user",
                "target_discord_id": "123456789012345678",
            }
        )
        path = agent.ROOT / "state" / "session-commands" / "42.json"
        assert json.loads(path.read_text(encoding="utf-8")) == {
            "action": "ban_user",
            "discordId": "123456789012345678",
        }
        with patch.object(
            agent.subprocess,
            "run",
            return_value=SimpleNamespace(returncode=0, stdout="peer installed", stderr=""),
        ) as run:
            success, output = agent.execute(
                {
                    "id": 43,
                    "action": "wireguard_add_peer",
                    "target_discord_id": "123456789012345678",
                    "payload": json.dumps(
                        {
                            "deviceId": "device-one",
                            "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                            "address": "10.203.20.2",
                        }
                    ),
                }
            )
            assert success and output == "peer installed"
            arguments = run.call_args.args[0]
            assert arguments[:3] == ["sudo", agent.WIREGUARD_HELPER, "add"]
            assert "123456789012345678" in arguments
        peers = [
            {
                "deviceId": "device-one",
                "discordId": "123456789012345678",
                "publicKey": "AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "address": "10.203.20.2",
            }
        ]
        with patch.object(agent, "request_json", return_value={"peers": peers}), patch.object(
            agent.subprocess,
            "run",
            return_value=SimpleNamespace(returncode=0, stdout="", stderr=""),
        ) as run:
            agent.synchronize_wireguard("https://control.invalid", "x" * 32)
            assert run.call_args.args[0] == ["sudo", agent.WIREGUARD_HELPER, "sync"]
            assert json.loads(run.call_args.kwargs["input"]) == peers
    print("PASS Linux host agent publishes an atomic shared-runtime ban signal")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
