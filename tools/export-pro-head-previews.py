"""Export lossless panel PNG previews of the bundled DDS portraits (requires Pillow).

Run from any directory: python tools/export-pro-head-previews.py
This is an offline asset tool; the C# server reads the resulting PNGs directly.
"""
from pathlib import Path
from PIL import Image


def main():
    folder = Path(__file__).resolve().parents[1] / "ultimateteam/runtime-source/ultimateteam21/custom-playerheads"
    for source in sorted(folder.glob("p*.dds")):
        with Image.open(source) as image:
            target = source.with_suffix(".png")
            image.convert("RGBA").save(target)
            print(f"{source.name} -> {target.name} ({image.width}x{image.height})")


if __name__ == "__main__":
    main()
