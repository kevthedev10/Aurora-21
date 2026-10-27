"""Isolated persistence and tenant-isolation checks for saved club identity."""
from __future__ import annotations

import logging
import os
import sys
import tempfile
from pathlib import Path


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-club-identity-") as temporary:
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

        registry = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=4)
        first_id = "111111111111111111"
        second_id = "222222222222222222"
        try:
            with registry.scope(first_id):
                assert server.STATE.club_name() == "Local FC"
                assert server.STATE.club_abbr() == "LCL"
                status, _, body = server.route_fut(
                    "PUT", "/ut/game/fifa21/user/club", {},
                    b'{"clubName":"  First   Player FC  ","clubAbbr":"oneplus"}',
                )
                assert status == 200
                assert b'"clubName":"First Player FC"' in body
                assert b'"clubAbbr":"ONE"' in body

            with registry.scope(second_id):
                assert server.STATE.club_name() == "Local FC"
                assert server.STATE.club_abbr() == "LCL"

            with registry.scope(first_id):
                assert server.STATE.club_name() == "First Player FC"
                assert server.STATE.club_abbr() == "ONE"
                squad = server.STATE.get_squad(server.STATE.active_squad_id())
                assert squad is not None
                squad["name"] = "Weekend XI"
                squad["squadName"] = "Weekend XI"
                server.STATE.save_squad(squad)
                saved_squad = server.STATE.get_squad(int(squad["id"]))
                assert saved_squad is not None
                assert saved_squad["name"] == "Weekend XI"
                assert saved_squad["squadName"] == "Weekend XI"
                status, _, body = server.route_fut(
                    "PUT", "/ut/game/fifa21/user/club", {},
                    b'{"clubName":"\\u0000\\t","clubAbbr":""}',
                )
                assert status == 400 and b'"success":false' in body
                assert server.STATE.club_name() == "First Player FC"
        finally:
            registry.close()
            logging.shutdown()

    print("PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
