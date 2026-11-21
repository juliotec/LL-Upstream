"""The launcher's core library (Core/): tests/launcher_core compiles its sources and checks them over authored inputs, with
no game, no network and no window. The version vectors (TRACKS, ORDER, UNORDERED) are this file's own; the signed release files
are tests/fixtures/release's (made by scripts/release.py). Its scratch folder is named "ゲーム Zélda 🎮", as tests/test_utf8_paths.py's.

    python -I tests/test_launcher_core.py [--dotnet <dotnet>] [--strict] [--longshot <checkout of MagicBeanDev/Longshot>] [--pinned]

With --longshot it also checks what the launcher copies from Longshot: make_game.py's languages, the SDK commits the DLSS and
FSR 4 licence texts name, the first-import player ID, the shader caches' names, the translator's stamped files and its
compiler's tools.lock parts, and what the save editor (scripts/longshot_save_editor.py) prints, which the launcher's Progress view
reads (tests/save_editor_fixtures.py; skipped for a Longshot from before the editor). CI checks out the commit tests/longshot.ref pins (--pinned
makes the test refuse a checkout that is at another commit); a scheduled CI run also checks Longshot's main, as advice. To
move the pin, see docs/ci.md.

With no dotnet here the check is skipped (exit 0, saying why), unless --strict.
--strict also builds and runs the app-level settings check (which needs the launcher's Avalonia packages).
"""
import argparse
import ast
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
LONGSHOT_REF = ROOT / "tests" / "longshot.ref"
RUN_ALL_ARGS = ["--dotnet", "{dotnet}"]


# The version order the launcher keeps (Core/Versions.cs, docs/releases.md): (version, channel). Longshot's versions
# start at v3.0.0: what came before is no version, on no track
TRACKS = [
    ("v3.0.0", "stable"), ("v3.1.0", "stable"), ("v10.0.0", "stable"),
    # (the release candidates there were: no version now, on the stable track like any other name)
    ("v3.1.0-rc.1", "stable"), ("v3.1.0-rc.12", "stable"),
    ("nightly-2029-c544828f", "nightly"), ("nightly-2029-c544828f-dirty", "nightly"), ("nightly-1-00000000", "nightly"),
    # (not a nightly's name: the count, then 8 lowercase hex; nor git describe's, nor a version before 3.0.0)
    ("nightly-2029", "stable"), ("nightly-2029-c544828", "stable"), ("nightly-2029-C544828F", "stable"),
    ("nightly-x-c544828f", "stable"), ("nightly-1234567890-c544828f", "stable"),
    ("v3.1.0-rc.1-3-g1a2b3c4", "stable"), ("v2.1.0-rc.1", "stable"), ("v2.0.7", "stable"),
    # (a pull request's build: pr-<number>-<8 lowercase hex of its head>, on its own track; anything else is no version)
    ("pr-222-1a2b3c4d", "pr"), ("pr-1-00000000", "pr"),
    ("pr-0-1a2b3c4d", "stable"), ("pr-222-1A2B3C4D", "stable"), ("pr-222-1a2b3c4", "stable"), ("pr-222", "stable"),
]
# newer, older (by number; nightlies by their count, a changed checkout after the build of its commit)
ORDER = [
    ("v3.1.0", "v3.0.14"), ("v3.0.10", "v3.0.9"), ("v4.0.0", "v3.9.9"), ("v10.0.0", "v9.0.0"),
    ("nightly-2030-0a1b2c3d", "nightly-2029-c544828f"), ("nightly-10000-aaaaaaaa", "nightly-9999-bbbbbbbb"),
    ("nightly-2029-c544828f-dirty", "nightly-2029-c544828f"),
]
# neither newer than the other: a nightly and a release (a nightly's name has no version in it), the numbers before 3.0.0
# and git describe's names, an old release candidate, and a name that is no version
UNORDERED = [
    ("v3.1.0", "v3.1.0-rc.2"), ("v3.1.0-rc.2", "v3.1.0-rc.1"), ("v3.1.0-rc.1", "v3.0.14"),
    ("nightly-2029-c544828f", "v3.1.0"), ("nightly-2029-c544828f", "v3.1.0-rc.3"), ("nightly-1-aaaaaaaa", "v3.0.1"),
    ("v3.1.0", "v2.9.0"), ("v3.0.0", "v2.2.0-rc.1"), ("v3.1.0-rc.3-248-gc544828f", "v3.1.0"),
    ("nightly-2029-c544828f", "dev"), ("v3.1.0", "dev"), ("nightly-2029-c544828f", "nightly"),
    ("pr-222-bbbbbbbb", "pr-222-1a2b3c4d"), ("pr-223-1a2b3c4d", "pr-222-1a2b3c4d"), ("pr-222-1a2b3c4d", "nightly-2029-c544828f"),
    ("pr-222-1a2b3c4d", "v3.1.0"),
]

def vectors():
    return {"tracks": TRACKS, "order": ORDER, "unordered": UNORDERED}


def read_pin(path=LONGSHOT_REF):
    """The Longshot commit tests/longshot.ref pins: the first line that is not blank or a # comment, 40 lowercase hex digits"""
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            if not re.fullmatch(r"[0-9a-f]{40}", line):
                raise SystemExit("%s: not a full commit id: %r" % (path, line))
            return line
    raise SystemExit("%s names no commit" % path)


def check_pin(longshot):
    """The --longshot checkout is the commit tests/longshot.ref pins (CI clones it from the pin; a stale clone would test nothing)"""
    want = read_pin()
    done = subprocess.run(["git", "-C", str(longshot), "rev-parse", "HEAD"], capture_output=True, text=True)
    have = done.stdout.strip()
    if done.returncode or have != want:
        raise SystemExit("%s is at %s, not the %s tests/longshot.ref pins" % (longshot, have or "(no commit)", want))


def check_languages(longshot):
    """Core/Languages.cs lists Longshot's make_game.py LANGUAGES, in its order (the codes --language takes)"""
    tree = ast.parse((longshot / "nx2" / "make_game.py").read_text(encoding="utf-8"))
    want = next(ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign) and len(n.targets) == 1
                and isinstance(n.targets[0], ast.Name) and n.targets[0].id == "LANGUAGES")
    source = (ROOT / "Core" / "Languages.cs").read_text(encoding="utf-8")
    body = source[source.index("All = new[]"):source.index("};", source.index("All = new[]"))]
    have = re.findall(r'\("([^"]+)", "([^"]+)"\)', body)
    if have != [tuple(x) for x in want]:
        raise SystemExit("Core/Languages.cs differs from make_game.py's LANGUAGES:\n%s\n%s" % (have, want))


def check_licences(longshot):
    """The DLSS, FSR 4 and XeSS licences the launcher shows before a build fetches the SDKs link the commits Longshot fetches
    (CONTRIBUTING.md: a change to the pinned release changes the licence text the player accepts): scripts/fetch.py's
    DLSS_COMMIT, FSR_COMMIT and XESS_COMMIT, or, in a Longshot from before #1802 (nightly-611), the COMMIT of scripts/ngx_sdk.py,
    scripts/ffx_sdk.py and scripts/xess_sdk.py. A Longshot from before scripts/xess_sdk.py has no XeSS download to check"""
    source = (ROOT / "App" / "ViewModels" / "SettingsStepViewModel.cs").read_text(encoding="utf-8")

    def assigned(script, name):
        tree = ast.parse((longshot / "scripts" / script).read_text(encoding="utf-8"))
        value = next((ast.literal_eval(n.value) for n in tree.body if isinstance(n, ast.Assign) and len(n.targets) == 1
                      and isinstance(n.targets[0], ast.Name) and n.targets[0].id == name), None)
        if value is None:
            raise SystemExit("Longshot's scripts/%s has no %s: review the licence check against how it pins the SDKs now" % (script, name))
        return value

    if (longshot / "scripts" / "fetch.py").is_file():
        pins = [("fetch.py", name) for name in ("DLSS_COMMIT", "FSR_COMMIT", "XESS_COMMIT")]
    else:
        pins = [(script, "COMMIT") for script in ("ngx_sdk.py", "ffx_sdk.py", "xess_sdk.py")
                if script != "xess_sdk.py" or (longshot / "scripts" / script).is_file()]
    for script, name in pins:
        commit = assigned(script, name)
        if commit not in source:
            raise SystemExit("App/ViewModels/SettingsStepViewModel.cs's licence text does not name scripts/%s's %s %s"
                             % (script, name, commit))


def check_xess_fetch(longshot):
    """Core/MakeGameCommand.cs's XessBuilds takes scripts/xess_sdk.py or scripts/fetch.py in a version's tools as the sign that
    its make_game.py fetches Intel's XeSS library (--fetch-xess-runtime). A Longshot whose make_game.py takes the flag with
    neither script would have its XeSS switch greyed out in the launcher, as every nightly from 611 was until XessBuilds took
    fetch.py too"""
    make_game = (longshot / "nx2" / "make_game.py").read_text(encoding="utf-8")
    if "--fetch-xess-runtime" not in make_game:
        return
    if not any((longshot / "scripts" / script).is_file() for script in ("xess_sdk.py", "fetch.py")):
        raise SystemExit("Longshot's make_game.py takes --fetch-xess-runtime, but its tools carry neither scripts/xess_sdk.py nor "
                         "scripts/fetch.py: Core/MakeGameCommand.cs's XessBuilds would grey Intel XeSS out")


def check_save_user(longshot):
    """A first import must use the player the game opens, not the emulator's profile ID."""
    host = (longshot / "host" / "nn_applet.c").read_text(encoding="utf-8")
    uid = re.search(r"user_uid\[2\]\s*=\s*\{\s*0x([0-9a-fA-F]+)ULL,\s*0x([0-9a-fA-F]+)ULL\s*\}", host)
    if uid is None:
        raise SystemExit("Longshot's user_uid changed: review the first-import save destination.")
    expected = f"{int(uid[2], 16):016x}{int(uid[1], 16):016x}"
    source = (ROOT / "Core" / "Saves.cs").read_text(encoding="utf-8")
    if f'UserId = "{expected}"' not in source:
        raise SystemExit("Core/Saves.cs's UserId differs from Longshot's player: " + expected)


def check_shader_caches(longshot):
    """Core/ShaderCaches.cs names the caches Longshot's scripts/dev_packs.py copies (CACHES, PLAY_FILES) and stamps the
    translator with the folders and files of nx2/translator_files.py (DIRS, FILES), in its order, and its compiler with parts
    tools.lock unpacks, each system's cpm among them"""
    def literal(script, name):
        tree = ast.parse((longshot / script).read_text(encoding="utf-8"))
        return next(n.value for n in tree.body if isinstance(n, ast.Assign) and len(n.targets) == 1
                    and isinstance(n.targets[0], ast.Name) and n.targets[0].id == name)
    source = (ROOT / "Core" / "ShaderCaches.cs").read_text(encoding="utf-8")

    def listed(name):
        at = source.index(name + " = new[]")
        return re.findall(r'"([^"]+)"', source[at:source.index("};", at)])
    for name, script, theirs in (("Names", "scripts/dev_packs.py", "CACHES"), ("TranslatorDirs", "nx2/translator_files.py", "DIRS"),
                                 ("TranslatorFiles", "nx2/translator_files.py", "FILES")):
        want = list(ast.literal_eval(literal(script, theirs)))
        if listed(name) != want:
            raise SystemExit("Core/ShaderCaches.cs's %s differs from %s's %s:\n%s\n%s" % (name, script, theirs, listed(name), want))
    play = ast.literal_eval(literal("scripts/dev_packs.py", "PLAY_FILES").args[0])
    if play != r"longshot_vk_pipelines_play_[0-9]+\.bin" or 'PlayPrefix = "longshot_vk_pipelines_play_"' not in source:
        raise SystemExit("scripts/dev_packs.py's PLAY_FILES changed: review Core/ShaderCaches.cs's IsCache (%s)" % play)
    # (the compiler's parts, by the folder tools.lock unpacks them to: each a dest of its system's lines, and its cpm among
    # them, where nx2/build.py's header_package takes the Vulkan and SPIR-V headers from, RX_CPM)
    dests, cpm = {}, {}
    for line in (longshot / "tools.lock").read_text(encoding="utf-8").splitlines():
        w = line.split()
        if len(w) >= 9 and w[0] == "tool":
            dests.setdefault(w[3], set()).add(w[6])
            if w[1] == "cpm":
                cpm[w[3]] = w[6]
    for name, system in (("WindowsCompiler", "windows-x64"), ("LinuxCompiler", "linux-x64")):
        missing = [d for d in listed(name) if d not in dests.get(system, set())]
        if not listed(name) or missing:
            raise SystemExit("Core/ShaderCaches.cs's %s names folders tools.lock's %s lines do not unpack to: %s" % (name, system, missing))
        if cpm.get(system) not in listed(name):
            raise SystemExit("Core/ShaderCaches.cs's %s lacks tools.lock's %s cpm (%s), where the translator's headers come from"
                             % (name, system, cpm.get(system)))


def check_running_excludes_own_runtime():
    """(#118) Running() must ask IsOwnRuntime about each process's argv[0] before counting it: excluding only the
    launcher's own pid leaves its AppImage runtime (a different process, comm like the file's name, argv[0] the file)
    in the candidate list, and a Gear-Lever rename (longshot-2.appimage) made that name read as a running game"""
    saves = (ROOT / "Core" / "Saves.cs").read_text(encoding="utf-8")
    if "IsOwnRuntime(argv0, ownImage)" not in saves:
        raise SystemExit("Core/Saves.cs: Running() must skip the process whose argv[0] is the launcher's own $APPIMAGE "
                         "(IsOwnRuntime(argv0, ownImage)) — pid-only self-exclusion misses the AppImage runtime (#118)")


def check_package_desktop_name():
    """(#118) The AppImage's own menu entry must not call the launcher just "Longshot": Gear Lever names and dedupes
    shortcuts after the app's name, and renamed the launcher's file to longshot[-2].appimage, a name the launcher's
    own game-AppImage detection reads as the game (its update then refused forever). The launcher's Settings menu
    entry (Core/Shortcuts.cs) already says "Longshot Launcher"; the packaged one must say the same, so a shortcut
    manager names the launcher after itself, never after the game it builds."""
    pack = (ROOT / "scripts" / "package.sh").read_text(encoding="utf-8")
    entry = pack.split("[Desktop Entry]", 1)[1].split("EOF", 1)[0] if "[Desktop Entry]" in pack else ""
    names = [l.strip() for l in entry.splitlines() if l.strip().startswith("Name=")]
    if names != ["Name=Longshot Launcher"]:
        raise SystemExit("package.sh: the AppImage's desktop entry must be Name=Longshot Launcher, not %s: Gear Lever "
                         "renames the file after the name, and a bare Longshot name makes the launcher look like the "
                         "game it runs (#118)" % names)


def check_save_editor(longshot):
    """The launcher's Progress view (Core/SaveEditor.cs) reads what Longshot's save editor prints: tests/fixtures/save-editor is
    that output on the made-up game of Longshot's own tests, and must be what the checkout prints now. A Longshot from before the
    editor, or a Python with no zstd for the made-up game, has nothing to check: said, not a failure"""
    sys.path.insert(0, str(ROOT / "tests"))
    import save_editor_fixtures
    try:
        now = save_editor_fixtures.outputs(longshot)
    except save_editor_fixtures.Unavailable as e:
        print("save editor fixtures: skipped: %s" % e)
        return
    save_editor_fixtures.check(now)


def check_translations():
    """The catalogs' texts are the window's, with their placeholders and plural forms (scripts/translations.py). A text a catalog
    lacks shows in English: said, not a failure"""
    sys.path.insert(0, str(ROOT / "scripts"))
    import translations
    _, faults, lacking = translations.check()
    for code, texts in lacking.items():
        if texts:
            print("translations: Core/Lang/%s.json lacks %d texts (they show in English)" % (code, len(texts)))
    if faults:
        raise SystemExit("translations (python -I scripts/translations.py):\n" + "\n".join(faults[:40]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default=None)
    parser.add_argument("--strict", action="store_true")
    parser.add_argument("--longshot", type=Path, default=None)
    parser.add_argument("--pinned", action="store_true", help="--longshot must be at the commit tests/longshot.ref pins")
    args = parser.parse_args()
    if args.pinned and not args.longshot:
        raise SystemExit("--pinned needs --longshot <checkout of MagicBeanDev/Longshot>")
    check_translations()
    check_package_desktop_name()
    check_running_excludes_own_runtime()
    if args.longshot:
        if args.pinned:
            check_pin(args.longshot)
        check_languages(args.longshot)
        check_licences(args.longshot)
        check_xess_fetch(args.longshot)
        check_save_user(args.longshot)
        check_shader_caches(args.longshot)
        check_save_editor(args.longshot)
    elif args.strict:
        raise SystemExit("--strict needs --longshot <checkout of MagicBeanDev/Longshot>")
    dotnet = args.dotnet or shutil.which("dotnet") or (str(Path.home() / ".dotnet" / "dotnet")
                                                       if (Path.home() / ".dotnet" / "dotnet").is_file() else None)
    if not dotnet:
        if args.strict:
            raise SystemExit("no dotnet found (pass --dotnet)")
        print("skipped: no dotnet here (pass --dotnet <path>)")
        return
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")
    if not env.get("DOTNET_ROOT") and os.path.isabs(dotnet):
        env["DOTNET_ROOT"] = os.path.dirname(os.path.realpath(dotnet))
    with tempfile.TemporaryDirectory(prefix="launcher-core-") as folder:
        root = Path(folder)
        (root / "vectors.json").write_text(json.dumps(vectors(), ensure_ascii=False), encoding="utf-8")
        if args.strict:
            app_out = root / "app-build"
            subprocess.run([dotnet, "build", str(ROOT / "tests" / "launcher_app" / "LauncherAppTests.csproj"),
                            "--configuration", "Release", "--artifacts-path", str(app_out), "--disable-build-servers", "-nologo", "-v:q"],
                           cwd=root, env=env, check=True)
            app_dll = next((app_out / "bin" / "LauncherAppTests").rglob("LauncherAppTests.dll"))
            subprocess.run([dotnet, str(app_dll)], cwd=root, env=env, check=True, timeout=300)
        out = root / "build"
        subprocess.run([dotnet, "build", str(ROOT / "tests" / "launcher_core" / "LauncherCoreTests.csproj"), "--configuration", "Release",
                        "--artifacts-path", str(out), "--disable-build-servers", "-nologo", "-v:q"], cwd=root, env=env, check=True)
        dll = next((out / "bin").rglob("LauncherCoreTests.dll"))
        scratch = root / "ゲーム Zélda 🎮"
        subprocess.run([dotnet, str(dll), str(root / "vectors.json"), str(ROOT / "tests" / "fixtures"), str(scratch)],
                       cwd=root, env=env, check=True, timeout=300)


if __name__ == "__main__":
    main()
