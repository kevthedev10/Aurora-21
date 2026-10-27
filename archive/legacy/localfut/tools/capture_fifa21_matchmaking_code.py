"""One-shot diagnostic: capture FIFA 21's unpacked matchmaking/UI code pages."""

from __future__ import annotations

import ctypes
import datetime as dt
import pathlib
import time
from ctypes import wintypes


TH32CS_SNAPPROCESS = 0x00000002
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010
PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260),
    ]


class MODULEENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD), ("th32ModuleID", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD), ("GlblcntUsage", wintypes.DWORD),
        ("ProccntUsage", wintypes.DWORD), ("modBaseAddr", ctypes.POINTER(ctypes.c_byte)),
        ("modBaseSize", wintypes.DWORD), ("hModule", wintypes.HMODULE),
        ("szModule", wintypes.WCHAR * 256), ("szExePath", wintypes.WCHAR * 260),
    ]


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
k32.OpenProcess.restype = wintypes.HANDLE


def find_process() -> int | None:
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == INVALID_HANDLE_VALUE:
        return None
    entry = PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        ok = k32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.lower() == "fifa21.exe":
                return int(entry.th32ProcessID)
            ok = k32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return None


def module_base(pid: int) -> int | None:
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == INVALID_HANDLE_VALUE:
        return None
    entry = MODULEENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    try:
        ok = k32.Module32FirstW(snap, ctypes.byref(entry))
        while ok:
            if entry.szModule.lower() == "fifa21.exe":
                return ctypes.cast(entry.modBaseAddr, ctypes.c_void_p).value
            ok = k32.Module32NextW(snap, ctypes.byref(entry))
    finally:
        k32.CloseHandle(snap)
    return None


def main() -> int:
    while (pid := find_process()) is None:
        time.sleep(0.25)
    # Let the title unpack its executable pages, but capture before matchmaking.
    time.sleep(8.0)
    base = module_base(pid)
    if base is None:
        return 2
    handle = k32.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, False, pid)
    if not handle:
        return 3
    ranges = (
        # Immediate caller of the stable +F91CCE crash and its surrounding
        # matchmaking/UI manager code.
        (0x008F0000, 0x00110000, "8f0000-a00000"),
        # Faulting function and async-result accessor.
        (0x00ED0000, 0x000D0000, "ed0000-fa0000"),
    )
    try:
        output_dir = pathlib.Path(__file__).resolve().parents[1] / "runtime" / "diagnostics"
        output_dir.mkdir(parents=True, exist_ok=True)
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        captured = 0
        for start_rva, size, label in ranges:
            buffer = ctypes.create_string_buffer(size)
            read = ctypes.c_size_t()
            ok = k32.ReadProcessMemory(
                handle, ctypes.c_void_p(base + start_rva), buffer, size,
                ctypes.byref(read),
            )
            if not ok or read.value == 0:
                continue
            output = output_dir / f"fifa21-unpacked-{label}-{stamp}.bin"
            output.write_bytes(buffer.raw[: read.value])
            (output.with_suffix(".txt")).write_text(
                f"pid={pid}\nbase={base:#x}\nstart_rva={start_rva:#x}\nbytes={read.value}\n",
                encoding="utf-8",
            )
            captured += 1
    finally:
        k32.CloseHandle(handle)
    if captured != len(ranges):
        return 4
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
