from __future__ import annotations

import logging
import ipaddress
import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable


log = logging.getLogger("localfut15")


class GameplayRelayError(RuntimeError):
    """Base error raised by the FIFA gameplay relay."""


class GameplayRelayCapacityError(GameplayRelayError):
    """Raised when no complete two-port allocation can be reserved."""


@dataclass
class GameplayRelayAllocation:
    game_id: int
    player_ids: tuple[str, str]
    expected_ips: dict[str, str]
    advertised_addresses: dict[str, str]
    sockets: dict[str, socket.socket]
    ports: dict[str, int]
    created_at: float
    last_activity: float
    endpoints: dict[str, tuple[str, int]] = field(default_factory=dict)
    pending: dict[str, list[tuple[bytes, float, str, int]]] = field(
        default_factory=dict
    )
    pending_bytes: int = 0
    packets_received: int = 0
    packets_forwarded: int = 0
    bytes_forwarded: int = 0
    dropped_packets: int = 0
    captured_packets: dict[str, int] = field(default_factory=dict)
    connected_edges: set[tuple[str, str]] = field(default_factory=set)
    promoted_edges: set[tuple[str, str]] = field(default_factory=set)
    capture_active: bool = True
    rate_window_started: float = 0.0
    rate_window_packets: int = 0
    rate_window_bytes: int = 0
    closed: bool = False
    lock: threading.RLock = field(default_factory=threading.RLock)

    def endpoint_for(self, recipient_id: str) -> tuple[str, int]:
        identity = str(recipient_id)
        return self.advertised_addresses[identity], self.ports[identity]


class GameplayRelayManager:
    """Bounded, payload-transparent two-player UDP relay.

    Each player is assigned a separate server-side UDP ingress socket. Traffic
    sent by player A to A's assigned port is forwarded to B from B's assigned
    port, matching the endpoint projection used by every captured FIFA 21
    handshake that reached phase 04 in both directions.
    """

    def __init__(
        self,
        *,
        enabled: bool = False,
        bind_address: str = "auto",
        advertised_address: str = "auto",
        port_start: int = 31000,
        port_end: int = 31199,
        idle_timeout_seconds: float = 120.0,
        maximum_lifetime_seconds: float = 10800.0,
        maximum_pending_packets: int = 64,
        maximum_pending_bytes: int = 262144,
        maximum_packets_per_second: int = 2000,
        maximum_bytes_per_second: int = 8 * 1024 * 1024,
        maximum_datagram_size: int = 65507,
        enforce_source_ip: bool = True,
        handshake_capture_packets: int = 0,
        handshake_capture_bytes: int = 512,
    ) -> None:
        self.enabled = bool(enabled)
        self.bind_address = str(bind_address or "auto")
        self.advertised_address = str(advertised_address or "auto")
        self.port_start = int(port_start)
        self.port_end = int(port_end)
        self.idle_timeout_seconds = float(idle_timeout_seconds)
        self.maximum_lifetime_seconds = float(maximum_lifetime_seconds)
        self.maximum_pending_packets = int(maximum_pending_packets)
        self.maximum_pending_bytes = int(maximum_pending_bytes)
        self.maximum_packets_per_second = int(maximum_packets_per_second)
        self.maximum_bytes_per_second = int(maximum_bytes_per_second)
        self.maximum_datagram_size = int(maximum_datagram_size)
        self.enforce_source_ip = bool(enforce_source_ip)
        self.handshake_capture_packets = max(0, int(handshake_capture_packets))
        self.handshake_capture_bytes = max(1, int(handshake_capture_bytes))
        self._allocations: dict[int, GameplayRelayAllocation] = {}
        self._used_ports: set[int] = set()
        self._lock = threading.RLock()
        self._stop_event = threading.Event()
        self._started = False
        self._effective_bind_address = ""
        self._peer_link_callback: Callable[[int, str, str, str], None] | None = None

    def set_peer_link_callback(
        self, callback: Callable[[int, str, str, str], None] | None
    ) -> None:
        """Observe each direction's first forwarded FIFA 21 phase-04 packet."""
        self._peer_link_callback = callback

    @property
    def active(self) -> bool:
        return self.enabled and self._started

    @property
    def effective_bind_address(self) -> str:
        return self._effective_bind_address

    def start(self, server_bind_address: str) -> None:
        if self._started:
            return
        self._validate_configuration()
        if not self.enabled:
            return
        self._effective_bind_address = (
            str(server_bind_address)
            if self.bind_address.casefold() == "auto"
            else self.bind_address
        )
        self._validate_ipv4(self._effective_bind_address, "relay bind address", allow_any=True)
        if self.advertised_address.casefold() != "auto":
            self._validate_ipv4(self.advertised_address, "relay advertised address")
        self._stop_event.clear()
        self._started = True
        threading.Thread(
            target=self._sweep_loop,
            name="GameplayRelaySweeper",
            daemon=True,
        ).start()
        log.info(
            "GAMEPLAY RELAY READY bind=%s ports=%d-%d advertised=%s capacity=%d",
            self._effective_bind_address,
            self.port_start,
            self.port_end,
            self.advertised_address,
            (self.port_end - self.port_start + 1) // 2,
        )

    def _validate_configuration(self) -> None:
        if not 1024 <= self.port_start <= 65535:
            raise GameplayRelayError("gameplay relay port start must be 1024-65535")
        if not self.port_start < self.port_end <= 65535:
            raise GameplayRelayError("gameplay relay port end must be greater than start and at most 65535")
        if self.port_end - self.port_start + 1 < 2:
            raise GameplayRelayError("gameplay relay requires at least two UDP ports")
        positive = (
            self.idle_timeout_seconds,
            self.maximum_lifetime_seconds,
            self.maximum_pending_packets,
            self.maximum_pending_bytes,
            self.maximum_packets_per_second,
            self.maximum_bytes_per_second,
            self.maximum_datagram_size,
        )
        if any(value <= 0 for value in positive):
            raise GameplayRelayError("gameplay relay limits must all be positive")

    @staticmethod
    def _validate_ipv4(value: str, label: str, allow_any: bool = False) -> None:
        try:
            address = ipaddress.ip_address(str(value))
        except ValueError as error:
            raise GameplayRelayError(f"{label} must be an IPv4 literal") from error
        if address.version != 4 or (address.is_unspecified and not allow_any):
            raise GameplayRelayError(f"{label} must be a usable IPv4 literal")

    def _advertised_for(self, connection: Any) -> str:
        if self.advertised_address.casefold() != "auto":
            return self.advertised_address
        candidates = (
            str(getattr(connection, "server_ip", "") or ""),
            self._effective_bind_address,
        )
        for candidate in candidates:
            try:
                address = ipaddress.ip_address(candidate)
            except ValueError:
                continue
            if address.version == 4 and not address.is_unspecified:
                return candidate
        raise GameplayRelayError(
            "automatic relay advertisement could not determine a concrete server IPv4 address"
        )

    def allocate(
        self, game_id: int, player_entries: list[dict[str, Any]]
    ) -> GameplayRelayAllocation | None:
        if not self.active:
            return None
        if len(player_entries) != 2:
            raise GameplayRelayError("gameplay relay currently requires exactly two players")
        identities = tuple(str(entry["discord_id"]) for entry in player_entries)
        if identities[0] == identities[1]:
            raise GameplayRelayError("gameplay relay players must have distinct identities")
        expected_ips = {
            str(entry["discord_id"]): str(entry["connection"].peer_ip)
            for entry in player_entries
        }
        # The production clients are joined through WireGuard and FIFA binds
        # gameplay UDP to 3659.  Seed those already-authenticated endpoints so
        # the first peer packet can be forwarded immediately.  Previously the
        # relay queued it until the destination sent its own first packet; a
        # slow client could exceed FIFA's short mesh timeout and disconnect
        # before that happened.  A received datagram still refreshes the entry
        # below, so the observed source port remains authoritative.
        seeded_endpoints = {
            identity: (address, 3659)
            for identity, address in expected_ips.items()
            if not ipaddress.ip_address(address).is_loopback
        }
        advertised_addresses = {
            str(entry["discord_id"]): self._advertised_for(entry["connection"])
            for entry in player_entries
        }
        with self._lock:
            existing = self._allocations.get(int(game_id))
            if existing is not None:
                return existing
            bound = self._bind_socket_pair_locked()
            if bound is None:
                raise GameplayRelayCapacityError(
                    f"no free UDP relay pair in {self.port_start}-{self.port_end}"
                )
            ports, sockets = bound
            now = time.monotonic()
            allocation = GameplayRelayAllocation(
                game_id=int(game_id),
                player_ids=identities,
                expected_ips=expected_ips,
                advertised_addresses=advertised_addresses,
                sockets=dict(zip(identities, sockets)),
                ports=dict(zip(identities, ports)),
                created_at=now,
                last_activity=now,
                pending={identity: [] for identity in identities},
                endpoints=seeded_endpoints,
                captured_packets={identity: 0 for identity in identities},
                rate_window_started=now,
            )
            self._allocations[int(game_id)] = allocation
        for identity in identities:
            threading.Thread(
                target=self._receive_loop,
                args=(allocation, identity),
                name=f"GameplayRelay-{game_id}-{identity}",
                daemon=True,
            ).start()
        log.info(
            "GAMEPLAY RELAY ALLOCATED gameId=%d players=%s ports=%s",
            int(game_id), identities, allocation.ports,
        )
        return allocation

    def _bind_socket_pair_locked(
        self,
    ) -> tuple[tuple[int, int], tuple[socket.socket, socket.socket]] | None:
        for first_port in range(self.port_start, self.port_end):
            second_port = first_port + 1
            if first_port in self._used_ports or second_port in self._used_ports:
                continue
            sockets: list[socket.socket] = []
            try:
                for port in (first_port, second_port):
                    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                    sock.settimeout(0.25)
                    sock.bind((self._effective_bind_address, port))
                    sockets.append(sock)
            except OSError:
                for sock in sockets:
                    sock.close()
                continue
            self._used_ports.update((first_port, second_port))
            return (first_port, second_port), (sockets[0], sockets[1])
        return None

    def allocation_for(self, game_id: int) -> GameplayRelayAllocation | None:
        with self._lock:
            return self._allocations.get(int(game_id))

    def stop_handshake_capture(self, game_id: int) -> bool:
        """Stop verbose packet tracing once the game reaches IN_GAME."""
        allocation = self.allocation_for(game_id)
        if allocation is None:
            return False
        with allocation.lock:
            allocation.capture_active = False
        return True

    def release(self, game_id: int, reason: str = "released") -> bool:
        with self._lock:
            allocation = self._allocations.pop(int(game_id), None)
            if allocation is None:
                return False
            self._used_ports.difference_update(allocation.ports.values())
        with allocation.lock:
            allocation.closed = True
            sockets = list(allocation.sockets.values())
            allocation.pending.clear()
            allocation.pending_bytes = 0
        for sock in sockets:
            try:
                sock.close()
            except OSError:
                pass
        log.info(
            "GAMEPLAY RELAY RELEASED gameId=%d reason=%s received=%d forwarded=%d bytes=%d dropped=%d",
            int(game_id), reason, allocation.packets_received,
            allocation.packets_forwarded, allocation.bytes_forwarded,
            allocation.dropped_packets,
        )
        return True

    def stop(self) -> None:
        self._stop_event.set()
        with self._lock:
            game_ids = list(self._allocations)
        for game_id in game_ids:
            self.release(game_id, "server-stop")
        self._started = False

    def _receive_loop(
        self, allocation: GameplayRelayAllocation, source_identity: str
    ) -> None:
        sock = allocation.sockets[source_identity]
        while not self._stop_event.is_set():
            try:
                data, endpoint = sock.recvfrom(self.maximum_datagram_size + 1)
            except socket.timeout:
                if allocation.closed:
                    return
                continue
            except OSError:
                return
            self._handle_datagram(allocation, source_identity, data, endpoint)

    def _handle_datagram(
        self,
        allocation: GameplayRelayAllocation,
        source_identity: str,
        data: bytes,
        endpoint: tuple[str, int],
    ) -> None:
        sends: list[
            tuple[
                socket.socket, tuple[str, int], bytes, float, str, str, int, bool
            ]
        ] = []
        now = time.monotonic()
        with allocation.lock:
            if allocation.closed:
                return
            allocation.packets_received += 1
            if len(data) > self.maximum_datagram_size:
                allocation.dropped_packets += 1
                return
            expected_ip = allocation.expected_ips[source_identity]
            if self.enforce_source_ip and endpoint[0] != expected_ip:
                allocation.dropped_packets += 1
                return
            if now - allocation.rate_window_started >= 1.0:
                allocation.rate_window_started = now
                allocation.rate_window_packets = 0
                allocation.rate_window_bytes = 0
            allocation.rate_window_packets += 1
            allocation.rate_window_bytes += len(data)
            if (
                allocation.rate_window_packets > self.maximum_packets_per_second
                or allocation.rate_window_bytes > self.maximum_bytes_per_second
            ):
                allocation.dropped_packets += 1
                return
            allocation.last_activity = now
            allocation.endpoints[source_identity] = endpoint

            capture_index = allocation.captured_packets[source_identity]
            packet_number = capture_index + 1
            capture_enabled = (
                allocation.capture_active
                and (
                    self.handshake_capture_packets == 0
                    or capture_index < self.handshake_capture_packets
                )
            )
            if capture_enabled:
                allocation.captured_packets[source_identity] = capture_index + 1
                sample = data[:self.handshake_capture_bytes]
                log.warning(
                    "GAMEPLAY RELAY HANDSHAKE gameId=%d direction=%s->%s packet=%d "
                    "source=%s:%d bytes=%d captured=%d truncated=%s hex=%s",
                    allocation.game_id,
                    source_identity,
                    next(identity for identity in allocation.player_ids if identity != source_identity),
                    capture_index + 1,
                    endpoint[0], endpoint[1], len(data), len(sample),
                    len(sample) != len(data), sample.hex(),
                )

            queued = allocation.pending[source_identity]
            for queued_payload, received_at, queued_source, queued_number in queued:
                sends.append((
                    allocation.sockets[source_identity], endpoint, queued_payload,
                    received_at, queued_source, source_identity, queued_number, True,
                ))
                allocation.pending_bytes -= len(queued_payload)
            queued.clear()

            target_identity = next(
                identity for identity in allocation.player_ids
                if identity != source_identity
            )
            target_endpoint = allocation.endpoints.get(target_identity)
            if target_endpoint is None:
                pending = allocation.pending[target_identity]
                if (
                    len(pending) >= self.maximum_pending_packets
                    or allocation.pending_bytes + len(data) > self.maximum_pending_bytes
                ):
                    allocation.dropped_packets += 1
                    return
                pending.append((bytes(data), now, source_identity, packet_number))
                allocation.pending_bytes += len(data)
            else:
                sends.append((
                    allocation.sockets[target_identity], target_endpoint, bytes(data),
                    now, source_identity, target_identity, packet_number, False,
                ))

        for (
            send_socket, target_endpoint, payload, received_at,
            source_identity, target_identity, packet_number, was_queued,
        ) in sends:
            try:
                sent = send_socket.sendto(payload, target_endpoint)
            except OSError:
                with allocation.lock:
                    allocation.dropped_packets += 1
                continue
            with allocation.lock:
                allocation.packets_forwarded += 1
                allocation.bytes_forwarded += sent
            if (
                allocation.capture_active
                and (
                    self.handshake_capture_packets == 0
                    or packet_number <= self.handshake_capture_packets
                )
            ):
                log.warning(
                    "GAMEPLAY RELAY FORWARD gameId=%d direction=%s->%s "
                    "packet=%d bytes=%d latencyMs=%.3f queued=%s sourcePort=%d",
                    allocation.game_id, source_identity, target_identity,
                    packet_number, sent,
                    (time.monotonic() - received_at) * 1000.0,
                    was_queued, send_socket.getsockname()[1],
                )
            # FIFA 21's 39-byte 81 01 80 04 datagram is the directed
            # established-link phase seen immediately before native setup
            # advances. Count it only after forwarding succeeds, once per
            # source/target direction. The relay remains payload-transparent.
            if len(payload) == 39 and payload[:4] == b"\x81\x01\x80\x04":
                edge = (source_identity, target_identity)
                first_connected = False
                with allocation.lock:
                    if edge not in allocation.connected_edges:
                        allocation.connected_edges.add(edge)
                        first_connected = True
                if first_connected:
                    log.warning(
                        "GAMEPLAY RELAY PEER LINK gameId=%d source=%s target=%s edges=%s",
                        allocation.game_id, source_identity, target_identity,
                        sorted(allocation.connected_edges),
                    )
                    callback = self._peer_link_callback
                    if callback is not None:
                        try:
                            callback(
                                allocation.game_id, source_identity, target_identity,
                                "phase04",
                            )
                        except Exception:
                            log.exception(
                                "GAMEPLAY RELAY phase-04 callback failed gameId=%d",
                                allocation.game_id,
                            )
            # The low byte is a sequence-dependent CommUDP value, not a fixed
            # lifecycle enum: healthy captures have used 04/07/0c as well as
            # 08/0b/10. Crossing from 81 01 80 xx negotiation into the
            # 00 00 80 xx family is the stable promotion signal. Require it
            # independently in both directions and keep duplicates idempotent.
            if len(payload) >= 4 and payload[:3] == b"\x00\x00\x80":
                edge = (source_identity, target_identity)
                first_promoted = False
                with allocation.lock:
                    if edge not in allocation.promoted_edges:
                        allocation.promoted_edges.add(edge)
                        first_promoted = True
                if first_promoted:
                    log.warning(
                        "GAMEPLAY RELAY PROMOTED gameId=%d source=%s target=%s "
                        "packetType=%s promoted=%s",
                        allocation.game_id, source_identity, target_identity,
                        payload[:4].hex(), sorted(allocation.promoted_edges),
                    )
                    callback = self._peer_link_callback
                    if callback is not None:
                        try:
                            callback(
                                allocation.game_id, source_identity, target_identity,
                                "80xx",
                            )
                        except Exception:
                            log.exception(
                                "GAMEPLAY RELAY 80xx callback failed gameId=%d",
                                allocation.game_id,
                            )

    def _sweep_loop(self) -> None:
        while not self._stop_event.wait(1.0):
            now = time.monotonic()
            with self._lock:
                expired = [
                    (game_id, "idle-timeout" if now - allocation.last_activity
                     >= self.idle_timeout_seconds else "maximum-lifetime")
                    for game_id, allocation in self._allocations.items()
                    if (
                        now - allocation.last_activity >= self.idle_timeout_seconds
                        or now - allocation.created_at >= self.maximum_lifetime_seconds
                    )
                ]
            for game_id, reason in expired:
                self.release(game_id, reason)
