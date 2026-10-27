from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import logging
import os
from pathlib import Path
import shutil
import sys
import tempfile
import time


def linux_memory() -> tuple[float | None, float | None]:
    if sys.platform != "linux":
        return None, None
    values: dict[str, int] = {}
    for line in Path("/proc/self/status").read_text(encoding="ascii").splitlines():
        if line.startswith(("VmRSS:", "VmHWM:")):
            key, value, _ = line.split()
            values[key.rstrip(":")] = int(value)
    return values.get("VmRSS", 0) / 1024, values.get("VmHWM", 0) / 1024


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Create isolated real LocalFUT player contexts to measure host capacity"
    )
    parser.add_argument("--players", type=int, default=25)
    parser.add_argument("--workers", type=int, default=min(8, os.cpu_count() or 1))
    parser.add_argument("--keep", action="store_true", help="Keep generated test databases")
    args = parser.parse_args()
    if not 1 <= args.players <= 1000:
        parser.error("--players must be between 1 and 1000")
    if not 1 <= args.workers <= 32:
        parser.error("--workers must be between 1 and 32")

    test_root = Path(tempfile.mkdtemp(prefix="project21-capacity-"))
    bootstrap_runtime = test_root / "bootstrap"
    bootstrap_config = bootstrap_runtime / "config"
    bootstrap_config.mkdir(parents=True)
    os.environ["LOCALFUT_RUNTIME_ROOT"] = str(bootstrap_runtime)
    os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(bootstrap_config)
    os.environ["LOCALFUT_MULTI_TENANT_BOOTSTRAP"] = "1"
    logging.basicConfig(level=logging.WARNING)
    source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
    sys.path.insert(0, str(source))
    started = time.monotonic()
    failures: list[str] = []
    registry = None
    try:
        import server  # noqa: PLC0415 - isolated roots must be configured first

        registry = server.enable_multi_tenant(
            test_root, idle_seconds=3600, max_cached_players=args.players
        )

        def create(index: int) -> None:
            identity = str(700000000000000000 + index)
            try:
                with registry.scope(identity):
                    assert server.STATE.credits() >= 0
                    assert server.STATE.list_items("club")
            except Exception as error:  # report every failed identity after the run
                failures.append(f"{identity}: {error}")

        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            list(executor.map(create, range(args.players)))
        elapsed = time.monotonic() - started
        current_rss, peak_rss = linux_memory()
        total_bytes = sum(path.stat().st_size for path in test_root.rglob("*") if path.is_file())
        result = {
            "playersCreated": args.players - len(failures),
            "playersRequested": args.players,
            "workers": args.workers,
            "elapsedSeconds": round(elapsed, 2),
            "currentRssMiB": None if current_rss is None else round(current_rss, 1),
            "peakRssMiB": None if peak_rss is None else round(peak_rss, 1),
            "testStateMiB": round(total_bytes / 1024 / 1024, 1),
            "failures": failures[:20],
            "scope": "state/context capacity only; run a separate live protocol load test before launch",
        }
        print(json.dumps(result, indent=2))
        return 0 if not failures and result["playersCreated"] == args.players else 1
    finally:
        if registry is not None:
            registry.close()
        logging.shutdown()
        if args.keep:
            print(f"Kept test data: {test_root}")
        else:
            shutil.rmtree(test_root, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
