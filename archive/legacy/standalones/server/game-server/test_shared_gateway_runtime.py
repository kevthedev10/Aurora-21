from __future__ import annotations

import json
import logging
import os
from pathlib import Path
import socket
import sys
import tempfile
import threading


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import authenticated_gateway as gateway


class Validator:
    IDENTITIES = {
        "first-token": "111111111111111111",
        "second-token": "222222222222222222",
    }

    def validate(self, token: str, force: bool = False) -> tuple[bool, str]:
        identity = self.IDENTITIES.get(token, "")
        return bool(identity), identity


class SessionMonitor:
    def add(
        self,
        token: str,
        identity: str,
        connection: socket.socket,
        game_session: bool = False,
    ) -> None:
        pass

    def remove(self, token: str, connection: socket.socket) -> None:
        pass


def request(address: tuple[str, int], token: str) -> dict:
    with socket.create_connection(address, timeout=5) as client:
        client.sendall(f"P21AUTH {token}\n".encode("ascii"))
        assert client.recv(6) == b"P21OK\n"
        client.sendall(
            b"GET /ut/game/fifa21/user/credits HTTP/1.1\r\n"
            b"Host: project21.test\r\nConnection: close\r\n\r\n"
        )
        response = bytearray()
        while b"\r\n\r\n" not in response:
            chunk = client.recv(65536)
            assert chunk
            response.extend(chunk)
        raw_headers, _, body = bytes(response).partition(b"\r\n\r\n")
        content_length = 0
        for line in raw_headers.split(b"\r\n"):
            if line.lower().startswith(b"content-length:"):
                content_length = int(line.split(b":", 1)[1].strip())
                break
        while len(body) < content_length:
            body += client.recv(content_length - len(body))
    return json.loads(body)


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-shared-gateway-") as temporary:
        root = Path(temporary)
        bootstrap_runtime = root / "bootstrap"
        bootstrap_config = bootstrap_runtime / "config"
        bootstrap_config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
        localfut = gateway.load_localfut_runtime()
        registry = localfut.enable_multi_tenant(root, idle_seconds=60, max_cached_players=750)
        server = gateway.ReusableTcpServer(("127.0.0.1", 0), gateway.SharedRuntimeTcpHandler)
        gateway.attach_shared(
            server, Validator(), SessionMonitor(), registry, localfut.FutHandler
        )
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            first = request(server.server_address, "first-token")
            second = request(server.server_address, "second-token")
            assert first["currencies"][0]["funds"] == second["currencies"][0]["funds"]
            with registry.scope(Validator.IDENTITIES["first-token"]):
                localfut.STATE.add_credits(4321)
            first_after = request(server.server_address, "first-token")
            second_after = request(server.server_address, "second-token")
            assert first_after["currencies"][0]["funds"] == first["currencies"][0]["funds"] + 4321
            assert second_after["currencies"][0]["funds"] == second["currencies"][0]["funds"]
        finally:
            server.shutdown()
            server.server_close()
            registry.close()
            logging.shutdown()
    print("PASS real FUT HTTP handler keeps authenticated Discord clubs isolated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
