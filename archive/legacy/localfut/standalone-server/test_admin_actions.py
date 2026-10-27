from __future__ import annotations

import base64
from contextlib import closing
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parent
SCRIPT = ROOT / "runtime-source" / "localfut21" / "admin_actions.py"


def encode(value: dict) -> str:
    return base64.urlsafe_b64encode(json.dumps(value).encode()).decode().rstrip("=")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-admin-test-") as temporary:
        temp = Path(temporary)
        environment = os.environ.copy()
        environment["LOCALFUT_RUNTIME_ROOT"] = str(temp / "state")
        environment["LOCALFUT21_USER_CONFIG_ROOT"] = str(temp / "config")
        common = [str(SCRIPT), "--target-discord-id", "123456789012345678"]
        subprocess.run([sys.executable, *common, "--action", "grant_pack", "--payload-base64", encode({"packId": 3, "count": 2})], env=environment, check=True)
        with closing(sqlite3.connect(temp / "state" / "data" / "localfut15.sqlite3")) as database:
            initial_credits = json.loads(database.execute("SELECT value FROM kv WHERE key='credits'").fetchone()[0])
            packs = database.execute("SELECT COUNT(*) FROM unopened_packs WHERE pack_id=3").fetchone()[0]
            assert packs == 2, packs
        subprocess.run([sys.executable, *common, "--action", "add_coins", "--payload-base64", encode({"amount": 1234})], env=environment, check=True)
        subprocess.run([sys.executable, *common, "--action", "grant_card", "--payload-base64", encode({"resourceId": 50527512})], env=environment, check=True)
        with closing(sqlite3.connect(temp / "state" / "data" / "localfut15.sqlite3")) as database:
            final_credits = json.loads(database.execute("SELECT value FROM kv WHERE key='credits'").fetchone()[0])
            assert final_credits == initial_credits + 1234, (initial_credits, final_credits)
            card_count = database.execute("SELECT COUNT(*) FROM items WHERE resource_id=50527512").fetchone()[0]
            assert card_count == 1, card_count
    print("PASS host admin coin, pack and card actions")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
