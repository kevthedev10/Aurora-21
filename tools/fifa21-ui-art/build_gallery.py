#!/usr/bin/env python3
"""Decode native UI bitmaps and create a portable, offline gallery. Requires Pillow."""
import argparse
import collections
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import struct
from datetime import datetime, timezone

from PIL import Image

SHARED = {
    "background", "banners", "tiles", "tileIcons", "tileShards", "common", "components",
    "Controllers", "settingsImg", "settingsIcn", "separatorLine", "online", "futOverlay",
    "seasonsDivisionCrests", "trophy", "trophy256x256", "balls", "boots", "stadium", "smheads",
    "playerTraitsLarge", "playerTraitsSmall", "playerInstructionIcon", "flags512x512", "league",
}


def select_assets(inventory):
    result = []
    for asset in inventory:
        name = asset["Name"]
        low = name.lower()
        parts = name.split("/")
        dedicated = "/ion_fut/" in low
        shared = name.startswith("data/ui/imgAssets/") and len(parts) > 3 and parts[3] in SHARED
        extra = name.startswith("data/ui/") and asset["Type"] == "BIG" and any(
            word in low for word in ("fut", "pause", "mainmenu", "mainhub"))
        if asset["Type"] in ("DDS", "PNG", "BIG") and (dedicated or shared or extra):
            result.append(name)
    return sorted(set(result))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def refpack(data):
    """EA 0x10FB stream, with bounded literals and overlapping back references."""
    if not data.startswith(b"\x10\xfb"):
        return data
    if len(data) < 6:
        raise ValueError("Truncated RefPack header")
    expected = int.from_bytes(data[2:5], "big")
    if expected > 128 * 1024 * 1024:
        raise ValueError("RefPack output exceeds the export limit")
    pos = 5
    out = bytearray()

    def take(count):
        nonlocal pos
        if pos + count > len(data):
            raise ValueError("Truncated RefPack command")
        part = data[pos:pos + count]
        pos += count
        return part

    while pos < len(data):
        code = take(1)[0]
        length = distance = 0
        if code < 0x80:
            b = take(1)[0]
            literals = code & 3
            length = ((code >> 2) & 7) + 3
            distance = ((code & 0x60) << 3) + b + 1
        elif code < 0xC0:
            a, b = take(2)
            literals = a >> 6
            length = (code & 0x3F) + 4
            distance = ((a & 0x3F) << 8) + b + 1
        elif code < 0xE0:
            a, b, c = take(3)
            literals = code & 3
            length = ((code & 0x0C) << 6) + c + 5
            distance = ((code & 0x10) << 12) + (a << 8) + b + 1
        else:
            literals = (code & 3) if code >= 0xFC else ((code & 0x1F) << 2) + 4
        if len(out) + literals + length > expected:
            raise ValueError("RefPack command exceeds declared output")
        out.extend(take(literals))
        if length:
            if distance > len(out):
                raise ValueError("RefPack reference precedes output")
            for _ in range(length):
                out.append(out[-distance])
        if code >= 0xFC:
            break
    if len(out) != expected:
        raise ValueError(f"RefPack length mismatch: {len(out)} != {expected}")
    return bytes(out)


def big_members(data):
    """BIGF/BIG4 index observed in the shipped FIFA21 UI assets; member identity is retained."""
    if data[:4] not in (b"BIGF", b"BIG4") or len(data) < 16:
        raise ValueError("Unsupported UI archive signature: " + repr(data[:4]))
    count, header_end = struct.unpack_from(">II", data, 8)
    if count > 100000 or header_end > len(data) or header_end < 16:
        raise ValueError("Invalid BIG index bounds")
    pos = 16
    members = []
    for _ in range(count):
        if pos + 8 > header_end:
            raise ValueError("Truncated BIG index")
        offset, size = struct.unpack_from(">II", data, pos)
        pos += 8
        end = data.find(b"\0", pos, header_end)
        if end < 0:
            raise ValueError("Unterminated BIG member name")
        name = data[pos:end].decode("utf-8", errors="strict")
        pos = end + 1
        if size:
            if offset < header_end or offset + size > len(data):
                raise ValueError("BIG member outside archive")
            members.append((name, refpack(data[offset:offset + size])))
    return members


def area(name):
    low = name.lower()
    if "/tiles/mainhub/" in low and "fut" in Path(name).stem.lower(): return "03-main-menu-fut-tile"
    if "/ion_fut/tiles/" in low: return "01-hub-tiles"
    if "gamehub" in low or "metropanel" in low or "hubframe" in low: return "02-hub-frames-and-panels"
    if "pause" in low: return "22-match-pause-menu"
    if "banner" in low or "livemsg" in low or "message" in low: return "04-banners-and-messages"
    if "itembgs" in low: return "07-card-backgrounds"
    if "cards_glow" in low or "cards/icon" in low or "highlight" in low: return "09-card-icons-and-glows"
    if "/cards/tifo/" in low or "/cards/theme/" in low or any(t in low for t in ("seatcolor", "pitchprop", "pyrotechnic", "stadiumcolor", "goalpackage", "mowpattern", "linecolor")):
        return "23-stadium-customisation"
    if "/cards/" in low or "/myclub/card/" in low: return "08-item-cards"
    if "pack" in low or "store" in low or "cardshop" in low: return "06-store-and-packs"
    if "sbc" in low: return "24-squad-building-challenges"
    if "objective" in low or "seasonalcampaign" in low: return "25-objectives-and-season-progress"
    if "rivals" in low: return "26-division-rivals"
    if "champions" in low or "weekendleague" in low: return "27-fut-champions"
    if "squadbattle" in low: return "28-squad-battles"
    if "draft" in low: return "29-fut-draft"
    if "leaderboard" in low: return "17-leaderboards"
    if "totw" in low or "teamoftheweek" in low: return "18-team-of-the-week"
    if "auction" in low or "transfer" in low: return "15-transfers"
    if "myclub" in low or "unassigned" in low or "newitem" in low: return "16-my-club"
    if "squad" in low or "formation" in low: return "14-squads"
    if "season" in low or "divisioncrest" in low: return "13-seasons"
    if "trophy" in low or "reward" in low: return "12-trophies-and-rewards"
    if "smheads" in low: return "11-staff-portraits"
    if "houserule" in low or "friendly" in low: return "30-friendlies-and-house-rules"
    if "online" in low or "friend" in low: return "19-online-and-friends"
    if "background" in low or "backdrop" in low: return "05-backgrounds"
    if any(word in low for word in ("item", "card", "trade", "radialmenu")): return "10-card-components"
    if "/ion_fut/" in low: return "20-login-settings-and-other-fut"
    return "31-shared-ui-and-supporting-art"


def output_name(source, member):
    parts = source.split("/")
    if "ion_fut" in parts:
        parts = parts[parts.index("ion_fut") + 1:]
    elif parts[:2] == ["data", "ui"]:
        parts = parts[2:]
    name = "__".join(parts)
    name = re.sub(r"\.(dds|png|big)$", "", name, flags=re.I)
    if member is not None: name += "__texture_" + member
    name = re.sub(r'[^\w.\-]+', '_', name)
    # Leave room for the output root on Windows; stable identity prevents collisions after shortening.
    if len(name) > 130: name = name[:110] + "__" + digest((source + "#" + str(member)).encode())[:16]
    return name + ".png"


def build(native, output, only_sources=None):
    manifest = json.loads((native / "native-manifest.json").read_text())
    if manifest["game"] != "FIFA21" or manifest["schema"] != 1:
        raise ValueError("Unsupported native manifest")
    output.mkdir(parents=True, exist_ok=True)
    images, failures, nonbitmap = [], [], []
    paths = set()
    native_dds = {asset["name"].lower() for asset in manifest["assets"] if asset["type"] == "DDS"}
    selected = [asset for asset in manifest["assets"] if only_sources is None or asset["name"] in only_sources]
    if only_sources is not None and (not (output / "index.json").exists() or len(selected) != len(only_sources)):
        raise ValueError("A repair requires an existing index and exact native source names")
    for number, asset in enumerate(selected, 1):
        name = asset["name"]
        try:
            source_path = (native / "raw" / name).resolve()
            if not source_path.is_relative_to((native / "raw").resolve()): raise ValueError("Unsafe native path")
            raw = source_path.read_bytes()
            if digest(raw) != asset["sha256"]: raise ValueError("Native asset SHA256 mismatch")
            if asset["type"] == "BIG":
                candidates = [(member, data) for member, data in big_members(raw)
                              if data.startswith((b"DDS ", b"\x89PNG\r\n\x1a\n"))]
                if not candidates: nonbitmap.append({"source": name, "reason": "No DDS/PNG bitmap members; vector, layout or runtime content may remain."})
            else:
                candidates = [(None, raw)]
            for member, data in candidates:
                try:
                    with Image.open(io.BytesIO(data)) as bitmap:
                        bitmap.load()
                        width, height = bitmap.size
                        rgba = bitmap.convert("RGBA")
                    category = area(name)
                    relative = category + "/" + output_name(name, member)
                    # EA ships a DDS and PNG with the same fallback name. Keep both native identities.
                    if member is None and name.lower().endswith(".png") and name[:-4].lower() + ".dds" in native_dds:
                        relative = relative[:-4] + "__source_png.png"
                    if relative.casefold() in paths: raise ValueError("Output filename collision")
                    paths.add(relative.casefold())
                    destination = output / relative
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    # Never replace an artist's edits when rerunning a gallery export.
                    if destination.exists():
                        with Image.open(destination) as previous:
                            if previous.size != rgba.size or previous.convert("RGBA").tobytes() != rgba.tobytes():
                                raise ValueError("Existing PNG differs from source; refusing to overwrite")
                    else:
                        rgba.save(destination, compress_level=6)
                    with Image.open(destination) as check:
                        check.load()
                        if check.size != rgba.size or check.convert("RGBA").tobytes() != rgba.tobytes():
                            raise ValueError("PNG round-trip pixel mismatch")
                    fmt = "PNG"
                    if data.startswith(b"DDS "):
                        fmt = data[84:88].decode("ascii", errors="replace").rstrip("\0") or "uncompressed"
                        if fmt == "DX10" and len(data) >= 148: fmt += ":" + str(struct.unpack_from("<I", data, 128)[0])
                    images.append({"file": relative, "area": category, "source": name, "member": member,
                                   "width": width, "height": height, "alpha": rgba.getchannel("A").getextrema()[0] < 255,
                                   "sourceFormat": fmt, "sourceAssetSha256": asset["sha256"],
                                   "sourceBitmapSha256": digest(data), "pngSha256": digest(destination.read_bytes()),
                                   "pixelSha256": digest(rgba.tobytes()), "grouping": "inferred from native asset path"})
                except Exception as error:
                    failures.append({"source": name, "member": member, "error": str(error)})
        except Exception as error:
            failures.append({"source": name, "member": None, "error": str(error)})
        if number % 100 == 0:
            print(f"Decoded {number:,}/{len(selected):,} native assets; {len(images):,} PNGs, {len(failures)} failures.", flush=True)
    if only_sources is not None:
        previous = json.loads((output / "index.json").read_text())
        if previous["gameProfile"] != manifest["gameProfile"] or previous["fmtRevision"] != manifest["fmtRevision"]:
            raise ValueError("Repair native build differs from gallery provenance")
        images = [image for image in previous["images"] if image["source"] not in only_sources] + images
        failures = [failure for failure in previous["failures"] if failure["source"] not in only_sources] + failures
        nonbitmap = [archive for archive in previous["nonBitmapArchives"] if archive["source"] not in only_sources] + nonbitmap
    images.sort(key=lambda image: image["file"])
    counts = dict(sorted(collections.Counter(i["area"] for i in images).items()))
    index = {"schema": 1, "game": "FIFA21", "generated": datetime.now(timezone.utc).isoformat(),
             "gameProfile": manifest["gameProfile"], "fmtRevision": manifest["fmtRevision"],
             "scope": manifest["scope"], "areas": counts, "images": images,
             "failures": failures, "nativeExtractionFailures": manifest["failures"], "nonBitmapArchives": nonbitmap,
             "notes": "PNG pixels match decoded original top mip. Areas inferred from names; screen placement/cropping not verified. Vector APT content and server-delivered artwork are not rasterized."}
    if (failures or manifest["failures"]) and (output / "index.json").exists():
        # A failed rerun must preserve the artist's existing gallery and provenance indexes too.
        report = output / ("failed-rerun-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + ".json")
        report.write_text(json.dumps({"failures": failures, "nativeExtractionFailures": manifest["failures"]}, indent=2), encoding="utf-8")
        print(f"Failed rerun; existing gallery/indexes preserved. See {report}", flush=True)
        return index
    (output / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")
    with (output / "index.csv").open("w", newline="", encoding="utf-8-sig") as file:
        writer = csv.DictWriter(file, fieldnames=list(images[0]) if images else ["file"])
        writer.writeheader()
        writer.writerows(images)
    (output / "native-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    (output / "index.html").write_text(gallery_html(images, counts), encoding="utf-8")
    (output / "README.txt").write_text(
        "FIFA 21 Ultimate Team — UI artwork\n===================================\n\n"
        f"{len(images):,} PNGs extracted from {len(manifest['assets']):,} verified native assets.\n"
        "Open index.html in your browser. It works offline; search by asset name/path or select an area.\n"
        "index.csv and index.json retain dimensions, transparency, source archive/member, format and hashes.\n\n"
        "Editing: preserve each PNG's name, dimensions and alpha channel. PNG encoding is lossless relative\n"
        "to the decoded original top mip; DDS compression artifacts already in the source remain.\n"
        "These are individual UI textures, not screenshots or fully rendered screens. Some images are atlases.\n"
        "Folder grouping is inferred from native names; screen placement and clipping are unconfirmed.\n"
        "APT vectors/layouts, dynamically rendered content and server-delivered imagery are not rasterized.\n"
        "General player portraits, kits and badge collections are outside this export's scope.\n"
        f"Conversion failures: {len(failures)}; native extraction failures: {len(manifest['failures'])}.\n"
        f"Archives with no DDS/PNG bitmap members: {len(nonbitmap)} (listed in index.json).\n\n"
        "No files in the game installation were changed. Original input hashes are in native-manifest.json.\n"
        "An import/repacking tool is not included; a changed PNG alone cannot be installed into the game.\n\n"
        + "Folders\n" + "".join(f"{name:42} {count:6,}\n" for name, count in counts.items()), encoding="utf-8")
    print(f"Finished: {len(images):,} PNGs, {len(failures)} conversion failures, {len(nonbitmap)} non-bitmap archives.", flush=True)
    return index


def gallery_html(images, counts):
    # Inline metadata avoids file:// fetch restrictions; PNGs remain separate editable files.
    payload = json.dumps([{key: item[key] for key in ("file", "area", "source", "member", "width", "height", "alpha")}
                          for item in images], ensure_ascii=True).replace("<", "\\u003c")
    return r'''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>FIFA 21 FUT — UI Art</title><style>
*{box-sizing:border-box}body{margin:0;background:#11151c;color:#eef1f5;font:15px/1.5 system-ui,sans-serif}
header{padding:28px max(20px,4vw);background:#1b222d;border-bottom:1px solid #354051}h1{margin:0 0 8px;font-size:30px}
header p{margin:0;color:#b4bfd0}.controls{display:flex;flex-wrap:wrap;gap:12px;margin-top:20px}input,select,button{font:inherit;background:#11151c;color:inherit;border:1px solid #546178;border-radius:8px;padding:10px 14px}input{flex:2;min-width:min(360px,100%)}select{flex:1;min-width:240px}button{cursor:pointer}button:disabled{opacity:.4;cursor:default}button:hover:not(:disabled){border-color:#e5c373}a{color:#e5c373}main{padding:24px max(20px,4vw)}
.status,.pager{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap;gap:12px;margin-bottom:20px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:16px}.card{background:#1b222d;border:1px solid #354051;border-radius:12px;overflow:hidden;min-width:0}.art{display:flex;height:220px;padding:16px;align-items:center;justify-content:center;background-color:#252d38;background-image:linear-gradient(45deg,#303947 25%,transparent 25%),linear-gradient(-45deg,#303947 25%,transparent 25%),linear-gradient(45deg,transparent 75%,#303947 75%),linear-gradient(-45deg,transparent 75%,#303947 75%);background-size:20px 20px;background-position:0 0,0 10px,10px -10px,-10px 0}.art img{max-height:100%;max-width:100%;object-fit:contain}.info{padding:14px}.name{display:block;font-weight:600;overflow-wrap:anywhere}.source{color:#b4bfd0;font:12px/1.5 ui-monospace,monospace;overflow-wrap:anywhere;margin:10px 0}.meta{color:#b4bfd0;font-size:13px}.pager{margin-top:24px}input:focus,select:focus,button:focus{outline:2px solid #e5c373;outline-offset:2px}
</style><header><h1>FIFA 21 FUT · UI Art</h1><p>Original game textures, with source paths and dimensions. Folder grouping follows asset names; rendered screen placement is unverified.</p>
<div class="controls"><input id="search" type="search" placeholder="Search tiles, packs, frames, icons or native asset paths…" aria-label="Search artwork"><select id="area" aria-label="Area"><option value="">All areas</option></select></div></header>
<main><div class="status"><span id="count" role="status"></span><span><a href="index.csv">CSV index</a> · <a href="index.json">Full provenance</a> · <a href="README.txt">Read me</a></span></div><div id="grid" class="grid"></div><div class="pager"><button id="prev">Previous</button><span id="page"></span><button id="next">Next</button></div></main>
<script>
const images=PAYLOAD;const size=120;let page=0,filtered=images;
const $=id=>document.getElementById(id);const areas=[...new Set(images.map(i=>i.area))];
for(const area of areas){const o=document.createElement('option');o.value=area;o.textContent=area.replace(/^\d+-/,'').replaceAll('-',' ')+' ('+images.filter(i=>i.area===area).length.toLocaleString()+')';$('area').append(o)}
function render(){const start=page*size,subset=filtered.slice(start,start+size);$('grid').replaceChildren();for(const i of subset){const card=document.createElement('article');card.className='card';const art=document.createElement('a');art.className='art';art.href=i.file;art.target='_blank';art.rel='noopener';const image=document.createElement('img');image.src=i.file;image.loading='lazy';image.alt=i.file.split('/').at(-1);art.append(image);const info=document.createElement('div');info.className='info';const name=document.createElement('a');name.className='name';name.href=i.file;name.target='_blank';name.rel='noopener';name.textContent=i.file.split('/').at(-1);const meta=document.createElement('div');meta.className='meta';meta.textContent=i.width+' × '+i.height+' · '+(i.alpha?'Transparency':'Opaque');const source=document.createElement('p');source.className='source';source.textContent=i.source+(i.member===null?'':' · texture '+i.member);info.append(name,meta,source);card.append(art,info);$('grid').append(card)}$('count').textContent=filtered.length.toLocaleString()+' images'+(filtered.length?' · showing '+(start+1)+'–'+(start+subset.length):'');$('page').textContent='Page '+(filtered.length?page+1:0)+' of '+Math.ceil(filtered.length/size);$('prev').disabled=page===0;$('next').disabled=(page+1)*size>=filtered.length}
function filter(){const q=$('search').value.toLowerCase().trim(),area=$('area').value;filtered=images.filter(i=>(!area||i.area===area)&&(!q||(i.source+' '+i.file+' '+(i.member??'')).toLowerCase().includes(q)));page=0;render()}
$('search').addEventListener('input',filter);$('area').addEventListener('change',filter);$('prev').onclick=()=>{page--;render();scrollTo(0,0)};$('next').onclick=()=>{page++;render();scrollTo(0,0)};render();
</script></html>'''.replace("PAYLOAD", payload)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inventory", type=Path, help="Existing legacy-index.json; writes a reviewed selection instead of decoding")
    parser.add_argument("--native", type=Path)
    parser.add_argument("--repair-failures", action="store_true", help="Retry only reported failed sources, preserving other indexed images")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.inventory:
        selection = select_assets(json.loads(args.inventory.read_text()))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(selection, indent=2), encoding="utf-8")
        print(f"Selected {len(selection):,} assets.")
    elif args.native:
        only_sources = None
        if args.repair_failures:
            prior = json.loads((args.output / "index.json").read_text())
            only_sources = {failure["source"] for failure in prior["failures"]}
        result = build(args.native, args.output, only_sources=only_sources)
        if result["failures"] or result["nativeExtractionFailures"]:
            raise SystemExit(1)
    else:
        parser.error("Provide --inventory or --native")
