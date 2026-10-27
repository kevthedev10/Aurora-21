from __future__ import annotations

import os
import logging
import socket
import sys
import tempfile
import threading
import time
from pathlib import Path
from types import SimpleNamespace


def _free_pair() -> tuple[int, int]:
    for first in range(32000, 60000):
        sockets: list[socket.socket] = []
        try:
            for port in (first, first + 1):
                sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                sock.bind(("127.0.0.1", port))
                sockets.append(sock)
        except OSError:
            for sock in sockets:
                sock.close()
            continue
        for sock in sockets:
            sock.close()
        return first, first + 1
    raise RuntimeError("no free contiguous loopback UDP ports")


def _receive(sock: socket.socket) -> tuple[bytes, tuple[str, int]]:
    deadline = time.monotonic() + 2.0
    while True:
        try:
            return sock.recvfrom(65535)
        except socket.timeout:
            if time.monotonic() >= deadline:
                raise


def _receive_fire2(sock: socket.socket, server) -> object:
    raw = bytearray()
    while len(raw) < 16:
        raw.extend(sock.recv(16 - len(raw)))
    total = 16 + int.from_bytes(raw[0:4], "big") + int.from_bytes(raw[4:6], "big")
    while len(raw) < total:
        raw.extend(sock.recv(total - len(raw)))
    packet = server._fire2_try_parse(raw)
    assert packet is not None
    return packet


def main() -> int:
    source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
    sys.path.insert(0, str(source))
    from gameplay_relay import (  # noqa: PLC0415
        GameplayRelayCapacityError,
        GameplayRelayManager,
    )

    first_port, last_port = _free_pair()
    manager = GameplayRelayManager(
        enabled=True,
        bind_address="127.0.0.1",
        advertised_address="127.0.0.1",
        port_start=first_port,
        port_end=last_port,
        idle_timeout_seconds=30,
    )
    manager.start("127.0.0.1")
    observed_edges: list[tuple[int, str, str, str]] = []
    manager.set_peer_link_callback(
        lambda game_id, source, target, evidence: observed_edges.append(
            (game_id, source, target, evidence)
        )
    )
    players = [
        {"discord_id": "111111111111111111", "connection": SimpleNamespace(peer_ip="127.0.0.1", server_ip="127.0.0.1")},
        {"discord_id": "222222222222222222", "connection": SimpleNamespace(peer_ip="127.0.0.1", server_ip="127.0.0.1")},
    ]
    allocation = manager.allocate(41, players)
    assert allocation is not None
    first_id, second_id = allocation.player_ids

    first_client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    second_client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    first_client.bind(("127.0.0.1", 0))
    second_client.bind(("127.0.0.1", 0))
    first_client.settimeout(0.1)
    second_client.settimeout(0.1)
    try:
        # A packet sent before the peer endpoint is learned is retained, then
        # flushed from the exact relay port advertised to the peer.
        first_client.sendto(b"first-before-peer", allocation.endpoint_for(first_id))
        second_client.sendto(b"second-learns", allocation.endpoint_for(second_id))
        second_payload, second_source = _receive(second_client)
        first_payload, first_source = _receive(first_client)
        assert second_payload == b"first-before-peer"
        assert first_payload == b"second-learns"
        assert second_source[1] == allocation.ports[second_id]
        assert first_source[1] == allocation.ports[first_id]

        first_client.sendto(b"byte-for-byte", allocation.endpoint_for(first_id))
        payload, source_endpoint = _receive(second_client)
        assert payload == b"byte-for-byte"
        assert source_endpoint[1] == allocation.ports[second_id]

        phase04_first = b"\x81\x01\x80\x04" + bytes(35)
        phase04_second = b"\x81\x01\x80\x04" + bytes(range(35))
        first_client.sendto(phase04_first, allocation.endpoint_for(first_id))
        assert _receive(second_client)[0] == phase04_first
        deadline = time.monotonic() + 1.0
        while len(observed_edges) < 1 and time.monotonic() < deadline:
            time.sleep(0.01)
        assert observed_edges == [(41, first_id, second_id, "phase04")]
        # Phase-04 callbacks are directional and duplicate-idempotent.
        first_client.sendto(phase04_first, allocation.endpoint_for(first_id))
        assert _receive(second_client)[0] == phase04_first
        second_client.sendto(phase04_second, allocation.endpoint_for(second_id))
        assert _receive(first_client)[0] == phase04_second
        deadline = time.monotonic() + 1.0
        while len(observed_edges) < 2 and time.monotonic() < deadline:
            time.sleep(0.01)
        assert observed_edges == [
            (41, first_id, second_id, "phase04"),
            (41, second_id, first_id, "phase04"),
        ]

        # The low byte varies by connection; promotion is the 000080 family,
        # not one exact sequence value or packet length.
        promoted_first = b"\x00\x00\x80\x04" + bytes(22)
        promoted_second = b"\x00\x00\x80\x08" + bytes(range(22))
        first_client.sendto(promoted_first, allocation.endpoint_for(first_id))
        assert _receive(second_client)[0] == promoted_first
        time.sleep(0.02)
        # Promotion is a distinct, directional readiness signal even when the
        # same link already emitted phase-04.
        assert observed_edges[-1] == (41, first_id, second_id, "80xx")
        first_client.sendto(promoted_first, allocation.endpoint_for(first_id))
        assert _receive(second_client)[0] == promoted_first
        time.sleep(0.02)
        assert len(observed_edges) == 3
        second_client.sendto(promoted_second, allocation.endpoint_for(second_id))
        assert _receive(first_client)[0] == promoted_second
        deadline = time.monotonic() + 1.0
        while len(observed_edges) < 4 and time.monotonic() < deadline:
            time.sleep(0.01)
        assert observed_edges == [
            (41, first_id, second_id, "phase04"),
            (41, second_id, first_id, "phase04"),
            (41, first_id, second_id, "80xx"),
            (41, second_id, first_id, "80xx"),
        ]

        try:
            manager.allocate(42, players)
        except GameplayRelayCapacityError:
            pass
        else:
            raise AssertionError("a two-port range accepted a second allocation")
    finally:
        first_client.close()
        second_client.close()
        manager.release(41, "test-complete")
        manager.stop()
    assert manager.allocation_for(41) is None

    with tempfile.TemporaryDirectory(prefix="project21-relay-projection-") as temporary:
        root = Path(temporary)
        bootstrap = root / "bootstrap"
        config = bootstrap / "config"
        config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(config)
        os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"
        import server  # noqa: PLC0415

        player_registry = server.enable_multi_tenant(root)

        class IdentityHandler(server.socketserver.BaseRequestHandler):
            def handle(self) -> None:
                context = server._active_player_context()
                assert context is not None
                self.request.sendall(str(context.discord_id).encode("ascii"))

        scoped_server = server.PeerScopedTCPServer(("127.0.0.1", 0), IdentityHandler)
        scoped_thread = threading.Thread(target=scoped_server.serve_forever, daemon=True)
        scoped_thread.start()
        try:
            with socket.create_connection(scoped_server.server_address, 2) as client:
                scoped_identity = client.recv(64).decode("ascii")
            assert scoped_identity == server._peer_player_identity("127.0.0.1")
        finally:
            scoped_server.shutdown()
            scoped_server.server_close()
            scoped_thread.join(timeout=2)

        projection_first, projection_last = _free_pair()
        projection_manager = GameplayRelayManager(
            enabled=True,
            bind_address="127.0.0.1",
            advertised_address="127.0.0.1",
            port_start=projection_first,
            port_end=projection_last,
        )
        projection_manager.start("127.0.0.1")
        registry = server.MatchmakingSessionRegistry(projection_manager)
        socket_pairs: list[tuple[socket.socket, socket.socket]] = []
        entries = []
        for index, identity in enumerate(("333333333333333333", "444444444444444444")):
            server_sock, peer_sock = socket.socketpair()
            peer_sock.settimeout(2)
            socket_pairs.append((server_sock, peer_sock))
            connection = server.BlazeClientConnection(
                server_sock, identity, 1001 + index, f"Player{index + 1}",
                "127.0.0.1", "127.0.0.1",
            )
            entries.append((identity, connection))
        start_payload = (
            server._tdf_field_union(
                "PNET", 2,
                server._tdf_field_group(
                    "EXIP", server._tdf_field_int("IP", 0)
                    + server._tdf_field_int("PORT", 3659)
                )
                + server._tdf_field_group(
                    "INIP", server._tdf_field_int("IP", 0)
                    + server._tdf_field_int("PORT", 3659)
                ),
            )
            + server._tdf_field_str("SCNM", "FUT_Rivals")
        )
        registry.start(entries[0][0], "FUT_Rivals", start_payload, entries[0][1])
        registry.start(entries[1][0], "FUT_Rivals", start_payload, entries[1][1])
        assert len(registry.games) == 1
        match = next(iter(registry.games.values()))
        relay = match["gameplay_relay"]
        assert relay is not None
        assert server._blaze_projected_endpoint(match, match["players"][0], match["players"][0]) == ("127.0.0.1", 3659)
        assert server._blaze_projected_endpoint(match, match["players"][0], match["players"][1]) == relay.endpoint_for(entries[0][0])
        assert server._blaze_projected_endpoint(match, match["players"][1], match["players"][0]) == relay.endpoint_for(entries[1][0])
        assert server._deliver_blaze_peer_subscriptions(match) == 2
        for index, (_, peer_sock) in enumerate(socket_pairs):
            projected_ports: list[int] = []
            for _ in range(3):
                notification = _receive_fire2(peer_sock, server)
                projected_ports.extend(server._tdf_get_ints(notification.payload, "PORT"))
            assert relay.ports[entries[index][0]] in projected_ports
        first_setup_ports = server._tdf_get_ints(server._blaze_game_setup(match, 0), "PORT")
        second_setup_ports = server._tdf_get_ints(server._blaze_game_setup(match, 1), "PORT")
        assert relay.ports[entries[0][0]] in first_setup_ports
        assert relay.ports[entries[1][0]] in second_setup_ports
        registry.reset()
        assert projection_manager.allocation_for(int(match["game_id"])) is None
        projection_manager.stop()
        for server_sock, peer_sock in socket_pairs:
            server_sock.close()
            peer_sock.close()
        player_registry.close()
        logging.shutdown()

    print("PASS: gameplay relay preserves UDP bytes and projects peer endpoints")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
