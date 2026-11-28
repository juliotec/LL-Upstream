"""Run launcher tests and compare their builder contract with pinned Longshot."""
import argparse
import ast
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def read(repo, path):
    return (repo / path).read_text(encoding="utf-8")


def check(label, actual, expected):
    if actual != expected:
        raise SystemExit(f"{label} differs from pinned Longshot:\nlauncher: {actual}\nLongshot: {expected}")


def assigned(source, name):
    return next(
        node.value for node in ast.parse(source).body
        if isinstance(node, ast.Assign) and len(node.targets) == 1
        and isinstance(node.targets[0], ast.Name) and node.targets[0].id == name
    )


def rust_array(source, name):
    body = re.search(rf"\b{name}: [^\n]+? = &?\[(.*?)\];", source, re.S)
    if body is None:
        raise SystemExit(f"Cannot find the Rust array {name}; update the contract check.")
    return body[1]


def compare_builder(longshot):
    ref = next(line for line in read(ROOT, "tests/longshot.ref").splitlines() if line and not line.startswith("#"))
    if not re.fullmatch(r"[0-9a-f]{40}", ref):
        raise SystemExit("tests/longshot.ref must contain a full commit ID.")
    commit = subprocess.check_output(["git", "-C", longshot, "rev-parse", "HEAD"], text=True).strip()
    check("Longshot checkout", commit, ref)

    options = read(longshot, "builder/crates/build-driver/src/options.rs")
    expected_languages = re.findall(r'\("([^"]+)", "[^"]+"\)', rust_array(options, "LANGUAGES"))
    languages = re.findall(r'"([^"]+)"', rust_array(read(ROOT, "src/game/mod.rs"), "LANGS"))
    check("Builder languages", languages, expected_languages)

    fetch = read(longshot, "scripts/fetch.py")
    licenses = read(ROOT, "src/game/licenses.rs")
    for sdk, vendor in (("DLSS", "nvidia"), ("FSR", "amd"), ("XESS", "intel")):
        pin = ast.literal_eval(assigned(fetch, f"{sdk}_COMMIT"))
        receipt = re.search(rf'receipt: Some\(\("{vendor}", "([0-9a-f]+)"\)\)', licenses)
        check(f"{sdk} license receipt", receipt[1] if receipt else None, pin)
        if not re.search(rf'/blob/{pin}/[^"]+', licenses):
            raise SystemExit(f"{sdk} license document must link to the same pinned SDK.")

    # The FP16 DLSS 5 files Settings accepts are the ones the game runs, by size and SHA-256.
    model = read(longshot, "gpu/vk/dlss_nr_model.h")
    known = re.search(r"kFp16Known\[\]\s*=\s*\{(.*?)\n\};", model, re.S)
    if known is None:
        raise SystemExit("Cannot find kFp16Known in gpu/vk/dlss_nr_model.h; update the contract check.")
    expected_fp16 = sorted((int(size), sha) for size, sha in re.findall(r'\{\s*(\d+)ull\s*,\s*"([0-9a-f]{64})"\s*\}', known[1]))
    fp16 = sorted((int(size.replace("_", "")), sha) for size, sha in re.findall(
        r'size: ([0-9_]+),\s*sha256: "([0-9a-f]{64})"', rust_array(read(ROOT, "src/dlss5.rs"), "FP16_KNOWN")))
    check("FP16 DLSS 5 files", fp16, expected_fp16)

    applet = read(longshot, "host/nn_applet.c")
    uid = re.search(r"user_uid\[2\]\s*=\s*\{\s*0x([0-9a-fA-F]+)ULL,\s*0x([0-9a-fA-F]+)ULL\s*\}", applet)
    saves = read(ROOT, "src/players/saves.rs")
    actual_uid = re.search(r'const UID: &str = "([a-f0-9]+)"', saves)
    check("Save user", actual_uid[1], f"{int(uid[2], 16):016x}{int(uid[1], 16):016x}")

    packs = read(longshot, "scripts/devtools/packs.py")
    shaders = read(ROOT, "src/storage/shaders.rs")
    expected_caches = list(ast.literal_eval(assigned(packs, "CACHES")))
    # The builder seeds this notes file to prepare shaders. Storage cleanup preserves it.
    expected_caches.remove("longshot_pipelines.bin")
    check("Removable shader cache names", re.findall(r'"([^"]+)"', rust_array(shaders, "GAME_FILES")), expected_caches)
    play = ast.literal_eval(assigned(packs, "PLAY_FILES").args[0])
    prefix = re.search(r'\.strip_prefix\("([^"]+)"\)', shaders[shaders.index("fn is_game_cache"):])[1]
    check("Play pipeline cache name", f"{prefix}[0-9]+\\.bin", play)

    events = read(longshot, "docs/dev/reference/build-events.md")
    codes = events.split("## Codes\n", 1)[1].split("\n## ", 1)[0]
    table = [line for line in codes.splitlines() if line.startswith("|")]
    check("Builder error codes", read(ROOT, "tests/fixtures/longshot-builder/codes.md").splitlines(), table)
    process = read(ROOT, "src/game/process.rs")
    for label, name in (("The game files", "GAME_FILE_CODES"), ("The builder's own failures", "BUILDER_FAILURE_CODES")):
        cell = next(row.split("|")[2].strip() for row in table if row.startswith(f"| {label} |"))
        expected = re.findall(r"(?:^|, )`([a-z_0-9]+)`", cell)
        check(label, sorted(re.findall(r'"([^"]+)"', rust_array(process, name))), sorted(expected))
    documented = {code for code in re.findall(r"`([a-z][a-z0-9_]+)`", codes)}
    for path in ("src/mods/builder.rs", "src/game/launch.rs"):
        source = read(ROOT, path)
        for code in re.findall(r'\["code"\] == "([a-z_]+)"', source):
            if code not in documented:
                raise SystemExit(f"{path} handles an undocumented builder code: {code}")
    play_status = read(ROOT, "src/game/launch.rs").split("match status.code()", 1)[1]
    for status in (120, 121, 123):
        if not re.search(rf"\b{status}\b", events) or f"Some({status}) =>" not in play_status:
            raise SystemExit(f"Review the builder play exit status {status}.")
    print(f"Verified languages, licenses, FP16 DLSS 5 files, save user, shader names and error codes against {ref[:10]}.", flush=True)


def check_desktop_names():
    for path in ("scripts/package.sh", "src/desktop.rs"):
        source = read(ROOT, path)
        if "Name=Longshot Launcher" not in source:
            raise SystemExit(f"{path} must keep the Longshot Launcher desktop entry name.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--longshot", type=Path, default=os.getenv("LONGSHOT"))
    parser.add_argument("--contract-only", action="store_true")
    args = parser.parse_args()
    if args.longshot:
        compare_builder(args.longshot.resolve())
    elif args.contract_only:
        parser.error("--contract-only requires a checkout of the pinned Longshot revision")
    else:
        print("Builder source checks require --longshot or LONGSHOT.", flush=True)
    if not args.contract_only:
        check("Tooling public keyring", read(ROOT, "scripts/release_keys.json"), read(ROOT, "assets/keys/release_keys.json"))
        check_desktop_names()
        subprocess.run([sys.executable, "-I", "scripts/translations.py"], cwd=ROOT, check=True)
        subprocess.run(["cargo", "test", "--locked"], cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
