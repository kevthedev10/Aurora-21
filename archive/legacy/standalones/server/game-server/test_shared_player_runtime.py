from __future__ import annotations

import os
import logging
from pathlib import Path
import sys
import tempfile


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-shared-runtime-") as temporary:
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

        registry = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=750)
        first_id = "111111111111111111"
        second_id = "222222222222222222"
        with registry.scope(first_id):
            first_initial = server.STATE.credits()
            assert server.STATE.add_credits(1234) == first_initial + 1234
            assert server.CFG["club_name"] == "Local FC"
        with registry.scope(second_id):
            assert server.STATE.credits() == first_initial
            server.PENDING_USERSESSION_REFRESH.set()
            assert server.PENDING_USERSESSION_REFRESH.is_set()
        with registry.scope(first_id):
            assert server.STATE.credits() == first_initial + 1234
            assert not server.PENDING_USERSESSION_REFRESH.is_set()

        first_db = root / "state" / "players" / first_id / "data" / "localfut15.sqlite3"
        second_db = root / "state" / "players" / second_id / "data" / "localfut15.sqlite3"
        assert first_db.is_file() and second_db.is_file() and first_db != second_db
        registry.close()
        logging.shutdown()
    print("PASS shared catalogue runtime keeps Discord player state isolated")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
