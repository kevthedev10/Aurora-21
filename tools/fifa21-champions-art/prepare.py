"""Prepare Aurora Champions logos for the reviewed FIFA 21 UI textures.

This offline utility preserves each native DDS header, dimensions and length.
Rank crests and Champions Points icons have separate meanings and are
deliberately outside this logo inventory.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import sys

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageEnhance, ImageOps

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fifa21-currency-art"))
from prepare import encode, native_member  # noqa: E402


PREFIX = "data/ui/external/ion_fut/imgAssets/"
TARGETS = [
    ("champions-logo", "champions/logo/logo_champions.dds", "colour"),
    ("champions-highlight", "champions/logo/logo_tint_hl.dds", "dim"),
    ("champions-unhighlight", "champions/logo/logo_tint_unhl.dds", "colour"),
    ("champions-weekend", "champions/logo/logo_weekend.dds", "colour"),
    ("champions-sellscreen", "champions/sellscreen/Logo.dds", "colour"),
    ("champions-watermark", "champions/sellscreen/Logo_Watermark.dds", "watermark"),
    ("champions-rivals", "rivals/logo/logo_champs.dds", "colour"),
    ("champions-rivals-flat", "rivals/logo/logo_champs_flat.dds", "flat"),
    ("champions-weekendleague", "rivals/logo/logo_weekendleague.dds", "colour"),
    ("champions-weekendleague-small", "rivals/logo/logo_WL_small.dds", "colour"),
    ("champions-ribbon", "GameHub/Gamehub_RibbonChampions.dds", "ribbon"),
    ("champions-rivals-intro", "components/FutRivalsThresholdOverlay.big", "intro"),
]
COMPOSITES = [
    ("champions-hub-prize", "GameHub/GameHub_ChampPrize.dds", "prize"),
    ("champions-hub-rewards", "GameHub/GameHub_ChampsRewards.dds", "prize"),
    ("champions-hub-channel-active", "GameHub/GameHub_ChampionsChannel_Active.dds", "channel"),
    ("champions-hub-channel-inactive", "GameHub/GameHub_ChampionsChannel_Inactive.dds", "channel"),
    ("champions-hub-idle-active", "GameHub/GameHub_ChampionsInactive_Active.dds", "idle"),
    ("champions-hub-idle-inactive", "GameHub/GameHub_ChampionsInactive_Inactive.dds", "idle"),
    ("champions-sellscreen-beat2", "champions/sellscreen/Beat2.dds", "sellscreen"),
    ("champions-hub-play", "GameHub/GameHub_Play.dds", "play"),
    ("champions-hub-leaderboard", "GameHub/GameHub_LeaderboardImage.dds", "leaderboard"),
    *[(f"champions-rivals-sellscreen-{index}", f"rivals/sellscreen/sellscreen_hub_{index}.dds", "rivals-sellscreen")
      for index in range(4)],
]
# Measured on the 2026-10-04 extraction: the five native mode icons in
# GameHub_Play are this one flat grey, and the native hub ribbon is the 256px
# Champions logo at full size, moved 32px up from its y=6 bounds.
PLAY_GREY = (126, 125, 131)
RIBBON_LOGO_HEIGHT = 242
RIBBON_TOP = -26
MEDAL_FILL = (0, 0, 0, 255)
MEDAL_RING = (223, 192, 102, 255)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def variant(art, style, alpha_max):
    result = art.copy()
    alpha = result.getchannel("A")
    if style == "dim":
        rgb = ImageEnhance.Brightness(result.convert("RGB")).enhance(0.42)
        result = rgb.convert("RGBA")
    elif style == "flat":
        # This is a large grey backdrop behind the selected Champions tile.
        grey = ImageOps.grayscale(result)
        result = ImageOps.colorize(grey, "#514d59", "#c5bdcc").convert("RGBA")
        alpha = alpha.point(lambda value: round(value * 0.58))
    elif style == "intro":
        pass
    elif style == "ribbon":
        # Native is the full-colour logo, faded to the tile's watermark alpha.
        alpha = alpha.point(lambda value: round(value * alpha_max / 255))
    elif style == "watermark":
        result = Image.new("RGBA", art.size, "#f4eaf0")
        alpha = alpha.point(lambda value: round(value * alpha_max / 255))
    result.putalpha(alpha)
    return result


def fit_logo(art, size):
    """Fit the source to the reviewed native bounds, including larger textures."""
    width, height = size
    ratio = min(width / art.width, height / art.height)
    scaled = art.convert("RGBa").resize((round(art.width * ratio), round(art.height * ratio)), Image.Resampling.LANCZOS)
    result = Image.new("RGBA", size)
    result.alpha_composite(scaled.convert("RGBA"), ((width - scaled.width) // 2, (height - scaled.height) // 2))
    return result


def bleed(image, rounds=8):
    """Spread edge colours into transparent texels.

    The game samples these straight-alpha textures bilinearly and magnifies
    them (2x at 1440p), so a transparent neighbour's colour tints every edge.
    """
    pixels = np.asarray(image.convert("RGBA")).astype(float)
    rgb, alpha = pixels[..., :3].copy(), pixels[..., 3]
    known = alpha > 0
    for _ in range(rounds):
        total, count = np.zeros_like(rgb), np.zeros(alpha.shape)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            # np.roll wraps around; the logos never touch two opposite edges.
            near = np.roll(known, (dy, dx), (0, 1))
            total += np.roll(rgb, (dy, dx), (0, 1)) * near[..., None]
            count += near
        new = ~known & (count > 0)
        rgb[new] = total[new] / count[new][:, None]
        known |= new
    return Image.fromarray(np.dstack((rgb, alpha)).round().astype(np.uint8), "RGBA")


def ribbon(art, size, left):
    """Place the faded logo as native does: full size, cut off by the tile."""
    width = round(art.width * RIBBON_LOGO_HEIGHT / art.height)
    scaled = art.convert("RGBa").resize((width, RIBBON_LOGO_HEIGHT), Image.Resampling.LANCZOS).convert("RGBA")
    result = Image.new("RGBA", size)
    result.alpha_composite(scaled.crop((0, -RIBBON_TOP, size[0] - left, size[1] - RIBBON_TOP)), (left, 0))
    return result


def play_icon(art, size):
    """Cut the logo out of flat grey, like the other five Play panel icons."""
    # Gold and cream stay; the red shield body and banner lettering open up.
    light = ImageOps.grayscale(art.convert("RGB")).point(
        lambda value: max(0, min(255, round((value - 96) * 255 / 48))))
    mask = ImageChops.multiply(art.getchannel("A"), light)
    ratio = min(size[0] / art.width, size[1] / art.height)
    mask = mask.resize((round(art.width * ratio), round(art.height * ratio)), Image.Resampling.LANCZOS)
    alpha = Image.new("L", size)
    alpha.paste(mask, ((size[0] - mask.width) // 2, (size[1] - mask.height) // 2))
    result = Image.new("RGBA", size, PLAY_GREY)
    result.putalpha(alpha)
    return result


def medal(result, center, radius):
    """Draw the smooth-edged disc that covers a native channel medal."""
    scale, side = 4, 2 * radius + 2
    disc = Image.new("RGBA", (side * scale, side * scale))
    ImageDraw.Draw(disc).ellipse((scale, scale, (side - 1) * scale - 1, (side - 1) * scale - 1),
                                 fill=MEDAL_FILL, outline=MEDAL_RING, width=2 * scale)
    disc = disc.convert("RGBa").resize((side, side), Image.Resampling.LANCZOS).convert("RGBA")
    result.alpha_composite(disc, (center[0] - radius - 1, center[1] - radius - 1))


def replace_hub_badge(before, art, style):
    result = before.copy()
    if style == "play":
        if result.size != (1024, 576):
            raise ValueError("Unexpected Play panel dimensions")
        block_box = (268, 296, 392, 408)
        # Transparent texels keep the icon grey so magnified edges stay grey.
        result.paste(PLAY_GREY + (0,), block_box)
        # The native Champions icon occupies 109x103 from (273, 298).
        result.paste(play_icon(art, (109, 103)), (273, 298))
    elif style == "leaderboard":
        if result.size != (340, 192):
            raise ValueError("Unexpected leaderboard panel dimensions")
        block_box = (236, 48, 312, 124)
        # A small red underlay hides the old FUT crest while leaving the pack,
        # coins, and adjacent player item intact.
        ImageDraw.Draw(result).ellipse((238, 49, 310, 121), fill=(112, 4, 7, 255))
        badge = fit_logo(art, (69, 72))
        result.alpha_composite(badge, (240, 49))
    elif style == "rivals-sellscreen":
        if result.size != (1872, 376):
            raise ValueError("Unexpected Rivals sell screen dimensions")
        block_box = (1400, 36, 1684, 324)
        result.paste((0, 0, 0, 0), block_box)
        badge = fit_logo(art, (276, 276))
        result.alpha_composite(badge, (1404, 40))
    elif style == "sellscreen":
        if result.size != (848, 716):
            raise ValueError("Unexpected Champions sell screen dimensions")
        badge = fit_logo(art, (190, 190))
        result.alpha_composite(badge, (537, 204))
        block_box = (536, 204, 728, 396)
    elif style == "prize":
        if result.size != (1024, 576):
            raise ValueError("Unexpected Champions prize panel dimensions")
        badge = fit_logo(art, (198, 193))
        result.alpha_composite(badge, (25, 328))
        block_box = (24, 328, 224, 524)
    else:
        if style == "channel":
            if result.size != (464, 200):
                raise ValueError("Unexpected Champions channel panel dimensions")
            center, radius, size = (197, 135), 48, 90
            block_box = (148, 84, 248, 188)
        else:
            if result.size != (1456, 412):
                raise ValueError("Unexpected Champions idle panel dimensions")
            center, radius, size = (614, 287), 105, 196
            block_box = (508, 180, 724, 396)
        # Cover the whole native medal, including its EA SPORTS / CHANNEL ring.
        cx, cy = center
        medal(result, center, radius)
        badge = fit_logo(art, (size, size))
        result.alpha_composite(badge, (cx-badge.width//2, cy-badge.height//2))
    return result, block_box


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    native = json.loads((args.native / "native-manifest.json").read_text())
    hashes = {entry["name"]: entry["sha256"] for entry in native["assets"]}
    with Image.open(args.source) as image:
        source = image.convert("RGBA")
    bounds = source.getchannel("A").getbbox()
    if bounds is None or source.getchannel("A").getextrema() != (0, 255):
        raise ValueError("Aurora Champions PNG must have a transparent background and opaque artwork")
    art = source.crop(bounds)
    args.output.mkdir(parents=True, exist_ok=True)
    args.report.mkdir(parents=True, exist_ok=True)
    edits, details = [], []
    for label, suffix, style in TARGETS:
        name = (PREFIX if style != "intro" else "data/ui/external/ion_fut/") + suffix
        original = (args.native / "raw" / name).read_bytes()
        if sha(original) != hashes[name]:
            raise ValueError("Native extraction hash mismatch: " + name)
        member = "116" if style == "intro" else None
        bitmap = native_member(original, member)
        with Image.open(io.BytesIO(bitmap)) as image:
            before = image.convert("RGBA")
        box = before.getchannel("A").getbbox()
        if box is None:
            raise ValueError("Original logo has no visible pixels: " + name)
        alpha_max = before.getchannel("A").getextrema()[1]
        if style == "ribbon":
            after = ribbon(variant(art, style, alpha_max), before.size, box[0])
        else:
            graphic = fit_logo(variant(art, style, alpha_max), (box[2] - box[0], box[3] - box[1]))
            after = Image.new("RGBA", before.size)
            after.alpha_composite(graphic, box[:2])
        modified = encode(bleed(after), bitmap)
        filename = label + ".dds"
        (args.output / filename).write_bytes(modified)
        with Image.open(io.BytesIO(modified)) as image:
            image.convert("RGBA").save(args.report / (label + ".png"))
        edits.append({"name": name, "member": member, "sourceAssetSha256": sha(original),
                      "sourceBitmapSha256": sha(bitmap), "replacementFile": filename,
                      "replacementSha256": sha(modified)})
        details.append({"name": name, "dimensions": list(before.size), "bounds": list(box),
                        "style": style, "sourceSha256": sha(original), "replacementSha256": sha(modified)})
    for label, suffix, style in COMPOSITES:
        name = PREFIX + suffix
        original = (args.native / "raw" / name).read_bytes()
        if sha(original) != hashes[name]:
            raise ValueError("Native extraction hash mismatch: " + name)
        with Image.open(io.BytesIO(original)) as image:
            before = image.convert("RGBA")
        after, block_box = replace_hub_badge(before, art, style)
        modified = encode(after, original, rectangle=block_box)
        filename = label + ".dds"
        (args.output / filename).write_bytes(modified)
        with Image.open(io.BytesIO(modified)) as image:
            image.convert("RGBA").save(args.report / (label + ".png"))
        edits.append({"name": name, "member": None, "sourceAssetSha256": sha(original),
                      "sourceBitmapSha256": sha(original), "replacementFile": filename,
                      "replacementSha256": sha(modified)})
        details.append({"name": name, "dimensions": list(before.size), "style": style,
                        "modifiedBlocks": list(block_box),
                        "sourceSha256": sha(original), "replacementSha256": sha(modified)})
    (args.output / "artwork.json").write_text(json.dumps(edits, indent=2) + "\n")
    (args.report / "preparation.json").write_text(json.dumps({
        "sourcePngSha256": sha(args.source.read_bytes()), "gameProfile": native["gameProfile"],
        "textures": details}, indent=2) + "\n")
    print(f"Prepared {len(edits)} Aurora Champions logo DDS textures")


if __name__ == "__main__":
    main()
