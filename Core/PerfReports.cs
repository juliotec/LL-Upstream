using System.Diagnostics;

namespace LongshotLauncher.Core;

/// The launcher's side of Longshot's performance statistics (Longshot's docs/performance-statistics.md). The Settings page's "Send
/// performance statistics" tick (LauncherSettings.SendPerformanceStatistics, off by default) is the player's answer for every
/// build: each game folder keeps it as longshot_perf.ini (send=1), which the game reads at its start. With it on, the game writes a
/// summary into perf_pending/ every few minutes and at its end, and the launcher sends them when a game is next started.
///
/// "Keep a copy of what was sent" (LauncherSettings.KeepSentPerformanceStatistics, on by default) is keep_sent=0 in that file when
/// off: the sender then keeps no copy of what it sent in perf_sent/, and deletes those it kept.
///
/// The launcher writes that file itself, as perf_report.py's --tick does (SetSending), so the tick waits for no Python and the
/// player's no is kept even when the version's tools are gone (the tick off also deletes what is waiting). What a summary holds and
/// how it is sent is Longshot's nx2/perf_report.py, run with the build's own version's Python: --send in a process of its own, so a
/// game that starts at once (Play closes the launcher) doesn't cut it short. A version from before the statistics has no
/// perf_report.py, and is left as it is
public static class PerfReports
{
    public const string ChoiceName = "longshot_perf.ini";
    public const string PendingName = "perf_pending";
    /// The copies of what was sent (keep_sent); and in perf_pending/, when the relay may be asked again after it was busy
    public const string SentName = "perf_sent", NextName = ".next";

    public static string Script(Toolchain tools) => Path.Combine(tools.ToolsRoot, "nx2", "perf_report.py");

    /// The folder's tick as the game reads it: send=1 in its longshot_perf.ini
    public static bool Ticked(string folder)
    {
        try
        {
            return File.ReadLines(Path.Combine(folder, ChoiceName)).Any(l => l.Trim() == "send=1");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The folder's copies setting as the sender reads it: on unless keep_sent=0 in its longshot_perf.ini
    public static bool KeptSent(string folder)
    {
        try
        {
            return !File.ReadLines(Path.Combine(folder, ChoiceName)).Any(l => l.Trim() == "keep_sent=0");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// Summaries the game left to send (perf_pending/*.json)
    public static bool Waiting(string folder)
    {
        try
        {
            var pending = Path.Combine(folder, PendingName);
            return Directory.Exists(pending) && Directory.EnumerateFiles(pending, "*.json").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The tools that read and send this folder's summaries: its own version's, when they have perf_report.py; null otherwise (a
    /// version from before the statistics, or one whose tools were let go)
    public static Toolchain? ToolsFor(string folder, Toolchains tools, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return GameFolder.Version(folder) is { } own && tools.Find(own) is { } mine && fileExists(Script(mine)) ? mine : null;
    }

    /// What to do for a folder before its game starts, from the player's answers: the tick to write when the folder's differs, or
    /// its copies setting does while the tick is on (null: it already agrees), and whether to send what waits (only with the tick on)
    public static (bool? Tick, bool Send) Plan(bool on, bool keep, bool ticked, bool kept, bool waiting) =>
        (on != ticked || on && keep != kept ? on : null, on && waiting);

    /// --send, in a process of its own
    public static ProcessStartInfo SendCommand(Toolchain tools, string folder) => Command(tools, folder, "--send");

    /// The folder's tick written by the launcher itself, as Longshot's nx2/perf_report.py writes it (set_sending), with no Python:
    /// on, longshot_perf.ini is send=1 (and keep_sent=0 with the copies off, which also empties perf_sent/); off, longshot_perf.ini
    /// is removed and perf_pending/ and perf_sent/ emptied, so nothing recorded is sent and no copy is kept: the player said no.
    /// Nothing thrown: a file that can't be written or removed is left as it is
    public static void SetSending(string folder, bool on, bool keep = true)
    {
        var path = Path.Combine(folder, ChoiceName);
        if (on)
        {
            try
            {
                File.WriteAllText(path + ".tmp", "send=1\n" + (keep ? "" : "keep_sent=0\n"));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            if (!keep) RemoveAll(folder, SentName, ".json", ".tmp");
            return;
        }
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        RemoveAll(folder, PendingName, ".json", ".tmp", NextName);   // (and a summary cut short while it was written: its .tmp)
        RemoveAll(folder, SentName, ".json", ".tmp");
    }

    /// sub's files whose names end with one of ends deleted (perf_report.py's remove_all); nothing thrown
    static void RemoveAll(string folder, string sub, params string[] ends)
    {
        string[] files;
        try { files = Directory.GetFiles(Path.Combine(folder, sub)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var file in files)
        {
            if (!ends.Any(end => Path.GetFileName(file).EndsWith(end, StringComparison.Ordinal))) continue;
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// Before a game starts (LauncherService.PlayFolder), on the window's thread: the folder's tick made the player's answer
    /// (SetSending, written by the launcher: the game reads it at its start, and nothing waits for a Python), then what waits sent
    /// by its version's perf_report.py in a process left running. Turned off, the tick is honoured even when that version's tools
    /// are gone. Turned on, it is written only for a version with perf_report.py (ToolsFor): one from before the statistics is left
    /// as it is. Never stops the game from starting: a Python that doesn't start is left as it is
    public static void BeforePlay(string folder, bool on, bool keep, Toolchains tools)
    {
        var plan = Plan(on, keep, Ticked(folder), KeptSent(folder), Waiting(folder));
        if (plan.Tick == false)
        {
            SetSending(folder, false);
            return;
        }
        if (plan.Tick == null && !plan.Send) return;
        if (ToolsFor(folder, tools) is not { } mine) return;
        if (plan.Tick == true) SetSending(folder, true, keep);
        try
        {
            if (plan.Send) Process.Start(SendCommand(mine, folder))?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    /// The command: perf_report.py with its version's Python, in the game's environment (GamePlay's rule: without the AppImage
    /// runtime's and the tools' settings, LD_LIBRARY_PATH among them), its output nowhere
    static ProcessStartInfo Command(Toolchain tools, string folder, params string[] options)
    {
        var si = new ProcessStartInfo(tools.Python)
        {
            WorkingDirectory = tools.ToolsRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        si.ArgumentList.Add("-I");
        si.ArgumentList.Add(Script(tools));
        si.ArgumentList.Add(folder);
        foreach (var o in options) si.ArgumentList.Add(o);
        var current = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        si.Environment.Clear();
        foreach (var (k, v) in GamePlay.Environment(Platforms.Current, current)) si.Environment[k] = v;
        si.Environment["PYTHONIOENCODING"] = "utf-8";
        return si;
    }
}
