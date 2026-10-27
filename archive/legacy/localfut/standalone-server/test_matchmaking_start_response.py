from __future__ import annotations

import logging
import os
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-matchmaking-response-") as temporary:
        root = Path(temporary)
        bootstrap_runtime = root / "bootstrap"
        bootstrap_config = bootstrap_runtime / "config"
        bootstrap_config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
        os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"

        source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
        sys.path.insert(0, str(source))
        import server  # noqa: PLC0415 - environment must be isolated before import

        registry = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=8)
        try:
            # Native FIFA 21 c4/k16 identifies its scenario through SCNM. ATTR is a
            # generic-value map, not the map<string,string> used by older fixtures.
            payload = server._tdf_field_str("SCNM", "FUT_Rivals")

            def packet(command: int, payload_bytes: bytes) -> server.Fire2Packet:
                return server.Fire2Packet(
                    payload_size=len(payload_bytes),
                    metadata_size=0,
                    component=4,
                    command=command,
                    msg_num=1,
                    msg_type=0,
                    metadata=b"",
                    payload=payload_bytes,
                    raw=b"",
                )

            def start(identity: str) -> tuple[int, bytes, str, str]:
                with registry.scope(identity):
                    response, label = server._fire2_local_response(packet(16, payload))
                entries = [
                    (session_id, entry)
                    for session_id, entry in server.MATCHMAKING_SESSIONS.sessions.items()
                    if str(entry["discord_id"]) == identity
                ]
                assert len(entries) == 1
                session_id, entry = entries[0]
                return session_id, response, label, str(entry["mode"])

            identities = [str(111111111111111110 + index) for index in range(8)]
            with ThreadPoolExecutor(max_workers=8) as executor:
                results = list(executor.map(start, identities))

            session_ids = [result[0] for result in results]
            assert all(session_id > 0 for session_id in session_ids)
            assert len(set(session_ids)) == len(session_ids)
            assert all(server._tdf_get_int(result[1], "MSID") == 0 for result in results)
            assert all(result[1] != b"" for result in results)
            assert all(result[2] == "GameManager.StartMatchmaking" for result in results)
            assert all(result[3] == "FUT_Rivals" for result in results)

            first_id = session_ids[0]
            # The client-supplied MSID is not the server's private queue id.
            cancel_payload = server._tdf_field_int("MSID", 123456)
            with registry.scope(identities[1]):
                wrong_response, wrong_label = server._fire2_local_response(
                    packet(17, cancel_payload)
                )
            assert wrong_response == b""
            assert wrong_label == "GameManager.CancelMatchmaking"
            assert first_id in server.MATCHMAKING_SESSIONS.sessions

            with registry.scope(identities[0]):
                cancel_response, cancel_label = server._fire2_local_response(
                    packet(17, cancel_payload)
                )
            assert cancel_response == b""
            assert cancel_label == "GameManager.CancelMatchmaking"
            assert first_id not in server.MATCHMAKING_SESSIONS.sessions

            with registry.scope(identities[0]):
                notification_payload = server._blaze_matchmaking_cancelled(0)
                expected_user_session_id = int(server.CFG["persona_id"])
            assert server._tdf_get_int(notification_payload, "MAXF") == 0
            assert server._tdf_get_int(notification_payload, "MSID") == 0
            assert server._tdf_get_int(notification_payload, "RSLT") == 4
            assert server._tdf_get_int(notification_payload, "USID") == expected_user_session_id
        finally:
            registry.close()
            logging.shutdown()

    print("PASS: c4/k16 starts and c4/k17 safely cancels global Rivals searches")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
