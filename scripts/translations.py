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


def source_keys():
    keys = set()
    for path in (ROOT / "ui").glob("*.slint"):
        source = path.read_text(encoding="utf-8")
        keys.update(json.loads(m) for m in MESSAGE.findall(source))
        for conditional in re.findall(r'Locale\.t\(([^()]*\?[^()]*)\)', source):
            keys.update(json.loads(m) for m in re.findall(r'(?:\?|:)\s*("(?:[^"\\]|\\.)*")', conditional))
    for path in (ROOT / "src").rglob("*.rs"):
        source = path.read_text(encoding="utf-8").split("#[cfg(test)]\nmod tests", 1)[0]
        keys.update(json.loads(m) for m in re.findall(
            r'(?:i18n::lookup\([^,]*,|i18n::key\(|\btr\()\s*("(?:[^"\\]|\\.)*")', source))
    # Status labels in VersionRow and Channel are translated by the row component.
    keys.update({"Update available", "Up to date", "Installed", "Stable", "Nightly", "Main", "Pull request"})
    return keys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--missing", metavar="LANG")
    args = parser.parse_args()
    catalogs = {p.stem: load_catalog(p) for p in sorted(CATALOGS.glob("*.json"))}
    parents = {"fr-CA": "fr", "es-419": "es"}
    keys = source_keys()
    def missing(lang):
        if lang == "en-GB":
            return set()  # English source is the base catalog.
        return keys - catalogs[lang].keys() - catalogs.get(parents.get(lang), {}).keys()

    if args.missing:
        if args.missing not in catalogs:
            parser.error("choose a catalog from " + ", ".join(catalogs))
        print(json.dumps({k: "" for k in sorted(missing(args.missing))}, indent=2, ensure_ascii=False))
        return

    for lang, catalog in catalogs.items():
        if absent := missing(lang):
            raise ValueError(f"{lang}: missing {len(absent)} messages, including {min(absent)!r}")
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
