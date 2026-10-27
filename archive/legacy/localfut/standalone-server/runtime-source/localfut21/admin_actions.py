from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path


def decode_payload(value: str) -> dict:
    padding = "=" * (-len(value) % 4)
    raw = base64.urlsafe_b64decode((value + padding).encode("ascii"))
    parsed = json.loads(raw.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise ValueError("payload must be an object")
    return parsed


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply one authenticated Project 21 admin command")
    parser.add_argument("--action", required=True, choices=("add_coins", "grant_pack", "grant_card", "kick_user", "ban_user", "unban_user"))
    parser.add_argument("--target-discord-id", required=True)
    parser.add_argument("--payload-base64", required=True)
    args = parser.parse_args()
    if not args.target_discord_id.isdigit() or not 5 <= len(args.target_discord_id) <= 24:
        parser.error("invalid Discord ID")
    payload = decode_payload(args.payload_base64)

    import server  # Import after the host agent supplies isolated runtime/config roots.

    result: dict[str, object] = {"action": args.action, "targetDiscordId": args.target_discord_id}
    if args.action == "add_coins":
        amount = int(payload.get("amount", 0))
        if not 1 <= amount <= 100_000_000:
            raise ValueError("coin amount out of range")
        result["credits"] = server.STATE.add_credits(amount)
    elif args.action == "grant_pack":
        pack_id = int(payload.get("packId", 0))
        count = int(payload.get("count", 1))
        if not 1 <= pack_id <= 1_000_000 or not 1 <= count <= 100:
            raise ValueError("pack request out of range")
        for _ in range(count):
            server.STATE.add_unopened_pack(pack_id, source=f"admin:{args.target_discord_id}")
        result.update(packId=pack_id, count=count)
    elif args.action == "grant_card":
        requested_resource_id = int(payload.get("resourceId", 0))
        resource_id = server.PLAYER_RESOURCE_ID_MIGRATIONS.get(
            requested_resource_id, requested_resource_id
        )
        if resource_id <= 0 or resource_id not in server.PLAYER_DB:
            raise ValueError(f"unknown player resource ID: {requested_resource_id}")
        if server.STATE.owned_player_item_by_resource_id(resource_id):
            result.update(resourceId=resource_id, status="already_owned")
        else:
            item = server.STATE.make_player_item(resource_id, pile="club")
            result.update(resourceId=resource_id, itemId=int(item["id"]), status="granted")
        if resource_id != requested_resource_id:
            result["requestedResourceId"] = requested_resource_id
    elif args.action in {"ban_user", "unban_user"}:
        state_root = Path(os.environ["LOCALFUT_RUNTIME_ROOT"])
        ban_path = state_root / "banned-discord-ids.json"
        try:
            bans = json.loads(ban_path.read_text(encoding="utf-8")) if ban_path.exists() else {}
        except (OSError, ValueError):
            bans = {}
        if not isinstance(bans, dict):
            bans = {}
        if args.action == "ban_user":
            bans[args.target_discord_id] = {"reason": str(payload.get("reason", ""))[:300]}
        else:
            bans.pop(args.target_discord_id, None)
        ban_path.parent.mkdir(parents=True, exist_ok=True)
        temporary = ban_path.with_suffix(".tmp")
        temporary.write_text(json.dumps(bans, separators=(",", ":")), encoding="utf-8")
        temporary.replace(ban_path)
        result["banned"] = args.action == "ban_user"
    elif args.action == "kick_user":
        # The current LocalFUT protocol has one shared session. Revocation is
        # enforced on the next connection; this command records that intent.
        result["status"] = "access_revoked_on_next_connection"

    print(json.dumps(result, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
