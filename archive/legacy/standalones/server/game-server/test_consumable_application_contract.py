"""Focused regression checks for consumable apply and club rename prompting."""

from __future__ import annotations

import json
import importlib.util
import logging
import os
import sys
import tempfile
from pathlib import Path


def main() -> int:
    source = Path(
        os.environ.get(
            "PROJECT21_SERVER_SOURCE",
            Path(__file__).resolve().parent / "runtime-source" / "localfut21",
        )
    ).resolve()
    source_file = Path(os.environ.get("PROJECT21_SERVER_FILE", source / "server.py")).resolve()

    with tempfile.TemporaryDirectory(prefix="project21-consumable-contract-") as temporary:
        root = Path(temporary)
        runtime = root / "runtime"
        config = root / "config"
        runtime.mkdir()
        config.mkdir()
        os.environ["LOCALFUT_RUNTIME_ROOT"] = str(runtime)
        os.environ["LOCALFUT21_USER_CONFIG_ROOT"] = str(config)
        os.environ.pop("LOCALFUT_MULTI_TENANT_BOOTSTRAP", None)
        sys.path.insert(0, str(source))
        sys.path.insert(0, str(source_file.parent))
        spec = importlib.util.spec_from_file_location("server", source_file)
        if spec is None or spec.loader is None:
            raise RuntimeError(f"could not load {source_file}")
        server = importlib.util.module_from_spec(spec)
        sys.modules["server"] = server
        spec.loader.exec_module(server)

        assert server._native_user()["clubNameChangeAllowed"] == 0

        target = next(
            item
            for item in server.STATE.list_items()
            if str(item.get("itemType", "player")) == "player"
            and any(
                definition["source"] == item.get("preferredPosition")
                for definition in server._POSITION_MODIFIER_BY_RESOURCE.values()
            )
        )
        resource_id, definition = next(
            (resource_id, definition)
            for resource_id, definition in server._POSITION_MODIFIER_BY_RESOURCE.items()
            if definition["source"] == target["preferredPosition"]
        )
        modifier_id = server.STATE.next_item_id()
        modifier = {
            "id": modifier_id,
            "resourceId": resource_id,
            "definitionId": resource_id,
            "assetId": 34,
            "carddbid": resource_id,
            "cardassetid": 34,
            "itemType": "training",
            "itemState": "free",
            "cardsubtypeid": definition["cardsubtypeid"],
            "consumableType": f'{definition["source"]}{definition["target"]}',
            "sourcePosition": definition["source"],
            "targetPosition": definition["target"],
            "position": definition["target"],
            "category": "Positioning",
            "level": "Gold",
            "amount": 0,
            "pile": "club",
        }
        presented = server._native_consumable_item(modifier)
        assert presented["assetId"] == 34
        assert presented["definitionId"] == resource_id
        assert presented["resourceId"] == resource_id
        assert presented["consumableType"] == f'{definition["source"]}{definition["target"]}'
        assert isinstance(presented["consumableType"], str)
        assert presented["amount"] == 0
        assert "consumableId" not in presented
        with server.STATE.lock:
            server.STATE.conn.execute(
                "INSERT INTO items(id,resource_id,pile,data) VALUES(?,?,?,?)",
                (modifier_id, resource_id, "club", json.dumps(modifier)),
            )
        server.STATE.conn.commit()
        expected = definition["target"]

        catalogue_status, _, catalogue_body = server.route_fut(
            "GET",
            "/ut/game/fifa21/club/consumables/development",
            {},
            b"",
        )
        catalogue = json.loads(catalogue_body)
        assert catalogue_status == 200
        assert catalogue["itemData"]
        assert catalogue["positionModifiers"]
        assert catalogue["positionModifiers"][0]["resourceId"] == resource_id
        assert catalogue["positionModifiers"][0]["consumableType"] == (
            f'{definition["source"]}{definition["target"]}'
        )

        status, _, body = server.route_fut(
            "POST",
            f"/ut/game/fifa21/item/resource/{modifier['resourceId']}",
            {},
            json.dumps({"apply": [{"id": target["id"]}]}).encode("utf-8"),
        )
        payload = json.loads(body)
        assert status == 200
        assert set(payload) == {"itemData"}
        assert len(payload["itemData"]) == 1
        assert payload["itemData"][0]["id"] == target["id"]
        assert payload["itemData"][0]["preferredPosition"] == expected
        assert server.STATE.get_item(target["id"])["preferredPosition"] == expected
        assert server.STATE.get_item(modifier["id"]) is None

        if server._ONLINE_MARKET is not None:
            server._ONLINE_MARKET.close()
            server._ONLINE_MARKET = None
        server.STATE.conn.close()
        for handler in list(logging.getLogger().handlers):
            logging.getLogger().removeHandler(handler)
            handler.close()

    print("PASS: consumable response and club prompt contracts")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
