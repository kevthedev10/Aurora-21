from __future__ import annotations

import socket
import socketserver
import threading
from pathlib import Path
import sys
import tempfile


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


def run_server(server: socketserver.BaseServer) -> threading.Thread:
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return thread


def main() -> int:
    validator = Validator()
    origin_tcp = socketserver.ThreadingTCPServer(("127.0.0.1", 0), EchoTcp)
    port = origin_tcp.server_address[1]
    gateway_tcp = gateway.ReusableTcpServer(("127.0.0.2", port), gateway.GatewayTcpHandler)
    gateway.attach(gateway_tcp, validator, "127.0.0.1")
    run_server(origin_tcp)
    run_server(gateway_tcp)
    try:
        with socket.create_connection(("127.0.0.2", port), 3) as client:
            client.sendall(b"P21AUTH valid-test-token\n")
            assert client.recv(6) == b"P21OK\n"
            client.sendall(b"relay-test")
            assert client.recv(64) == b"relay-test"
        with socket.create_connection(("127.0.0.2", port), 3) as denied:
            denied.sendall(b"P21AUTH invalid\n")
            assert denied.recv(64) == b""
    finally:
        gateway_tcp.shutdown()
        gateway_tcp.server_close()
        origin_tcp.shutdown()
        origin_tcp.server_close()

    origin_udp = socketserver.ThreadingUDPServer(("127.0.0.1", 0), EchoUdp)
    udp_port = origin_udp.server_address[1]
    gateway_udp = gateway.ReusableUdpServer(("127.0.0.2", udp_port), gateway.GatewayUdpHandler)
    gateway.attach(gateway_udp, validator, "127.0.0.1")
    run_server(origin_udp)
    run_server(gateway_udp)
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
            client.settimeout(3)
            client.sendto(b"P21AUTH valid-test-token\nudp-test", ("127.0.0.2", udp_port))
            response, _ = client.recvfrom(64)
            assert response == b"udp-test"
    finally:
        gateway_udp.shutdown()
        gateway_udp.server_close()
        origin_udp.shutdown()
        origin_udp.server_close()

    with tempfile.TemporaryDirectory(prefix="project21-instance-test-") as temporary:
        manager = gateway.InstanceManager(Path(temporary))
        manager.server_script = Path(__file__).resolve().parent / "runtime-source" / "localfut21" / "server.py"
        try:
            address = manager.get_origin("123456789012345678")
            assert address.startswith("127.")
            with socket.create_connection((address, 8090), 3):
                pass
        finally:
            manager.stop_all()
    print("PASS authenticated TCP and UDP gateway")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
