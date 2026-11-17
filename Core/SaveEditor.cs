using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// Why the save editor cannot be run for a build (Ready: it can)
public enum SaveEditorWhy { Ready, NoSave, NoBuild, NoGameFiles, NoTools, NoEditor }

/// The save editor of one build: Longshot's scripts/longshot_save_editor.py in that version's own tools, its Python, and the game
/// folder (the one holding romfs) it reads the game's lists from
public sealed record SaveEditorTool(string Python, string Script, string ToolsRoot, string Game);

/// What a command of the editor printed and how it ended
public sealed record SaveEditorRun(int Exit, string Output, string Error);

public delegate Task<SaveEditorRun> SaveEditorRunner(ProcessStartInfo start, CancellationToken cancel);

/// An item `give` takes, from `items --json`: its id, its name in the game's text, the id of the category it is listed under (the
/// report's categories), and whether it is counted (an amount, within what the bags Link has allow)
public sealed record SaveEditorItem(string Category, string Id, string Label, bool Counted);

/// A dungeon whose small keys `give` takes (`<dungeon>-keys=N`) and how many it places
public sealed record SaveEditorKeys(string Dungeon, int Of);

/// What `give` takes, from `items --json`: the items, the dungeons with small keys, and the most hearts the game gives (and the
/// fewest)
public sealed record SaveEditorGives(IReadOnlyList<SaveEditorItem> Items, IReadOnlyList<SaveEditorKeys> SmallKeys, int HeartsMin, int HeartsMax);

/// A named start place, from `places --json`: the area (the game's map name), the name of the start in it ("" is the area's
/// default), what `start` takes for it, the age the area is for (null: both ages have it) and whether the game can load it
public sealed record SaveEditorPlace(string Area, string Start, string Spec, string? Age, bool CanLoad)
{
    /// As a picker says it: the area in words, and the start in it when the area has several
    public string Name => Start.Length == 0 ? SaveEditorText.Humanize(Area) : $"{SaveEditorText.Humanize(Area)}: {SaveEditorText.Humanize(Start)}";
}

public enum SaveEditKind { Set, Clear, Give, Start, Copy }

/// One edit the player asked for, as the arguments the tool takes after its command's name (the tool says what each means:
/// docs/save-editor.md). What: the words the confirmation says it in
public sealed record SaveEditPlan(SaveEditKind Kind, string Slot, IReadOnlyList<string> Args, string What)
{
    static readonly Regex SlotName = new("^slot_[0-9]{2,3}$", RegexOptions.CultureInvariant);

    /// set / clear: a target (`all`, a category, CATEGORY/AREA, one entry's id) in a slot
    public static SaveEditPlan Set(string slot, string target, string what) => new(SaveEditKind.Set, CheckSlot(slot), [Safe(target)], what);
    public static SaveEditPlan Clear(string slot, string target, string what) => new(SaveEditKind.Clear, CheckSlot(slot), [Safe(target)], what);

    /// give: items (`Item_Hookshot`, `Item_Bomb=20`), hearts=N, rupees=N, DUNGEON-keys=N
    public static SaveEditPlan Give(string slot, IReadOnlyList<string> gives, string what) =>
        new(SaveEditKind.Give, CheckSlot(slot), gives.Select(Safe).ToList(), what);

    /// start: AREA/NAME from `places`; age: "child" or "adult" for an area both ages have (null: the game's choice)
    public static SaveEditPlan Start(string slot, string place, string? age, string what) =>
        new(SaveEditKind.Start, CheckSlot(slot), age == null ? [Safe(place)] : [Safe(place), "--age", age is "child" or "adult" ? age : throw new ArgumentException("age")], what);

    /// copy: the slot's files to another slot; a slot that holds a save is replaced only with overwrite
    public static SaveEditPlan Copy(string slot, string to, bool overwrite, string what) =>
        new(SaveEditKind.Copy, CheckSlot(slot), overwrite ? [CheckSlot(to), "--overwrite"] : [CheckSlot(to)], what);

    /// The slot as the tool takes it: always slot_NN, never a number (the Saves page counts from 1, the folders from 0)
    public static string CheckSlot(string slot) => SlotName.IsMatch(slot) ? slot : throw new ArgumentException("not a slot: " + slot);

    /// Nothing the tool could read as an option (an id or name comes from the game's own text and lists)
    static string Safe(string text) =>
        text.Length > 0 && text[0] != '-' && !text.Any(char.IsControl) ? text : throw new ArgumentException("not an argument: " + text);

    public IReadOnlyList<string> Command(bool dryRun)
    {
        var verb = Kind switch { SaveEditKind.Set => "set", SaveEditKind.Clear => "clear", SaveEditKind.Give => "give", SaveEditKind.Start => "start", _ => "copy" };
        var args = new List<string> { verb, Slot };
        args.AddRange(Args);
        if (dryRun) args.Add("--dry-run");
        return args;
    }
}

/// What the editor read back after writing: whether the slot is complete now, and how many are missing
public sealed record SaveEditReadBack(bool Complete, int MissingTotal);

/// What an edit did (or, with Dry, would do), from the editor's answer: whether it changes anything, the lines it said, the files
/// it wrote or would write (relative to the save folder), the backup it made first (null: none, nothing was changed) and what it
/// read back after writing
public sealed record SaveEditResult(bool Changed, IReadOnlyList<string> Said, IReadOnlyList<string> Files, string? Backup, SaveEditReadBack? ReadBack, bool Dry)
{
    /// The answer of a set, clear, give, start or copy (SaveEditorAnswer.Of: a SaveFailure when it is not that answer)
    public static SaveEditResult Parse(string json, string command)
    {
        using var doc = SaveEditorAnswer.Of(new SaveEditorRun(0, json, ""), command);
        return Read(doc.RootElement);
    }

    public static SaveEditResult Read(JsonElement root)
    {
        try
        {
            var back = root.GetProperty("read_back");
            return new SaveEditResult(root.GetProperty("changed").GetBoolean(),
                                      root.GetProperty("said").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                                      root.GetProperty("files").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                                      root.GetProperty("backup").ValueKind == JsonValueKind.String ? root.GetProperty("backup").GetString() : null,
                                      back.ValueKind == JsonValueKind.Object ? new SaveEditReadBack(back.GetProperty("complete").GetBoolean(), back.GetProperty("missing_total").GetInt32()) : null,
                                      root.GetProperty("dry_run").GetBoolean());
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SaveFailure(Tr.T("The save editor's answer is not in a form this launcher reads."));
        }
    }
}

/// The save editor: what a slot has and is missing, and the edits, by running Longshot's scripts/longshot_save_editor.py with the
/// build's own version's Python (as PerfReports runs perf_report.py). The launcher reads no game file and no save itself: it
/// passes the game folder, the save folder and the backup folder, and shows what the tool says. Every edit is refused here while
/// a game or an emulator runs (the tool refuses too, and backs the whole save up first, writes each file whole and reads it
/// back: docs/save-editor.md in Longshot)
public sealed class SaveEditing
{
    readonly SaveEditorTool tool;
    readonly SaveEnvironment env;
    readonly SaveEditorRunner run;
    readonly Func<IReadOnlyList<string>> running;

    /// save: the save folder to edit (the user id folder holding account.sav and the slots); run: how a process is run (the tests
    /// give their own); running: what runs now, for the refusal
    public SaveEditing(SaveEditorTool tool, SaveEnvironment env, string save, SaveEditorRunner? run = null, Func<IReadOnlyList<string>>? running = null)
    {
        this.tool = tool;
        this.env = env;
        Save = save;
        this.run = run ?? RunProcess;
        this.running = running ?? (() => Saves.Running(env.Platform));
    }

    public string Save { get; }

    /// The game folder the editor reads the game's own lists from (the build's)
    public string Game => tool.Game;

    /// The games and emulators running now (an edit is refused while any runs)
    public IReadOnlyList<string> Busy() => running();

    /// Where the backup of an edit goes (the folder the tool makes edit-<profile>-<date>-<time> in)
    public string BackupRoot => env.BackupRoot;

    public const string ScriptName = "longshot_save_editor.py";

    public static string ScriptOf(Toolchain tools) => Path.Combine(tools.ToolsRoot, "scripts", ScriptName);

    /// The build's save editor, or why there is none: the build's own version's tools with the editor's script (a version from
    /// before the editor, or one whose tools were let go, has none), and the game's files in the build's folder (the tool reads the
    /// game's lists from them: make_game.py unpacks them to its romfs folder)
    public static (SaveEditorTool? Tool, SaveEditorWhy Why) Find(BuildEntry? build, Toolchains tools, Func<string, bool>? file = null,
                                                                 Func<string, bool>? folder = null)
    {
        file ??= File.Exists;
        folder ??= Directory.Exists;
        if (build == null || !folder(build.Folder)) return (null, SaveEditorWhy.NoBuild);
        if (!folder(Path.Combine(build.Folder, "romfs"))) return (null, SaveEditorWhy.NoGameFiles);
        if (GameFolder.Version(build.Folder) is not { } version || tools.Find(version) is not { } own || !file(own.Python)) return (null, SaveEditorWhy.NoTools);
        var script = ScriptOf(own);
        if (!file(script) || !file(Path.Combine(Path.GetDirectoryName(script)!, "oot_savefile.py"))) return (null, SaveEditorWhy.NoEditor);
        return (new SaveEditorTool(own.Python, script, own.ToolsRoot, build.Folder), SaveEditorWhy.Ready);
    }

    /// What the page says when there is no editor
    public static string Say(SaveEditorWhy why) => why switch
    {
        SaveEditorWhy.NoSave => Tr.T("There is no save to edit yet."),
        SaveEditorWhy.NoBuild => Tr.T("Build Longshot first: the save editor reads the game's own files."),
        SaveEditorWhy.NoGameFiles => Tr.T("This build's game files are not in its folder, and the save editor reads them. Build it again."),
        SaveEditorWhy.NoTools => Tr.T("This build's tools are not on this PC, and the save editor runs from them. Update or build it again."),
        SaveEditorWhy.NoEditor => Tr.T("This version has no save editor yet. A newer version has it."),
        _ => "",
    };

    /// The command: the tool's Python, isolated, the script, the game, the save and the backup folder, then the tool's own arguments;
    /// in the game's environment (GamePlay: without the AppImage runtime's and the tools' settings, and any PYTHON setting of
    /// the player's), its output UTF-8
    public static ProcessStartInfo Command(SaveEditorTool tool, Platform platform, string save, string backups, IEnumerable<string> args,
                                           IReadOnlyDictionary<string, string>? current = null)
    {
        var si = new ProcessStartInfo(tool.Python)
        {
            WorkingDirectory = tool.ToolsRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // (-B: nothing is written into the tools' folder)
        foreach (var a in new[] { "-I", "-B", tool.Script, "--game", tool.Game, "--save", save, "--backup-dir", backups }) si.ArgumentList.Add(a);
        foreach (var a in args) si.ArgumentList.Add(a);
        current ??= System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        si.Environment.Clear();
        foreach (var (k, v) in GamePlay.Environment(platform, current)) si.Environment[k] = v;
        si.Environment["PYTHONIOENCODING"] = "utf-8";
        return si;
    }

    /// The slots' report (one slot: slot_NN), read from the files as they are now
    public async Task<SaveEditorReport> ReportAsync(string? slot, CancellationToken cancel = default)
    {
        var args = new List<string> { "report" };
        if (slot != null) args.Add(SaveEditPlan.CheckSlot(slot));
        using var answer = await Run(args, cancel).ConfigureAwait(false);
        return SaveEditorReport.Read(answer.RootElement);
    }

    /// What `give` takes
    public async Task<SaveEditorGives> ItemsAsync(CancellationToken cancel = default)
    {
        using var answer = await Run(["items"], cancel).ConfigureAwait(false);
        return ReadItems(answer.RootElement);
    }

    /// The named places `start` takes
    public async Task<IReadOnlyList<SaveEditorPlace>> PlacesAsync(CancellationToken cancel = default)
    {
        using var answer = await Run(["places"], cancel).ConfigureAwait(false);
        return ReadPlaces(answer.RootElement);
    }

    /// What an edit would do, with --dry-run: nothing is written, no backup is made, so a game running does not matter
    public async Task<SaveEditResult> PreviewAsync(SaveEditPlan plan, CancellationToken cancel = default)
    {
        using var answer = await Run(plan.Command(dryRun: true), cancel).ConfigureAwait(false);
        return SaveEditResult.Read(answer.RootElement);
    }

    /// The edit. Refused (SaveFailure, Reason Running) while a game or an emulator runs, with nothing run; the tool then backs the
    /// whole save up first (SaveEditResult.Backup), and says why it refused (SaveFailure.Code and words) or that it put the files back
    public async Task<SaveEditResult> ApplyAsync(SaveEditPlan plan, CancellationToken cancel = default)
    {
        if (running() is { Count: > 0 } who)
            throw new SaveFailure(Tr.F("{0} is running. A running game writes its save again when it quits and would undo the edit. Close it, then try again.",
                                       string.Join(Tr.T(" and "), who)), SaveRefusal.Running, who, code: "running");
        using var answer = await Run(plan.Command(dryRun: false), cancel).ConfigureAwait(false);
        return SaveEditResult.Read(answer.RootElement);
    }

    /// The editor's answer to a command, run with --json (the command is args[0]); else a SaveFailure (SaveEditorAnswer)
    async Task<JsonDocument> Run(IReadOnlyList<string> args, CancellationToken cancel)
    {
        SaveEditorRun done;
        try { done = await run(Command(tool, env.Platform, Save, env.BackupRoot, args.Append("--json")), cancel).ConfigureAwait(false); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw new SaveFailure(Tr.F("The save editor could not start: {0}", e.Message));
        }
        return SaveEditorAnswer.Of(done, args[0]);
    }

    /// `items --json`: `items` (id, label, category, counted), `small_keys` (dungeon, of) and `hearts` (min, max)
    public static SaveEditorGives ReadItems(JsonElement root)
    {
        try
        {
            return new SaveEditorGives(
                root.GetProperty("items").EnumerateArray().Select(i => new SaveEditorItem(i.GetProperty("category").GetString() ?? "", i.GetProperty("id").GetString() ?? "",
                                                                                           i.GetProperty("label").GetString() ?? "", i.GetProperty("counted").GetBoolean())).ToList(),
                root.GetProperty("small_keys").EnumerateArray().Select(k => new SaveEditorKeys(k.GetProperty("dungeon").GetString() ?? "", k.GetProperty("of").GetInt32())).ToList(),
                root.GetProperty("hearts").GetProperty("min").GetInt32(), root.GetProperty("hearts").GetProperty("max").GetInt32());
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SaveFailure(Tr.T("The save editor's answer is not in a form this launcher reads."));
        }
    }

    /// `places --json`: `places` (area, name, default, spec, age, loadable)
    public static IReadOnlyList<SaveEditorPlace> ReadPlaces(JsonElement root)
    {
        try
        {
            return root.GetProperty("places").EnumerateArray().Select(p => new SaveEditorPlace(
                p.GetProperty("area").GetString() ?? "", p.GetProperty("default").GetBoolean() ? "" : p.GetProperty("name").GetString() ?? "",
                p.GetProperty("spec").GetString() ?? "", p.GetProperty("age").ValueKind == JsonValueKind.String ? p.GetProperty("age").GetString() : null,
                p.GetProperty("loadable").GetBoolean())).ToList();
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new SaveFailure(Tr.T("The save editor's answer is not in a form this launcher reads."));
        }
    }

    /// A process run to its end, its output read whole (a report is a few hundred kilobytes at most; a runaway one is cut)
    static async Task<SaveEditorRun> RunProcess(ProcessStartInfo start, CancellationToken cancel)
    {
        const int Limit = 32 << 20;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            async Task<string> Read(StreamReader r)
            {
                var buffer = new char[8192];
                var text = new StringBuilder();
                int n;
                while ((n = await r.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (text.Length + n > Limit) throw new IOException("the save editor said more than " + Limit + " characters");
                    text.Append(buffer, 0, n);
                }
                return text.ToString();
            }
            var output = Read(process.StandardOutput);
            var error = Read(process.StandardError);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new SaveEditorRun(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Kill(process);
            throw new IOException("the save editor did not finish in 3 minutes");
        }
        catch (Exception)
        {
            Kill(process);
            throw;
        }
    }

    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
