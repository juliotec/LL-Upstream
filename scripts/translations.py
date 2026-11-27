"""Validate launcher catalogs, or list untranslated literal Slint messages for a language."""
import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOGS = ROOT / "assets" / "lang"
MESSAGE = re.compile(r'Locale\.t\(\s*("(?:[^"\\]|\\.)*")\s*\)')
PLACEHOLDER = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")


def load_catalog(path):
    def distinct(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"{path.name}: duplicate key {key!r}")
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=distinct)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--missing", metavar="LANG")
    args = parser.parse_args()
    catalogs = {p.stem: load_catalog(p) for p in sorted(CATALOGS.glob("*.json"))}
    if args.missing:
        if args.missing not in catalogs:
            parser.error("choose a catalog from " + ", ".join(catalogs))
        regional = {"fr-CA": "fr", "es-419": "es"}
        translated = catalogs.get(regional.get(args.missing), {}) | catalogs[args.missing]
        keys = {json.loads(m) for path in (ROOT / "ui").glob("*.slint")
                for m in MESSAGE.findall(path.read_text(encoding="utf-8"))}
        print(json.dumps({k: "" for k in sorted(keys - translated.keys())}, indent=2, ensure_ascii=False))
        return

    for lang, catalog in catalogs.items():
        for key, value in catalog.items():
            if key.startswith("@"):
                continue
            if isinstance(value, dict):
                if "other" not in value or not all(isinstance(t, str) and t for t in value.values()):
                    raise ValueError(f"{lang}: invalid plural forms for {key!r}")
            elif (not isinstance(value, str) or not value
                  or set(PLACEHOLDER.findall(key)) != set(PLACEHOLDER.findall(value))):
                raise ValueError(f"{lang}: invalid translation or placeholders for {key!r}")
    print(f"Validated {len(catalogs)} launcher catalogs.")


if __name__ == "__main__":
    main()
