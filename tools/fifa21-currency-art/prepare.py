"""Prepare reviewed FIFA Points texture replacements from PNG or SVG artwork.

Development utility only: requires Pillow and NumPy (DDS DXT3/DXT5 encoding).
Original extracts and artist inputs are read-only. Runtime mod creation is C#.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import shutil
import struct

from PIL import Image, ImageDraw, ImageFilter
from render_compact import render
from bcn import encode as encode_bcn


PREFIX = "data/ui/external/ion_fut/"
# Native identities reviewed against the 2026-10-04 extraction and contact sheets.
# The tutorial is an atlas: only the points symbol, above the shared lower stripe,
# is replaced. Preserve the neighbouring FUT coins and all other pixels/blocks.
TARGETS = [
    ("store-large", "imgAssets/store/icon/icon_fifaPoints_lg.dds", None, "single", None),
    ("store-small", "imgAssets/store/icon/icon_fifaPoints_sm.dds", None, "single", None),
    ("header", "components/PlayerIdentity.big", "80", "single", None),
    ("pack-preview", "screens/futPackPreviewScreen.big", "33", "single", None),
    ("purchase-large", "components/FUTControls.big", "114", "wide", None),
    ("purchase-glow", "components/FUTControls.big", "116", "glow", None),
    ("draft-tile", "imgAssets/GameHub/GameHub_DraftPoints.dds", None, "wide", None),
    ("live-message", "imgAssets/liveMessage/liveMessage_5.dds", None, "wide", None),
    ("points-product-single", "imgAssets/pack/background/background_99000.dds", None, "single", None),
    ("points-product-double", "imgAssets/pack/background/background_99001.dds", None, "wide", None),
    ("points-product-triple", "imgAssets/pack/background/background_99002.dds", None, "wide", None),
    ("tutorial", "artAssets/ChalkTutorials/tutorial16.big", "13", "wide", (600, 0, 904, 180)),
]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def native_member(data, member):
    if member is None:
        return data
    if data[:4] not in (b"BIGF", b"BIG4"):
        raise ValueError("Unexpected BIG signature")
    count, end = struct.unpack_from(">II", data, 8)
    pos = 16
    matches = []
    for _ in range(count):
        offset, size = struct.unpack_from(">II", data, pos)
        stop = data.index(b"\0", pos + 8, end)
        name = data[pos + 8:stop].decode("ascii")
        pos = stop + 1
        if name == member:
            if offset < end or offset + size > len(data):
                raise ValueError("Invalid BIG member bounds")
            matches.append(data[offset:offset + size])
    if len(matches) != 1 or not matches[0].startswith(b"DDS "):
        raise ValueError("Expected one uncompressed DDS member")
    return matches[0]


def fit(art, size):
    # Resize premultiplied pixels to avoid dark/white fringes from transparent RGB.
    art = art.convert("RGBa")
    art.thumbnail(size, Image.Resampling.LANCZOS)
    result = Image.new("RGBA", size)
    result.alpha_composite(art.convert("RGBA"), ((size[0] - art.width) // 2, (size[1] - art.height) // 2))
    return result


def encode(image, original, rectangle=None):
    if original[:4] != b"DDS " or struct.unpack_from("<I", original, 4)[0] != 124:
        raise ValueError("Unexpected DDS header")
    if struct.unpack_from("<I", original, 28)[0] not in (0, 1):
        raise ValueError("Review mipmapped textures separately")
    height, width = struct.unpack_from("<II", original, 12)
    if image.size != (width, height):
        raise ValueError("Texture dimensions changed")
    fourcc = original[84:88]
    if fourcc in (b"DXT3", b"DXT5"):
        encoded = encode_bcn(image, fourcc)
        payload_length = ((width + 3) // 4) * ((height + 3) // 4) * 16
        if len(encoded) != payload_length or len(original) < 128 + payload_length:
            raise ValueError("Unexpected compressed DDS payload length")
        # The standalone 32px Store source carries eight opaque trailing bytes.
        # They are not another mip level; retain every byte after the top image.
        encoded += original[128 + payload_length:]
        if rectangle:
            if any(value % 4 for value in rectangle):
                raise ValueError("Atlas replacement must align to compression blocks")
            body = bytearray(original[128:])
            left, top, right, bottom = rectangle
            for y in range(top // 4, bottom // 4):
                start = (y * (width // 4) + left // 4) * 16
                stop = (y * (width // 4) + right // 4) * 16
                body[start:stop] = encoded[start:stop]
            encoded = bytes(body)
    elif fourcc == b"\0\0\0\0":
        bits = struct.unpack_from("<I", original, 88)[0]
        masks = struct.unpack_from("<4I", original, 92)
        if bits != 32 or set(masks) != {0xff, 0xff00, 0xff0000, 0xff000000}:
            raise ValueError("Unreviewed uncompressed DDS channel masks")
        channels = {mask: band for mask, band in zip(masks, image.split())}
        encoded = Image.merge("RGBA", [channels[0xff << (8 * n)] for n in range(4)]).tobytes()
    else:
        raise ValueError("Unreviewed DDS compression: " + repr(fourcc))
    modified = original[:128] + encoded
    if len(modified) != len(original):
        raise ValueError(f"DDS size changed: {image.size}, {fourcc!r}, original {len(original)}, encoded {len(modified)}")
    with Image.open(io.BytesIO(modified)) as check:
        check.load()
        if check.size != image.size or check.convert("RGBA").getchannel("A").getextrema()[0] != 0:
            raise ValueError("DDS dimensions/transparency failed roundtrip")
    return modified


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native", type=Path, required=True)
    parser.add_argument("--single", type=Path, required=True)
    parser.add_argument("--wide", type=Path, required=True)
    parser.add_argument("--compact", type=Path, help="Compact SVG or PNG for native icons up to 64px")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads((args.native / "native-manifest.json").read_text())
    source_hashes = {i["name"]: i["sha256"] for i in manifest["assets"]}
    artwork = {}
    inputs = [("single", args.single), ("wide", args.wide)]
    if args.compact:
        inputs.append(("compact", args.compact))
    for role, path in inputs:
        with render(path, 1024) if path.suffix.lower() == '.svg' else Image.open(path) as image:
            image = image.convert("RGBA")
            box = image.getchannel("A").getbbox()
            if not box or image.getchannel("A").getextrema()[0] != 0:
                raise ValueError("Artist image must contain transparent pixels")
            artwork[role] = image.crop(box)
    args.output.mkdir(parents=True, exist_ok=True)
    args.report.mkdir(parents=True, exist_ok=True)
    source_dir = args.output / "source"
    source_dir.mkdir(exist_ok=True)
    for path, name in ((args.single, "aurora-points-single" + args.single.suffix), (args.wide, "aurora-points-wide" + args.wide.suffix)):
        if path.resolve() != (source_dir / name).resolve():
            shutil.copyfile(path, source_dir / name)
    replacements, reports = [], []
    for label, suffix, member, role, rectangle in TARGETS:
        name = PREFIX + suffix
        asset = (args.native / "raw" / name).read_bytes()
        if sha(asset) != source_hashes[name]:
            raise ValueError("Native extraction differs: " + name)
        original = native_member(asset, member)
        with Image.open(io.BytesIO(original)) as image:
            before = image.convert("RGBA")
        bounds = rectangle or before.getchannel("A").getbbox()
        left, top, right, bottom = bounds
        selected = "compact" if args.compact and role == "single" and max(before.size) <= 64 else "single" if role == "single" else "wide"
        icon = fit(artwork[selected], (right - left, bottom - top))
        if role == "glow":
            icon = icon.filter(ImageFilter.GaussianBlur(2))
            icon.putalpha(icon.getchannel("A").point(lambda a: round(a * 0.65)))
        after = before.copy() if rectangle else Image.new("RGBA", before.size)
        after.paste((0, 0, 0, 0), bounds)
        after.alpha_composite(icon, (left, top))
        modified = encode(after, original, rectangle)
        file = label + ".dds"
        (args.output / file).write_bytes(modified)
        after.save(args.report / (label + "-prepared.png"))
        with Image.open(io.BytesIO(modified)) as check:
            check.convert("RGBA").save(args.report / (label + "-dds.png"))
        if rectangle:
            with Image.open(io.BytesIO(modified)) as check:
                check = check.convert("RGBA")
                # Mask our rectangle and compare every other decoded atlas pixel.
                a, b = before.copy(), check.copy()
                a.paste((0, 0, 0, 0), rectangle)
                b.paste((0, 0, 0, 0), rectangle)
                if a.tobytes() != b.tobytes():
                    raise ValueError("Unrelated atlas pixels changed")
        replacements.append({"name": name, "member": member, "sourceAssetSha256": sha(asset),
            "sourceBitmapSha256": sha(original), "replacementFile": file, "replacementSha256": sha(modified)})
        reports.append({"label": label, "name": name, "member": member, "role": role,
            "dimensions": list(before.size), "bounds": list(bounds), "format": original[84:88].decode("ascii"), "artwork": selected,
            "preservedHeader": True, "preservedLength": True, "atlasPixelsPreserved": bool(rectangle)})
    (args.output / "artwork.json").write_text(json.dumps(replacements, indent=2) + "\n")
    (args.report / "preparation.json").write_text(json.dumps({"gameProfile": manifest["gameProfile"],
        "inputs": {role: sha(path.read_bytes()) for role, path in inputs},
        "textures": reports}, indent=2) + "\n")
    sheet = Image.new("RGB", (1000, 630), (65, 65, 65))
    draw = ImageDraw.Draw(sheet)
    for number, (label, *_) in enumerate(TARGETS):
        with Image.open(args.report / (label + "-dds.png")) as preview:
            preview = preview.convert("RGBA")
            preview.thumbnail((240, 175), Image.Resampling.LANCZOS)
            x, y = number % 4 * 250, number // 4 * 210
            sheet.paste(preview, (x + (240 - preview.width) // 2, y), preview)
            draw.text((x, y + 180), label, fill="white")
    sheet.save(args.report / "replacement-preview.png")
    print(f"Prepared {len(replacements)} textures in {len(set(i['name'] for i in replacements))} native assets.")


if __name__ == "__main__":
    main()
