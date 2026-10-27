from __future__ import annotations

"""Authenticated loopback relay used by the standalone Aurora 21 (Alpha v1) launcher."""

import argparse
import base64
import ctypes
from ctypes import wintypes
import json
import logging
import os
from pathlib import Path
import select
import socket
import socketserver
import sys
import threading


ROOT = Path(__file__).resolve().parents[2]
SETTINGS_PATH = ROOT / "remote-server.json"
CONTROL_PATH = ROOT / "control-plane.json"
TCP_PORTS = (44321, 8090, 10853, 17502, 8081, 42231, 42232)
UDP_PORTS = (17502,)
REQUIRED_REMOTE_PORTS = (44321, 8090, 10853)
AUTH_PREFIX = b"P21AUTH "
log = logging.getLogger("localfut-relay")


class DataBlob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]


def unprotect_windows_dpapi(value: str, entropy: bytes) -> str:
    if os.name != "nt":
        raise RuntimeError("Aurora 21 (Alpha v1) player credentials require Windows DPAPI")
    encrypted = base64.b64decode(value)
    encrypted_buffer = ctypes.create_string_buffer(encrypted)
    entropy_buffer = ctypes.create_string_buffer(entropy)
    input_blob = DataBlob(len(encrypted), ctypes.cast(encrypted_buffer, ctypes.POINTER(ctypes.c_byte)))
    entropy_blob = DataBlob(len(entropy), ctypes.cast(entropy_buffer, ctypes.POINTER(ctypes.c_byte)))
    output_blob = DataBlob()
    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(input_blob), None, ctypes.byref(entropy_blob), None, None, 0, ctypes.byref(output_blob)
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData).decode("utf-8")
    finally:
        ctypes.windll.kernel32.LocalFree(output_blob.pbData)


def control_plane_enabled() -> bool:
    try:
        document = json.loads(CONTROL_PATH.read_text(encoding="utf-8-sig"))
        return document.get("enabled") is True
    except (OSError, ValueError, AttributeError):
        return False


def load_access_token() -> str:
    if not control_plane_enabled():
        return ""
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise ValueError("LOCALAPPDATA is unavailable")
    path = Path(local_app_data) / "Project21" / "player-access.json"
    try:
        document = json.loads(path.read_text(encoding="utf-8-sig"))
        protected = str(document["protectedAccessToken"])
    except (OSError, ValueError, KeyError) as error:
        raise ValueError("Discord device access is missing; reconnect in the launcher") from error
    token = unprotect_windows_dpapi(protected, b"Project21.PlayerAccess.v1")
    if not token or len(token) > 7000:
        raise ValueError("Discord device access token is invalid")
    return token


def load_settings() -> tuple[str, float]:
    try:
        raw = json.loads(SETTINGS_PATH.read_text(encoding="utf-8-sig"))
    except FileNotFoundError as error:
        raise ValueError(f"Missing {SETTINGS_PATH}") from error
    except json.JSONDecodeError as error:
        raise ValueError(f"Invalid JSON in {SETTINGS_PATH}: {error}") from error
    if not isinstance(raw, dict):
        raise ValueError("remote-server.json must contain one JSON object")
    address = raw.get("serverAddress")
    if not isinstance(address, str) or not address.strip() or address == "CHANGE_ME_TO_SERVER_IP":
        raise ValueError("remote-server.json must contain a configured serverAddress")
    timeout = float(raw.get("connectTimeoutSeconds", 5))
    if not 1 <= timeout <= 30:
        raise ValueError("connectTimeoutSeconds must be between 1 and 30")
    socket.getaddrinfo(address.strip(), 8090, type=socket.SOCK_STREAM)
    return address.strip(), timeout


def authenticate(remote: socket.socket, token: str, timeout: float) -> None:
    if not token:
        return
    remote.settimeout(timeout)
    remote.sendall(AUTH_PREFIX + token.encode("ascii") + b"\n")
    acknowledgement = bytearray()
    while b"\n" not in acknowledgement and len(acknowledgement) < 64:
        chunk = remote.recv(64 - len(acknowledgement))
        if not chunk:
            break
        acknowledgement.extend(chunk)
    if bytes(acknowledgement) != b"P21OK\n":
        raise PermissionError("the host rejected this Discord device")
    remote.settimeout(None)


class ReusableThreadingTCPServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


class ReusableThreadingUDPServer(socketserver.ThreadingUDPServer):
    allow_reuse_address = True
    daemon_threads = True


class TcpRelay(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        port = int(self.server.server_address[1])
        try:
            remote = socket.create_connection((self.server.remote_host, port), self.server.connect_timeout)
            authenticate(remote, self.server.access_token, self.server.connect_timeout)
            log.info("TCP %d connected through authenticated gateway", port)
            sockets = [self.request, remote]
            while sockets:
                readable, _, _ = select.select(sockets, [], [], 60)
                for source in readable:
                    data = source.recv(65536)
                    if not data:
                        return
                    target = remote if source is self.request else self.request
                    target.sendall(data)
        except (ConnectionError, OSError, PermissionError) as error:
            log.error("TCP %d relay failed: %s", port, error)
        finally:
            try:
                remote.close()
            except (UnboundLocalError, OSError):
                pass


class UdpRelay(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        payload, local_socket = self.request
        port = int(self.server.server_address[1])
        family = socket.AF_INET6 if ":" in self.server.remote_host else socket.AF_INET
        try:
            packet = payload
            if self.server.access_token:
                packet = AUTH_PREFIX + self.server.access_token.encode("ascii") + b"\n" + payload
            with socket.socket(family, socket.SOCK_DGRAM) as remote:
                remote.settimeout(self.server.connect_timeout)
                remote.sendto(packet, (self.server.remote_host, port))
                response, _ = remote.recvfrom(65535)
            local_socket.sendto(response, self.client_address)
        except OSError as error:
            log.error("UDP %d relay failed: %s", port, error)


def attach_settings(server: socketserver.BaseServer, address: str, timeout: float, token: str) -> None:
    server.remote_host = address
    server.connect_timeout = timeout
    server.access_token = token


def check_remote(address: str, timeout: float, token: str) -> None:
    failures: list[str] = []
    for port in REQUIRED_REMOTE_PORTS:
        try:
            with socket.create_connection((address, port), timeout) as remote:
                authenticate(remote, token, timeout)
            log.info("Authenticated remote check passed: %s:%d", address, port)
        except (OSError, PermissionError) as error:
            failures.append(f"{address}:{port} ({error})")
    if failures:
        raise ConnectionError("Standalone server is not reachable: " + ", ".join(failures))


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    parser = argparse.ArgumentParser(description="Aurora 21 (Alpha v1) standalone-launcher authenticated relay")
    parser.add_argument("--lsx-mode", choices=("auto", "off", "required"), default="off")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        address, timeout = load_settings()
        token = load_access_token()
        check_remote(address, timeout, token)
        if args.check:
            print(f"Standalone server authorized at {address}")
            return 0
        tcp_ports = list(TCP_PORTS)
        if args.lsx_mode == "required":
            tcp_ports.append(3216)
        servers: list[socketserver.BaseServer] = []
        for port in tcp_ports:
            server = ReusableThreadingTCPServer(("127.0.0.1", port), TcpRelay)
            attach_settings(server, address, timeout, token)
            servers.append(server)
        for port in UDP_PORTS:
            server = ReusableThreadingUDPServer(("127.0.0.1", port), UdpRelay)
            attach_settings(server, address, timeout, token)
            servers.append(server)
        for server in servers:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        log.info("Local authenticated relay ready for %s", address)
        threading.Event().wait()
    except (OSError, ValueError, ConnectionError) as error:
        log.error("Relay startup failed: %s", error)
        return 2
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
