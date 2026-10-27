from __future__ import annotations

import json
import logging
import os
from pathlib import Path
import sys
import tempfile


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="project21-promo-packs-") as temporary:
        root = Path(temporary)
        runtime = root / "runtime"
        config = root / "config"
        config.mkdir(parents=True)
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(config)

        source = Path(__file__).resolve().parent / "runtime-source" / "localfut21"
        sys.path.insert(0, str(source))
        import server  # noqa: PLC0415 - isolate paths before importing runtime

        # Simulate a player profile containing the entire old Promo tab. The
        # curated migration must replace it rather than merging both lineups.
        saved = {
            "next_pack_id": 26,
            "packs": {
                "1": server.DEFAULT_PACK_SETTINGS["packs"]["1"],
                "2": server.DEFAULT_PACK_SETTINGS["packs"]["2"],
                "3": server.DEFAULT_PACK_SETTINGS["packs"]["3"],
                "4": {"name": "Old Promo", "store_group": "special"},
                "25": {"name": "Nick Pack", "store_group": "special"},
            },
        }
        (config / "pack_settings.json").write_text(
            json.dumps(saved), encoding="utf-8"
        )
        settings = server.load_pack_settings()
        promo = {
            pack_id: pack for pack_id, pack in settings["packs"].items()
            if pack.get("store_group") == "special"
        }
        assert set(promo) == server.PROMO_PACK_IDS
        assert len(promo) == 8
        assert len({pack["name"] for pack in promo.values()}) == 8
        assert "4" not in settings["packs"]
        assert "25" not in settings["packs"]

        for pack_id in sorted(server.PROMO_PACK_IDS, key=int):
            result = server.STATE.create_pack(int(pack_id))
            assert len(result["itemData"]) == int(promo[pack_id]["size"])

        low_special = server.STATE.create_pack(34)["itemData"]
        assert len(low_special) == 12
        assert all(int(item["rareflag"]) > 1 for item in low_special)
        assert all(65 <= int(item["rating"]) <= 79 for item in low_special)

        for _ in range(10):
            sudgey = server.STATE.create_pack(36)["itemData"]
            assert len(sudgey) == 20
            assert all(int(item["rating"]) >= 88 and item["itemType"] == "player" for item in sudgey)

        cosmetics = server.STATE.create_pack(37)["itemData"]
        assert len(cosmetics) == 4
        assert [item["itemType"] for item in cosmetics].count("kit") == 3
        assert [item["itemType"] for item in cosmetics].count("stadium") == 1
        assert len({item["resourceId"] for item in cosmetics if item["itemType"] == "kit"}) == 3

        server.STATE.conn.close()
        logging.shutdown()

    print("PASS: seven promo packs; 79-max specials and Sudgey 88x20 verified")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
