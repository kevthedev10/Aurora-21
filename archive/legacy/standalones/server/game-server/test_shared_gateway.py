from __future__ import annotations

import contextlib
from pathlib import Path
import socket
import socketserver
import sys
import threading


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source"))
import authenticated_gateway as gateway


class EchoTcp(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        while data := self.request.recv(4096):
            self.request.sendall(data)


class EchoUdp(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        data, server = self.request
        server.sendto(data, self.client_address)


class Validator:
    def validate(self, token: str, force: bool = False) -> tuple[bool, str]:
        return token == "valid-test-token", "123456789012345678"


class SessionMonitor:
    def add(
        self,
        token: str,
        identity: str,
        connection: socket.socket,
        game_session: bool = False,
    ) -> None:
        del game_session
        pass

    def remove(self, token: str, connection: socket.socket) -> None:
        pass


class PlayerRegistry:
    @contextlib.contextmanager
    def scope(self, identity: str):
        assert identity == "123456789012345678"
        yield identity


def run(server: socketserver.BaseServer) -> threading.Thread:
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return thread


def main() -> int:
    validator = Validator()
    monitor = SessionMonitor()
    registry = PlayerRegistry()

    tcp = gateway.ReusableTcpServer(("127.0.0.1", 0), gateway.SharedRuntimeTcpHandler)
    gateway.attach_shared(tcp, validator, monitor, registry, EchoTcp)
    run(tcp)
    try:
        with socket.create_connection(tcp.server_address, timeout=3) as client:
            client.sendall(b"P21AUTH valid-test-token\n")
            assert client.recv(6) == b"P21OK\n"
            client.sendall(b"shared-runtime-tcp")
            assert client.recv(64) == b"shared-runtime-tcp"
    finally:
        tcp.shutdown()
        tcp.server_close()

    udp = gateway.ReusableUdpServer(("127.0.0.1", 0), gateway.SharedRuntimeUdpHandler)
    gateway.attach_shared(udp, validator, monitor, registry, EchoUdp)
    run(udp)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.settimeout(3)
            client.sendto(b"P21AUTH valid-test-token\nshared-runtime-udp", udp.server_address)
            response, _ = client.recvfrom(64)
            assert response == b"shared-runtime-udp"
    finally:
        udp.shutdown()
        udp.server_close()

    print("PASS authenticated TCP/UDP dispatch uses one shared player runtime")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
