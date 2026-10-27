"""Regression checks for the FIFA 21 SCMP objective payload."""

import importlib.util
import sys
from pathlib import Path


SERVER = Path(__file__).parent / "runtime-source" / "localfut21" / "server.py"
sys.path.insert(0, str(SERVER.parent))
spec = importlib.util.spec_from_file_location("localfut21_server_for_scmp", SERVER)
assert spec and spec.loader
server = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = server
spec.loader.exec_module(server)


def test_welcome_group_is_unlocked_by_fifa21_enum_values():
    group = server._scmp_group()
    # ObjectiveGroupStateEnum.COMPLETED=2 and ObjectiveGroupType.SEASONAL=3
    # in the FIFA 21 client. Group state is numeric (objective state is a name).
    assert group["groupState"] == 2
    assert group["state"] == 2
    assert group["groupType"] == 3
    assert group["groupIdsToUnlock"] == []


def test_claimed_welcome_group_is_rewarded():
    assert server._scmp_group(True)["groupState"] == 4
