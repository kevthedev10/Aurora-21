from __future__ import annotations

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

from authenticated_gateway import ROOT, load_host_secret


WIREGUARD_HELPER = "/usr/local/sbin/project21-wireguard-peers"


def request_json(url: str, host_secret: str, method: str = "GET", body: dict | None = None) -> dict:
    encoded = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=encoded,
        method=method,
        headers={
            "content-type": "application/json",
            "x-project21-host-secret": host_secret,
            "user-agent": "Project21-Linux-Host-Agent/1",
        },
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        return json.loads(response.read(1024 * 1024).decode("utf-8"))


def execute(command: dict) -> tuple[bool, str]:
    discord_id = str(command.get("target_discord_id", ""))
    if not discord_id.isascii() or not discord_id.isdecimal() or not 5 <= len(discord_id) <= 32:
        return False, "invalid target Discord ID"
    payload = str(command.get("payload", "{}"))
    action = str(command.get("action", ""))
    if action == "wireguard_add_peer":
        try:
            values = json.loads(payload)
            arguments = [
                "sudo", WIREGUARD_HELPER, "add",
                "--discord-id", discord_id,
                "--device-id", str(values["deviceId"]),
                "--public-key", str(values["publicKey"]),
                "--address", str(values["address"]),
            ]
        except (KeyError, TypeError, ValueError, json.JSONDecodeError):
            return False, "invalid WireGuard peer payload"
        completed = subprocess.run(
            arguments, stdin=subprocess.DEVNULL, capture_output=True,
            text=True, timeout=30, check=False,
        )
        output = (completed.stdout + completed.stderr).strip()[-16000:]
        return completed.returncode == 0, output or f"exit code {completed.returncode}"
    if action == "revoke_user":
        completed = subprocess.run(
            ["sudo", WIREGUARD_HELPER, "remove-user", "--discord-id", discord_id],
            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False,
        )
        output = (completed.stdout + completed.stderr).strip()[-16000:]
        return completed.returncode == 0, output or f"exit code {completed.returncode}"
    payload_encoded = base64.urlsafe_b64encode(payload.encode("utf-8")).decode("ascii").rstrip("=")
    environment = os.environ.copy()
    environment["LOCALFUT_RUNTIME_ROOT"] = str(ROOT / "state" / "players" / discord_id)
    environment["LOCALFUT21_USER_CONFIG_ROOT"] = str(ROOT / "config" / "players" / discord_id)
    Path(environment["LOCALFUT_RUNTIME_ROOT"]).mkdir(parents=True, exist_ok=True)
    Path(environment["LOCALFUT21_USER_CONFIG_ROOT"]).mkdir(parents=True, exist_ok=True)
    action_script = ROOT / "runtime-source" / "localfut21" / "admin_actions.py"
    completed = subprocess.run(
        [
            sys.executable,
            str(action_script),
            "--action",
            action,
            "--target-discord-id",
            discord_id,
            "--payload-base64",
            payload_encoded,
        ],
        cwd=str(action_script.parent),
        env=environment,
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )
    output = (completed.stdout + completed.stderr).strip()[-16000:]
    if completed.returncode != 0:
        return False, output or f"exit code {completed.returncode}"
    if action == "ban_user":
        wireguard = subprocess.run(
            ["sudo", WIREGUARD_HELPER, "remove-user", "--discord-id", discord_id],
            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=30, check=False,
        )
        wireguard_output = (wireguard.stdout + wireguard.stderr).strip()[-8000:]
        if wireguard.returncode != 0:
            return False, (output + "\n" + wireguard_output).strip()
    return True, output or "completed"


def signal_session_command(command: dict) -> None:
    action = str(command.get("action", ""))
    if action not in {"ban_user", "kick_user", "revoke_user"}:
        return
    command_id = int(command["id"])
    discord_id = str(command["target_discord_id"])
    directory = ROOT / "state" / "session-commands"
    directory.mkdir(parents=True, exist_ok=True)
    destination = directory / f"{command_id}.json"
    temporary = directory / f".{command_id}.{os.getpid()}.tmp"
    temporary.write_text(
        json.dumps({"action": action, "discordId": discord_id}), encoding="utf-8"
    )
    temporary.replace(destination)


def synchronize_wireguard(base_url: str, host_secret: str) -> None:
    response = request_json(base_url + "/api/host/wireguard-peers", host_secret)
    peers = response.get("peers")
    if not isinstance(peers, list) or len(peers) > 1100:
        raise ValueError("control panel returned an invalid WireGuard peer list")
    completed = subprocess.run(
        ["sudo", WIREGUARD_HELPER, "sync"],
        input=json.dumps(peers, separators=(",", ":")),
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    if completed.returncode != 0:
        output = (completed.stdout + completed.stderr).strip()[-16000:]
        raise RuntimeError(output or "WireGuard peer synchronization failed")


def main() -> int:
    parser = argparse.ArgumentParser(description="Project 21 Linux control-plane host agent")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    control = json.loads((ROOT / "control-plane-host.json").read_text(encoding="utf-8-sig"))
    if control.get("enabled") is not True:
        raise RuntimeError("control-plane-host.json is disabled")
    base_url = str(control.get("baseUrl", "")).rstrip("/")
    if not base_url.startswith("https://"):
        raise RuntimeError("control-plane baseUrl must use HTTPS")
    secret_path = ROOT / "config" / "control-plane-host-secret.json"
    secret_document = json.loads(secret_path.read_text(encoding="utf-8-sig")) if secret_path.exists() else {}
    host_secret = load_host_secret(secret_document)
    poll_seconds = min(30, max(2, int(control.get("pollSeconds", 5))))
    delay = poll_seconds
    next_wireguard_sync = 0.0
    while True:
        try:
            if time.monotonic() >= next_wireguard_sync:
                synchronize_wireguard(base_url, host_secret)
                next_wireguard_sync = time.monotonic() + 300
            response = request_json(base_url + "/api/host/commands", host_secret)
            for command in response.get("commands", []):
                success, result = execute(command)
                if success or str(command.get("action", "")) in {"ban_user", "kick_user", "revoke_user"}:
                    signal_session_command(command)
                request_json(
                    f"{base_url}/api/host/commands/{int(command['id'])}/result",
                    host_secret,
                    method="POST",
                    body={"success": success, "result": result},
                )
            delay = poll_seconds
        except (OSError, ValueError, KeyError, RuntimeError, urllib.error.URLError, subprocess.SubprocessError) as error:
            print(f"Control-plane poll failed: {error}", file=sys.stderr, flush=True)
            delay = min(60, max(poll_seconds, delay * 2))
        if args.once:
            return 0
        time.sleep(delay)


if __name__ == "__main__":
    raise SystemExit(main())
