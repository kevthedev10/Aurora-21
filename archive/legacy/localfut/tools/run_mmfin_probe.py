"""Wait for FIFA 21, attach the read-only MatchmakingFinished probe, and log it."""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import time

import frida


def main() -> int:
    device = frida.get_local_device()
    pid: int | None = None
    while pid is None:
        for process in device.enumerate_processes():
            if process.name.lower() == "fifa21.exe":
                pid = process.pid
                break
        if pid is None:
            time.sleep(0.25)
    time.sleep(8.0)
    session = device.attach(pid)
    source = pathlib.Path(r"C:\Users\Hello\Downloads\mmfin_probe.js").read_text(
        encoding="utf-8"
    )
    output_dir = pathlib.Path(__file__).resolve().parents[1] / "runtime" / "diagnostics"
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    output = output_dir / f"fifa21-mmfinished-{stamp}.jsonl"

    def on_message(message: dict, data: bytes | None) -> None:
        record = dict(message)
        if data is not None:
            record["data_hex"] = data.hex()
        with output.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps(record, default=str) + "\n")

    script = session.create_script(source)
    script.on("message", on_message)
    script.load()
    while True:
        time.sleep(0.25)


if __name__ == "__main__":
    raise SystemExit(main())
