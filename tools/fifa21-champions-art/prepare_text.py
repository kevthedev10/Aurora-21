"""Inventory and prepare exact FIFA 21 Aurora Champions localisation edits.

Inputs are the verified, read-only FifaLibrary table exports. Every output row
pins its original text so the client mod refuses a different game database.
"""
import argparse
from collections import defaultdict
import json
from pathlib import Path
import re


PATTERNS = {
    "ara_sa": [r"أبطال\s+FUT(?![A-Za-z])"],
    "chs_cn": [r"FUT\s+(?:(?:白银|青铜|黄金|终极|高级)\s*)?冠军"],
    "ger_de": [r"FUT[-\s\u00a0]Champions(?![A-Za-z])"],
    "ita_it": [r"Champions\s+FUT(?![A-Za-z])"],
    "kor_kr": [r"FUT\s+챔피언스?"],
    "pol_pl": [r"Mistrzostw(?:a|ach)?[\s\u00a0]+FUT(?![A-Za-z])", r"MISTRZ\.[\s\u00a0]+FUT(?![A-Za-z])"],
    "por_pt": [r"FUT\s+ULTIMATE\s+CHAMPIONS(?![A-Za-z])"],
}
DEFAULT = r"FUT[-\s\u00a0]+Champions(?![A-Za-z])"
ABBREVIATED = r"FUT[-\s\u00a0]+Champ(?:ionship|ion|s)?\.?(?![A-Za-z])"
PLACEHOLDER = re.compile(r"%[0-9]*[A-Za-z]")
NEWLINE = re.compile(r"\r\n|\r|\n")


def target_text(language, source):
    result = source
    for expression in PATTERNS.get(language, []) + [DEFAULT, ABBREVIATED]:
        def replacement(match):
            word = match.group()
            latin = re.findall(r"[A-Za-z]+", word)
            upper = len(latin) > 1 and all(part.isupper() for part in latin)
            return re.sub(r"(?<![A-Za-z])FUT(?![A-Za-z])",
                          "AURORA" if upper else "Aurora", word, flags=re.IGNORECASE)
        result = re.sub(expression, replacement, result, flags=re.IGNORECASE)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tables", required=True, type=Path)
    parser.add_argument("--profile", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    profile = json.loads(args.profile.read_text())
    reserved = {edit["stringId"] for edit in profile["textEdits"] + profile["captions"]}
    edits = defaultdict(dict)
    stats = {}
    for language in profile["languages"]:
        changed = 0
        for table in ("LanguageStrings1.json", "LanguageStrings2.json"):
            rows = json.loads((args.tables / language / table).read_text())
            for row in rows:
                key, source = row["stringid"], row["sourcetext"]
                target = target_text(language, source)
                if source == target:
                    continue
                if key in reserved:
                    raise ValueError("Existing caption/currency edit overlaps: " + key)
                if key in edits and language in edits[key]:
                    raise ValueError("Duplicate native string ID: " + key)
                if PLACEHOLDER.findall(source) != PLACEHOLDER.findall(target) or NEWLINE.findall(source) != NEWLINE.findall(target):
                    raise ValueError("Formatting changed: " + language + "/" + key)
                edits[key][language] = {"source": source, "target": target}
                changed += 1
        stats[language] = changed
    result = [{"stringId": key, "languages": edits[key]} for key in sorted(edits)]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    args.report.write_text(json.dumps({"keys": len(result), "changesByLanguage": stats,
                                       "sourceTables": str(args.tables)}, indent=2) + "\n")
    print(f"Prepared {sum(stats.values())} exact text edits across {len(result)} string IDs and {len(stats)} languages")


if __name__ == "__main__":
    main()
