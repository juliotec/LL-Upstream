using LongshotLauncher.Core;

/// Copy for a bug report (BugReport) and the launcher's log it carries (LauncherLog): the lines stamped and kept, the report's
/// parts in order, the home folder as ~ (never a longer folder that starts like it), and the requests the fetchers log: a refused
/// connection with why, and the CDN's fall back
static partial class Program
{
    static async Task ReportChecks()
    {
        var clock = LauncherLog.Clock;
        try
        {
            LauncherLog.Clear();
            LauncherLog.Clock = () => new DateTimeOffset(2026, 11, 15, 7, 48, 17, 5, TimeSpan.Zero);
            LauncherLog.Add("one\ntwo\r\nthree");
            Require(LauncherLog.Tail(10).SequenceEqual(new[] { "07:48:17.005 one two three" }), "A line is stamped, and stays one line: " + LauncherLog.Tail(10)[0]);
            for (var i = 0; i < LauncherLog.Keep + 5; i++) LauncherLog.Add("line " + i);
            var all = LauncherLog.Tail(int.MaxValue);
            Require(all.Count == LauncherLog.Keep && all[0].EndsWith(" line 5", StringComparison.Ordinal)
                    && all[^1].EndsWith($" line {LauncherLog.Keep + 4}", StringComparison.Ordinal), "The log keeps its last lines, oldest first.");
            Require(LauncherLog.Tail(2).Count == 2 && LauncherLog.Tail(2)[1] == all[^1], "Tail: the last lines.");

            // (the report: the facts, what happened, what the screen adds, then the log; the home folder as ~)
            var facts = new ReportFacts("Longshot Launcher v1.0.0-rc.6 (installed)", "Windows 11, X64, .NET 10.0.0",
                                        BugReport.Network(true, null));
            var report = BugReport.Make(facts, "Updates were not checked.\nThis PC could not look up git.magicbean.dev's address.",
                                        new[] { "tools: v3.0.0-rc.4 (rc)", @"from C:\Users\Peter\Games\Hookshot" },
                                        new[] { @"12:00:00.000 GET https://git.magicbean.dev/x failed: c:\users\PETER\AppData\x" }, @"C:\Users\Peter");
            var lines = report.Split(Environment.NewLine);
            Require(lines[0] == facts.Launcher && lines[1] == facts.System
                    && lines[2] == "network: git-cdn.magicbean.dev first, then git.magicbean.dev; proxy: none"
                    && lines[3] == "what happened: Updates were not checked. / This PC could not look up git.magicbean.dev's address."
                    && lines[4] == "" && lines[5] == "tools: v3.0.0-rc.4 (rc)" && lines[6] == @"from ~\Games\Hookshot"
                    && lines[7] == "" && lines[8] == "launcher log, its last 1 lines:" && lines[9] == @"12:00:00.000 GET https://git.magicbean.dev/x failed: ~\AppData\x",
                    "The report's parts, in order, the home folder as ~ in any case:\n" + report);
            Require(!report.Contains("Peter", StringComparison.OrdinalIgnoreCase), "No user name in the report.");
            Require(BugReport.Make(facts, "x", log: Array.Empty<string>(), home: "/home/p").Contains("launcher log: empty"), "An empty log says so.");
            Require(BugReport.Network(false, new Uri("http://proxy.lan:3128")) == "network: git.magicbean.dev only (LONGSHOT_LAUNCHER_NO_CDN=1); proxy: proxy.lan:3128",
                    "The network line: no CDN, and the proxy");

            Require(BugReport.Scrub(@"C:\Users\Peter2\x and C:\Users\Peter.old and C:\Users\Peter", @"C:\Users\Peter\") == @"C:\Users\Peter2\x and C:\Users\Peter.old and ~",
                    "A folder that only starts like the home folder stays as it is.");
            Require(BugReport.Scrub("/home/pat/.local/share/longshot and /home/PAT/x and /home/pat", "/home/pat") == "~/.local/share/longshot and /home/PAT/x and ~",
                    "Linux: the home folder as ~, in its own case only.");
            Require(BugReport.Scrub("/x", "/") == "/x" && BugReport.Scrub(@"C:\x", @"C:\") == @"C:\x" && BugReport.Scrub("x", null) == "x",
                    "No home folder, or a root: nothing replaced.");
            // (Windows: either separator, also doubled as JSON writes it, and a file:// link)
            foreach (var (from, to) in new[]
                     {
                         ("C:/Users/Pat/work/build.log", "~/work/build.log"),
                         ("file:///C:/Users/Pat/work/x", "file:///~/work/x"),
                         (@"""C:\\Users\\Pat\\AppData""", @"""~\\AppData"""),
                         (@"c:/users\PAT\x", @"~\x"),
                     })
                Require(BugReport.Scrub(from, @"C:\Users\Pat") == to, $"Windows' other separators: {from} -> {BugReport.Scrub(from, @"C:\Users\Pat")}");
            Require(BugReport.Scrub(@"C:\Users\Pat Smith\work\build.log and C:/Users/Pat%20Smith/x and C:\Users\Pat-x", @"C:\Users\Pat")
                    == @"C:\Users\Pat Smith\work\build.log and C:/Users/Pat%20Smith/x and C:\Users\Pat-x",
                    "A longer folder beside the home folder, a space in its name, stays as it is.");
            Require(BugReport.Scrub(@"C:\Users\Pat Smith\x and file:///C:/Users/Pat%20Smith/y", @"C:\Users\Pat Smith") == @"~\x and file:///~/y",
                    "A home folder with a space: also as %20 in a link.");
            Require(BugReport.Scrub("'C:\\Users\\Pat' is denied. Saved in C:\\Users\\Pat. C:\\Users\\Pat is full (C:\\Users\\Pat), C:\\Users\\Pat\nC:\\other\\x",
                                    @"C:\Users\Pat") == "'~' is denied. Saved in ~. ~ is full (~), ~\nC:\\other\\x",
                    "The home folder alone in a sentence, a quote or brackets, and before a line with a path.");
            Require(!BugReport.Make(facts, "x", new[] { "log: C:/Users/Pat/work/build.log" }, Array.Empty<string>(), @"C:\Users\Pat").Contains("Pat"),
                    "A report with a forward-slash path names no user.");

            // (a request that got no answer is logged with why; the CDN's fall back too)
            LauncherLog.Clear();
            LauncherLog.Clock = clock;
            using (var http = new HttpFetcher(TimeSpan.FromSeconds(10)))
                await ThrowsAsync<FetchException>(() => http.GetTextAsync("https://127.0.0.1:1/list", null, CancellationToken.None), "Nothing listens on port 1.");
            var refused = LauncherLog.Tail(1).Single();
            Require(refused.Contains(" GET https://127.0.0.1:1/list failed after ") && refused.Contains(" ms"), "A failed request is logged: " + refused);
            Require(LauncherLog.Tail(10).Any(l => l.Contains(" connect 127.0.0.1 127.0.0.1:1: ConnectionRefused after ")),
                    "The refused address is logged: " + string.Join(" | ", LauncherLog.Tail(10)));

            // (the connection's own lines: HttpFetcher's failures read as before, with the addresses and their ends logged)
            await ConnectionChecks();

            LauncherLog.Clear();
            var server = new FailingFetcher();
            const string release = "https://git.magicbean.dev/MagicBeanDev/Longshot/releases/download/v3.1.0/SHA256SUMS";
            server.Files["https://git-cdn.magicbean.dev/MagicBeanDev/Longshot/releases/download/v3.1.0/SHA256SUMS"] = new Served(new byte[] { 1 }, 0, 503);
            server.Files[CdnFetcher.Direct(release)] = new Served(new byte[] { 2 });
            await new CdnFetcher(server).DownloadAsync(release, new MemoryStream(), null, CancellationToken.None);
            Require(LauncherLog.Tail(5).Any(l => l.EndsWith(" git-cdn.magicbean.dev failed (HTTP 503): trying git.magicbean.dev", StringComparison.Ordinal)),
                    "The CDN's fall back is logged: " + string.Join(" | ", LauncherLog.Tail(5)));
        }
        finally
        {
            LauncherLog.Clock = clock;
            LauncherLog.Clear();
        }
    }
}
