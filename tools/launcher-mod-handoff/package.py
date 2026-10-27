"""Build the developer ZIP from explicit reviewed payloads, excluding live caches.

Run with Windows Python so assets on the Windows workspace can be written.
The verified profile is read only; game data and personal settings are excluded.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import stat
import zipfile


def safe_archive_path(name, directory=False):
    """Only portable relative ZIP paths, with no Windows drive/stream syntax."""
    if not isinstance(name, str) or not name or "\\" in name or ":" in name or "\0" in name:
        raise ValueError(f"Invalid package path: {name!r}")
    value = name[:-1] if directory and name.endswith("/") else name
    parts = value.split("/")
    reserved = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
    if any(part in ("", ".", "..") or part.endswith((".", " "))
           or part.split(".")[0].upper() in reserved
           or any(ord(char) < 32 or char in '<>"|?*' for char in part) for part in parts):
        raise ValueError(f"Invalid package path: {name!r}")
    return value


def validate_refresh_archive(archive, profile):
    """Validate every payload before reusing the original verification evidence."""
    members = {}
    seen = set()
    for entry in archive.infolist():
        name = safe_archive_path(entry.filename, entry.is_dir())
        if name.casefold() in seen:
            raise ValueError(f"Duplicate package path: {name}")
        seen.add(name.casefold())
        if stat.S_ISLNK(entry.external_attr >> 16):
            raise ValueError(f"Symbolic link in package: {name}")
        if not entry.is_dir():
            members[name] = entry
    if "checksums.json" not in members:
        raise ValueError("Existing ZIP has no checksums.json")
    index = json.loads(archive.read("checksums.json"))
    if index.get("schema") != 1 or index.get("revision") != profile["revision"]:
        raise ValueError("Existing ZIP checksum revision differs from the repository profile")
    checked = set()
    for entry in index["files"]:
        name = safe_archive_path(entry["path"])
        if name in checked or name == "checksums.json" or name not in members:
            raise ValueError(f"Invalid checksum entry: {name}")
        expected = entry["sha256"]
        if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
            raise ValueError(f"Invalid SHA-256: {name}")
        digest = hashlib.sha256()
        with archive.open(members[name]) as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(chunk)
        if digest.hexdigest() != expected:
            raise ValueError(f"Existing ZIP file differs: {name}")
        checked.add(name)
    if checked != set(members) - {"checksums.json"}:
        raise ValueError("Existing ZIP contains payload files absent from checksums.json")
    bundled = json.loads(archive.read("ultimateteam/leaderboard-client-profile.json"))
    marker = json.loads(archive.read("evidence/verified-build.json"))
    if bundled.get("revision") != profile["revision"] or marker.get("revision") != profile["revision"]:
        raise ValueError("Existing ZIP profile or verified-build revision differs from the repository profile")
    if marker.get("verifiedLanguages") != 21 or marker.get("verifiedArtworkAssets") != 11:
        raise ValueError("Existing ZIP lacks the original reviewed build verification")
    mod_name = f"ready-mod/aurora21-leaderboards-{profile['revision']}.fbmod"
    if mod_name not in members:
        raise ValueError("Existing ZIP lacks the verified ready mod")
    return members


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--verified-profile", type=Path)
    source.add_argument("--refresh-from", type=Path, help="Refresh a checksum-verified handoff while retaining its ready mod and evidence")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--staging", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[2]
    stage = args.staging.resolve()
    if stage.exists() or args.output.exists():
        raise ValueError("Use fresh staging and ZIP paths; existing handoffs are preserved")
    profile = json.loads((repo / "ultimateteam/leaderboard-client-profile.json").read_text())
    if args.output.resolve().is_relative_to(stage):
        raise ValueError("ZIP output must be outside the staging directory")
    if args.refresh_from:
        with zipfile.ZipFile(args.refresh_from) as archive:
            members = validate_refresh_archive(archive, profile)
            stage.mkdir(parents=True)
            for name, entry in members.items():
                if name.startswith(("ready-mod/", "evidence/")):
                    dest = stage / name
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(entry) as source, dest.open("wb") as target:
                        shutil.copyfileobj(source, target)
    else:
        marker = json.loads((args.verified_profile / "compiled-profile.json").read_text())
        assert marker["revision"] == profile["revision"]
        assert marker["verifiedLanguages"] == 21 and marker["verifiedArtworkAssets"] == 11
        stage.mkdir(parents=True)
    assert len(profile["artwork"]) == 12

    def copy(source, relative):
        dest = stage / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)

    handoff = Path(__file__).parent
    for name in ("START-HERE.md", "Prepare-ClientMods.ps1", "Verify-Package.ps1"):
        copy(handoff / name, name)
    for name in ("Client-LeaderboardMods.ps1", "leaderboard-client-profile.json",
                 "Build-LeaderboardClientTool.ps1", "leaderboard-client-launcher.md"):
        copy(repo / "ultimateteam" / name, "ultimateteam/" + name)
    copy(repo / "ultimateteam/Launch-UltimateTeam.ps1", "reference/Launch-UltimateTeam.ps1")
    for folder in ("tools/fifa21-leaderboard-ui", "tools/fifa21-currency-art"):
        for source in (repo / folder).iterdir():
            if source.is_file() and source.suffix in (".cs", ".csproj", ".targets", ".md", ".py", ".json", ".ps1"):
                copy(source, folder + "/" + source.name)
    for source in (repo / "ultimateteam/assets/currency").rglob("*"):
        if source.is_file():
            copy(source, source.relative_to(repo))
    for edit in profile["artwork"]:
        payload = stage / "ultimateteam/assets/currency" / edit["replacementFile"]
        assert hashlib.sha256(payload.read_bytes()).hexdigest() == edit["replacementSha256"]

    runtime = repo / "ultimateteam/client-mod-tools"
    assert not (runtime / "DotNetZip.dll").exists()
    for source in runtime.iterdir():
        if source.is_file() and (source.suffix == ".dll" or source.name in (
                "Aurora21-LeaderboardUiTool.exe", "Aurora21-LeaderboardUiTool.deps.json",
                "Aurora21-LeaderboardUiTool.runtimeconfig.json")):
            if source.name not in ("Aurora21.StoreUiProbe.dll", "Aurora21.UiArtExport.dll"):
                copy(source, "ultimateteam/client-mod-tools/" + source.name)
    for folder in ("SDK", "Plugins", "FrostbiteProfiles"):
        for source in (runtime / folder).rglob("*"):
            if source.is_file():
                copy(source, "ultimateteam/client-mod-tools/" + source.relative_to(runtime).as_posix())
    # Only the packaged native libraries; exclude other-game EXEs and configs.
    for source in (runtime / "ThirdParty").glob("*.dll"):
        copy(source, "ultimateteam/client-mod-tools/ThirdParty/" + source.name)
    mod_name = "aurora21-leaderboards-" + profile["revision"] + ".fbmod"
    if not args.refresh_from:
        copy(args.verified_profile / mod_name, "ready-mod/" + mod_name)
        evidence = repo / "outputs/verification/currency-art-encoder-review-20261006"
        for name in ("caption-validation.json", "artwork-validation.json", "collector-validation.json", "replacement-preview.png"):
            copy(evidence / name, "evidence/" + name)
        marker.pop("gameDirectory", None)
        marker["recordOnlyNotInstallMarker"] = True
        (stage / "evidence/verified-build.json").write_text(json.dumps(marker, indent=2) + "\n")
    for source in (repo / "ultimateteam/runtime-source/ultimateteam21/custom-playerheads").iterdir():
        if source.is_file() and source.suffix in (".dds", ".png", ".txt"):
            copy(source, "server-assets/custom-playerheads/" + source.name)
    if not args.refresh_from:
        copy(repo / "outputs/verification/dost-portrait-left26-20261006/verification.json", "evidence/dost-verification.json")
        copy(repo / "outputs/verification/dost-portrait-left26-20261006/comparison.png", "evidence/dost-comparison.png")
    copy(repo / "docs/aurora-tokens-localisation-investigation.md", "docs/aurora-tokens-localisation-investigation.md")
    copy(repo / "tools/repair-dost-portrait.py", "tools/repair-dost-portrait.py")
    files = sorted(p for p in stage.rglob("*") if p.is_file())
    index = dict(schema=1, revision=profile["revision"], files=[dict(
        path=p.relative_to(stage).as_posix(), sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in files])
    (stage / "checksums.json").write_text(json.dumps(index, indent=2) + "\n")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for source in sorted(stage.rglob("*")):
            if source.is_file():
                archive.write(source, source.relative_to(stage).as_posix())
    with zipfile.ZipFile(args.output) as archive:
        assert archive.testzip() is None
        for entry in index["files"]:
            assert hashlib.sha256(archive.read(entry["path"])).hexdigest() == entry["sha256"]
    print(json.dumps(dict(zip=str(args.output.resolve()), files=len(index["files"])+1,
                         bytes=args.output.stat().st_size,
                         sha256=hashlib.sha256(args.output.read_bytes()).hexdigest()), indent=2))


if __name__ == "__main__":
    main()
