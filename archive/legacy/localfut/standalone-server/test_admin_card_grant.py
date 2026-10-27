from __future__ import annotations

import base64
import contextlib
import io
import json
from pathlib import Path
import sys
from types import SimpleNamespace
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parent / "runtime-source" / "localfut21"))
import admin_actions  # noqa: E402


class FakeState:
    def __init__(self) -> None:
        self.granted_resource_id = 0

    def owned_player_item_by_resource_id(self, resource_id: int) -> None:
        return None

    def make_player_item(self, resource_id: int, *, pile: str) -> dict[str, int]:
        assert pile == "club"
        self.granted_resource_id = resource_id
        return {"id": 900000000001}


def encoded_payload(resource_id: int) -> str:
    raw = json.dumps({"resourceId": resource_id}).encode("utf-8")
    return base64.urlsafe_b64encode(raw).decode("ascii").rstrip("=")


def main() -> int:
    old_resource_id = 246525
    migrated_resource_id = old_resource_id + 0x01000000
    state = FakeState()
    fake_server = SimpleNamespace(
        PLAYER_DB={migrated_resource_id: {"name": "Pelé"}},
        PLAYER_RESOURCE_ID_MIGRATIONS={old_resource_id: migrated_resource_id},
        STATE=state,
    )
    arguments = [
        "admin_actions.py",
        "--action", "grant_card",
        "--target-discord-id", "505839712938754088",
        "--payload-base64", encoded_payload(old_resource_id),
    ]
    output = io.StringIO()
    with patch.dict(sys.modules, {"server": fake_server}), patch.object(sys, "argv", arguments):
        with contextlib.redirect_stdout(output):
            assert admin_actions.main() == 0
    result = json.loads(output.getvalue())
    assert state.granted_resource_id == migrated_resource_id
    assert result["resourceId"] == migrated_resource_id
    assert result["requestedResourceId"] == old_resource_id
    print("PASS admin card grants translate legacy Icon Moments resource IDs")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
