using System.Diagnostics;
using System.Text.Json;
using LongshotLauncher.Core;

/// The save editor's core (Core/SaveEditor*.cs): Longshot's scripts/longshot_save_editor.py's JSON answers (report, items, places, each
/// edit, each refusal with its code), the command the launcher runs and the refusals, over tests/fixtures/save-editor (the editor's
/// real answers on the made-up game of Longshot's own tests; tests/save_editor_fixtures.py checks them against a Longshot checkout).
/// No save and no game file is read: a stand-in for the tools' Python stands in for the tool
static partial class Program
{
    static void SaveEditorChecks(string fixtures)
    {
        var shown = Path.Combine(fixtures, "save-editor");
        string Shown(string name) => File.ReadAllText(Path.Combine(shown, name));

        // ---- the report: slots, categories with what they have, areas, and what is missing by name ----
        var report = SaveEditorReport.Parse(Shown("report.json"));
        Require(report.Save == "<save>" && report.Slots.Select(s => s.Slot).SequenceEqual(new[] { "slot_00", "slot_01", "slot_02", "slot_03", "slot_06" }),
                "The report: the save folder and its five slots, in the tool's order.");
        Require(report.Slots.Select(s => s.Number).SequenceEqual(new[] { 1, 2, 3, 4, 7 }), "slot_00 is Slot 1 on the Saves page, as SaveText.Number counts.");
        Require(report.Slots[4].Problem!.Contains("another game version") && report.Slots[4].Categories.Count == 19, "A slot of another game version: read, with the editor's warning.");
        var empty = report.Slots[0];
        Require(empty is { Unreadable: null, Problem: null, Age: "child", Hearts: 3, Rupees: 0, Place: null, Clock: "day 0 00:00", Saved: "2026-11-09 20:08", Cleared: false, Complete: false, MissingTotal: 38 },
                "A new slot: Link a child with 3 hearts, no place, the game's clock, saved when, 38 missing.");
        Require(empty.Categories.Count == 19 && empty.Listed.Count == 18 && empty.Listed.All(c => c.Id != "rupees"),
                "Nineteen categories, one the game has nothing in (no wallet: rupees 0 of 0) left out of what is listed.");
        var chests = empty.Categories.Single(c => c.Id == "chests");
        Require(chests is { Title: "Chests opened", Have: 0, Of: 6, Counted: true } && !chests.Complete && chests.Fraction == 0
                && chests.Areas.Select(a => (a.Area, a.Have, a.Of)).SequenceEqual(new[] { ("TestArea", 0, 1), ("TestTemple", 0, 5) }),
                "Chests: 0 of 6, in two areas.");
        var first = chests.Missing[0];
        Require(first == new SaveEditorEntry("chests/TestArea/1", "Chest 1 in Test Area (Obj Heart Piece)", "TestArea", 0, 1, "room -1 at 1, 2, 3") && chests.Missing.Count == 6,
                "A missing chest: the id set takes, its name and area in the game's text, and where it is.");
        Require(empty.Categories.Single(c => c.Id == "small-keys").Missing.Single() is { Id: "small-keys/TestTemple/keys", Have: 0, Of: 3, Note: "0 in hand" },
                "Small keys: found against what the dungeon places, with the ones in hand.");
        var mini = empty.Categories.Single(c => c.Id == "mini-games");
        Require(!mini.Counted && mini.Note!.StartsWith("not counted") && mini.Missing.Count == 1, "Mini-games are listed and never counted.");
        var partial = report.Slots[1];
        Require(partial.Place == "Test Area" && partial.MissingTotal == 32 && partial.Categories.Single(c => c.Id == "hearts") is { Have: 5, Of: 5, Complete: true },
                "A started slot: its place, hearts and what is left.");
        var items = partial.Categories.Single(c => c.Id == "items");
        Require(items is { Have: 1, Of: 3 } && Math.Abs(items.Fraction - 1.0 / 3) < 1e-9 && items.Missing.Select(m => m.Id).SequenceEqual(new[] { "items/Item_Bomb", "items/Item_Bottle_Poe" })
                && items.Missing[0].Note == "0 of 0", "Items: what is held is not listed, a counted item says its amount against its cap.");
        var damaged = report.Slots[2];
        Require(damaged.Unreadable == "slot_02/progress.sav is 23 bytes: not a save file" && damaged.Categories.Count == 0 && damaged.Number == 3,
                "A damaged slot says why, and does not hide the others.");
        var full = report.Slots[3];
        Require(full is { Complete: true, MissingTotal: 0, Rupees: 500 } && full.Categories.Where(c => c.Counted).All(c => c.Complete),
                "A slot with nothing missing says so; the mini-games do not count.");
        Require(SaveEditorText.Percent(0, 6) == 0 && SaveEditorText.Percent(5, 6) == 83 && SaveEditorText.Percent(599, 600) == 99 && SaveEditorText.Percent(6, 6) == 100
                && SaveEditorText.Percent(0, 0) == 100, "A percentage is 100 only when nothing is missing.");
        Require(SaveEditorText.Humanize("KakarikoVillage") == "Kakariko Village" && SaveEditorText.Humanize("Weapon_Sword_Master") == "Weapon Sword Master"
                && SaveEditorText.Humanize("Test_Area2Gate") == "Test Area2 Gate" && SaveEditorText.Humanize("  a__b ") == "a b",
                "Names cut into words as the tool cuts them (humanize).");
        Require(SaveEditorText.Hearts(3) == "3" && SaveEditorText.Hearts(10.5) == "10.5" && SaveEditorText.Hearts(0.25) == "0.25", "Hearts as %g says them.");
        foreach (var bad in new[] { "", "not json", "[]", """{"save": "x", "slots": []}""", """{"schema": 1, "command": "report", "ok": true, "save": "x"}""",
                                    """{"schema": 1, "command": "report", "ok": true, "save": "x", "slots": [{"slot": "slot_00"}]}""",
                                    """{"schema": 1, "command": "report", "ok": true, "save": 1, "slots": []}""",
                                    """{"schema": 1, "command": "items", "ok": true, "save": "x", "slots": []}""",
                                    """{"schema": 2, "command": "report", "ok": true, "save": "x", "slots": []}""",
                                    """{"schema": 1, "command": "report", "ok": false, "save": "x", "slots": []}""" })
            Throws<SaveFailure>(() => SaveEditorReport.Parse(bad), "An answer in another form is refused: " + bad);
        Require(Assert<SaveFailure>(() => SaveEditorReport.Parse("""{"schema": 2, "command": "report", "ok": true, "save": "x", "slots": []}""")).Message.Contains("newer than the launcher"),
                "A newer schema says the launcher is older.");
        Require(SaveEditorReport.Parse("""{"schema": 1, "command": "report", "ok": true, "save": "x", "slots": []}""").Slots.Count == 0, "Schema 1: the form this launcher reads.");

        // ---- the screenshots' report (tools/screenshots/save-editor-report.json, authored with invented names): in the same form ----
        var drawn = SaveEditorReport.Parse(File.ReadAllText(Path.Combine(fixtures, "..", "..", "tools", "screenshots", "save-editor-report.json"))).Slots.Single();
        Require(drawn.Categories.Count == 19 && drawn.MissingTotal == drawn.Categories.Where(c => c.Counted).Sum(c => c.Of - c.Have) && !drawn.Complete,
                "The screenshots' slot: nineteen categories, and the missing total is what the counted ones lack.");
        foreach (var c in drawn.Categories)
            Require((c.Have >= c.Of) == (c.Missing.Count == 0) && c.Missing.Count <= c.Of - c.Have && (c.Areas.Count == 0 || (c.Areas.Sum(a => a.Have) == c.Have && c.Areas.Sum(a => a.Of) == c.Of))
                    && c.Missing.All(m => m.Id.StartsWith(c.Id + "/") && (m.Area.Length == 0 || c.Areas.Any(a => a.Area == m.Area))),
                    "The screenshots' " + c.Id + ": what is missing and the areas add up to what it has of what there is.");

        // ---- the lists give and start take ----
        JsonDocument Answer(string name, string command) => SaveEditorAnswer.Of(new SaveEditorRun(0, Shown(name), ""), command);
        using (var doc = Answer("items.json", "items"))
        {
            var give = SaveEditing.ReadItems(doc.RootElement);
            Require(give.Items.Count == 12 && give.Items[0] == new SaveEditorItem("equipment", "Weapon_Sword_Master", "Master Sword", false)
                    && give.Items.Single(i => i.Counted) == new SaveEditorItem("items", "Item_Bomb", "Item Bomb", true)
                    && give.Items[^1] == new SaveEditorItem("quest", "Item_Stone_Forest", "Item Stone Forest", false),
                    "The items: each with its id, name and category, the counted one marked.");
            Require(give.SmallKeys.Single() == new SaveEditorKeys("TestTemple", 3) && give is { HeartsMin: 1, HeartsMax: 5 }, "The dungeons with small keys, and the hearts the game gives.");
        }
        using (var doc = Answer("places.json", "places"))
        {
            var places = SaveEditing.ReadPlaces(doc.RootElement);
            Require(places.Select(p => p.Spec).SequenceEqual(new[] { "NoFiles/(default)", "TestArea_Adult/(default)", "TestArea_Child/(default)", "TestArea_Child/Gate", "TestTemple/EntrancePos" })
                    && !places[0].CanLoad && places.Skip(1).All(p => p.CanLoad) && places[3] == new SaveEditorPlace("TestArea_Child", "Gate", "TestArea_Child/Gate", "child", true)
                    && places[2].Start == "" && places[4].Age == null && places[1].Age == "adult" && places[4].Name == "Test Temple: Entrance Pos",
                    "The places: area and start, the area's age (none: both have it), an area with no placement files marked, a default start with no name.");
        }

        // ---- what an edit says ----
        var set = SaveEditResult.Parse(Shown("set.json"), "set");
        Require(set is { Changed: true, Dry: false, Backup: "<backups>/edit-5a3c9e1f0b7d2468-<stamp>", ReadBack: { Complete: false, MissingTotal: 36 } } && set.Said.SequenceEqual(new[] { "set skulltulas/TestArea: 2 of 2 changed" })
                && set.Files.SequenceEqual(new[] { "slot_00/progress.sav", "slot_00/caption.sav" }),
                "A written edit: what it said, the files it wrote, the backup it made first, what it read back.");
        var nothing = SaveEditResult.Parse(Shown("set-nothing.json"), "set");
        Require(nothing is { Changed: false, Backup: null, ReadBack: null } && nothing.Said.Count == 1 && nothing.Files.Count == 0, "Nothing to change: no file, no backup, and it says so.");
        var dry = SaveEditResult.Parse(Shown("give-dry.json"), "give");
        Require(dry is { Changed: true, Dry: true, Backup: null, ReadBack: null } && dry.Said.SequenceEqual(new[] { "Hookshot: given", "rupees: 0" }), "A dry run: the lines, no backup.");
        Require(SaveEditResult.Parse(Shown("start-dry.json"), "start").Said.Single() == "start: child Link in TestArea_Child at 40.0, 5.0, 40.0"
                && SaveEditResult.Parse(Shown("copy-dry.json"), "copy") is { Said: [var copy], Files: ["slot_04/caption.sav", "slot_04/progress.sav"] } && copy == "copy slot_00 -> slot_04 (caption.sav, progress.sav)",
                "Start and copy say what they would do, and copy the files it would write.");
        Throws<SaveFailure>(() => SaveEditResult.Parse(Shown("set.json"), "give"), "An answer to another command is not this command's.");
        Throws<SaveFailure>(() => SaveEditResult.Parse("""{"schema": 1, "command": "set", "ok": true, "slot": "slot_00"}""", "set"), "An edit's answer without what it holds is refused.");

        // ---- a refusal: the editor's code, and the launcher's words for the ones it can say better ----
        SaveFailure Refused(string name, string command, int exit = 1) =>
            Assert<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(exit, Shown(name), ""), command).Dispose());
        var hearts = Refused("error-hearts.json", "give");
        var bomb = Refused("error-bomb.json", "give");
        Require(hearts.Code == "out_of_range" && hearts.Message == "hearts: 1 to 5 (3 to start, 1 heart containers, 4 heart pieces)" && hearts.Reason == SaveRefusal.Other
                && bomb.Code == "needs_bag" && bomb.Message.Contains("give Item_BombBag_Small first") && Refused("error-target.json", "set").Code == "unknown_target"
                && Refused("error-target.json", "set").Message == "nothing called 'nothing' (see: targets)",
                "A refusal that holds what only the editor knows (a count, the bag to give first, a name) is said in the editor's words, with its code.");
        var copy2 = Refused("error-copy.json", "copy");
        Require(copy2.Code == "would_overwrite" && copy2.Message == "That slot already holds a save. Choose to replace it, or another slot.", "A copy over a slot: the launcher's words.");
        var running = Refused("error-running.json", "set");
        Require(running.Code == "running" && running.Reason == SaveRefusal.Running && running.Who.SequenceEqual(new[] { "Longshot" }) && running.Message.StartsWith("Longshot is running."),
                "The editor's own running-game refusal is a Running refusal naming the games, in the launcher's words.");
        var version = Refused("error-version.json", "set");
        Require(version.Code == "other_version" && version.Message.StartsWith("This save was written by another game version") && version.Message.Contains("flag list revision 4243"),
                "Another game version: the launcher's words, then the editor's for the numbers.");
        Require(Refused("error-damaged.json", "set") is { Code: "damaged" } d && d.Message == "A save file is damaged: slot_02/progress.sav is 23 bytes: not a save file"
                && Refused("error-slot.json", "set") is { Code: "no_slot" } n && n.Message.StartsWith("That slot is not in the save any more."), "A damaged file and a slot that is gone.");
        var io = Refused("error-io.json", "set");
        var putBack = Refused("error-write.json", "set");
        Require(io.Code == "io_error" && io.Backup == "<backups>/edit-5a3c9e1f0b7d2468-<stamp>" && io.Message == "A file could not be read or written: disk full"
                && putBack.Code == "write_failed" && putBack.Backup == io.Backup && putBack.Message.StartsWith("The edit could not be written, and the files were put back as they were."),
                "A write that failed after the backup: the files put back, and the backup named.");
        Require(Say("link").StartsWith("The save folder holds a link") && Say("no_zstd").Contains("compressed files") && Say("needs_age").StartsWith("That area has both ages")
                && Say("same_slot").Contains("same slot") && Say("nx2navi_pending").StartsWith("NX2navi has a save waiting") && Say("no_game").StartsWith("The save editor could not read")
                && Say("unsupported_game_file").Contains("cannot read") && Say("backup_inside_save").Contains("inside the save folder"),
                "The codes the launcher says in its own words.");
        foreach (var code in new[] { "needs_bag", "out_of_range", "ambiguous_item", "unknown_item", "unknown_target", "unknown_flag", "unknown_place", "needs_value", "bad_value", "wrong_age",
                                     "not_loadable", "unsupported_save", "no_language", "no_save", "pick_profile", "bad_slot", "error", "a_code_of_a_newer_editor" })
            Require(Say(code) == "the editor's words", "The editor's own words stay for " + code + ".");
        string Say(string code) => SaveEditorAnswer.Say(code, "the editor's words", Array.Empty<string>());
        Require(SaveEditorAnswer.Say("running", "x", Array.Empty<string>()).StartsWith("A game is running."), "A running refusal that names no game says 'a game'.");
        Require(SaveEditorAnswer.Failure(new SaveEditorRun(1, Shown("error-hearts.json"), "")).Code == "out_of_range", "The refusal of a command that exited 1.");
        Require(Assert<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(3, "", ""), "set").Dispose()).Message.Contains("exit code 3")
                && Assert<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(2, "", "usage: x\nlongshot_save_editor.py: error: the following arguments are required: slot\n"), "set").Dispose()).Message.Contains("arguments are required")
                && Assert<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(1, "", "Traceback (most recent call last):\nValueError: x\n"), "set").Dispose()).Message == "ValueError: x"
                && Assert<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(0, "no json here", ""), "set").Dispose()).Message.Contains("not in a form"),
                "A command that printed no answer: argparse's or Python's last line, its exit code, or 'not in a form'.");
        Throws<SaveFailure>(() => SaveEditorAnswer.Of(new SaveEditorRun(1, Shown("set.json"), ""), "set").Dispose(), "An ok answer from a command that exited 1 is not taken.");

        // ---- which build, which tools: the folder's own version's, with the editor's script and the game's files ----
        var root = Folder("save-editor");
        var toolchains = new Toolchains(Path.Combine(root, "tools"), new FakeFetcher());
        var game = Path.Combine(root, "ゲーム Zélda", "v3.4.0");
        Game(game, "v3.4.0");
        var build = new BuildEntry { Folder = game, Version = "v3.4.0" };
        Require(SaveEditing.Find(null, toolchains).Why == SaveEditorWhy.NoBuild && SaveEditing.Find(new BuildEntry { Folder = Path.Combine(root, "gone") }, toolchains).Why == SaveEditorWhy.NoBuild,
                "No build, or a folder that is gone: build first.");
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoGameFiles, "No romfs in the build's folder: the editor has no game to read.");
        Directory.CreateDirectory(Path.Combine(game, "romfs"));
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoTools, "No tools installed for the build's version.");
        var other = PerfTools(Path.Combine(root, "tools"), "v3.5.0");
        Touch(SaveEditing.ScriptOf(other)); Touch(Path.Combine(other.ToolsRoot, "scripts", "oot_savefile.py")); Touch(other.Python);
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoTools, "Another version's tools, with the editor, are not this build's.");
        var own = PerfTools(Path.Combine(root, "tools"), "v3.4.0");
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoTools, "The version's tools without their Python: none.");
        Touch(own.Python);
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoEditor, "A version whose tools have no save editor.");
        Touch(SaveEditing.ScriptOf(own));
        Require(SaveEditing.Find(build, toolchains).Why == SaveEditorWhy.NoEditor, "The script without the save file code beside it: no editor.");
        Touch(Path.Combine(own.ToolsRoot, "scripts", "oot_savefile.py"));
        var (tool, why) = SaveEditing.Find(build, toolchains);
        Require(why == SaveEditorWhy.Ready && tool == new SaveEditorTool(own.Python, SaveEditing.ScriptOf(own), own.ToolsRoot, game), "The build's own version's Python and editor, and the build's folder as the game.");
        Require(SaveEditing.Find(new BuildEntry { Folder = game, Version = null }, toolchains).Why == SaveEditorWhy.Ready,
                "(the version is the folder's stamp, not the registry's)");
        var plain = Folder("save-editor-no-stamp");
        Directory.CreateDirectory(Path.Combine(plain, "romfs"));
        Require(SaveEditing.Find(new BuildEntry { Folder = plain }, toolchains).Why == SaveEditorWhy.NoTools, "A folder the launcher did not build (no version stamp): no tools to use.");
        foreach (var w in new[] { SaveEditorWhy.NoSave, SaveEditorWhy.NoBuild, SaveEditorWhy.NoGameFiles, SaveEditorWhy.NoTools, SaveEditorWhy.NoEditor })
            Require(SaveEditing.Say(w).Length > 20, "Each reason is said: " + w);
        Require(SaveEditing.Say(SaveEditorWhy.NoEditor) is var noEditor && !noEditor.Contains("A newer version has it") && noEditor.Contains("3.0.0")
                && noEditor.Contains("update Longshot"), "No editor: no released version has it yet (#217), so it says up to which version none has, and to update once one does, not that a newer one has it.");

        // ---- the command ----
        var save = Path.Combine(root, "data", "save", "5a3c9e1f0b7d24680000000000000001");
        var data = Path.Combine(root, "data");
        var env = new SaveEnvironment(Platform.Linux, k => k switch { "XDG_DATA_HOME" => data, "HOME" => root, _ => null });
        var start = SaveEditing.Command(tool!, Platform.Linux, save, env.BackupRoot, ["report", "--json"],
                                        new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["LD_LIBRARY_PATH"] = "/tmp/appimage", ["PYTHONHOME"] = "/x", ["LONGSHOT_GAME"] = "/other" });
        Require(start.FileName == own.Python && start.WorkingDirectory == own.ToolsRoot
                && start.ArgumentList.SequenceEqual(new[] { "-I", "-B", SaveEditing.ScriptOf(own), "--game", game, "--save", save, "--backup-dir", env.BackupRoot, "report", "--json" }),
                "The tools' Python, isolated and writing nothing, the script, the game, the save, the launcher's backup folder, then the command.");
        Require(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError, "No window, the output read.");
        Require(start.Environment["PYTHONIOENCODING"] == "utf-8" && start.Environment["PATH"] == "/usr/bin" && !start.Environment.ContainsKey("LD_LIBRARY_PATH")
                && !start.Environment.ContainsKey("PYTHONHOME") && !start.Environment.ContainsKey("LONGSHOT_GAME"),
                "The game's environment (GamePlay's rule): the AppImage runtime's and any PYTHON setting dropped, the tool's output UTF-8.");
        Require(env.BackupRoot == Path.Combine(data, "longshot", "save-backups"), "(the launcher's backup folder: where every other copy's backup goes)");

        // ---- the plans: what each edit asks the tool ----
        Require(SaveEditPlan.Set("slot_03", "skulltulas/KakarikoVillage", "x").Command(false).SequenceEqual(new[] { "set", "slot_03", "skulltulas/KakarikoVillage" })
                && SaveEditPlan.Clear("slot_00", "all", "x").Command(true).SequenceEqual(new[] { "clear", "slot_00", "all", "--dry-run" }), "Set and clear: a slot and a target.");
        Require(SaveEditPlan.Give("slot_01", ["hearts=10", "Item_Bomb=20", "ForestTemple-keys=3"], "x").Command(false).SequenceEqual(new[] { "give", "slot_01", "hearts=10", "Item_Bomb=20", "ForestTemple-keys=3" }),
                "Give: the items and amounts as the tool takes them.");
        Require(SaveEditPlan.Start("slot_01", "TestArea_Child/Gate", null, "x").Command(false).SequenceEqual(new[] { "start", "slot_01", "TestArea_Child/Gate" })
                && SaveEditPlan.Start("slot_01", "TestArea/(default)", "adult", "x").Command(true).SequenceEqual(new[] { "start", "slot_01", "TestArea/(default)", "--age", "adult", "--dry-run" }),
                "Start: a place, and the age when the area has both.");
        Require(SaveEditPlan.Copy("slot_00", "slot_04", false, "x").Command(false).SequenceEqual(new[] { "copy", "slot_00", "slot_04" })
                && SaveEditPlan.Copy("slot_00", "slot_01", true, "x").Command(false).SequenceEqual(new[] { "copy", "slot_00", "slot_01", "--overwrite" }), "Copy: replacing a slot only when told.");
        foreach (var slot in new[] { "3", "03", "slot_3", "slot_000x", "../slot_00", "", "slot_00 --force" })
            Throws<ArgumentException>(() => SaveEditPlan.Set(slot, "all", "x"), "A slot is slot_NN, never a number the page and the tool count differently: " + slot);
        foreach (var arg in new[] { "--force", "-", "", "all\nx" })
            Throws<ArgumentException>(() => SaveEditPlan.Set("slot_00", arg, "x"), "Nothing the tool could read as an option is passed: " + arg);
        Throws<ArgumentException>(() => SaveEditPlan.Start("slot_00", "A/B", "teen", "x"), "Only child or adult.");

        // ---- the session over a stand-in for the tool: the refusals and the arguments ----
        var starts = new List<ProcessStartInfo>();
        var said = new Dictionary<string, SaveEditorRun>
        {
            ["report"] = new(0, Shown("report.json"), ""), ["items"] = new(0, Shown("items.json"), ""), ["places"] = new(0, Shown("places.json"), ""),
            ["set"] = new(0, Shown("set.json"), ""), ["clear"] = new(1, Shown("error-target.json").Replace("\"command\": \"set\"", "\"command\": \"clear\""), ""),
        };
        SaveEditorRunner runner = (si, _) => { starts.Add(si); return Task.FromResult(si.ArgumentList.Contains("--dry-run") ? new SaveEditorRun(0, Shown("set-dry.json"), "") : said[si.ArgumentList[9]]); };
        IReadOnlyList<string> busy = Array.Empty<string>();
        var editing = new SaveEditing(tool!, env, save, runner, () => busy);
        Require(editing.Save == save && editing.BackupRoot == env.BackupRoot, "The session edits the save it is given, and backs up where the launcher does.");
        Require(editing.ReportAsync(null).GetAwaiter().GetResult().Slots.Count == 5 && starts[^1].ArgumentList.Skip(9).SequenceEqual(new[] { "report", "--json" }), "The report of every slot.");
        editing.ReportAsync("slot_01").GetAwaiter().GetResult();
        Require(starts[^1].ArgumentList.Skip(9).SequenceEqual(new[] { "report", "slot_01", "--json" }), "The report of one slot.");
        Throws<ArgumentException>(() => editing.ReportAsync("1").GetAwaiter().GetResult(), "A report of a slot named by number is refused.");
        Require(editing.ItemsAsync().GetAwaiter().GetResult().Items.Count == 12 && editing.PlacesAsync().GetAwaiter().GetResult().Count == 5, "The lists the editor's pickers show.");
        var plan = SaveEditPlan.Set("slot_00", "skulltulas/TestArea", "Gold Skulltulas in Test Area");
        var before = starts.Count;
        busy = ["Longshot"];
        Require(editing.PreviewAsync(plan).GetAwaiter().GetResult().Dry && starts[^1].ArgumentList.Skip(9).SequenceEqual(new[] { "set", "slot_00", "skulltulas/TestArea", "--dry-run", "--json" }),
                "A preview is a dry run, and a game running does not matter: it writes nothing.");
        var refused = Assert<SaveFailure>(() => editing.ApplyAsync(plan).GetAwaiter().GetResult());
        Require(refused.Reason == SaveRefusal.Running && refused.Who.SequenceEqual(new[] { "Longshot" }) && refused.Message.Contains("Longshot is running") && starts.Count == before + 1,
                "An edit while a game runs is refused before the tool is run.");
        busy = ["Longshot", "Lichii"];
        Require(Assert<SaveFailure>(() => editing.ApplyAsync(plan).GetAwaiter().GetResult()).Message.StartsWith("Longshot and Lichii is running"), "Every program that runs is named.");
        busy = Array.Empty<string>();
        var written = editing.ApplyAsync(plan).GetAwaiter().GetResult();
        Require(written is { Backup: not null, Dry: false } && starts[^1].ArgumentList.Skip(9).SequenceEqual(new[] { "set", "slot_00", "skulltulas/TestArea", "--json" })
                && !starts[^1].ArgumentList.Contains("--force"), "With nothing running: the edit, always as JSON, never with --force.");
        Require(Assert<SaveFailure>(() => editing.ApplyAsync(SaveEditPlan.Clear("slot_00", "nothing", "x")).GetAwaiter().GetResult()) is { Code: "unknown_target", Message: "nothing called 'nothing' (see: targets)" },
                "A refusal by the tool reaches the page with its code and its words.");
        var cannot = new SaveEditing(tool!, env, save, (_, _) => throw new System.ComponentModel.Win32Exception("No such file"), () => busy);
        Require(Assert<SaveFailure>(() => cannot.ReportAsync(null).GetAwaiter().GetResult()).Message.Contains("could not start") , "A Python that does not start is said, not thrown.");

        // ---- the real process runner, over a stand-in for Python (a shell script: not on Windows) ----
        if (OperatingSystem.IsWindows()) return;
        var fake = Path.Combine(root, "fake-python");
        var standIn = new SaveEditorTool(fake, "script.py", root, Path.Combine(root, "ゲーム Zélda"));
        File.WriteAllText(fake, """
            #!/bin/sh
            d=$(dirname "$0")
            shift 9
            printf '%s\n' "$@" > "$d/args.txt"
            case "$1" in
              items) printf '%s\n' '{"schema": 1, "command": "items", "ok": true, "items": [{"id": "Weapon_Master", "label": "Zélda ゲーム", "category": "equipment", "counted": false}], "gives": ["hearts", "rupees"], "small_keys": [], "hearts": {"min": 1, "max": 5}}' ;;
              report) printf '%s\n' '{"schema": 1, "command": "report", "ok": true, "save": "x", "slots": []}' ;;
              set) printf '%s\n' '{"schema": 1, "command": "set", "ok": false, "error": {"code": "running", "message": "close Longshot first", "games": ["Longshot"]}}'; exit 1 ;;
              places) exit 7 ;;
              give) printf 'Traceback\n' >&2; exit 1 ;;
            esac
            """.Replace("\r", "") + "\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var real = new SaveEditing(standIn, env, save, running: () => Array.Empty<string>());
        Require(real.ReportAsync("slot_00").GetAwaiter().GetResult().Slots.Count == 0
                && File.ReadAllText(Path.Combine(root, "args.txt")) == "report\nslot_00\n--json\n", "A process run: its arguments reach it as they are, and its answer is read.");
        Require(real.ItemsAsync().GetAwaiter().GetResult().Items.Single().Label == "Zélda ゲーム", "Its output is read as UTF-8.");
        Require(Assert<SaveFailure>(() => real.ApplyAsync(plan).GetAwaiter().GetResult()) is { Reason: SaveRefusal.Running, Code: "running" } r && r.Who.SequenceEqual(new[] { "Longshot" }),
                "Its answer on stdout and its exit code make the refusal.");
        Require(Assert<SaveFailure>(() => real.PlacesAsync().GetAwaiter().GetResult()).Message.Contains("exit code 7"), "A tool that stops with nothing to say: its exit code.");
        Require(Assert<SaveFailure>(() => real.ApplyAsync(SaveEditPlan.Give("slot_00", ["hearts=5"], "x")).GetAwaiter().GetResult()).Message == "Traceback",
                "A tool that crashes: the last line it said.");
        var gone = new SaveEditing(new SaveEditorTool(Path.Combine(root, "no-such-python"), "s.py", root, "g"), env, save);
        Require(Assert<SaveFailure>(() => gone.ReportAsync(null).GetAwaiter().GetResult()).Message.Contains("could not start"), "A Python that is not there: said.");
    }

    static T Assert<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        throw new Exception("not refused: " + typeof(T).Name);
    }
}
