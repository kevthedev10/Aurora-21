from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import logging
import os
from pathlib import Path
import shutil
import socket
import sys
import tempfile
import threading
import time


SOURCE_ROOT = Path(__file__).resolve().parent / "runtime-source"
sys.path.insert(0, str(SOURCE_ROOT))

import authenticated_gateway as gateway  # noqa: E402


class ProbeValidator:
    def validate(self, token: str, force: bool = False) -> tuple[bool, str]:
        del force
        return token.isascii() and token.isdecimal(), token


class ProbeSessionMonitor:
    def add(self, token: str, identity: str, connection: socket.socket) -> None:
        del token, identity, connection

    def remove(self, token: str, connection: socket.socket) -> None:
        del token, connection


def linux_process_metrics() -> dict[str, float | int | None]:
    result: dict[str, float | int | None] = {
        "currentRssMiB": None,
        "peakRssMiB": None,
        "threads": None,
        "openFileDescriptors": None,
    }
    if not sys.platform.startswith("linux"):
        return result
    values: dict[str, int] = {}
    for line in Path("/proc/self/status").read_text(encoding="ascii").splitlines():
        if line.startswith(("VmRSS:", "VmHWM:", "Threads:")):
            name, raw = line.split(":", 1)
            values[name] = int(raw.strip().split()[0])
    result.update(
        currentRssMiB=round(values.get("VmRSS", 0) / 1024, 1),
        peakRssMiB=round(values.get("VmHWM", 0) / 1024, 1),
        threads=values.get("Threads"),
        openFileDescriptors=len(list(Path("/proc/self/fd").iterdir())),
    )
    return result


def receive_http_json(connection: socket.socket) -> dict:
    response = bytearray()
    while b"\r\n\r\n" not in response:
        chunk = connection.recv(65536)
        if not chunk:
            raise ConnectionError("HTTP response ended before its headers")
        response.extend(chunk)
    raw_headers, _, body = bytes(response).partition(b"\r\n\r\n")
    content_length = 0
    for line in raw_headers.split(b"\r\n"):
        if line.lower().startswith(b"content-length:"):
            content_length = int(line.split(b":", 1)[1].strip())
            break
    while len(body) < content_length:
        chunk = connection.recv(content_length - len(body))
        if not chunk:
            raise ConnectionError("HTTP response body ended early")
        body += chunk
    return json.loads(body[:content_length])


def authenticate(address: tuple[str, int], identity: str) -> socket.socket:
    connection = socket.create_connection(address, timeout=30)
    connection.sendall(f"P21AUTH {identity}\n".encode("ascii"))
    acknowledgement = b""
    while len(acknowledgement) < 6:
        chunk = connection.recv(6 - len(acknowledgement))
        if not chunk:
            break
        acknowledgement += chunk
    if acknowledgement != b"P21OK\n":
        connection.close()
        raise PermissionError("gateway authentication failed")
    connection.settimeout(30)
    return connection


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Exercise shared LocalFUT state, live sockets and real FUT HTTP handlers"
    )
    parser.add_argument("--players", type=int, default=50)
    parser.add_argument("--workers", type=int, default=32)
    parser.add_argument("--startup-timeout", type=int, default=1800)
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()
    if not 1 <= args.players <= 1000:
        parser.error("--players must be between 1 and 1000")
    if not 1 <= args.workers <= 128:
        parser.error("--workers must be between 1 and 128")

    test_root = Path(tempfile.mkdtemp(prefix="project21-protocol-load-"))
    bootstrap_runtime = test_root / "bootstrap"
    bootstrap_config = bootstrap_runtime / "config"
    bootstrap_config.mkdir(parents=True)
    os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
    os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
    logging.basicConfig(level=logging.WARNING)
    threading.stack_size(512 * 1024)
    localfut = gateway.load_localfut_runtime()
    registry = localfut.enable_multi_tenant(
        test_root,
        idle_seconds=3600,
        max_cached_players=args.players,
        initialization_workers=min(4, args.workers),
    )
    server = gateway.ReusableTcpServer(
        ("127.0.0.1", 0), gateway.SharedRuntimeTcpHandler
    )
    gateway.attach_shared(
        server,
        ProbeValidator(),
        ProbeSessionMonitor(),
        registry,
        localfut.BlazeOrHttpHandler,
    )
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    identities = [str(700000000000000000 + index) for index in range(args.players)]
    held_connections: list[socket.socket] = []
    failures: list[str] = []
    started = time.monotonic()
    try:
        def connect_held(identity: str) -> tuple[str, socket.socket | None, str]:
            try:
                return identity, authenticate(server.server_address, identity), ""
            except Exception as error:
                return identity, None, str(error)

        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            for identity, connection, error in executor.map(connect_held, identities):
                if connection is not None:
                    held_connections.append(connection)
                else:
                    failures.append(f"{identity}: {error}")

        deadline = time.monotonic() + args.startup_timeout
        ready = 0
        pending = 0
        while time.monotonic() < deadline:
            with registry.lock:
                ready = len(registry.contexts)
                pending = len(registry.loading)
            if ready == args.players or failures:
                break
            time.sleep(0.25)
        if ready != args.players:
            failures.append(
                f"only {ready}/{args.players} contexts became ready ({pending} still initializing)"
            )

        def request_credits(identity: str) -> None:
            try:
                with authenticate(server.server_address, identity) as connection:
                    connection.sendall(
                        b"GET /ut/game/fifa21/user/credits HTTP/1.1\r\n"
                        b"Host: project21.test\r\nConnection: close\r\n\r\n"
                    )
                    payload = receive_http_json(connection)
                    if not payload.get("currencies"):
                        raise ValueError("credit response did not contain currencies")
            except Exception as error:
                failures.append(f"{identity} FUT request: {error}")

        if not failures:
            with ThreadPoolExecutor(max_workers=args.workers) as executor:
                list(executor.map(request_credits, identities))

        state_bytes = sum(
            path.stat().st_size for path in test_root.rglob("*") if path.is_file()
        )
        result = {
            "playersRequested": args.players,
            "playerContextsReady": ready,
            "heldAuthenticatedConnections": len(held_connections),
            "realFutRequestsCompleted": args.players if not failures else None,
            "elapsedSeconds": round(time.monotonic() - started, 2),
            "testStateMiB": round(state_bytes / 1024 / 1024, 1),
            **linux_process_metrics(),
            "failures": failures[:20],
            "scope": (
                "synthetic authenticated socket and FUT-handler capacity; "
                "not proof of native FIFA gameplay"
            ),
        }
        print(json.dumps(result, indent=2))
        return 0 if not failures else 1
    finally:
        for connection in held_connections:
            connection.close()
        server.shutdown()
        server.server_close()
        registry.close()
        logging.shutdown()
        if not args.keep:
            shutil.rmtree(test_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
