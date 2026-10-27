from __future__ import annotations

import json
from pathlib import Path
import socket
import sys
import tempfile
import time
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import authenticated_gateway as gateway  # noqa: E402
import host_control_agent as agent  # noqa: E402


class Validator:
    def validate(self, token: str, force: bool = False) -> tuple[bool, str]:
        del force
        return token == "valid-token", "123456789012345678"


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-presence-") as temporary:
        temporary_path = Path(temporary)
        presence_path = temporary_path / "active-sessions.json"
        monitor = gateway.SessionMonitor(
            Validator(),
            command_directory=temporary_path / "commands",
            presence_path=presence_path,
            online_grace_seconds=0,
        )
        client, server = socket.socketpair()
        try:
            monitor.add("valid-token", "123456789012345678", server, game_session=True)
            document = json.loads(presence_path.read_text(encoding="utf-8"))
            assert document["players"] == [
                {"discordId": "123456789012345678", "connections": 1}
            ]

            agent.PRESENCE_PATH = presence_path
            assert agent.read_active_player_ids() == ["123456789012345678"]
            with patch.object(agent, "request_json", return_value={"ok": True}) as request:
                agent.publish_presence("https://control.invalid", "x" * 32)
                assert request.call_args.args[:2] == (
                    "https://control.invalid/api/host/presence",
                    "x" * 32,
                )
            assert request.call_args.kwargs == {
                "method": "POST",
                "body": {
                    "players": ["123456789012345678"],
                    "gatewayOnline": True,
                    "gatewayUpdatedAt": document["updatedAt"],
                },
            }

            monitor.remove("valid-token", server)
            document = json.loads(presence_path.read_text(encoding="utf-8"))
            assert document["players"] == []

            presence_path.write_text(
                json.dumps({
                    "updatedAt": int(time.time()) - 60,
                    "players": [{"discordId": "123456789012345678", "connections": 1}],
                }),
                encoding="utf-8",
            )
            assert agent.read_active_player_ids() == []
            with patch.object(agent, "request_json", return_value={"ok": True}) as request:
                agent.publish_presence("https://control.invalid", "x" * 32)
                assert request.call_args.kwargs["body"] == {
                    "players": [],
                    "gatewayOnline": False,
                    "gatewayUpdatedAt": None,
                }
        finally:
            client.close()
            server.close()
            monitor.close()

    print("PASS persistent Blaze sessions publish expiring Discord presence")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
