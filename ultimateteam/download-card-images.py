#!/usr/bin/env python3
"""Download panel art using FIFA 21 IDs from PostgreSQL.

Categories:
  players   base player images, players/<asset id>.png
  dynamic   special-card art, players/p<resource id>.png (or players/<resource id>.png for
            ids below 2^24, such as Icons), saved as dynamic/p<resource id>.png: every card whose resource id
            differs from its player's asset id, plus each card's portrait override
            (player_card_definitions.dynamic_image_resource_id)
  clubs, leagues, nations   badges, flags and league logos of the catalogue
  cards/hd  HD card frames listed in the frame manifests (see --frames)

Requires: python -m pip install "psycopg[binary]" (not needed for --category cards/hd alone)
The local managed cluster is detected automatically when running. For another
database, set PGHOST, PGPORT, PGDATABASE, PGUSER and PGPASSWORD.

Try: python ultimateteam/download-card-images.py --limit 1
Full: python ultimateteam/download-card-images.py

By default the images go to ultimateteam/panel-assets beside this script, which the local panel serves
under /panel/assets/. For the VPS, download them on your PC and copy the folder to the
server, where compose.yaml mounts it read-only:
  rsync -a "ultimateteam/panel-assets/" deploy@<vps>:/opt/aurora21/panel-assets/
The copy needs no restart: the server reads each image when the panel asks for it.
A run resumes: existing images and URLs that answered 404 before (each
category's missing.txt) are skipped. Many cards have no separate dynamic image, and
some manifest frames are reconstructed names, so 404s are expected and recorded.
The database does not store Futbin's frame filenames; they come from the manifests.
"""

import argparse
import os
from pathlib import Path
import re
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urlparse
from urllib.request import Request, urlopen


CDN = "https://cdn.futbin.com/content/fifa21/img"
PNG = b"\x89PNG\r\n\x1a\n"
MAX_BYTES = 2 * 1024 * 1024
ROOT = Path(__file__).resolve().parent
QUERIES = {
    "players": """
        SELECT DISTINCT d.native_asset_id FROM ultimateteam.item_definitions d
        JOIN ultimateteam.player_card_definitions p USING (definition_id)
        WHERE d.game_year=2021 AND d.family_id=1 AND d.native_asset_id > 0 ORDER BY 1
    """,
    "dynamic": """
        SELECT id FROM (
            SELECT d.native_resource_id AS id FROM ultimateteam.item_definitions d
            JOIN ultimateteam.player_card_definitions p USING (definition_id)
            WHERE d.game_year=2021 AND d.family_id=1 AND d.native_resource_id > 0
              AND d.native_resource_id IS DISTINCT FROM d.native_asset_id
            UNION
            SELECT p.dynamic_image_resource_id FROM ultimateteam.player_card_definitions p
            JOIN ultimateteam.item_definitions d USING (definition_id)
            WHERE d.game_year=2021 AND p.dynamic_image_resource_id > 0
        ) ids ORDER BY 1
    """,
    "clubs": "SELECT DISTINCT native_team_id FROM ultimateteam.teams WHERE native_team_id > 0 ORDER BY 1",
    "leagues": "SELECT DISTINCT native_league_id FROM ultimateteam.leagues WHERE native_league_id > 0 ORDER BY 1",
    "nations": "SELECT DISTINCT native_nation_id FROM ultimateteam.nations WHERE native_nation_id > 0 ORDER BY 1",
}


def runtime_root():
    """The server's runtime (state) folder: ULTIMATETEAM_RUNTIME_ROOT, else %LOCALAPPDATA%\\FIFA21UltimateTeam."""
    local = Path(os.environ.get("LOCALAPPDATA") or Path.home() / "AppData" / "Local")
    return Path(os.environ.get("ULTIMATETEAM_RUNTIME_ROOT") or local / "FIFA21UltimateTeam")


def connection_options():
    if any(os.environ.get(key) for key in ("PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD")):
        return {}
    root = runtime_root()
    pid_file = root / "postgres" / "data-18" / "postmaster.pid"
    password_file = root / "postgres" / "ultimateteam.password"
    if not pid_file.is_file() or not password_file.is_file():
        raise RuntimeError("Local PostgreSQL is not running; start Aurora 21 or set the PG* variables")
    lines = pid_file.read_text(encoding="ascii").splitlines()
    if len(lines) < 4 or not lines[3].isdecimal():
        raise RuntimeError("The managed PostgreSQL port is missing from postmaster.pid")
    return dict(host="127.0.0.1", port=int(lines[3]), dbname="ultimateteam",
                user="ultimateteam", password=password_file.read_text(encoding="utf-8").strip())


def database_ids():
    try:
        import psycopg
    except ImportError as error:
        raise RuntimeError('Install psycopg first: python -m pip install "psycopg[binary]"') from error
    with psycopg.connect(**connection_options()) as connection:
        with connection.cursor() as cursor:
            cursor.execute("SET TRANSACTION READ ONLY")
            result = {}
            for category, sql in QUERIES.items():
                cursor.execute(sql)
                result[category] = [int(row[0]) for row in cursor.fetchall()]
            return result


FRAME_PREFIX = "/content/fifa21/img/cards/hd/"
FRAME_NAME = re.compile(r"[a-z0-9][a-z0-9_-]*\.png")
FRAME_URL_LINE = re.compile(r"(?:url:\s*)?(https?://\S+)")
DEFAULT_FRAMES = (ROOT / "futbin-card-frames.txt", ROOT.parent / "fifa21_futbin_card_frames_complete.txt")
# Futbin frames saved under other names. The panel picks a frame by its "<rareflag>_" prefix, so a frame whose filename
# has no card-type number, or that several card types share, is saved once per type. Identified 2026-09-29 from the
# frame art and the maintainer's examples: totsg is Team of the Season (11), totyn TOTY Honourable Mentions (64, the
# TOTY crest), futmas FUT Freeze (32, frozen glass), icon_moment Icon Moments (84), ucl_tott Team of the Group Stage
# (70), europa_tott TOTGS Europa League (68), ucl_motm Champions League MOTM (49); the objectives frame 91_os also
# draws Objective Players (98) and the Icon Swaps rewards (89, 92, 93, 94); europa_motm is UEL Man of the Match
# (45); FGS Swaps 1 and 2 (76, 77) share 76_fgs. Squad Foundations (87) has its own frame, 87_or (2026-10-04).
FRAME_ALIASES = {
    "totsg.png": ("11_totsg.png",),
    "totyn.png": ("64_totyn.png",),
    "futmas.png": ("32_futmas.png",),
    "icon_moment.png": ("84_icon_moment.png",),
    "ucl_tott.png": ("70_ucl_tott.png",),
    "europa_tott.png": ("68_europa_tott.png",),
    "ucl_motm.png": ("49_ucl_motm.png",),
    "91_os.png": ("91_os.png", "98_os.png", "89_os.png", "92_os.png", "93_os.png", "94_os.png"),
    "europa_motm.png": ("45_europa_motm.png",),
    "76_fgs.png": ("76_fgs.png", "77_fgs.png"),
}


def frame_urls(paths):
    """HD card-frame URLs by filename from every manifest that exists.

    A manifest may list bare URLs (one per line), "url: <URL>" entries, or bare filenames
    such as 5_toty.png (resolved against the Futbin HD frame folder). Other lines (titles,
    type/status/note fields, prose) are ignored. When several manifests name one file, a
    versioned URL (?v=N) is preferred, since Futbin serves it as the current revision.
    """
    urls = {}
    for path in paths:
        if not path.is_file():
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            if FRAME_NAME.fullmatch(text):
                name, url = text, CDN + "/cards/hd/" + text
            elif match := FRAME_URL_LINE.fullmatch(text):
                url = match.group(1)
                parsed = urlparse(url)
                if parsed.path == FRAME_PREFIX and not parsed.query:
                    continue  # the manifest's "CDN base" line names the folder, not a frame
                name = parsed.path.removeprefix(FRAME_PREFIX)
                if (parsed.scheme != "https" or parsed.netloc not in ("cdn.futbin.com", "www.futbin.com")
                        or not parsed.path.startswith(FRAME_PREFIX) or not FRAME_NAME.fullmatch(name)
                        or not re.fullmatch(r"(v=[0-9]+)?", parsed.query)):
                    raise ValueError(f"Invalid card frame URL in {path}, line {number}: {url}")
            else:
                continue
            for saved in FRAME_ALIASES.get(name, (name,)):
                if saved not in urls or ("?" in url and "?" not in urls[saved]):
                    urls[saved] = url
    return sorted(urls.items())


def download(url, target, timeout):
    request = Request(url, headers={"User-Agent": "Aurora21-PanelAssetDownload/1.0"})
    temporary = None
    try:
        with urlopen(request, timeout=timeout) as response:
            if response.headers.get_content_type() != "image/png":
                raise ValueError(f"unexpected content type: {response.headers.get_content_type()}")
            with tempfile.NamedTemporaryFile(prefix=target.stem + ".", suffix=".tmp",
                                             dir=target.parent, delete=False) as file:
                temporary = Path(file.name)
                first = response.read(len(PNG))
                if first != PNG:
                    raise ValueError("response is not a PNG")
                file.write(first)
                while chunk := response.read(64 * 1024):
                    file.write(chunk)
                    if file.tell() > MAX_BYTES:
                        raise ValueError("image exceeds 2 MiB")
        os.replace(temporary, target)
        os.chmod(target, 0o644)  # the temporary file was private; a served image must be readable
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def tasks(ids, frames):
    # The small sets first (frames, flags, league logos, badges: well under an hour at the minimum delay), then the
    # player images, then the special-card art, so a partial run already gives the panel frames and badges.
    for name, url in frames:
        yield "cards/hd", name, url
    for nation_id in ids["nations"]:
        yield "nations", f"{nation_id}.png", f"nation/{nation_id}.png"
    for league_id in ids["leagues"]:
        yield "leagues", f"{league_id}.png", f"league/{league_id}.png"
    for team_id in ids["clubs"]:
        yield "clubs", f"{team_id}.png", f"clubs/{team_id}.png"
    for asset_id in ids["players"]:
        yield "players", f"{asset_id}.png", f"players/{asset_id}.png?v=23"
    for resource_id in ids["dynamic"]:
        # Futbin keeps most special art at p<resource id>. A card whose resource id has no version bits (below 2^24:
        # Icons, Beckham, portrait overrides) may instead have it at <resource id>, without the p (Shearer's Icon is
        # players/51.png). Either is saved as p<resource id>.png, the name the panel looks for.
        urls = [f"players/p{resource_id}.png?v=23"]
        if resource_id < 1 << 24:
            urls.append(f"players/{resource_id}.png?v=23")
        yield "dynamic", f"p{resource_id}.png", urls


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "panel-assets",
                        help="where the images go; the default is the folder the local server's panel serves "
                             "(ultimateteam/panel-assets beside this script). For the VPS, download anywhere and copy the "
                             "folder to /opt/aurora21/panel-assets")
    parser.add_argument("--frames", type=Path, action="append",
                        help="an HD card-frame manifest (URLs, url: entries or filenames); may be repeated. "
                             "Default: futbin-card-frames.txt here and fifa21_futbin_card_frames_complete.txt in the repository root")
    parser.add_argument("--category", action="append",
                        choices=(*QUERIES.keys(), "cards/hd"),
                        help="download only this category; may be repeated")
    parser.add_argument("--limit", type=int, default=0, help="first N assets per category, for a trial run")
    parser.add_argument("--delay", type=float, default=0.1, help="seconds between CDN requests")
    parser.add_argument("--timeout", type=int, default=15, help="seconds per request")
    args = parser.parse_args()
    if args.limit < 0 or args.delay < 0 or args.timeout < 1:
        parser.error("limit and delay must be nonnegative; timeout must be at least 1")
    wanted = set(args.category or (*QUERIES.keys(), "cards/hd"))
    try:
        frames = frame_urls(args.frames or DEFAULT_FRAMES) if "cards/hd" in wanted else []
        # PostgreSQL is only needed for the categories whose ids live in the catalogue.
        ids = database_ids() if wanted & QUERIES.keys() else {category: [] for category in QUERIES}
    except (RuntimeError, ValueError) as error:
        print(f"Could not prepare assets: {error}", file=sys.stderr)
        return 1
    except Exception as error:
        print(f"Could not read asset IDs from PostgreSQL ({type(error).__name__}).", file=sys.stderr)
        return 1
    if args.limit:
        for category in QUERIES:
            ids[category] = ids[category][:args.limit]
        frames = frames[:args.limit]

    output = args.output.resolve()
    saved = present = known_missing = absent = failed = 0
    missing_by_category = {}
    processed_by_category = {}
    for category, filename, relative in tasks(ids, frames):
        if args.category and category not in args.category:
            continue
        folder = output / category
        folder.mkdir(parents=True, exist_ok=True)
        target = folder / filename
        marker = folder / "missing.txt"
        if category not in missing_by_category:
            missing_by_category[category] = set(marker.read_text(encoding="ascii").splitlines()) if marker.exists() else set()
            processed_by_category[category] = 0
            print(f"Downloading {category}...", flush=True)
        processed_by_category[category] += 1
        if processed_by_category[category] % 100 == 0:
            print(f"  {category}: {processed_by_category[category]} checked", flush=True)
        # Existing PNGs and known 404s are never requested again on a resume.
        if target.is_file() and target.stat().st_size > len(PNG):
            present += 1
            continue
        if filename in missing_by_category[category]:
            known_missing += 1
            continue
        # A task names one URL or several candidates; a 404 is recorded only when every candidate answers 404.
        candidates = [relative] if isinstance(relative, str) else relative
        for index, candidate in enumerate(candidates):
            if index:
                time.sleep(args.delay)
            try:
                download(candidate if candidate.startswith("https://") else f"{CDN}/{candidate}", target, args.timeout)
                saved += 1
                break
            except HTTPError as error:
                if error.code == 404:
                    if index + 1 < len(candidates):
                        continue
                    with marker.open("a", encoding="ascii") as file:
                        file.write(filename + "\n")
                    missing_by_category[category].add(filename)
                    absent += 1
                elif error.code in (403, 429):
                    print(f"CDN returned {error.code}; stopping at {category}/{filename}.", file=sys.stderr)
                    return 1
                else:
                    failed += 1
                    print(f"{category}/{filename}: HTTP {error.code}", file=sys.stderr)
                break
            except (OSError, URLError, ValueError) as error:
                failed += 1
                print(f"{category}/{filename}: {error}", file=sys.stderr)
                break
        time.sleep(args.delay)
    print(f"Done: downloaded={saved}, already present={present}, known 404={known_missing}, new 404={absent}, failed={failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
