from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path
from types import SimpleNamespace


MODULE_PATH = Path(__file__).parent / "runtime-source" / "localfut21" / "admin_actions.py"
SPEC = importlib.util.spec_from_file_location("project21_admin_actions", MODULE_PATH)
assert SPEC and SPEC.loader
ADMIN_ACTIONS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ADMIN_ACTIONS)


class LegacyState:
    def __init__(self) -> None:
        self.values: dict[str, object] = {}

    def get(self, key: str, default: object = None) -> object:
        return self.values.get(key, default)

    def set(self, key: str, value: object) -> None:
        self.values[key] = value


class CurrentState(LegacyState):
    def club_name(self) -> str:
        return "Current Name"

    def club_abbr(self) -> str:
        return "CUR"

    def update_club_identity(self, payload: object) -> tuple[str, str]:
        return "Current Updated", "CPU"


class AdminActionsCompatibilityTests(unittest.TestCase):
    def test_legacy_state_reads_defaults_and_persists_identity(self) -> None:
        server = SimpleNamespace(STATE=LegacyState(), CFG={"club_name": "Default FC", "club_abbr": "DFC"})
        self.assertEqual(ADMIN_ACTIONS.state_club_name(server), "Default FC")
        self.assertEqual(ADMIN_ACTIONS.state_club_abbr(server), "DFC")
        result = ADMIN_ACTIONS.update_club_identity(
            server, {"clubName": "  Aurora\u0000 United  ", "clubAbbr": "au21"}
        )
        self.assertEqual(result, ("Aurora United", "AU2"))
        self.assertEqual(server.STATE.values["club_name"], "Aurora United")
        self.assertEqual(server.STATE.values["club_abbr"], "AU2")

    def test_current_state_methods_remain_authoritative(self) -> None:
        server = SimpleNamespace(STATE=CurrentState(), CFG={"club_name": "Default FC", "club_abbr": "DFC"})
        self.assertEqual(ADMIN_ACTIONS.state_club_name(server), "Current Name")
        self.assertEqual(ADMIN_ACTIONS.state_club_abbr(server), "CUR")
        self.assertEqual(
            ADMIN_ACTIONS.update_club_identity(server, {"clubName": "Ignored"}),
            ("Current Updated", "CPU"),
        )


if __name__ == "__main__":
    unittest.main()
