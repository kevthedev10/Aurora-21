"""Reframe Dost's bundled portrait from the original photo, using Python/Pillow.

Supply the original (pre-repair) DDS as the baseline. Output is a candidate
only: inspect the independently decoded DDS before copying it into the runtime.
The registered original head alpha is retained; only the lower-body silhouette
is extended from the photo. No generated pixels or other portraits are used.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).parent / "fifa21-currency-art"))
from bcn import encode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--photo", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True, help="Original p167961228.dds")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--install-dir", type=Path, help="Copy verified pair into this portrait folder, retaining originals")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    header = args.baseline.read_bytes()[:128]
    old = Image.open(args.baseline).convert("RGBA")
    assert old.size == (220, 256) and header[84:88] == b"DXT5"
    photo = Image.open(args.photo).convert("RGB").resize((800, 800), Image.Resampling.LANCZOS)

    # Registration of the existing head onto the linked photo, measured by RGB
    # fitting opaque head/neck pixels. Coordinates use an 800px reference photo.
    head_mask = old.getchannel("A").transform(
        (800, 800), Image.Transform.AFFINE,
        (.462, 0, -61.4, 0, .462, -17.4), Image.Resampling.BILINEAR)
    body_mask = Image.new("L", (800, 800))
    outline = [(352,250), (345,265), (330,273), (308,282), (293,293),
               (282,308), (277,322), (276,346), (281,382), (275,414),
               (273,473), (274,507), (285,525), (285,553), (283,600),
               (258,711), (250,756), (247,799), (670,799), (664,739),
               (644,652), (630,590), (620,550), (622,538), (641,522),
               (629,505), (619,484), (607,453), (593,416), (584,371),
               (577,341), (569,315), (555,294), (534,274), (511,258),
               (489,249), (472,243), (465,231), (458,221), (447,219),
               (439,213), (431,230), (405,248), (379,253)]
    ImageDraw.Draw(body_mask).polygon(outline, fill=255)
    body_mask = body_mask.filter(ImageFilter.GaussianBlur(.8))
    # Blend in an opaque part of the neck/shoulders, before the old torso fades.
    mix = np.clip((np.arange(800)[:, None]-270)/20, 0, 1)
    mask = np.rint(np.array(head_mask)*(1-mix) + np.array(body_mask)*mix).astype(np.uint8)
    cutout = photo.convert("RGBA")
    cutout.putalpha(Image.fromarray(mask))
    # Crown y~5; face centre x~114; larger head and torso extending through name.
    # User framing review: move 26px left from the first repair, preserving size/fade.
    scale, offset_x, offset_y = .55, -89., -23.
    portrait = cutout.transform((880, 1024), Image.Transform.AFFINE,
        (1/(4*scale), 0, -offset_x/scale, 0, 1/(4*scale), -offset_y/scale),
        Image.Resampling.BICUBIC).resize((220, 256), Image.Resampling.LANCZOS)
    pixels = np.array(portrait)
    t = np.clip((np.arange(256)-175)/75, 0, 1)
    fade = 1-t*t*(3-2*t)
    pixels[:, :, 3] = np.rint(pixels[:, :, 3]*fade[:, None]).astype(np.uint8)
    pixels[pixels[:, :, 3] == 0, :3] = 0
    portrait = Image.fromarray(pixels)
    candidate = args.output / "p167961228.dds"
    candidate.write_bytes(header + encode(portrait, b"DXT5"))
    decoded = Image.open(candidate).convert("RGBA")
    decoded.save(candidate.with_suffix(".png"))
    assert decoded.size == old.size and candidate.read_bytes()[:128] == header
    assert len(candidate.read_bytes()) == len(args.baseline.read_bytes())
    assert decoded.getchannel("A").getbbox()[3] >= 245
    assert np.array(decoded)[250:, :, 3].max() == 0
    # Preview uses the compressed DDS, not the uncompressed working image.
    preview = Image.new("RGB", (960, 580), "#262632")
    draw = ImageDraw.Draw(preview)
    for x, label, im in ((20, "Original DDS", old), (490, "Reframed DDS", decoded)):
        draw.text((x, 12), label, fill="white")
        preview.paste(im.resize((440, 512), Image.Resampling.NEAREST), (x, 42),
                      im.getchannel("A").resize((440, 512), Image.Resampling.NEAREST))
    preview.save(args.output / "comparison.png")
    report = dict(resourceId=167961228, sourceUrl="https://image.parool.nl/12658569/width/2480/bas-dost-heeft-gescoord-dost-spant-een-spoedarbitragezaak-aan",
                  sourceSha256=hashlib.sha256(args.photo.read_bytes()).hexdigest(),
                  baselineSha256=hashlib.sha256(args.baseline.read_bytes()).hexdigest(),
                  candidateSha256=hashlib.sha256(candidate.read_bytes()).hexdigest(),
                  dimensions=list(decoded.size), format="DXT5", horizontalShiftFromFirstRepair=-26,
                  oldAlphaBounds=old.getchannel("A").getbbox(),
                  newAlphaBounds=decoded.getchannel("A").getbbox(), headerPreserved=True,
                  verified="Pillow DDS decode; PNG equals decoded DDS; transparent bottom rows",
                  liveGameVerified=False)
    if args.install_dir:
        other_hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in args.install_dir.glob("p*.*") if p.stem != "p167961228"}
        for ext in ("dds", "png"):
            target = args.install_dir / f"p167961228.{ext}"
            backup = args.output / f"original-p167961228.{ext}"
            if not backup.exists():
                shutil.copy2(target, backup)
            shutil.copy2(candidate.with_suffix(f".{ext}"), target)
        assert np.array_equal(np.array(Image.open(args.install_dir / candidate.name).convert("RGBA")),
                              np.array(Image.open(args.install_dir / candidate.with_suffix(".png").name).convert("RGBA")))
        assert all(hashlib.sha256((args.install_dir / name).read_bytes()).hexdigest() == digest
                   for name, digest in other_hashes.items())
        shutil.copy2(args.photo, args.output / "source.jpg")
        report["installed"] = True
        report["otherPortraitsUnchanged"] = True
    (args.output / "verification.json").write_text(json.dumps(report, indent=2)+"\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
