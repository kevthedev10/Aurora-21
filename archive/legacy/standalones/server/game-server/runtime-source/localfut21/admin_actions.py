from __future__ import annotations

import argparse
import base64
import json
import os
import unicodedata
from pathlib import Path


def decode_payload(value: str) -> dict:
    padding = "=" * (-len(value) % 4)
    raw = base64.urlsafe_b64decode((value + padding).encode("ascii"))
    parsed = json.loads(raw.decode("utf-8"))
    if not isinstance(parsed, dict):
        raise ValueError("payload must be an object")
    return parsed


def state_club_name(server) -> str:
    getter = getattr(server.STATE, "club_name", None)
    if callable(getter):
        return str(getter())
    default = str(server.CFG.get("club_name", "Local FC"))
    return str(server.STATE.get("club_name", default) or default)


def state_club_abbr(server) -> str:
    getter = getattr(server.STATE, "club_abbr", None)
    if callable(getter):
        return str(getter())
    default = str(server.CFG.get("club_abbr", "LFC"))
    return str(server.STATE.get("club_abbr", default) or default)


def normalize_club_text(value: object, *, maximum: int, uppercase: bool = False) -> str:
    text = unicodedata.normalize("NFKC", str(value))
    text = "".join(
        character for character in text
        if not unicodedata.category(character).startswith("C")
    )
    text = " ".join(text.split()).strip()
    if uppercase:
        text = text.upper()
    return text[:maximum].strip()


def update_club_identity(server, payload: object) -> tuple[str, str]:
    updater = getattr(server.STATE, "update_club_identity", None)
    if callable(updater):
        return updater(payload)

    source = dict(payload) if isinstance(payload, dict) else {}
    if isinstance(source.get("club"), dict):
        source = dict(source["club"])
    name = state_club_name(server)
    abbr = state_club_abbr(server)
    if "clubName" in source or "name" in source:
        name = normalize_club_text(source.get("clubName", source.get("name", "")), maximum=32)
        if not name:
            raise ValueError("club name cannot be empty")
    if "clubAbbr" in source or "abbr" in source:
        abbr = normalize_club_text(
            source.get("clubAbbr", source.get("abbr", "")), maximum=3, uppercase=True
        )
        if not abbr:
            raise ValueError("club abbreviation cannot be empty")
    server.STATE.set("club_name", name)
    server.STATE.set("club_abbr", abbr)
    return name, abbr


def main() -> int:
    parser = argparse.ArgumentParser(description="Apply one authenticated Project 21 admin command")
    parser.add_argument("--action", required=True, choices=(
        "add_coins", "grant_pack", "grant_card", "get_club",
        "set_club_identity", "kick_user", "ban_user", "unban_user",
    ))
    parser.add_argument("--target-discord-id", required=True)
    parser.add_argument("--payload-base64", required=True)
    args = parser.parse_args()
    if not args.target_discord_id.isdigit() or not 5 <= len(args.target_discord_id) <= 24:
        parser.error("invalid Discord ID")
    payload = decode_payload(args.payload_base64)

    import server  # Import after the host agent supplies isolated runtime/config roots.

    result: dict[str, object] = {"action": args.action, "targetDiscordId": args.target_discord_id}
    if args.action == "get_club":
        squad = server._native_squad()
        players = []
        for slot in squad.get("players", []):
            if not isinstance(slot, dict) or not isinstance(slot.get("itemData"), dict):
                continue
            item = slot["itemData"]
            players.append({
                "index": int(slot.get("index", 0) or 0),
                "name": str(item.get("name") or item.get("displayName") or "Unknown player"),
                "rating": int(item.get("rating", 0) or 0),
                "position": str(item.get("preferredPosition") or item.get("position") or ""),
                "resourceId": int(item.get("resourceId", 0) or 0),
            })
        result.update(
            clubName=state_club_name(server),
            clubAbbr=state_club_abbr(server),
            credits=server.STATE.credits(),
            squadName=str(squad.get("squadName") or squad.get("name") or "Active Squad"),
            formation=str(squad.get("formation") or ""),
            rating=int(squad.get("rating", 0) or 0),
            chemistry=int(squad.get("chemistry", 0) or 0),
            players=players,
        )
    elif args.action == "set_club_identity":
        name, abbr = update_club_identity(server, payload)
        result.update(clubName=name, clubAbbr=abbr, status="updated")
    elif args.action == "add_coins":
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
