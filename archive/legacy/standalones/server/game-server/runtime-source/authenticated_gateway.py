from __future__ import annotations

import base64
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import json
import logging
import os
from pathlib import Path
import select
import signal
import socket
import socketserver
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor


ROOT = Path(__file__).resolve().parents[1]
TCP_PORTS = (44321, 8090, 10853, 17502, 8081, 42231, 42232)
UDP_PORTS = (17502,)
AUTH_PREFIX = b"P21AUTH "
MAX_AUTH_LINE = 8192
log = logging.getLogger("project21-gateway")


class DataBlob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]


def unprotect_windows_dpapi(value: str, entropy: bytes) -> str:
    if os.name != "nt":
        raise RuntimeError("Project 21 host credentials require Windows DPAPI")
    encrypted = base64.b64decode(value)
    encrypted_buffer = ctypes.create_string_buffer(encrypted)
    entropy_buffer = ctypes.create_string_buffer(entropy)
    input_blob = DataBlob(len(encrypted), ctypes.cast(encrypted_buffer, ctypes.POINTER(ctypes.c_byte)))
    entropy_blob = DataBlob(len(entropy), ctypes.cast(entropy_buffer, ctypes.POINTER(ctypes.c_byte)))
    output_blob = DataBlob()
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    if not crypt32.CryptUnprotectData(
        ctypes.byref(input_blob), None, ctypes.byref(entropy_blob), None, None, 0, ctypes.byref(output_blob)
    ):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData).decode("utf-8")
    finally:
        kernel32.LocalFree(output_blob.pbData)


class AccessValidator:
    def __init__(self, base_url: str, host_secret: str) -> None:
        self.url = base_url.rstrip("/") + "/api/host/validate"
        self.host_secret = host_secret
        self.cache: dict[str, tuple[float, bool, str]] = {}
        self.inflight: dict[str, threading.Event] = {}
        self.lock = threading.Lock()

    def validate(self, token: str, force: bool = False) -> tuple[bool, str]:
        waited = False
        while True:
            now = time.monotonic()
            with self.lock:
                cached = self.cache.get(token)
                if cached and (not force or waited) and cached[0] > now:
                    return cached[1], cached[2]
                gate = self.inflight.get(token)
                if gate is None:
                    gate = threading.Event()
                    self.inflight[token] = gate
                    break
            # FIFA opens several protocol sockets together.  Coalesce those
            # checks into one control-plane request for this device token.
            if not gate.wait(timeout=6):
                return False, ""
            waited = True
        body = json.dumps({"accessToken": token}, separators=(",", ":")).encode("utf-8")
        request = urllib.request.Request(
            self.url,
            data=body,
            method="POST",
            headers={
                "content-type": "application/json",
                "x-project21-host-secret": self.host_secret,
                "user-agent": "Project21-Authenticated-Gateway/1",
            },
        )
        allowed = False
        identity = ""
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                result = json.loads(response.read(65536).decode("utf-8"))
                allowed = bool(result.get("allowed"))
                identity = str(result.get("discordId", ""))
        except (urllib.error.URLError, TimeoutError, ValueError, OSError):
            allowed = False
        now = time.monotonic()
        with self.lock:
            self.cache[token] = (now + (5 if allowed else 2), allowed, identity)
            if len(self.cache) > 4096:
                self.cache = {key: value for key, value in self.cache.items() if value[0] > now}
            completed = self.inflight.pop(token, gate)
            completed.set()
        return allowed, identity


class InstanceManager:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.server_script = root / "runtime-source" / "localfut21" / "server.py"
        self.processes: dict[str, tuple[subprocess.Popen, str, float, object, object]] = {}
        self.address_owners: dict[str, str] = {}
        self.lock = threading.Lock()
        threading.Thread(target=self._reap_idle, daemon=True).start()

    @staticmethod
    def address_for(discord_id: str) -> str:
        digest = hashlib.sha256(discord_id.encode("ascii")).digest()
        return f"127.{1 + digest[0] % 253}.{1 + digest[1] % 253}.{1 + digest[2] % 253}"

    def get_origin(self, discord_id: str) -> str:
        with self.lock:
            existing = self.processes.get(discord_id)
            if existing and existing[0].poll() is None:
                self.processes[discord_id] = (existing[0], existing[1], time.monotonic(), existing[3], existing[4])
                return existing[1]
            if existing:
                existing[3].close()
                existing[4].close()
                self.processes.pop(discord_id, None)
                self.address_owners.pop(existing[1], None)
            address = self.address_for(discord_id)
            owner = self.address_owners.get(address)
            if owner and owner != discord_id:
                raise RuntimeError("Discord instance address collision")
            state_root = self.root / "state" / "players" / discord_id
            config_root = self.root / "config" / "players" / discord_id
            log_root = state_root / "logs"
            log_root.mkdir(parents=True, exist_ok=True)
            config_root.mkdir(parents=True, exist_ok=True)
            environment = os.environ.copy()
            environment["LOCALFUT_RUNTIME_ROOT"] = str(state_root)
            environment["LOCALFUT21_USER_CONFIG_ROOT"] = str(config_root)
            stdout = (log_root / "server.log").open("ab")
            stderr = (log_root / "server.error.log").open("ab")
            creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
            process = subprocess.Popen(
                [sys.executable, "-u", str(self.server_script), "--host", address, "--lsx-mode", "off"],
                cwd=str(self.server_script.parent.parent),
                env=environment,
                stdin=subprocess.DEVNULL,
                stdout=stdout,
                stderr=stderr,
                creationflags=creation_flags,
            )
            self.processes[discord_id] = (process, address, time.monotonic(), stdout, stderr)
            self.address_owners[address] = discord_id
            (state_root / "server.pid").write_text(str(process.pid), encoding="ascii")
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"LocalFUT instance exited with code {process.returncode}")
            try:
                with socket.create_connection((address, 8090), 0.5):
                    log.info("Started isolated LocalFUT instance Discord %s on %s", discord_id, address)
                    return address
            except OSError:
                time.sleep(0.25)
        self.stop(discord_id)
        raise TimeoutError("LocalFUT player instance did not become ready")

    def touch(self, discord_id: str) -> None:
        with self.lock:
            existing = self.processes.get(discord_id)
            if existing:
                self.processes[discord_id] = (existing[0], existing[1], time.monotonic(), existing[3], existing[4])

    def stop(self, discord_id: str) -> None:
        with self.lock:
            existing = self.processes.pop(discord_id, None)
            if not existing:
                return
            self.address_owners.pop(existing[1], None)
        process, _, _, stdout, stderr = existing
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        stdout.close()
        stderr.close()
        pid_path = self.root / "state" / "players" / discord_id / "server.pid"
        try:
            pid_path.unlink()
        except FileNotFoundError:
            pass

    def stop_all(self) -> None:
        with self.lock:
            identities = list(self.processes)
        for discord_id in identities:
            self.stop(discord_id)

    def _reap_idle(self) -> None:
        while True:
            time.sleep(60)
            cutoff = time.monotonic() - 1800
            with self.lock:
                stale = [identity for identity, entry in self.processes.items() if entry[2] < cutoff]
            for identity in stale:
                log.info("Stopping idle LocalFUT instance Discord %s", identity)
                self.stop(identity)


def read_auth_line(connection: socket.socket) -> tuple[str, bytes]:
    data = bytearray()
    connection.settimeout(10)
    while b"\n" not in data and len(data) < MAX_AUTH_LINE:
        # Read the authentication line exactly.  The native protocol bytes that
        # follow must remain in the socket when the connection is dispatched
        # directly into the shared LocalFUT runtime.
        chunk = connection.recv(1)
        if not chunk:
            break
        data.extend(chunk)
    line, separator, remainder = bytes(data).partition(b"\n")
    if not separator or not line.startswith(AUTH_PREFIX):
        raise PermissionError("missing Project 21 authentication header")
    token = line[len(AUTH_PREFIX):].decode("ascii", errors="strict")
    if not token or len(token) > 7000:
        raise PermissionError("invalid Project 21 token")
    return token, remainder


def load_localfut_runtime():
    source_root = ROOT / "runtime-source" / "localfut21"
    server_path = source_root / "server.py"
    if str(source_root) not in sys.path:
        sys.path.insert(0, str(source_root))
    spec = importlib.util.spec_from_file_location("project21_shared_localfut", server_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load LocalFUT runtime from {server_path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    previous_mode = os.environ.get("LOCALFUT_MULTI_TENANT_BOOTSTRAP")
    os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"
    try:
        spec.loader.exec_module(module)
    finally:
        if previous_mode is None:
            os.environ.pop("LOCALFUT_MULTI_TENANT_BOOTSTRAP", None)
        else:
            os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = previous_mode
    return module


def load_host_secret(secret_document: dict[str, object]) -> str:
    environment_secret = os.environ.get("PROJECT21_HOST_AGENT_SECRET", "")
    if environment_secret:
        return environment_secret
    if os.name == "nt" and secret_document.get("protectedSecret"):
        return unprotect_windows_dpapi(
            str(secret_document["protectedSecret"]), b"Project21.HostAgent.v1"
        )
    configured = str(secret_document.get("secretFile", "")).strip()
    if not configured:
        raise RuntimeError(
            "Set PROJECT21_HOST_AGENT_SECRET or configure secretFile for the Linux host"
        )
    secret_path = Path(configured)
    if not secret_path.is_absolute():
        secret_path = (ROOT / secret_path).resolve()
    if os.name != "nt":
        mode = secret_path.stat().st_mode
        # The Ubuntu unit deliberately runs in the dedicated project21 group,
        # so root:project21 0640 is valid.  Never accept world access or group
        # write/execute permission.
        if mode & 0o007 or mode & 0o030:
            raise PermissionError(
                f"Host secret must be root/user readable only: {secret_path}"
            )
    secret = secret_path.read_text(encoding="utf-8").strip()
    if len(secret) < 32:
        raise RuntimeError("HOST_AGENT_SECRET must contain at least 32 characters")
    return secret


class SessionMonitor:
    """Revalidate each unique active device once and close revoked sessions."""

    def __init__(
        self,
        validator: AccessValidator,
        interval_seconds: int = 900,
        command_directory: Path | None = None,
        presence_path: Path | None = None,
        online_grace_seconds: int = 10,
    ) -> None:
        self.validator = validator
        self.interval_seconds = max(60, int(interval_seconds))
        self.command_directory = command_directory or ROOT / "state" / "session-commands"
        self.command_directory.mkdir(parents=True, exist_ok=True)
        self.presence_path = presence_path or ROOT / "state" / "active-sessions.json"
        self.presence_path.parent.mkdir(parents=True, exist_ok=True)
        self.online_grace_seconds = max(0, int(online_grace_seconds))
        self.sessions: dict[str, tuple[str, set[socket.socket]]] = {}
        self.game_sessions: dict[str, dict[socket.socket, float]] = {}
        self.lock = threading.Lock()
        self.closed = threading.Event()
        self.executor = ThreadPoolExecutor(max_workers=32, thread_name_prefix="AccessRevalidate")
        with self.lock:
            self._write_presence_locked()
        threading.Thread(target=self._run, name="AccessSessionMonitor", daemon=True).start()

    def _write_presence_locked(self) -> None:
        current = time.monotonic()
        players = [
            {"discordId": identity, "connections": sum(
                1 for connected_at in connections.values()
                if current - connected_at >= self.online_grace_seconds
            )}
            for identity, connections in sorted(self.game_sessions.items())
        ]
        players = [player for player in players if int(player["connections"]) > 0]
        document = {"updatedAt": int(time.time()), "players": players}
        temporary = self.presence_path.with_name(f".{self.presence_path.name}.tmp")
        try:
            temporary.write_text(
                json.dumps(document, separators=(",", ":")) + "\n", encoding="utf-8"
            )
            temporary.replace(self.presence_path)
        except OSError as error:
            log.warning("Could not update active game presence: %s", error)
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass

    def add(
        self,
        token: str,
        identity: str,
        connection: socket.socket,
        game_session: bool = False,
    ) -> None:
        with self.lock:
            current_identity, connections = self.sessions.setdefault(
                token, (identity, set())
            )
            if current_identity != identity:
                raise PermissionError("device token identity changed")
            connections.add(connection)
            if game_session:
                self.game_sessions.setdefault(identity, {})[connection] = time.monotonic()
            self._write_presence_locked()

    def remove(self, token: str, connection: socket.socket) -> None:
        with self.lock:
            entry = self.sessions.get(token)
            if entry is None:
                return
            identity, connections = entry
            connections.discard(connection)
            if not connections:
                self.sessions.pop(token, None)
            game_connections = self.game_sessions.get(identity)
            if game_connections is not None:
                game_connections.pop(connection, None)
                if not game_connections:
                    self.game_sessions.pop(identity, None)
            self._write_presence_locked()

    def _validate(self, token: str) -> tuple[str, bool]:
        allowed, _ = self.validator.validate(token, force=True)
        return token, allowed

    def _disconnect_identities(self, identities: set[str]) -> None:
        with self.lock:
            revoked_tokens = [
                token for token, (identity, _) in self.sessions.items()
                if identity in identities
            ]
            connections = [
                connection
                for token in revoked_tokens
                for connection in self.sessions.pop(token)[1]
            ]
            for identity in identities:
                self.game_sessions.pop(identity, None)
            self._write_presence_locked()
        for connection in connections:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    def _disconnect_all(self) -> int:
        with self.lock:
            connections = [
                connection
                for _, connection_set in self.sessions.values()
                for connection in connection_set
            ]
            self.sessions.clear()
            self.game_sessions.clear()
            self._write_presence_locked()
        for connection in connections:
            try:
                connection.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        return len(connections)

    def _consume_local_commands(self) -> None:
        identities: set[str] = set()
        kick_all = False
        for path in self.command_directory.glob("*.json"):
            try:
                command = json.loads(path.read_text(encoding="utf-8"))
                identity = str(command.get("discordId", ""))
                action = str(command.get("action", ""))
                if action == "kick_all":
                    kick_all = True
                elif (
                    identity.isascii()
                    and identity.isdecimal()
                    and 5 <= len(identity) <= 32
                    and action in {"ban_user", "kick_user", "revoke_user"}
                ):
                    identities.add(identity)
            except (OSError, ValueError, AttributeError):
                log.warning("Ignoring invalid local session command %s", path.name)
            finally:
                try:
                    path.unlink()
                except OSError:
                    pass
        if kick_all:
            disconnected = self._disconnect_all()
            log.warning("Administrator kicked all active FUT sessions (%d connections)", disconnected)
            return
        if identities:
            self._disconnect_identities(identities)

    def _run(self) -> None:
        next_validation = time.monotonic() + self.interval_seconds
        next_presence = time.monotonic() + 5
        while not self.closed.wait(1):
            self._consume_local_commands()
            if time.monotonic() >= next_presence:
                with self.lock:
                    self._write_presence_locked()
                next_presence = time.monotonic() + 5
            if time.monotonic() < next_validation:
                continue
            with self.lock:
                tokens = list(self.sessions)
            for token, allowed in self.executor.map(self._validate, tokens):
                if allowed:
                    continue
                with self.lock:
                    entry = self.sessions.pop(token, None)
                    connections = list(entry[1]) if entry is not None else []
                    if entry is not None:
                        self.game_sessions.pop(entry[0], None)
                    self._write_presence_locked()
                for connection in connections:
                    try:
                        connection.shutdown(socket.SHUT_RDWR)
                    except OSError:
                        pass
            next_validation = time.monotonic() + self.interval_seconds

    def close(self) -> None:
        self.closed.set()
        self.executor.shutdown(wait=True, cancel_futures=True)
        with self.lock:
            self.sessions.clear()
            self.game_sessions.clear()
            self._write_presence_locked()


class SharedRuntimeTcpHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        port = int(self.server.server_address[1])
        token = ""
        identity = ""
        try:
            token, remainder = read_auth_line(self.request)
            if remainder:
                raise PermissionError("unexpected bytes after authentication header")
            allowed, identity = self.server.validator.validate(token)
            if not allowed:
                raise PermissionError("device access revoked")
            self.request.settimeout(None)
            self.request.sendall(b"P21OK\n")
            self.server.session_monitor.add(
                token, identity, self.request, game_session=(port == 44321)
            )
            log.info("TCP %d authorized Discord %s", port, identity)
            with self.server.player_registry.scope(identity):
                self.server.target_handler(self.request, self.client_address, self.server)
        except (OSError, PermissionError, UnicodeError, ValueError) as error:
            log.warning("TCP %d rejected %s: %s", port, self.client_address[0], error)
        finally:
            if token:
                self.server.session_monitor.remove(token, self.request)
            if identity:
                log.info("TCP %d disconnected Discord %s", port, identity)


class SharedRuntimeUdpHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        packet, listening_socket = self.request
        line, separator, payload = packet.partition(b"\n")
        if not separator or not line.startswith(AUTH_PREFIX):
            return
        try:
            token = line[len(AUTH_PREFIX):].decode("ascii", errors="strict")
            allowed, identity = self.server.validator.validate(token)
            if not allowed:
                return
            with self.server.player_registry.scope(identity):
                self.server.target_handler(
                    (payload, listening_socket), self.client_address, self.server
                )
        except (OSError, UnicodeError, ValueError):
            return


class GatewayTcpHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        port = int(self.server.server_address[1])
        try:
            token, initial = read_auth_line(self.request)
            allowed, identity = self.server.validator.validate(token)
            if not allowed:
                raise PermissionError("device access revoked")
            origin_host = self.server.instance_manager.get_origin(identity) if self.server.instance_manager else self.server.origin_host
            origin = socket.create_connection((origin_host, port), 5)
            self.request.sendall(b"P21OK\n")
            origin.setblocking(False)
            self.request.setblocking(False)
            if initial:
                origin.sendall(initial)
            log.info("TCP %d authorized Discord %s", port, identity)
            sockets = [self.request, origin]
            next_validation = time.monotonic() + 5
            while sockets:
                if time.monotonic() >= next_validation:
                    allowed, _ = self.server.validator.validate(token)
                    if not allowed:
                        log.warning("TCP %d revoked during active session Discord %s", port, identity)
                        break
                    if self.server.instance_manager:
                        self.server.instance_manager.touch(identity)
                    next_validation = time.monotonic() + 5
                readable, _, _ = select.select(sockets, [], [], 1)
                for source in readable:
                    chunk = source.recv(65536)
                    if not chunk:
                        return
                    destination = origin if source is self.request else self.request
                    destination.sendall(chunk)
        except (OSError, PermissionError, UnicodeError) as error:
            log.warning("TCP %d rejected %s: %s", port, self.client_address[0], error)
        finally:
            try:
                origin.close()
            except (UnboundLocalError, OSError):
                pass


class GatewayUdpHandler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        packet, listening_socket = self.request
        line, separator, payload = packet.partition(b"\n")
        if not separator or not line.startswith(AUTH_PREFIX):
            return
        try:
            token = line[len(AUTH_PREFIX):].decode("ascii", errors="strict")
            allowed, identity = self.server.validator.validate(token)
            if not allowed:
                return
            port = int(self.server.server_address[1])
            origin_host = self.server.instance_manager.get_origin(identity) if self.server.instance_manager else self.server.origin_host
            if self.server.instance_manager:
                self.server.instance_manager.touch(identity)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as origin:
                origin.settimeout(5)
                origin.sendto(payload, (origin_host, port))
                response, _ = origin.recvfrom(65535)
            listening_socket.sendto(response, self.client_address)
            log.debug("UDP %d authorized Discord %s", port, identity)
        except (OSError, UnicodeError):
            return


class ReusableTcpServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 2048


class ReusableUdpServer(socketserver.ThreadingUDPServer):
    allow_reuse_address = True
    daemon_threads = True
    request_queue_size = 2048


def attach(server: socketserver.BaseServer, validator: AccessValidator, origin_host: str, instance_manager: InstanceManager | None = None) -> None:
    server.validator = validator
    server.origin_host = origin_host
    server.instance_manager = instance_manager


def attach_shared(
    server: socketserver.BaseServer,
    validator: AccessValidator,
    session_monitor: SessionMonitor,
    player_registry,
    target_handler,
) -> None:
    server.validator = validator
    server.session_monitor = session_monitor
    server.player_registry = player_registry
    server.target_handler = target_handler


def main() -> int:
    settings = json.loads((ROOT / "gateway-settings.json").read_text(encoding="utf-8-sig"))
    log_level_name = str(settings.get("logLevel", "INFO")).upper()
    log_level = getattr(logging, log_level_name, None)
    if not isinstance(log_level, int):
        raise ValueError(f"invalid gateway logLevel: {log_level_name}")
    logging.basicConfig(level=log_level, format="%(asctime)s %(levelname)s %(message)s")

    def set_live_log_level(level: int, label: str) -> None:
        root_logger = logging.getLogger()
        if root_logger.level == level:
            return
        root_logger.setLevel(level)
        log.warning("Live player traffic logging %s", label)

    # project21-live-logs enables DEBUG only while an administrator watches.
    if hasattr(signal, "SIGUSR1"):
        signal.signal(
            signal.SIGUSR1,
            lambda _signum, _frame: set_live_log_level(logging.DEBUG, "enabled"),
        )
        signal.signal(
            signal.SIGUSR2,
            lambda _signum, _frame: set_live_log_level(log_level, "disabled"),
        )
    # Linux commonly reserves an 8 MiB virtual stack per native thread.  The
    # protocol handlers are shallow and I/O-bound, so a smaller stack keeps
    # hundreds of concurrent authenticated sessions practical.
    threading.stack_size(512 * 1024)
    control = json.loads((ROOT / "control-plane-host.json").read_text(encoding="utf-8-sig"))
    secret_document_path = ROOT / "config" / "control-plane-host-secret.json"
    secret_document = (
        json.loads(secret_document_path.read_text(encoding="utf-8-sig"))
        if secret_document_path.exists()
        else {}
    )
    if control.get("enabled") is not True:
        raise RuntimeError("control-plane-host.json is disabled")
    bind_address = str(settings.get("bindAddress", ""))
    origin_host = str(settings.get("originAddress", "127.0.0.1"))
    if not bind_address or bind_address == "127.0.0.1":
        raise RuntimeError("gateway bindAddress must be a reachable host IPv4 address")
    host_secret = load_host_secret(secret_document)
    validator = AccessValidator(str(control["baseUrl"]), host_secret)
    servers: list[socketserver.BaseServer] = []
    shared_runtime = bool(settings.get("singleRuntime", True))
    instance_manager = None
    session_monitor = None
    player_registry = None
    gameplay_relay = None
    if shared_runtime:
        localfut = load_localfut_runtime()
        gameplay_relay = localfut.GAMEPLAY_RELAY
        gameplay_relay.enabled = bool(settings.get("gameplayRelayEnabled", False))
        gameplay_relay.bind_address = str(
            settings.get("gameplayRelayBindAddress", "auto") or "auto"
        )
        gameplay_relay.advertised_address = str(
            settings.get("gameplayRelayAdvertisedAddress", "auto") or "auto"
        )
        gameplay_relay.port_start = int(settings.get("gameplayRelayPortStart", 31000))
        gameplay_relay.port_end = int(settings.get("gameplayRelayPortEnd", 31199))
        gameplay_relay.start(bind_address)
        player_registry = localfut.enable_multi_tenant(
            ROOT,
            idle_seconds=int(settings.get("idleContextSeconds", 900)),
            max_cached_players=int(settings.get("maxCachedPlayers", 1000)),
            initialization_workers=int(settings.get("contextInitWorkers", 4)),
        )
        session_monitor = SessionMonitor(
            validator, interval_seconds=int(settings.get("revalidateSeconds", 900))
        )
        tcp_targets = {
            44321: localfut.BlazeOrHttpHandler,
            8090: localfut.FutHandler,
            10853: localfut.SsfHandler,
            17502: localfut.QosHandler,
            8081: localfut.FutHandler,
            42231: localfut.RedirectHandler,
            42232: localfut.EaswHandler,
        }
        for port in TCP_PORTS:
            server = ReusableTcpServer((bind_address, port), SharedRuntimeTcpHandler)
            attach_shared(server, validator, session_monitor, player_registry, tcp_targets[port])
            servers.append(server)
        for port in UDP_PORTS:
            server = ReusableUdpServer((bind_address, port), SharedRuntimeUdpHandler)
            attach_shared(server, validator, session_monitor, player_registry, localfut.QosUdpHandler)
            servers.append(server)
    else:
        instance_manager = InstanceManager(ROOT)
        for port in TCP_PORTS:
            server = ReusableTcpServer((bind_address, port), GatewayTcpHandler)
            attach(server, validator, origin_host, instance_manager)
            servers.append(server)
        for port in UDP_PORTS:
            server = ReusableUdpServer((bind_address, port), GatewayUdpHandler)
            attach(server, validator, origin_host, instance_manager)
            servers.append(server)
    for server in servers:
        threading.Thread(target=server.serve_forever, daemon=True).start()
    log.info(
        "Authenticated gateway listening on %s; runtime=%s",
        bind_address,
        "shared multi-tenant" if shared_runtime else f"per-player at {origin_host}",
    )
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        return 0
    finally:
        for server in servers:
            server.shutdown()
            server.server_close()
        if session_monitor is not None:
            session_monitor.close()
        if gameplay_relay is not None:
            gameplay_relay.stop()
        if player_registry is not None:
            player_registry.close()
        if instance_manager is not None:
            instance_manager.stop_all()


if __name__ == "__main__":
    raise SystemExit(main())
