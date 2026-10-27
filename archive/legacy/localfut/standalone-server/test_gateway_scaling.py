from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import io
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import authenticated_gateway as gateway  # noqa: E402


class FakeResponse:
    def __enter__(self):
        return self

    def __exit__(self, *args) -> None:
        return None

    def read(self, limit: int) -> bytes:
        payload = json.dumps(
            {"allowed": True, "discordId": "123456789012345678"}
        ).encode("utf-8")
        return io.BytesIO(payload).read(limit)


def main() -> int:
    calls = 0
    calls_lock = threading.Lock()

    def urlopen(request, timeout: int):
        nonlocal calls
        del request, timeout
        with calls_lock:
            calls += 1
        time.sleep(0.1)
        return FakeResponse()

    validator = gateway.AccessValidator("https://control.invalid", "x" * 32)
    with patch.object(gateway.urllib.request, "urlopen", side_effect=urlopen):
        with ThreadPoolExecutor(max_workers=32) as executor:
            results = list(executor.map(validator.validate, ["same-token"] * 32))

    assert calls == 1, f"expected one coalesced validation request, got {calls}"
    assert all(result == (True, "123456789012345678") for result in results)

    with tempfile.TemporaryDirectory(prefix="project21-session-command-") as temporary:
        monitor = gateway.SessionMonitor(
            validator,
            interval_seconds=3600,
            command_directory=Path(temporary),
        )
        server_socket, player_socket = socket.socketpair()
        second_server = second_player = third_server = third_player = None
        try:
            monitor.add("same-token", "123456789012345678", server_socket)
            (Path(temporary) / "1.json").write_text(
                json.dumps(
                    {"action": "ban_user", "discordId": "123456789012345678"}
                ),
                encoding="utf-8",
            )
            player_socket.settimeout(5)
            assert player_socket.recv(1) == b""

            second_server, second_player = socket.socketpair()
            third_server, third_player = socket.socketpair()
            monitor.add("second-token", "222222222222222222", second_server)
            monitor.add("third-token", "333333333333333333", third_server)
            (Path(temporary) / "kick-all.json").write_text(
                json.dumps({"action": "kick_all"}), encoding="utf-8"
            )
            second_player.settimeout(5)
            third_player.settimeout(5)
            assert second_player.recv(1) == b""
            assert third_player.recv(1) == b""
        finally:
            monitor.close()
            server_socket.close()
            player_socket.close()
            for connection in (second_server, second_player, third_server, third_player):
                if connection is not None:
                    connection.close()
    print("PASS validation coalescing, individual revocation, and kick-all")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
