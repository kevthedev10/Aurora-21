from __future__ import annotations

import logging
import os
import socket
import sys
import tempfile
import threading
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path


def _recv_frame(peer: socket.socket, server) -> object:
    raw = bytearray()
    while len(raw) < 16:
        raw.extend(peer.recv(16 - len(raw)))
    total = 16 + int.from_bytes(raw[0:4], "big") + int.from_bytes(raw[4:6], "big")
    while len(raw) < total:
        raw.extend(peer.recv(total - len(raw)))
    packet = server._fire2_try_parse(raw)
    assert packet is not None
    return packet


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-rivals-pairing-") as temporary:
        root = Path(temporary)
        bootstrap_runtime = root / "bootstrap"
        bootstrap_config = bootstrap_runtime / "config"
        bootstrap_config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
        os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"

        source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
        sys.path.insert(0, str(source))
        import server  # noqa: PLC0415

        players = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=16)
        server.MATCHMAKING_SESSIONS.reset()

        # Raw capture is diagnostic-only. The normal multi-user profile keeps
        # GameManager evidence but must not duplicate high-frequency presence
        # traffic into .bin and .hex files until the disk fills.
        os.environ["LOCALFUT_FIRE2_CAPTURE_MODE"] = "game-manager"
        ordinary_packet = server.Fire2Packet(
            0, 0, 10, 5, 1, 0, b"", b"", b"ordinary"
        )
        game_packet = server.Fire2Packet(
            0, 0, 4, 16, 2, 0, b"", b"", b"game-manager"
        )
        assert server._save_fire2(ordinary_packet, ("10.203.20.10", 40000)) is None
        game_capture = server._save_fire2(game_packet, ("10.203.20.10", 40000))
        assert game_capture is not None and game_capture.read_bytes() == b"game-manager"
        capture_root = server.LOGS
        blocked_capture_root = root / "capture-path-is-a-file"
        blocked_capture_root.write_text("not a directory", encoding="utf-8")
        server.LOGS = blocked_capture_root
        try:
            assert server._save_fire2(game_packet, ("10.203.20.10", 40000)) is None
        finally:
            server.LOGS = capture_root

        identities = ("111111111111111111", "222222222222222222")
        socket_sets: list[tuple[socket.socket, socket.socket]] = []

        def connection(identity: str, address: str):
            server_sock, peer_sock = socket.socketpair()
            peer_sock.settimeout(2)
            socket_sets.append((server_sock, peer_sock))
            with players.scope(identity):
                return server.BlazeClientConnection(
                    server_sock,
                    identity,
                    int(server.CFG["persona_id"]),
                    str(server.CFG["display_name"]),
                    address,
                )

        first = connection(identities[0], "10.203.20.10")
        second = connection(identities[1], "10.203.20.11")
        assert first.persona_id != second.persona_id
        start_payload = (
            server._tdf_field_union(
                "PNET", 2,
                server._tdf_field_group(
                    "EXIP", server._tdf_field_int("IP", 0) + server._tdf_field_int("PORT", 3659)
                ) + server._tdf_field_group(
                    "INIP", server._tdf_field_int("IP", 0) + server._tdf_field_int("PORT", 3659)
                ),
            )
            + server._tdf_field_str("SCNM", "FUT_Rivals")
        )

        def packet(msg_num: int):
            return server.Fire2Packet(
                len(start_payload), 0, 4, 16, msg_num, 0, b"", start_payload, b""
            )

        # Exercise the production ordering: each RPC acknowledgement is sent
        # before the second caller claims and publishes the shared setup.
        with players.scope(identities[0]):
            payload, _ = server._fire2_local_response(packet(1), first)
        first.sendall(server._fire2_build(4, 16, 1, 1, payload))
        with players.scope(identities[1]):
            payload, _ = server._fire2_local_response(packet(2), second)
        second.sendall(server._fire2_build(4, 16, 2, 1, payload))
        match = server.MATCHMAKING_SESSIONS.take_ready(identities[1], second)
        assert match is not None
        assert server._deliver_blaze_game_setup(match) == 2

        first_ack = _recv_frame(socket_sets[0][1], server)
        first_setup = _recv_frame(socket_sets[0][1], server)
        second_ack = _recv_frame(socket_sets[1][1], server)
        second_setup = _recv_frame(socket_sets[1][1], server)
        assert (first_ack.component, first_ack.command, first_ack.msg_type) == (4, 16, 1)
        assert (second_ack.component, second_ack.command, second_ack.msg_type) == (4, 16, 1)
        assert (first_setup.component, first_setup.command, first_setup.msg_type) == (4, 20, 2)
        assert (second_setup.component, second_setup.command, second_setup.msg_type) == (4, 20, 2)
        assert server._tdf_get_int(first_setup.payload, "GID") == int(match["game_id"])
        assert server._tdf_get_int(second_setup.payload, "GID") == int(match["game_id"])
        assert server._tdf_get_int(first_setup.payload, "USID") == first.persona_id
        assert server._tdf_get_int(second_setup.payload, "USID") == second.persona_id
        roster_prefix = server._tdf_tag("PROS", server._TDF_LIST) + bytes([server._TDF_GROUP, 2])
        assert roster_prefix in first_setup.payload
        host_list_prefix = (
            server._tdf_tag("HNET", server._TDF_LIST)
            + bytes([server._TDF_GROUP, 1, 2])
        )
        malformed_host_list_prefix = (
            server._tdf_tag("HNET", server._TDF_LIST)
            + bytes([server._TDF_UNION, 1, 2])
        )
        assert host_list_prefix in first_setup.payload
        assert malformed_host_list_prefix not in first_setup.payload
        assert host_list_prefix + server._tdf_tag("VALU", server._TDF_GROUP) not in first_setup.payload
        wireguard_first = int.from_bytes(socket.inet_aton("10.203.20.10"), "big")
        wireguard_second = int.from_bytes(socket.inet_aton("10.203.20.11"), "big")
        assert server._tdf_varint(wireguard_first) in first_setup.payload
        assert server._tdf_varint(wireguard_second) in first_setup.payload

        # Run the real handler loop as well, proving it performs that same
        # acknowledgement-before-notification ordering automatically.
        server.MATCHMAKING_SESSIONS.reset()
        handler_sockets = [socket.socketpair(), socket.socketpair()]
        for server_sock, peer_sock in handler_sockets:
            peer_sock.settimeout(3)
            socket_sets.append((server_sock, peer_sock))

        def run_handler(identity: str, server_sock: socket.socket, peer_ip: str, port: int) -> None:
            with players.scope(identity):
                server.BlazeOrHttpHandler(server_sock, (peer_ip, port), object())

        handler_threads = [
            threading.Thread(
                target=run_handler,
                args=(identities[0], handler_sockets[0][0], "10.203.20.10", 40001),
            ),
            threading.Thread(
                target=run_handler,
                args=(identities[1], handler_sockets[1][0], "10.203.20.11", 40002),
            ),
        ]
        for thread in handler_threads:
            thread.start()
        handler_sockets[0][1].sendall(server._fire2_build(4, 16, 11, 0, start_payload))
        handler_first_ack = _recv_frame(handler_sockets[0][1], server)
        handler_sockets[1][1].sendall(server._fire2_build(4, 16, 12, 0, start_payload))
        handler_second_ack = _recv_frame(handler_sockets[1][1], server)
        handler_first_notifications = [
            _recv_frame(handler_sockets[0][1], server) for _ in range(5)
        ]
        handler_second_notifications = [
            _recv_frame(handler_sockets[1][1], server) for _ in range(5)
        ]
        assert (handler_first_ack.command, handler_first_ack.msg_type) == (16, 1)
        assert (handler_second_ack.command, handler_second_ack.msg_type) == (16, 1)
        expected_notifications = [
            (30722, 2, 2),
            (30722, 5, 2),
            (30722, 1, 2),
            (4, 10, 2),
            (4, 20, 2),
        ]
        assert [
            (packet.component, packet.command, packet.msg_type)
            for packet in handler_first_notifications
        ] == expected_notifications
        assert [
            (packet.component, packet.command, packet.msg_type)
            for packet in handler_second_notifications
        ] == expected_notifications
        for _, peer_sock in handler_sockets:
            peer_sock.shutdown(socket.SHUT_RDWR)
            peer_sock.close()
        for thread in handler_threads:
            thread.join(timeout=3)
            assert not thread.is_alive()
        assert not server.MATCHMAKING_SESSIONS.games

        # A real c4/k17 handler exchange must produce both its empty RPC reply
        # and the asynchronous SESSION_CANCELED notification.
        cancel_server, cancel_peer = socket.socketpair()
        cancel_peer.settimeout(3)
        socket_sets.append((cancel_server, cancel_peer))
        cancel_thread = threading.Thread(
            target=run_handler,
            args=(identities[0], cancel_server, "10.203.20.10", 40003),
        )
        cancel_thread.start()
        cancel_payload = server._tdf_field_int("MSID", 0)
        cancel_peer.sendall(server._fire2_build(4, 17, 13, 0, cancel_payload))
        cancel_ack = _recv_frame(cancel_peer, server)
        cancel_notice = _recv_frame(cancel_peer, server)
        assert (cancel_ack.command, cancel_ack.msg_type) == (17, 1)
        assert (cancel_notice.command, cancel_notice.msg_type) == (10, 2)
        assert server._tdf_get_int(cancel_notice.payload, "RSLT") == 4
        cancel_peer.shutdown(socket.SHUT_RDWR)
        cancel_peer.close()
        cancel_thread.join(timeout=3)
        assert not cancel_thread.is_alive()

        # Same users and unlike scenarios cannot match each other.
        isolated = server.MatchmakingSessionRegistry()
        isolated.start(identities[0], "FUT_Rivals", start_payload, first)
        isolated.start(identities[0], "FUT_Rivals", start_payload, first)
        assert len(isolated.sessions) == 1 and not isolated.games
        isolated.start(identities[1], "FUT_Champions", start_payload, second)
        assert len(isolated.sessions) == 2 and not isolated.games

        # Concurrent starts still form exactly one atomic pair.
        concurrent = server.MatchmakingSessionRegistry()
        barrier = threading.Barrier(2)

        def start(identity: str, client) -> int:
            barrier.wait(timeout=2)
            return concurrent.start(identity, "FUT_Rivals", start_payload, client)

        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = [
                executor.submit(start, identities[0], first),
                executor.submit(start, identities[1], second),
            ]
            [future.result(timeout=3) for future in futures]
        assert len(concurrent.games) == 1
        assert not concurrent.sessions
        assert len(concurrent.ready) == 1

        # Disconnect removes only the exact queued socket, then cleans a shared
        # game for both players once that owning socket leaves.
        queued = server.MatchmakingSessionRegistry()
        queued.start(identities[0], "FUT_Rivals", start_payload, first)
        queued.disconnect(identities[0], second)
        assert len(queued.sessions) == 1
        queued.disconnect(identities[0], first)
        assert not queued.sessions
        concurrent.disconnect(identities[0], first)
        assert not concurrent.games and not concurrent.games_by_player and not concurrent.ready

        for server_sock, peer_sock in socket_sets:
            server_sock.close()
            peer_sock.close()
        players.close()
        logging.shutdown()

    print("PASS: two authenticated FUT_Rivals searches pair and receive ordered c4/k20 setup")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
