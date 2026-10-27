"""One-shot read-only trace for the stable FIFA21.exe+F91CCE crash.

Attaches after FIFA 21 starts and observes the async-result accessor at
FIFA21.exe+EE3070 only when its caller is the failing FUT path.  It does not
alter the return value or executable code.
"""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import time

import frida


def main() -> int:
    device = frida.get_local_device()
    pid = None
    while pid is None:
        for process in device.enumerate_processes():
            if process.name.lower() == "fifa21.exe":
                pid = process.pid
                break
        if pid is None:
            time.sleep(0.25)

    # Give the protected title time to unpack the code containing the hook.
    time.sleep(8.0)
    session = device.attach(pid)
    output_dir = pathlib.Path(__file__).resolve().parents[1] / "runtime" / "diagnostics"
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    output = output_dir / f"fifa21-async-result-{stamp}.jsonl"

    source = r"""
const game = Process.getModuleByName('FIFA21.exe');
const accessor = game.base.add(0xEE3070);
const expectedReturn = game.base.add(0xF91CCE);
const identityLookup = game.base.add(0x95EB20);
const activeIdentityLookups = new Map();

function safeU32(address) {
  try { return address.readU32(); } catch (_) { return null; }
}
function safePtr(address) {
  try { return address.readPointer().toString(); } catch (_) { return null; }
}
function safeHex(address, length) {
  try {
    const bytes = new Uint8Array(address.readByteArray(length));
    return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  } catch (_) { return null; }
}
function safeCString(address) {
  try {
    if (address.isNull()) return null;
    return address.readUtf8String(256);
  } catch (_) { return null; }
}

/*
 * This lookup is the immediate ancestor of the stable crash.  Its EDX value
 * is the identity key used to retrieve the per-player object.  Emit on entry
 * because a crashing invocation never reaches onLeave.
 */
Interceptor.attach(identityLookup, {
  onEnter(args) {
    this.lookupThreadId = this.threadId;
    activeIdentityLookups.set(this.threadId, {
      caller: this.returnAddress.toString(), manager: args[0].toString(),
      key_u32: this.context.rdx.toUInt32(),
      key_hex: '0x' + this.context.rdx.toUInt32().toString(16).padStart(8, '0'),
      backtrace: Thread.backtrace(this.context, Backtracer.ACCURATE)
        .slice(0, 12).map(DebugSymbol.fromAddress).map(String)
    });
  },
  onLeave(retval) {
    activeIdentityLookups.delete(this.lookupThreadId);
  }
});

Interceptor.attach(accessor, {
  onEnter(args) {
    this.relevant = this.returnAddress.equals(expectedReturn);
    if (!this.relevant) return;
    this.container = args[0];
    // RAX still contains the object returned by FIFA21.exe+F90360; the caller
    // only copied [RAX+270] into RCX before invoking this accessor.
    this.owner = this.context.rax;
    this.before = {
      event: 'async-accessor-enter',
      pid: Process.id,
      threadId: this.threadId,
      caller: this.returnAddress.toString(),
      container: this.container.toString(),
      owner: this.owner.toString(),
      identity_lookup: activeIdentityLookups.get(this.threadId) || null,
      owner_vtable: safePtr(this.owner),
      owner_container_ptr_270: safePtr(this.owner.add(0x270)),
      owner_string_ptr_b0: safePtr(this.owner.add(0xb0)),
      owner_string_b0: safeCString(this.owner.add(0xb0).readPointer()),
      owner_string_ptr_2b0: safePtr(this.owner.add(0x2b0)),
      owner_string_2b0: safeCString(this.owner.add(0x2b0).readPointer()),
      owner_string_ptr_2e0: safePtr(this.owner.add(0x2e0)),
      owner_string_2e0: safeCString(this.owner.add(0x2e0).readPointer()),
      owner_bytes_200_300: safeHex(this.owner.add(0x200), 0x100),
      vtable: safePtr(this.container),
      status_u32_10: safeU32(this.container.add(0x10)),
      result_ptr_28: safePtr(this.container.add(0x28)),
      container_bytes: safeHex(this.container, 0x60),
      backtrace: Thread.backtrace(this.context, Backtracer.ACCURATE)
        .map(DebugSymbol.fromAddress).map(String)
    };
    // Persist before returning to the faulting caller.  The process may exit
    // before Frida can deliver onLeave after the null result is dereferenced.
    send(this.before);
  },
  onLeave(retval) {
    if (!this.relevant) return;
    send({
      event: 'async-accessor-leave',
      pid: Process.id,
      threadId: this.threadId,
      return_value: retval.toString(),
      status_after_u32_10: safeU32(this.container.add(0x10)),
      result_after_ptr_28: safePtr(this.container.add(0x28))
    });
  }
});
send({
  event: 'armed', pid: Process.id, base: game.base.toString(),
  accessor: accessor.toString(), identity_lookup: identityLookup.toString()
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
