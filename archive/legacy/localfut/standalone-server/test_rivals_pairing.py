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


def _assert_no_frame(peer: socket.socket, server) -> None:
    previous_timeout = peer.gettimeout()
    peer.settimeout(0.1)
    try:
        try:
            frame = _recv_frame(peer, server)
        except (TimeoutError, socket.timeout):
            return
        raise AssertionError(
            f"unexpected frame c={frame.component} k={frame.command} "
            f"type={frame.msg_type}"
        )
    finally:
        peer.settimeout(previous_timeout)


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
        assert server._tdf_get_int(first_setup.payload, "GSTA") == 3
        assert server._tdf_get_int(first_setup.payload, "NTOP") == 0
        assert server._tdf_get_string(first_setup.payload, "VSTR") == "patchmotueka"
        assert server._tdf_get_map_str_str(first_setup.payload, "ATTR")["GAME_MODE"] == "76"
        assert server._tdf_get_map_str_str(first_setup.payload, "ATTR")["MATCHUP_HASH"] == "1684366964"
        assert first_setup.payload.count(b"Guest") == 2
        assert server._tdf_get_int(first_setup.payload, "USID") == first.persona_id
        assert server._tdf_get_int(second_setup.payload, "USID") == second.persona_id
        # FIFA 21 requires explicit mesh identities.  In particular, the
        # ReplicatedGamePlayer constructor defaults a missing CSID to 0xff.
        assert first_setup.payload.count(server._tdf_tag("CONG", server._TDF_VARINT)) >= 4
        assert first_setup.payload.count(server._tdf_tag("CONG", server._TDF_OBJECT_ID)) == 0
        assert first_setup.payload.count(server._tdf_tag("CSID", server._TDF_VARINT)) >= 4
        # HSES is a ReplicatedGame field. Native FIFA 21 HostInfo contains
        # CONG/CSID/HPID/HSLT and must not duplicate HSES inside PHST/THST.
        assert first_setup.payload.count(server._tdf_tag("HSES", server._TDF_VARINT)) == 1
        assert server._tdf_field_int("CSID", 1) in first_setup.payload
        first_host_info = (
            server._tdf_field_int("CONG", first.persona_id)
            + server._tdf_field_int("CSID", 0)
            + server._tdf_field_int("HPID", first.persona_id)
            + server._tdf_field_int("HSLT", 0)
        )
        assert server._tdf_field_group("PHST", first_host_info) in first_setup.payload
        assert server._tdf_field_group("PHST", first_host_info) in second_setup.payload
        assert server._tdf_field_int("CONG", first.persona_id) in first_setup.payload
        assert server._tdf_field_int("CONG", second.persona_id) in first_setup.payload
        assert first_setup.payload.count(server._tdf_tag("UGID", server._TDF_OBJECT_ID)) == 2
        assert server._tdf_field_object_id("UGID", 30722, 2, first.persona_id) in first_setup.payload
        assert server._tdf_field_object_id("UGID", 30722, 2, second.persona_id) in first_setup.payload
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

        lookup_packet = server.Fire2Packet(0, 0, 30722, 50, 3, 0, b"", b"", b"")
        with players.scope(identities[0]):
            lookup_payload, lookup_label = server._fire2_local_response(
                lookup_packet, first
            )
        assert lookup_label == "UserSessions.LookupUsersByPersonaNames"
        assert (
            server._tdf_tag("ULST", server._TDF_LIST)
            + bytes([server._TDF_GROUP, 2])
        ) in lookup_payload
        assert first.display_name.encode() in lookup_payload
        assert second.display_name.encode() in lookup_payload

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
        first_peer_notifications = [
            _recv_frame(handler_sockets[0][1], server) for _ in range(3)
        ]
        second_peer_notifications = [
            _recv_frame(handler_sockets[1][1], server) for _ in range(3)
        ]
        first_success_probe = _recv_frame(handler_sockets[0][1], server)
        second_success_probe = _recv_frame(handler_sockets[1][1], server)
        handler_first_setup = _recv_frame(handler_sockets[0][1], server)
        handler_second_setup = _recv_frame(handler_sockets[1][1], server)
        assert (handler_first_ack.command, handler_first_ack.msg_type) == (16, 1)
        assert (handler_second_ack.command, handler_second_ack.msg_type) == (16, 1)
        assert server._tdf_get_int(handler_first_ack.payload, "MSID") == 0
        assert server._tdf_get_int(handler_second_ack.payload, "MSID") == 0
        assert [frame.command for frame in first_peer_notifications] == [2, 5, 1]
        assert [frame.command for frame in second_peer_notifications] == [2, 5, 1]
        assert all(frame.component == 30722 for frame in first_peer_notifications)
        assert all(frame.component == 30722 for frame in second_peer_notifications)
        assert server._tdf_get_int(first_peer_notifications[0].payload, "PORT") == 3659
        assert server._tdf_get_int(second_peer_notifications[0].payload, "PORT") == 3659
        # UserSessions projections describe the peer, not the recipient.
        assert server._tdf_get_int(first_peer_notifications[0].payload, "ID") == second.persona_id
        assert server._tdf_get_int(first_peer_notifications[1].payload, "ID") == second.persona_id
        assert server._tdf_get_int(first_peer_notifications[2].payload, "USID") == second.persona_id
        assert server._tdf_get_int(second_peer_notifications[0].payload, "ID") == first.persona_id
        assert server._tdf_get_int(second_peer_notifications[1].payload, "ID") == first.persona_id
        assert server._tdf_get_int(second_peer_notifications[2].payload, "USID") == first.persona_id
        for probe in (first_success_probe, second_success_probe):
            assert (probe.component, probe.command, probe.msg_type) == (4, 10, 2)
            assert server._tdf_get_int(probe.payload, "FIT") == 1000
            assert server._tdf_get_int(probe.payload, "MAXF") == 1000
            assert server._tdf_get_int(probe.payload, "RSLT") == 1
        assert server._tdf_get_int(first_success_probe.payload, "USID") == first.persona_id
        assert server._tdf_get_int(second_success_probe.payload, "USID") == second.persona_id
        assert (handler_first_setup.command, handler_first_setup.msg_type) == (20, 2)
        assert (handler_second_setup.command, handler_second_setup.msg_type) == (20, 2)
        game_id = server._tdf_get_int(handler_first_setup.payload, "GID")
        assert game_id is not None
        active_match = server.MATCHMAKING_SESSIONS.games[game_id]
        first_session_id = int(active_match["players"][0]["session_id"])
        second_session_id = int(active_match["players"][1]["session_id"])
        assert first_session_id > 0
        assert second_session_id > 0
        assert first_session_id != second_session_id
        assert server._tdf_get_int(first_success_probe.payload, "MSID") == first_session_id
        assert server._tdf_get_int(second_success_probe.payload, "MSID") == second_session_id
        assert server._tdf_get_int(first_success_probe.payload, "GID") == game_id
        assert server._tdf_get_int(second_success_probe.payload, "GID") == game_id
        assert server._tdf_get_int(handler_first_setup.payload, "MSID") == first_session_id
        assert server._tdf_get_int(handler_second_setup.payload, "MSID") == second_session_id
        # REAS is MatchmakingSetupContext (member 3). Its trailing fields are
        # recipient-owned even though GAME/PROS intentionally contain both
        # players' identities.
        reason_marker = server._tdf_tag("REAS", server._TDF_UNION)
        first_reason = handler_first_setup.payload.index(reason_marker) + len(reason_marker)
        second_reason = handler_second_setup.payload.index(reason_marker) + len(reason_marker)
        assert handler_first_setup.payload[first_reason] == 3
        assert handler_second_setup.payload[second_reason] == 3
        assert server._tdf_get_ints(handler_first_setup.payload, "USID")[-1] == first.persona_id
        assert server._tdf_get_ints(handler_second_setup.payload, "USID")[-1] == second.persona_id
        assert server._tdf_get_ints(handler_first_setup.payload, "RSLT")[-1] == 0
        assert server._tdf_get_ints(handler_second_setup.payload, "RSLT")[-1] == 1
        assert 3659 in server._tdf_get_ints(handler_first_setup.payload, "PORT")
        assert 3659 in server._tdf_get_ints(handler_second_setup.payload, "PORT")
        first_setup_machine_ids = server._tdf_get_ints(
            handler_first_setup.payload, "MACI"
        )
        assert first.machine_id in first_setup_machine_ids
        assert second.machine_id in first_setup_machine_ids
        assert first_setup_machine_ids.count(0) >= 4
        first_self_connected_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("FLGS", 0)
            + server._tdf_field_object_id("TCG", 30722, 2, first.persona_id)
        )
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 65, 13, 0, first_self_connected_payload)
        )
        first_connected_ack = _recv_frame(handler_sockets[0][1], server)
        assert (first_connected_ack.command, first_connected_ack.msg_type) == (65, 1)
        _assert_no_frame(handler_sockets[0][1], server)

        second_self_connected_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("FLGS", 0)
            + server._tdf_field_object_id("TCG", 30722, 2, 0)
        )
        handler_sockets[1][1].sendall(
            server._fire2_build(4, 65, 14, 0, second_self_connected_payload)
        )
        second_connected_ack = _recv_frame(handler_sockets[1][1], server)
        assert (second_connected_ack.command, second_connected_ack.msg_type) == (65, 1)
        # Endpoint initialization alone cannot complete connectivity
        # validation or emit any roster/session/state lifecycle.
        _assert_no_frame(handler_sockets[0][1], server)
        _assert_no_frame(handler_sockets[1][1], server)

        # The host update is retained, but notification 115 and all roster/state
        # lifecycle remain blocked until the full directed mesh is complete.
        session_update_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_blob("XNNC", b"")
            + server._tdf_field_blob("XSES", b"")
        )
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 15, 15, 0, session_update_payload)
        )
        first_update_ack = _recv_frame(handler_sockets[0][1], server)
        assert (first_update_ack.command, first_update_ack.msg_type) == (15, 1)
        if server.ENABLE_UNVERIFIED_PLATFORM_HOST_NOTIFICATION:
            first_platform_host = _recv_frame(handler_sockets[0][1], server)
            second_platform_host = _recv_frame(handler_sockets[1][1], server)
            assert (first_platform_host.command, first_platform_host.msg_type) == (71, 2)
            assert (second_platform_host.command, second_platform_host.msg_type) == (71, 2)
            assert server._tdf_get_int(first_platform_host.payload, "PHST") == 0
            assert server._tdf_get_int(second_platform_host.payload, "PHST") == 0
        first_validation = _recv_frame(handler_sockets[0][1], server)
        second_validation = _recv_frame(handler_sockets[1][1], server)
        assert (first_validation.command, first_validation.msg_type) == (11, 2)
        assert (second_validation.command, second_validation.msg_type) == (11, 2)
        assert server._tdf_get_int(first_validation.payload, "MSID") == first_session_id
        assert server._tdf_get_int(second_validation.payload, "MSID") == second_session_id
        for validation in (first_validation, second_validation):
            assert server._tdf_get_int(validation.payload, "DISP") == 1
            assert server._tdf_get_int(validation.payload, "QOSV") == 1
            assert server._tdf_get_int(validation.payload, "SCID") == 0
            assert server._tdf_get_int(validation.payload, "NTOP") is None
            assert server._tdf_get_int(validation.payload, "GID") is None
            assert server._tdf_get_object_id(validation.payload, "GRID") is None
            assert server._tdf_get_int(validation.payload, "USID") is None

        host_peer_connected_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("FLGS", 4)
            + server._tdf_field_object_id("TCG", 30722, 2, second.persona_id)
        )
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 65, 16, 0, host_peer_connected_payload)
        )
        host_peer_connected_ack = _recv_frame(handler_sockets[0][1], server)
        assert (host_peer_connected_ack.command, host_peer_connected_ack.msg_type) == (65, 1)
        # One directed cross-link remains incomplete, so no session,
        # connected, join, or state notification is allowed yet.
        _assert_no_frame(handler_sockets[0][1], server)
        _assert_no_frame(handler_sockets[1][1], server)

        guest_peer_connected_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("FLGS", 4)
            + server._tdf_field_object_id("TCG", 30722, 2, first.persona_id)
        )
        handler_sockets[1][1].sendall(
            server._fire2_build(4, 65, 17, 0, guest_peer_connected_payload)
        )
        guest_peer_connected_ack = _recv_frame(handler_sockets[1][1], server)
        assert (guest_peer_connected_ack.command, guest_peer_connected_ack.msg_type) == (65, 1)
        first_completed_notifications = [
            _recv_frame(handler_sockets[0][1], server) for _ in range(4)
        ]
        second_completed_notifications = [
            _recv_frame(handler_sockets[1][1], server) for _ in range(5)
        ]
        assert [frame.command for frame in first_completed_notifications] == [116, 116, 30, 30]
        assert [frame.command for frame in second_completed_notifications] == [116, 116, 30, 30, 115]

        # Duplicate edges are idempotent and completion is emitted exactly once.
        handler_sockets[1][1].sendall(
            server._fire2_build(4, 65, 18, 0, guest_peer_connected_payload)
        )
        duplicate_edge_ack = _recv_frame(handler_sockets[1][1], server)
        assert (duplicate_edge_ack.command, duplicate_edge_ack.msg_type) == (65, 1)
        _assert_no_frame(handler_sockets[0][1], server)
        _assert_no_frame(handler_sockets[1][1], server)

        advance_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("GSTA", 130)
        )
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 3, 17, 0, advance_payload)
        )
        # No transport promotion arrives in this regression. The bounded
        # 500 ms fallback must still commit PRE_GAME and release the original
        # pending RPC instead of leaving it hung forever. State notification
        # is deliberately placed on the wire before the correlated success.
        first_pregame = _recv_frame(handler_sockets[0][1], server)
        second_pregame = _recv_frame(handler_sockets[1][1], server)
        assert (first_pregame.command, first_pregame.msg_type) == (100, 2)
        assert (second_pregame.command, second_pregame.msg_type) == (100, 2)
        assert server._tdf_get_int(first_pregame.payload, "GSTA") == 130
        advance_ack = _recv_frame(handler_sockets[0][1], server)
        assert (advance_ack.command, advance_ack.msg_type) == (3, 1)
        assert advance_ack.msg_num == 17
        assert active_match["game_state"] == 130
        assert active_match["pregame_transition_source"] == "bounded-fallback"

        # A guest cannot mutate game state. Its request is acknowledged at the
        # RPC layer but must not produce another state notification.
        handler_sockets[1][1].sendall(
            server._fire2_build(4, 3, 18, 0, advance_payload)
        )
        duplicate_advance_ack = _recv_frame(handler_sockets[1][1], server)
        assert (duplicate_advance_ack.command, duplicate_advance_ack.msg_type) == (3, 1)
        for _, peer_sock in handler_sockets:
            peer_sock.settimeout(0.1)
            try:
                duplicate = _recv_frame(peer_sock, server)
            except (TimeoutError, socket.timeout):
                continue
            raise AssertionError(
                f"duplicate state notification c={duplicate.component} "
                f"k={duplicate.command}"
            )

        # Each participant's GSTA=131 request is one Advance press. The first
        # is acknowledged without changing state; the second broadcasts the
        # mutual-ready transition to IN_GAME.
        in_game_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_int("GSTA", 131)
        )
        handler_sockets[1][1].sendall(
            server._fire2_build(4, 3, 19, 0, in_game_payload)
        )
        guest_ready_ack = _recv_frame(handler_sockets[1][1], server)
        assert (guest_ready_ack.command, guest_ready_ack.msg_type) == (3, 1)
        _assert_no_frame(handler_sockets[0][1], server)
        _assert_no_frame(handler_sockets[1][1], server)
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 3, 20, 0, in_game_payload)
        )
        host_ready_ack = _recv_frame(handler_sockets[0][1], server)
        assert (host_ready_ack.command, host_ready_ack.msg_type) == (3, 1)
        first_in_game = _recv_frame(handler_sockets[0][1], server)
        second_in_game = _recv_frame(handler_sockets[1][1], server)
        assert server._tdf_get_int(first_in_game.payload, "GSTA") == 131
        assert server._tdf_get_int(second_in_game.payload, "GSTA") == 131

        # FIFA 21 c4/k171 is populated connection telemetry: preserve it as an
        # opaque request and acknowledge it with the observed empty success.
        telemetry_payload = (
            server._tdf_field_int("GID", game_id)
            + server._tdf_field_blob("DATA", b"fifa21-telemetry")
        )
        handler_sockets[0][1].settimeout(3)
        handler_sockets[0][1].sendall(
            server._fire2_build(4, 171, 22, 0, telemetry_payload)
        )
        telemetry_ack = _recv_frame(handler_sockets[0][1], server)
        assert (telemetry_ack.command, telemetry_ack.msg_type) == (171, 1)
        assert telemetry_ack.payload == b""
        telemetry_packet = server.Fire2Packet(
            0, 0, 4, 171, 23, 0, telemetry_payload, b"", b""
        )
        telemetry_response, telemetry_label = server._fire2_local_response(
            telemetry_packet, first
        )
        assert telemetry_response == b""
        assert telemetry_label == "GameManager.ConnectionTelemetry"
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

        # FIFA 21's host may request PRE_GAME early, but the request must stay
        # pending through both self reports and the first directed peer edge.
        race = server.MatchmakingSessionRegistry()
        race.start(identities[0], "FUT_Rivals", start_payload, first)
        race.start(identities[1], "FUT_Rivals", start_payload, second)
        race_match = next(iter(race.games.values()))
        advanced, changed = race.advance_game_state(
            identities[0], int(race_match["game_id"]), 130
        )
        assert advanced is race_match and changed
        assert race_match["pending_pregame"] is True
        assert race_match["pregame_notified"] is False
        _, claimed = race.claim_pregame_if_ready(int(race_match["game_id"]))
        assert not claimed
        game_id_race = int(race_match["game_id"])

        # FIFA 21 first reports TCG entityId=0/FLGS=0. Treat that as each
        # recipient's endpoint readiness without manufacturing a peer edge or
        # completing validation before the host's session update.
        for identity in identities:
            endpoint_match, first_endpoint = race.acknowledge_endpoint_initialization(
                identity, game_id_race
            )
            assert endpoint_match is race_match and first_endpoint
            assert race.claim_connection_validation(
                game_id_race, identity
            )[1] is False

        # The shared timing gate is now reachable: both endpoints are present
        # and the host has completed UpdateGameSession. Each recipient still
        # owns a separate one-shot validation result and session id.
        race.acknowledge_game_step(identities[0], game_id_race, "session_updated")
        for identity in identities:
            assert race.claim_connection_validation(
                game_id_race, identity
            )[1] is True
            assert race.claim_connection_validation(
                game_id_race, identity
            )[1] is False
        assert race_match["mesh_complete"] is False
        assert not race_match["connected_peer_edges"]
        assert race.claim_pregame_if_ready(game_id_race)[1] is False

        # (1) Later identity-bearing self-reports are idempotent and do not
        # complete the mesh after the status-zero endpoint callbacks.
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[0], game_id_race, first.persona_id, 0
        )
        assert not first_report and not completed_now
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[1], game_id_race, second.persona_id, 0
        )
        assert not first_report and not completed_now
        assert race_match["mesh_complete"] is False
        assert race.claim_join_notifications(game_id_race)[1] is False
        assert race.claim_pregame_if_ready(game_id_race)[1] is False

        # A non-CONNECTED cross status remains telemetry, not a peer edge.
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[0], game_id_race, second.persona_id, 3
        )
        assert not first_report and not completed_now
        assert not race_match["connected_peer_edges"]

        # (2) One real directed cross-link remains incomplete.
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[0], game_id_race, second.persona_id, 4
        )
        assert first_report and not completed_now
        assert race_match["mesh_complete"] is False
        assert race.claim_pregame_if_ready(game_id_race)[1] is False

        # (3) The reverse link completes exactly once.
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[1], game_id_race, first.persona_id, 4
        )
        assert first_report and completed_now
        assert race_match["mesh_complete"] is True

        # (4, 5) Duplicates change nothing and readiness cannot regress even
        # if later telemetry reports a non-connected status.
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[1], game_id_race, first.persona_id, 4
        )
        assert not first_report and not completed_now
        _, _, first_report, completed_now = race.acknowledge_mesh_target(
            identities[0], game_id_race, second.persona_id, 0
        )
        assert not first_report and not completed_now
        assert race_match["mesh_complete"] is True

        # (6) PRE_GAME (state 130) still requires the post-mesh lifecycle.
        assert race.claim_pregame_if_ready(game_id_race)[1] is False
        for identity in identities:
            race.mark_player_state_notifications_delivered(game_id_race, identity)
            race.mark_join_notifications_delivered(game_id_race, identity)
        race.acknowledge_game_step(
            identities[0], game_id_race, "session_updated"
        )
        race.acknowledge_transport_promotion(
            game_id_race, identities[0], identities[1]
        )
        race.acknowledge_transport_promotion(
            game_id_race, identities[1], identities[0]
        )
        assert race.claim_pregame_if_ready(game_id_race)[1] is True
        assert race_match["game_state"] == 130  # PRE_GAME, not IN_GAME.
        assert race.claim_pregame_if_ready(game_id_race)[1] is False
        _, changed = race.advance_game_state(identities[1], game_id_race, 131)
        assert not changed and race_match["game_state"] == 130
        _, changed = race.advance_game_state(identities[0], game_id_race, 131)
        assert changed and race_match["game_state"] == 131

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

        print("PASS: both Rivals players receive one recipient-correlated c4/k20")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
