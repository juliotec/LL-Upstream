"""What Longshot's save editor (scripts/longshot_save_editor.py) prints, for the launcher's tests (tests/launcher_core/SaveEditorChecks.cs).

The launcher runs that script from a build's own tools and reads what it prints (Core/SaveEditor.cs). The fixtures in
tests/fixtures/save-editor are its real output (--json: docs/save-editor.md, "For programs") on the made-up game and the made-up saves that Longshot's own tests build
(tests/test_longshot_save_editor.py: make_game, make_save_folder), in temporary folders: no game file and no save is read or kept
here, and the fixtures hold text only. With --longshot, tests/test_launcher_core.py runs this again against the checkout and fails
when the tool now prints something else, so a change to the report or to an edit's output reaches the launcher's tests first.

    python -I tests/save_editor_fixtures.py --longshot <checkout of MagicBeanDev/Longshot> [--write]

--write replaces the fixtures with what the checkout prints now (then read the diff: it is the contract changing).
A Longshot from before the editor has no script: nothing is checked (--require makes that an error).
"""
import argparse
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import sys
import tempfile
from unittest import mock

HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "fixtures" / "save-editor"
PROFILE = "5a3c9e1f0b7d24680000000000000001"


class Unavailable(Exception):
    """The checkout cannot run the editor here (no script, or no zstd for the made-up game's files)"""


def load(longshot):
    longshot = Path(longshot)
    if not (longshot / "scripts" / "longshot_save_editor.py").is_file():
        raise Unavailable("%s has no scripts/longshot_save_editor.py (a Longshot from before the save editor)" % longshot)
    test = longshot / "tests" / "test_longshot_save_editor.py"
    if not test.is_file():
        raise Unavailable("%s has no tests/test_longshot_save_editor.py: no made-up game to run the editor on" % longshot)
    sys.dont_write_bytecode = True
    sys.path.insert(0, str(longshot / "scripts"))
    spec = importlib.util.spec_from_file_location("longshot_save_editor_tests", test)
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except ImportError as e:
        raise Unavailable("the editor's tests need a module this Python lacks: %s" % e) from e
    return module


def outputs(longshot):
    """{fixture name: text}: each command's output on the made-up game, its folders and its backup's time named by placeholders"""
    t = load(longshot)
    editor = t.editor
    with tempfile.TemporaryDirectory(prefix="launcher-save-editor-") as tmp:
        tmp = Path(tmp)
        try:
            game_dir = t.make_game(tmp / "game")
            game = editor.Game(game_dir)
            game.catalogue
        except Exception as e:     # (no zstd: the made-up game's files are zstd frames)
            raise Unavailable("the made-up game could not be read: %s" % e) from e
        save = tmp / "save" / PROFILE
        backups = tmp / "backups"
        t.make_save_folder(save, game, slots=(0, 1, 2, 3))
        write = lambda slot, **kw: (save / ("slot_%02d" % slot) / "progress.sav").write_bytes(t.make_sav(game, **kw))
        # (slot 1: a start; slot 2: damaged; slot 3: nothing missing: the same edit the player would make, `set all`)
        write(1, values={"IsGet.Item_Hookshot": True, "PouchData.Item_Hookshot.StockNum": 1, "PlayerStatus.MaxLife": 20,
                         "PlayerStatus.Life": 20, "HeartPiece_Fishing": True, "DefeatedEnemyNum.Enemy_Test": 2,
                         "Sequence_CurrentBanc": "TestArea_Child"}, keys=[t.SKULL["a1"]])
        (save / "slot_02" / "progress.sav").write_bytes(b"this is not a save file")

        def run(*args, running=()):
            out, err = io.StringIO(), io.StringIO()
            argv = ["--game", game_dir, "--save", str(save), "--backup-dir", str(backups)] + list(args)
            with mock.patch.object(editor.saves, "running_games", return_value=list(running)), \
                    contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = editor.main(argv)
            return code, out.getvalue(), err.getvalue()

        def clean(text):
            text = text.replace(str(save), "<save>").replace(str(backups), "<backups>").replace(str(tmp), "<tmp>")
            return re.sub(r"(edit-[0-9a-f]{16}-)\d{8}-\d{6}(-\d+)?", r"\1<stamp>", text)

        def ok(*args):
            code, out, err = run(*args, "--json")
            assert code == 0 and not err.strip(), (args, err)
            assert json.loads(out)["ok"] is True, (args, out)
            return clean(out)

        def fails(*args, running=()):
            code, out, err = run(*args, "--json", running=running)
            assert code == 1 and not err.strip(), (args, code, out, err)     # (a refusal is an answer on stdout)
            assert json.loads(out)["ok"] is False, (args, out)
            return clean(out)

        ok("set", "slot_03", "all")
        ok("give", "slot_03", "hearts=5")
        (save / "slot_06").mkdir()
        write(6, revision=t.REVISION + 1)       # (another game version's flag list)
        (save / "slot_06" / "caption.sav").write_bytes(t.make_sav(game, caption=True, revision=t.REVISION + 1))
        shown = {}
        shown["report.json"] = ok("report")
        shown["items.json"] = ok("items")
        shown["places.json"] = ok("places")
        shown["set-dry.json"] = ok("set", "slot_00", "skulltulas/TestArea", "--dry-run")
        shown["give-dry.json"] = ok("give", "slot_00", "hookshot", "rupees=0", "--dry-run")
        shown["start-dry.json"] = ok("start", "slot_00", "TestArea_Child/Gate", "--dry-run")
        shown["copy-dry.json"] = ok("copy", "slot_00", "slot_04", "--dry-run")
        shown["error-hearts.json"] = fails("give", "slot_00", "hearts=10")
        shown["error-bomb.json"] = fails("give", "slot_00", "Item_Bomb=5")
        shown["error-target.json"] = fails("set", "slot_00", "nothing")
        shown["error-copy.json"] = fails("copy", "slot_00", "slot_01")
        shown["error-slot.json"] = fails("set", "slot_07", "all")
        shown["error-damaged.json"] = fails("set", "slot_02", "all")
        shown["error-version.json"] = fails("set", "slot_06", "all")
        shown["error-running.json"] = fails("set", "slot_00", "skulltulas", running=["Longshot"])
        shown["set.json"] = ok("set", "slot_00", "skulltulas/TestArea")
        shown["set-nothing.json"] = ok("set", "slot_00", "skulltulas/TestArea")
        # (a write that fails after the backup: the files are put back, and the answer names the backup)
        real = editor.osf.write_atomic

        def failing(how):
            calls = []

            def write(path, data):
                calls.append(path)
                if len(calls) == 1:
                    if how == "io":
                        raise OSError("disk full")
                    return real(path, data + b"x")      # (reads back different)
                return real(path, data)
            return write
        for how, name in (("io", "error-io.json"), ("different", "error-write.json")):
            with mock.patch.object(editor.osf, "write_atomic", failing(how)):
                shown[name] = fails("set", "slot_00", "chests/TestTemple")
        return shown


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--longshot", type=Path, required=True)
    parser.add_argument("--write", action="store_true", help="replace the fixtures with what the checkout prints now")
    parser.add_argument("--require", action="store_true", help="a checkout with no editor is an error")
    args = parser.parse_args()
    try:
        now = outputs(args.longshot)
    except Unavailable as e:
        if args.require:
            raise SystemExit(str(e))
        print("save editor fixtures: skipped: %s" % e)
        return
    if args.write:
        FIXTURES.mkdir(parents=True, exist_ok=True)
        for name, text in now.items():
            (FIXTURES / name).write_text(text, encoding="utf-8", newline="\n")
        print("wrote %d files to %s" % (len(now), FIXTURES))
        return
    check(now)
    print("save editor fixtures: the editor prints what %s holds" % FIXTURES)


def check(now):
    """The fixtures are exactly what the editor prints (SystemExit, naming the fixture, when not)"""
    have = {p.name for p in FIXTURES.iterdir() if p.is_file()} if FIXTURES.is_dir() else set()
    if set(now) != have:
        raise SystemExit("tests/fixtures/save-editor holds %s, the editor's output has %s (python -I tests/save_editor_fixtures.py --longshot ... --write)"
                         % (sorted(have), sorted(now)))
    for name, text in now.items():
        json.loads(text)
        if (FIXTURES / name).read_text(encoding="utf-8") != text:
            raise SystemExit("tests/fixtures/save-editor/%s is not what Longshot's save editor prints now: the interface the launcher "
                             "reads changed (python -I tests/save_editor_fixtures.py --longshot <checkout> --write, then read the diff "
                             "and Core/SaveEditor*.cs)" % name)


if __name__ == "__main__":
    main()
