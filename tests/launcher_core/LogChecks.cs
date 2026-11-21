using LongshotLauncher.Core;

/// The build log's levels (Core/LogLevels.cs): a line's level read from the game's tag ("[hos host] [trace] ..."), no tag is
/// info (a game from before the levels), a build's "error: " is an error; and the details' tail, by level and search
static partial class Program
{
    static void LogChecks(string repo)
    {
        foreach (var (line, want) in new (string, LogLevel)[]
        {
            ("[hos host] vk: device 0: Some GPU", LogLevel.Info),
            ("[hos host] [trace] vk: program 095fca6f01e3030a not translated: a network left out on this card", LogLevel.Trace),
            ("[hos Main] [debug] codec: Opus decoder 0: 48000 Hz, 2 channels", LogLevel.Debug),
            ("[hos host] [warn] x", LogLevel.Warning),
            ("[hos host] [error] x", LogLevel.Error),
            ("[trace] vk: a renderer line without the host's tag", LogLevel.Trace),
            ("error: the build stopped", LogLevel.Error),
            ("[Prepare shaders] Preparing the shaders", LogLevel.Info),
            ("[hos host] vk: a line that says [trace] later on", LogLevel.Info),
            ("[hos host] [verbose] an unknown tag", LogLevel.Info),
            ("", LogLevel.Info),
        })
            Require(LogLevels.Of(line) == want, $"log level of \"{line}\": {LogLevels.Of(line)}, not {want}");

        Require(LogLevels.All.Select(LogLevels.Prefix).SequenceEqual(["ERR", "WRN", "INF", "DBG", "TRC"]), "The details' prefixes, as wide as each other.");

        var log = new BuildLog();
        log.Add("[hos host] first");
        for (var i = 0; i < 50; i++) log.Add($"[hos host] [trace] vk: program {i} not translated: a network left out on this card");
        log.Add("[hos host] [debug] vk: 4 pools mapped");
        log.Add("error: Stopped");
        log.Add("[hos host] Last NETWORK line");
        Require(log.Count == 54, "every line kept");
        Require(log.Tail(LogLevel.Info, null, 10).SequenceEqual(["[hos host] first", "error: Stopped", "[hos host] Last NETWORK line"]),
                "Info: info and errors, trace and debug left out, in order");
        Require(log.Tail(LogLevel.Error, null, 10).SequenceEqual(["error: Stopped"]), "Errors: errors alone");
        Require(log.Tail(LogLevel.Debug, null, 10).SequenceEqual(["[hos host] first", "[hos host] [debug] vk: 4 pools mapped", "error: Stopped",
                "[hos host] Last NETWORK line"]), "Debug: debug too, not trace");
        Require(log.Tail(LogLevel.Trace, null, 100).Count == 54, "Trace: every line");
        Require(log.TailWithLevels(LogLevel.Debug, null, 10).Select(l => l.Level).SequenceEqual([LogLevel.Info, LogLevel.Debug, LogLevel.Error, LogLevel.Info]),
                "The details' lines carry their levels, for their prefixes.");
        Require(log.Tail(LogLevel.Trace, null, 3).SequenceEqual(["[hos host] [debug] vk: 4 pools mapped", "error: Stopped",
                "[hos host] Last NETWORK line"]), "the tail is the last lines shown");
        Require(log.Tail(LogLevel.Info, " network ", 10).SequenceEqual(["[hos host] Last NETWORK line"]),
                "search: any case, trimmed, within the level");
        Require(log.Tail(LogLevel.Trace, "network", 100).Count == 51, "search over trace lines");
        Require(log.Tail(LogLevel.Info, "   ", 10).Count == 3, "a blank search is no search");
        Require(log.Last(2).SequenceEqual(["error: Stopped", "[hos host] Last NETWORK line"]), "the report's lines: every level");

        // (the details during a build (#201): the lines shown are only those in view, and written again at most every fifth of a
        // second and only when they changed, not at each of a build's thousands of lines a second)
        var stepView = File.ReadAllText(Path.Combine(repo, "App", "Views", "BuildStepView.axaml"));
        Require(System.Text.RegularExpressions.Regex.IsMatch(stepView, @"<ItemsControl Name=""LogBlock"">\s*<ItemsControl.ItemsPanel>\s*<ItemsPanelTemplate><VirtualizingStackPanel/>"),
                "The details' lines are a VirtualizingStackPanel's: one text of 2000 lines laid out again froze the window (#201).");
        var views = File.ReadAllText(Path.Combine(repo, "App", "Views", "Views.cs"));
        Require(views.Contains("if (e.PropertyName == nameof(m.LogText)) WriteLogSoon();")
                && views.Contains("new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(200)")
                && views.Contains("ReferenceEquals(w[0].Text, lines[0].Text) && ReferenceEquals(w[^1].Text, lines[^1].Text)"),
                "A new line writes the details at most every 200 ms, and not when the lines shown are the same (#201).");
    }
}
