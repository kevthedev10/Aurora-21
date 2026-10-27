> Historical reference, archived on 2026-10-01. Paths, policies, runtime descriptions and test status below describe their original checkpoint. Use the [current guides](../../../README.md) for supported instructions.

# FIFA 21 FUT auth source: one live diagnostic run

This file preserves the original debugger procedure. The supported launcher now supplies a fresh synthetic code
without a debugger, and separate live FUT/Nucleus HTTPS probes reached the hub. For the remaining Discord,
certificate, Blaze, UDP and two-player work, use [native-hosting-investigation.md](../../../ultimateteam/native-hosting-investigation.md#remaining-work-after-the-https-feasibility-runs).

This is a debugger-only experiment for the installed FIFA 21 build whose EXE SHA-256 is `44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494` and CardsDLL SHA-256 is `37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49`. The addresses below are module-relative offsets for those exact files. The supported launcher and normal users must not depend on this debugger procedure.

## Preparation

1. Start Project 21 through `RUN-FIFA21.cmd` and wait at FIFA's front end **before opening FUT**. Keep the game's existing auth bridge active. Do not enable raw packet capture or change the club database.
2. Confirm the installed EXE and CardsDLL hashes with `Get-FileHash -Algorithm SHA256` in PowerShell. Abort if either differs. Attach a Windows x64 debugger to the FIFA process associated with that game path. Verify the loaded module bases and that the bytes at the two breakpoints match the instructions below. An address alone is insufficient when the build differs.
3. Set these two execution breakpoints, then continue into FUT:

| Module-relative breakpoint | Instruction at pause | Value to inspect |
| --- | --- | --- |
| `FIFA21.exe+0xB790ED` | `jmp 0x140b79145`, immediately after the direct call to `FUN_14102cbf0` | `EAX` result, `RBX` returned string pointer, `RSI` byte length; `R14+0x18` points to the job's NUL-terminated client ID. |
| `CardsDLL_Win64_retail.dll+0x3CD1C8` | `mov r8,-1`, immediately after the `FOS-SERVER` virtual getter | `RAX` points to the getter's NUL-terminated string. |

The absolute addresses in Ghidra assume bases `0x140000000` and `0x180000000`. Use the module-relative offsets in the debugger because Windows may relocate a module. If the first breakpoint hits, `EAX=0`, `RBX` is nonzero and `RSI` is between 1 and 256, hash exactly `RSI` bytes at `RBX`. If the CardsDLL breakpoint hits and `RAX` is nonzero, hash its bytes through the first NUL. Record the order of hits and approximate time; a getter may run more than once.

## Hash the paused values

From an **elevated Windows PowerShell** in the repository root, while the debugger has FIFA paused, run the read-only probe. Replace the example pointers and length with the register values at the corresponding breakpoint:

```powershell
$game = Get-Process FIFA21
& .\localfut\Hash-FIFA21-ProcessMemory.ps1 -ProcessId $game.Id -GamePath $game.Path -Address '0x0000012345678900' -ByteCount 0x28
& .\localfut\Hash-FIFA21-ProcessMemory.ps1 -ProcessId $game.Id -GamePath $game.Path -Address '0x000001234567AB00' -NullTerminated
```

Run only the command for the breakpoint currently paused. The first address comes from `RBX`, with `RSI` as `ByteCount`; the second comes from `RAX`. The probe opens the verified FIFA process for reading, returns only `len` and SHA-256, and does not write the credential to disk. If the client ID matters, inspect the text at `R14+0x18` in the debugger; do not include any returned code or raw memory dump in a handoff.

Resume the game promptly after each observation. After `/ut/auth` arrives, find the `AUTH EVIDENCE http POST /ut/auth` line in the current server log (normally `%LOCALAPPDATA%\FIFA21LocalFUT\logs\localfut15.log`; use the server's reported state/log path if overridden). Its `$.authcode=other(len=...,sha256=...)` uses the first 12 lowercase hex characters of SHA-256 on the parsed string's UTF-8 bytes. For an ASCII auth code, compare that prefix and length with each probe result. Do not share the raw code or an unredacted HTTP trace.

## Interpret the result

- A direct-call hash matching `/ut/auth` confirms that this job returned the submitted code in that run; its client ID names the request to investigate. A matching `FOS-SERVER` getter hash connects the CardsDLL serializer to the same value.
- A direct-call hit with a different hash means the retriever ran but this particular output was not the submitted code. Check other hits and the CardsDLL getter; do not conclude that the path is irrelevant from one mismatch.
- No direct-call hit after reaching the FUT hub means this call path was not observed in that run. Confirm the breakpoint and build before drawing a stronger conclusion.
- Matching hashes prove the source relationship for that run only. A second run and a different account or session are needed to test whether the value changes. A controlled in-memory substitution plus server validation would then test whether a player-specific replacement works.

Record the game and CardsDLL hashes, test time, LSX mode, whether both breakpoints hit, the first call's `EAX` and client ID, each `len`/hash prefix, the `/ut/auth` hash prefix, and whether the FUT hub loaded. Do not claim HTTPS, authenticated follow-up FUT requests, or playable matches from this test.
