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
/// The launcher writes that file itself (SetSending), so the tick waits for no program and the player's no is kept even when the
/// version's builder is gone (the tick off also deletes what is waiting). The version's own longshot-builder (Longshot #2286) reads
/// and sends the summaries, by the file's rules (send and keep_sent, the caps, the backoff, perf_sent/, one sender at a time), in a
/// process of its own, so a game that starts at once doesn't cut it short:
///     longshot-builder perf-report &lt;game folder&gt; [--output json]                                  the state, nothing sent
///     longshot-builder perf-report &lt;game folder&gt; --send [--output json]                           what waits sent
///     longshot-builder perf-report &lt;game folder&gt; --tick on|off [--keep-sent on|off] [--output json]
/// Exit 0 whatever the relay answered (what it did not take stays in perf_pending/), 2 a command line it does not take; with --output json
/// one perf_report event a run (ReadState). The tick is still written by the launcher itself (SetSending), in the same file
public static class PerfReports
{
    public const string ChoiceName = "longshot_perf.ini";
    public const string PendingName = "perf_pending";
    /// The copies of what was sent (keep_sent); and in perf_pending/, when the relay may be asked again after it was busy
    public const string SentName = "perf_sent", NextName = ".next";

    /// longshot-builder perf-report's answer (its perf_report event): the folder's tick and copies setting, the summaries waiting
    /// after the run and the copies kept, this run's sent and refused, the game's version and whether it is a release, the relay
    /// ("" when there is nowhere to send) and, after a busy answer, when it may be asked again (Unix seconds)
    public sealed record State(bool Tick, bool KeepSent, int Waiting, int Kept, int Sent, int Refused, string Version, bool Release,
                               string Relay, long? BusyUntil);

    /// A line of perf-report --json read; null for one that is not its event of version 1
    public static State? ReadState(string line)
    {
        line = line.Trim();
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object || !e.TryGetProperty("v", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Number
                || v.GetInt32() != 1 || !e.TryGetProperty("type", out var t) || t.ValueKind != System.Text.Json.JsonValueKind.String
                || t.GetString() != "perf_report") return null;
            bool Flag(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True;
            int Count(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetInt32(out var i) ? Math.Max(0, i) : 0;
            string Text(string n) => e.TryGetProperty(n, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() ?? "" : "";
            long? busy = e.TryGetProperty("busy_until", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number && b.TryGetInt64(out var u) ? u : null;
            return new State(Flag("tick"), Flag("keep_sent"), Count("waiting"), Count("kept"), Count("sent"), Count("refused"), Text("version"),
                             Flag("release"), Text("relay"), busy);
        }
        catch (Exception x) when (x is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// The last perf_report event of a run's stdout; null when it said none
    public static State? ReadState(IEnumerable<string> stdout) => stdout.Select(ReadState).LastOrDefault(s => s != null);

    /// longshot-builder perf-report's state of a folder, nothing sent
    public static ProcessStartInfo StateCommand(Toolchain tools, string folder, string? cache = null) =>
        Command(tools, folder, cache, "--output", "json");

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

    /// The builder that reads and sends this folder's summaries: its own version's longshot-builder; null when it is not installed
    /// (its tools were let go)
    public static Toolchain? ToolsFor(string folder, Toolchains tools, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return GameFolder.Version(folder) is { } own && tools.Find(own) is { } mine && fileExists(mine.BuilderExe) ? mine : null;
    }

    /// What to do for a folder before its game starts, from the player's answers: the tick to write when the folder's differs, or
    /// its copies setting does while the tick is on (null: it already agrees), and whether to send what waits (only with the tick on)
    public static (bool? Tick, bool Send) Plan(bool on, bool keep, bool ticked, bool kept, bool waiting) =>
        (on != ticked || on && keep != kept ? on : null, on && waiting);

    /// --send, in a process of its own (with --output json: its one event, read by nobody)
    public static ProcessStartInfo SendCommand(Toolchain tools, string folder, string? cache = null) =>
        Command(tools, folder, cache, "--send", "--output", "json");

    /// The folder's tick written by the launcher itself, as longshot-builder perf-report --tick writes it:
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

    /// sub's files whose names end with one of ends deleted; nothing thrown
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
    /// (SetSending, written by the launcher: the game reads it at its start, and nothing waits for a program), then what waits sent
    /// by its version's longshot-builder in a process left running. Turned off, the tick is honoured even when that version's
    /// builder is gone. Turned on, it is written only for a version whose builder is installed (ToolsFor). Never stops the game from
    /// starting: a builder that doesn't start is left as it is
    /// cache: longshot-builder's --cache, the launcher's (LongshotBuilder.Settings): its first run of a release unpacks its tools
    /// there, as for a build, never in the builder's default cache
    public static void BeforePlay(string folder, bool on, bool keep, Toolchains tools, string? cache = null)
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
            if (plan.Send) Process.Start(SendCommand(mine, folder, cache))?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
    }

    /// The command: longshot-builder perf-report, in the game's environment (GamePlay's rule: without the AppImage runtime's
    /// settings, LD_LIBRARY_PATH among them) less the PC's LONGSHOT_, RX_ and A64_ variables (LongshotBuilder.Dropped), which would
    /// redirect it; its output nowhere
    static ProcessStartInfo Command(Toolchain tools, string folder, string? cache, params string[] options)
    {
        var si = new ProcessStartInfo(tools.BuilderExe)
        {
            WorkingDirectory = tools.Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        si.ArgumentList.Add("perf-report");
        si.ArgumentList.Add(folder);
        foreach (var o in options) si.ArgumentList.Add(o);
        if (cache != null)
        {
            si.ArgumentList.Add("--cache");
            si.ArgumentList.Add(cache);
        }
        var current = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        si.Environment.Clear();
        foreach (var (k, v) in GamePlay.Environment(Platforms.Current, current))
            if (!LongshotBuilder.Dropped.Any(d => k.StartsWith(d, StringComparison.OrdinalIgnoreCase))) si.Environment[k] = v;
        return si;
    }
}
