"""Locate native FIFA 21 references to Blaze GameManager type/command names.

This is intentionally a read-only helper for the retail executable.  It scans
RIP-relative operands in executable PE sections so the free Binary Ninja build
is not required to expose its unavailable headless API.
"""

from __future__ import annotations

import argparse
import mmap
import re
import struct
from pathlib import Path

import pefile


DEFAULT_NAMES = (
    b"NotifyJoiningPlayerInitiateConnections",
    b"NotifyGameSessionUpdated",
    b"Blaze::GameManager::NotifyPlatformHostInitialized",
    b"Blaze::GameManager::GameSessionUpdatedNotification",
    b"Blaze::GameManager::NotifyGameSetup",
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("exe", type=Path)
    args = parser.parse_args()

    pe = pefile.PE(str(args.exe), fast_load=True)
    image_base = int(pe.OPTIONAL_HEADER.ImageBase)
    targets: dict[int, str] = {}
    with args.exe.open("rb") as handle:
        mapped = mmap.mmap(handle.fileno(), 0, access=mmap.ACCESS_READ)
        for name in DEFAULT_NAMES:
            start = 0
            while (offset := mapped.find(name, start)) >= 0:
                rva = int(pe.get_rva_from_offset(offset))
                targets[image_base + rva] = name.decode("ascii")
                start = offset + 1

    print("targets:")
    for address, name in sorted(targets.items()):
        print(f"  {address:#x} {name}")

    print("references:")
    # All compiler references to these read-only names are expected to use
    # x64 RIP-relative LEA/MOV. Scanning those encodings is dramatically faster
    # than decoding Frostbite's entire ~96 MB text section instruction by
    # instruction.
    rip_relative = re.compile(
        rb"(?P<rex>[\x40-\x4f])?(?P<op>[\x8b\x8d])"
        rb"(?P<modrm>[\x05\x0d\x15\x1d\x25\x2d\x35\x3d])"
        rb"(?P<disp>.{4})",
        re.DOTALL,
    )
    for section in pe.sections:
        if not (int(section.Characteristics) & 0x20000000):
            continue
        code = section.get_data()
        section_va = image_base + int(section.VirtualAddress)
        for match in rip_relative.finditer(code):
            instruction_va = section_va + match.start()
            next_instruction = section_va + match.end()
            displacement = struct.unpack("<i", match.group("disp"))[0]
            resolved = next_instruction + displacement
            if resolved in targets:
                print(
                    f"  {instruction_va:#x} -> {resolved:#x} "
                    f"{targets[resolved]} :: {match.group(0).hex()}"
                )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
