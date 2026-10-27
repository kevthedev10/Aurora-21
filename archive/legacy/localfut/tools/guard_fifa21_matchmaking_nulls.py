"""Temporary reversible guard for the post-matchmaking FIFA 21 null object.

The native function at FIFA21.exe+0x10D0450 accepts its object as argument 2
(RDX).  Its prologue completes at +0x10D045F; a null object would fault while
reading the embedded interface at +0x420.  Redirect only that case to the
function's own allocation-failure path at +0x10D04DE.  No on-disk game bytes
are modified.
"""

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

    # FIFA 21 unpacks this executable region shortly after startup.
    time.sleep(8.0)
    session = device.attach(pid)
    output_dir = pathlib.Path(__file__).resolve().parents[1] / "runtime" / "diagnostics"
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    output = output_dir / f"fifa21-matchmaking-null-guard-{stamp}.jsonl"

    source = r"""
const game = Process.getModuleByName('FIFA21.exe');
const functionEntry = game.base.add(0x10d0450);
let callCount = 0;
const original = new NativeFunction(
  functionEntry, 'pointer', ['pointer', 'pointer', 'pointer', 'pointer'], 'win64'
);
const replacement = new NativeCallback(function(arg0, arg1, arg2, arg3) {
  callCount += 1;
  const isNull = arg1.isNull();
  send({
    event: isNull ? 'null-object-returned' : 'non-null-call',
    callCount: callCount,
    pid: Process.id,
    threadId: Process.getCurrentThreadId(),
    functionEntry: functionEntry.toString(),
    arg0: arg0.toString(),
    arg1: arg1.toString(),
    arg2: arg2.toString(),
    arg3: arg3.toString()
  });
  if (isNull) return ptr(0);
  return original(arg0, arg1, arg2, arg3);
}, 'pointer', ['pointer', 'pointer', 'pointer', 'pointer'], 'win64');
Interceptor.replace(functionEntry, replacement);

send({
  event: 'armed',
  pid: Process.id,
  base: game.base.toString(),
  functionEntry: functionEntry.toString(),
  mode: 'entry-replacement'
});
"""

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
