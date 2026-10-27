from __future__ import annotations

import os
from pathlib import Path
import sys
import tempfile
import logging


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-rivals-gate-") as temporary:
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

        registry = server.enable_multi_tenant(root, idle_seconds=60, max_cached_players=2)
        try:
            with registry.scope("111111111111111111"):
                fresh = server._rivals_hub_json()
                assert fresh["gamesPlayed"] == 0
                assert fresh["gamesRemaining"] == server._RIVALS_WEEKLY_MATCHES
                assert fresh["rivalsEventMatchesSoftCap"] == 0
                assert fresh["rivalsEventMatchesSoftCapMax"] == server._RIVALS_WEEKLY_MATCHES

                progress = server._rivals_progress()
                progress["weeklyGames"] = 7
                server.STATE.set("rivals_progress", progress)
                active = server._rivals_hub_json()
                assert active["gamesPlayed"] == 7
                assert active["rivalsEventMatchesSoftCap"] == 7
                assert active["rivalsEventMatchesSoftCapMax"] == server._RIVALS_WEEKLY_MATCHES
        finally:
            registry.close()
            logging.shutdown()

    print("PASS: fresh Rivals players are below the weekly matchmaking cap")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
