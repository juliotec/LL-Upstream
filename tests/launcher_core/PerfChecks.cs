using LongshotLauncher.Core;

/// The performance statistics' core (PerfReports): the folder's tick and its waiting summaries as the game leaves them, the tools
/// that read them (the folder's own version's, with perf_report.py), what to do before a game starts, and the two commands
static partial class Program
{
    static void PerfChecks()
    {
        var game = Folder("perf-game");
        Game(game, "v3.1.0");
        Require(!PerfReports.Ticked(game) && !PerfReports.Waiting(game), "A new folder: not ticked, nothing waiting.");
        File.WriteAllText(Path.Combine(game, PerfReports.ChoiceName), "# Longshot's performance statistics\nsend=1\n");
        Require(PerfReports.Ticked(game), "send=1 in longshot_perf.ini: ticked.");
        File.WriteAllText(Path.Combine(game, PerfReports.ChoiceName), "send=0\n");
        Require(!PerfReports.Ticked(game), "send=0: not ticked.");
        Require(PerfReports.KeptSent(game), "No keep_sent line: the copies kept (the default).");
        File.WriteAllText(Path.Combine(game, PerfReports.ChoiceName), "send=1\nkeep_sent=0\n");
        Require(PerfReports.Ticked(game) && !PerfReports.KeptSent(game), "keep_sent=0: ticked, and no copies kept.");
        File.WriteAllText(Path.Combine(game, PerfReports.ChoiceName), "send=0\n");
        var pending = Path.Combine(game, PerfReports.PendingName);
        Directory.CreateDirectory(pending);
        Touch(Path.Combine(pending, "summary.json.tmp"));
        Require(!PerfReports.Waiting(game), "A summary still being written (.tmp) is not waiting.");
        Touch(Path.Combine(pending, "1794589941.json"), "{}");
        Require(PerfReports.Waiting(game), "A .json in perf_pending/: waiting.");

        // (the folder's own version's tools, and only with perf_report.py: no other version's script reads its summaries)
        var root = Folder("perf-tools");
        var toolchains = new Toolchains(root, new FakeFetcher());
        Require(PerfReports.ToolsFor(game, toolchains) == null, "No tools: nothing sends.");
        var other = PerfTools(root, "v3.2.0");
        Touch(PerfReports.Script(other), "# authored");
        Require(PerfReports.ToolsFor(game, toolchains) == null, "Another version's tools don't send this folder's summaries.");
        var own = PerfTools(root, "v3.1.0");
        Require(PerfReports.ToolsFor(game, toolchains) == null, "The folder's version from before the statistics (no perf_report.py): left as it is.");
        Require(PerfReports.ToolsFor(game, toolchains, p => p == PerfReports.Script(own))?.Tag == "v3.1.0", "Its own version's perf_report.py: those tools.");
        var noVersion = Folder("perf-no-version");
        Game(noVersion, null);
        Require(PerfReports.ToolsFor(noVersion, toolchains, _ => true) == null, "A folder with no version: none.");

        // (the tick written only when it or, with it on, the copies setting differs; the summaries sent only with it on)
        Require(PerfReports.Plan(on: true, keep: true, ticked: false, kept: true, waiting: false) == (true, false), "Ticked in the launcher: the folder's tick written on.");
        Require(PerfReports.Plan(on: true, keep: true, ticked: true, kept: true, waiting: true) == (null, true), "Already on, summaries waiting: only the send.");
        Require(PerfReports.Plan(on: true, keep: true, ticked: true, kept: true, waiting: false) == (null, false), "Already on, nothing waiting: nothing to do.");
        Require(PerfReports.Plan(on: false, keep: true, ticked: true, kept: true, waiting: true) == (false, false), "Unticked: the tick off (which deletes what waits), nothing sent.");
        Require(PerfReports.Plan(on: false, keep: true, ticked: false, kept: true, waiting: true) == (null, false), "Off and the folder agrees: what waits is never sent.");
        Require(PerfReports.Plan(on: true, keep: false, ticked: true, kept: true, waiting: true) == (true, true), "Copies turned off: the tick written again (with --keep-sent off), then the send.");
        Require(PerfReports.Plan(on: true, keep: true, ticked: true, kept: false, waiting: false) == (true, false), "Copies turned back on: written again.");
        Require(PerfReports.Plan(on: false, keep: false, ticked: false, kept: true, waiting: false) == (null, false), "Off: the copies setting waits for the tick.");

        var send = PerfReports.SendCommand(own, game);
        Require(send.FileName == own.Python && send.ArgumentList.SequenceEqual(new[] { "-I", PerfReports.Script(own), game, "--send" }),
                "--send: the tools' Python, isolated, the folder as one argument.");
        Require(!send.UseShellExecute && send.CreateNoWindow, "--send, with no window.");
        Require(send.Environment["PYTHONIOENCODING"] == "utf-8"
                && (Platforms.Current == Platform.Windows || !send.Environment.ContainsKey("LD_LIBRARY_PATH")),
                "The tools run in the game's environment (GamePlay's rule: no LD_LIBRARY_PATH on Linux).");

        // (the tick written by the launcher itself, as perf_report.py's set_sending writes it: no Python, nothing waited for)
        var ticking = Folder("perf-tick");
        var choice = Path.Combine(ticking, PerfReports.ChoiceName);
        var sentDir = Path.Combine(ticking, PerfReports.SentName);
        var pendingDir = Path.Combine(ticking, PerfReports.PendingName);
        PerfReports.SetSending(ticking, true);
        Require(File.ReadAllText(choice) == "send=1\n" && PerfReports.Ticked(ticking) && PerfReports.KeptSent(ticking), "On: send=1, as perf_report.py writes it.");
        Touch(Path.Combine(sentDir, "1794589941-0.json"), "{}");
        PerfReports.SetSending(ticking, true, keep: false);
        Require(File.ReadAllText(choice) == "send=1\nkeep_sent=0\n" && !PerfReports.KeptSent(ticking) && !Directory.EnumerateFiles(sentDir).Any()
                && !File.Exists(choice + ".tmp"), "Copies off: keep_sent=0, and perf_sent/ emptied.");
        Touch(Path.Combine(pendingDir, "1794589941-1.json"), "{}");
        Touch(Path.Combine(pendingDir, "1794589942-0.json.tmp"));
        Touch(Path.Combine(pendingDir, PerfReports.NextName), "0 3600");
        Touch(Path.Combine(pendingDir, "notes.txt"));
        Touch(Path.Combine(sentDir, "1794589941-0.json"), "{}");
        PerfReports.SetSending(ticking, false);
        Require(!File.Exists(choice) && !PerfReports.Ticked(ticking) && !PerfReports.Waiting(ticking) && !Directory.EnumerateFiles(sentDir).Any()
                && Directory.EnumerateFiles(pendingDir).Select(f => Path.GetFileName(f)).SequenceEqual(new[] { "notes.txt" }),
                "Off: longshot_perf.ini removed, perf_pending/ and perf_sent/ emptied (set_sending's off), other files left.");
        var gone = Path.Combine(ticking, "gone");
        PerfReports.SetSending(gone, false);
        Require(!Directory.Exists(gone), "Off for a folder that isn't there: nothing thrown, nothing made.");

        // (a version without perf_report.py: the game starts with nothing run, and nothing in the folder changed)
        PerfReports.BeforePlay(game, on: true, keep: true, toolchains);
        Require(!PerfReports.Ticked(game) && PerfReports.Waiting(game), "BeforePlay with tools that can't send: the folder as it was.");

        // (unticked, with the version's tools gone: the player's no honoured all the same, by the launcher)
        var noTools = Folder("perf-off-no-tools");
        Game(noTools, "v3.1.0");
        PerfReports.SetSending(noTools, true);
        Touch(Path.Combine(noTools, PerfReports.PendingName, "1794589941-0.json"), "{}");
        Touch(Path.Combine(noTools, PerfReports.SentName, "1794589940-0.json"), "{}");
        Require(PerfReports.ToolsFor(noTools, toolchains) == null && PerfReports.Ticked(noTools) && PerfReports.Waiting(noTools),
                "A ticked folder with summaries waiting, and no tools that send them.");
        PerfReports.BeforePlay(noTools, on: false, keep: true, toolchains);
        Require(!File.Exists(Path.Combine(noTools, PerfReports.ChoiceName)) && !PerfReports.Waiting(noTools)
                && !Directory.EnumerateFiles(Path.Combine(noTools, PerfReports.SentName)).Any(),
                "Unticked with the version's tools gone: the folder's tick off all the same, nothing left to send and no copy kept.");

        // (ticked, for a version with perf_report.py: the tick written by the launcher; nothing waits, so nothing is run)
        var onFolder = Folder("perf-on");
        Game(onFolder, "v3.1.0");
        Touch(PerfReports.Script(own), "# authored");
        PerfReports.BeforePlay(onFolder, on: true, keep: false, toolchains);
        Require(File.ReadAllText(Path.Combine(onFolder, PerfReports.ChoiceName)) == "send=1\nkeep_sent=0\n",
                "BeforePlay ticked, for a version with perf_report.py: the folder's tick written, keep_sent=0 with it.");
    }

    /// Send crash reports automatically (CrashReports): longshot_crash.ini as the game's crash helper reads it
    /// (hos_crash_auto_send), written before a game starts only when the player's answer differs
    static void CrashReportChecks()
    {
        var game = Folder("crash-choice");
        var ini = Path.Combine(game, CrashReports.ChoiceName);
        Require(!CrashReports.Chosen(game), "A new folder: the window asks.");
        CrashReports.BeforePlay(game, false);
        Require(!File.Exists(ini), "Off and never chosen: no file written.");
        CrashReports.BeforePlay(game, true);
        Require(CrashReports.Chosen(game) && File.ReadAllText(ini) == CrashReports.Text(true), "On: send=1 written.");
        var written = File.GetLastWriteTimeUtc(ini);
        File.SetLastWriteTimeUtc(ini, written.AddMinutes(-5));
        CrashReports.BeforePlay(game, true);
        Require(File.GetLastWriteTimeUtc(ini) == written.AddMinutes(-5), "On again: the file as it was, not written again.");
        CrashReports.BeforePlay(game, false);
        Require(!CrashReports.Chosen(game) && File.ReadAllText(ini) == CrashReports.Text(false), "Off: send=0, the window asks.");
        foreach (var (text, want) in new[] { ("  send=1 \r\n", true), ("x=2\nsend=1", true), ("send=10\n", false), ("#send=1\n", false) })
        {
            File.WriteAllText(ini, text);
            Require(CrashReports.Chosen(game) == want, "The helper's rule, line by line: " + text.Trim());
        }
    }

    static Toolchain PerfTools(string root, string tag)
    {
        var folder = Path.Combine(root, tag);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Toolchains.RecordName), $$"""{"format":1,"tag":"{{tag}}","kind":"Linux","parts":[]}""");
        return new Toolchains(root, new FakeFetcher()).Find(tag) ?? throw new Exception("The authored tools were not found: " + tag);
    }
}
