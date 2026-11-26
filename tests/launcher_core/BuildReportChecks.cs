using System.Net;
using System.Text.Json.Nodes;
using LongshotLauncher.Core;

/// A failed build's report (BuildReports): what it holds, the home folder taken out, the log's last lines only, and what each of
/// the server's answers becomes, from a fake server (never Longshot-api's)
static partial class Program
{
    sealed class AnswerWith(HttpStatusCode status, string body, Action<HttpRequestMessage, string>? seen = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            seen?.Invoke(request, request.Content is { } c ? await c.ReadAsStringAsync(cancel) : "");
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    sealed class NoServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            throw new HttpRequestException("No such host is known.");
    }

    static async Task BuildReportChecks(string repo)
    {
        DiagnosticChecks();
        BuildSummarySourceChecks(repo);
        await ShutdownSummaryChecks();
        var home = OperatingSystem.IsWindows() ? @"C:\Users\Ana" : "/home/ana";
        var sep = OperatingSystem.IsWindows() ? @"\" : "/";
        var homeAs = OperatingSystem.IsWindows() ? Privacy.WindowsHome : Privacy.LinuxHome;
        var log = Enumerable.Range(1, BuildReports.LogLines + 50).Select(i => $"line {i}").Append($"reading {home}{sep}Longshot{sep}x.bars").ToList();
        var failure = new BuildReports.Failure("v3.4.0", "v2.4.1", "Release", BuildReports.Update, "Windows 11, X64, .NET 10.0.0",
            "32 GB, 16 threads, AMD Radeon RX 7800 XT GPU", "memory automatic, workers automatic, DLSS off, FSR 4 off, XeSS off",
            $"File not found: {home}{sep}Longshot{sep}x.bars", null, log);
        var json = BuildReports.Json(failure, home);
        var report = JsonNode.Parse(json)!;
        Require(report["v"]!.GetValue<int>() == 1 && report["launcher"]!.GetValue<string>() == "v3.4.0" &&
                report["version"]!.GetValue<string>() == "v2.4.1" && report["kind"]!.GetValue<string>() == "update" &&
                report["error"]!.GetValue<string>() == "", "A build report holds the versions, what the build was, and an empty error.");
        // (what the build was as an id of its own, not the start button's words: Build, Update to, Go back to, Rebuild)
        Require(BuildReports.KindOf(update: false, rebuild: false, older: false) == "first build" &&
                BuildReports.KindOf(update: true, rebuild: false, older: false) == "update" &&
                BuildReports.KindOf(update: true, rebuild: false, older: true) == "go back" &&
                BuildReports.KindOf(update: false, rebuild: true, older: false) == "rebuild",
            "A report's kind: first build, update, go back or rebuild.");
        var lines = report["log"]!.AsArray();
        Require(lines.Count == BuildReports.LogLines && lines[^1]!.GetValue<string>().StartsWith("reading " + homeAs, StringComparison.Ordinal),
            "A build report keeps the log's last 200 lines, the home folder as %USERPROFILE% or ~.");
        Require(!json.Contains("Ana", StringComparison.Ordinal), "No user name in a build report: " + json[..200]);

        // (each text cut to the server's limit, on a whole character, so the report fits and Show what is sent is what goes)
        Require(BuildReports.Cut("abc", 3) == "abc" && BuildReports.Cut("abcd", 3) == "abc", "A text longer than its limit is cut to it.");
        Require(BuildReports.Cut("ab\U0001F600", 3) == "ab" && BuildReports.Cut("ab\U0001F600c", 4) == "ab\U0001F600",
            "A cut never leaves half of a surrogate pair.");
        var longText = new string('x', 5000);
        // (a path starts where the home does: #164's rule leaves a home inside a longer word alone, and a
        // real line names the home after a space or a quote, "reading /home/ana/x")
        var homeLine = "reading " + new string('x', 285) + " " + home + sep + "x.bars";
        var wide = new BuildReports.Failure(new string('1', 100), new string('2', 100), new string('3', 100), new string('k', 100),
            new string('o', 1000), new string('p', 1000), new string('s', 1000), longText, longText,
            Enumerable.Repeat(longText, BuildReports.LogLines - 1).Append(homeLine).ToList(), Cpu: new string('c', 1000));
        var wideJson = BuildReports.Json(wide, home);
        var cut = JsonNode.Parse(wideJson)!;
        int Length(string field) => cut[field]!.GetValue<string>().Length;
        Require(Length("launcher") == 40 && Length("version") == 40 && Length("channel") == 40 && Length("kind") == 20 && Length("os") == 200
                && Length("cpu") == 120 && Length("pc") == 300 && Length("settings") == 400 && Length("why") == 300 && Length("error") == 300,
            "Each field cut to the server's limit: 40 for the versions and the channel, 20 the kind, 200 the OS, 120 the CPU, 300 the PC, " +
            "400 the settings, 300 why and the error.");
        var cutLog = cut["log"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
        Require(cutLog.Count == BuildReports.LogLines && cutLog.Take(BuildReports.LogLines - 1).All(l => l.Length == 300),
            "Each log line cut to 300, all 200 kept when they fit.");
        Require(cutLog[^1] == BuildReports.Cut("reading " + new string('x', 285) + " " + homeAs + sep + "x.bars", BuildReports.LogLineLength) && !wideJson.Contains("Ana", StringComparison.Ordinal),
            "The home folder taken out before a line is cut: no cut leaves part of the user's name.");
        Require(System.Text.Encoding.UTF8.GetByteCount(wideJson) <= BuildReports.MaxBytes, "A report of long lines fits in 128 KiB.");
        var script = Enumerable.Range(0, BuildReports.LogLines).Select(i => i + new string('字', 400)).ToList();
        var scriptJson = BuildReports.Json(wide with { Log = script }, home);
        var scriptLog = JsonNode.Parse(scriptJson)!["log"]!.AsArray();
        Require(System.Text.Encoding.UTF8.GetByteCount(scriptJson) <= BuildReports.MaxBytes && scriptLog.Count is > 0 and < BuildReports.LogLines
                && scriptLog[^1]!.GetValue<string>().StartsWith("199", StringComparison.Ordinal),
            "Lines of 3 bytes a character: the oldest left out until the report fits in 128 KiB, the newest kept.");

        string? sentTo = null, sentBody = null;
        var created = new HttpClient(new AnswerWith(HttpStatusCode.Created,
            $$"""{"comment": "{{BuildReports.IssuePrefix}}90#issuecomment-1", "new": true}""", (r, b) => (sentTo, sentBody) = (r.RequestUri?.ToString(), b)));
        var answer = await BuildReports.SendAsync(created, json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Sent && answer.Link == BuildReports.IssuePrefix + "90#issuecomment-1" &&
                sentTo == BuildReports.Address && sentBody == json, "Sent: posted to /telemetry/build, and its comment to see.");
        Require(BuildReports.Address.StartsWith("https://data-services.magicbean.dev/", StringComparison.Ordinal),
            "Build reports go to Longshot-api on its magicbean.dev name.");
        var elsewhere = new HttpClient(new AnswerWith(HttpStatusCode.OK, """{"comment": "https://evil.example/x", "new": false}"""));
        answer = await BuildReports.SendAsync(elsewhere, json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Sent && answer.Link == null, "A comment off the build issue is not offered.");
        answer = await BuildReports.SendAsync(new HttpClient(new AnswerWith(HttpStatusCode.TooManyRequests, "{}")), json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Busy, "429: the server is busy.");
        answer = await BuildReports.SendAsync(new HttpClient(new AnswerWith(HttpStatusCode.BadRequest, """{"error": "why is missing"}""")), json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Refused && answer.Detail == "why is missing", "400: refused, with the server's reason.");
        answer = await BuildReports.SendAsync(new HttpClient(new AnswerWith(HttpStatusCode.NotFound, "not json")), json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Refused && answer.Detail == "HTTP 404", "404 (the relay switched off): refused.");
        answer = await BuildReports.SendAsync(new HttpClient(new AnswerWith(HttpStatusCode.BadGateway, "")), json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Unreachable, "502: not sent, try again.");
        answer = await BuildReports.SendAsync(new HttpClient(new NoServer()), json, CancellationToken.None);
        Require(answer.Outcome == BuildReports.Outcome.Unreachable && answer.Detail == "No such host is known.", "No answer: not sent, and why.");
        BuildStatisticsChecks(home);
    }

    static async Task ShutdownSummaryChecks()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new BuildReports.Shutdown();
        gate.Track(first.Task); // The failed attempt's summary is still sending when the player retries.
        gate.Track(retry.Task);
        Action? finalization = null;
        var closes = 0;
        gate.Terminate(close => finalization = close, () => { closes++; closed.TrySetResult(); }, TimeSpan.FromSeconds(5));
        gate.Terminate(_ => throw new InvalidOperationException("held twice"), () => closes++, TimeSpan.FromSeconds(5));
        retry.SetResult();
        Require(finalization != null && !closed.Task.IsCompleted, "Shutdown holds the build's finalization callback during rollback.");
        finalization!();
        Require(!closed.Task.IsCompleted, "After rollback, shutdown still waits for the earlier attempt's summary.");
        first.SetResult();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(closes == 1, "A repeated termination closes only once, after both attempts' summaries.");

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate = new BuildReports.Shutdown();
        gate.Track(pending.Task);
        finalization = null;
        gate.Terminate(close => finalization = close, () => closed.TrySetResult(), TimeSpan.FromMilliseconds(50));
        await Task.Delay(150);
        Require(!closed.Task.IsCompleted, "The send deadline cannot close the launcher while rollback is held.");
        finalization!();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!pending.Task.IsCompleted, "After rollback the deadline permits shutdown even if the send is still pending.");
    }

    /// How a build ended and each stage's progress and time (BuildProgress.Times), the CPU's name, and the statistics' summary: the
    /// same fields without the log, why or error line
    static void BuildStatisticsChecks(string home)
    {
        var at = new DateTime(2026, 11, 16, 1, 0, 0, DateTimeKind.Utc);
        var p = new BuildProgress(download: false, shaders: false) { Now = () => at };
        Feed(p, StageEvent("read"));
        at = at.AddSeconds(200);
        Feed(p, StageEvent("lift"));
        at = at.AddSeconds(100);
        Feed(p, StageEvent("compile"));
        Feed(p, ProgressEvent("compile", 0.25));
        at = at.AddSeconds(60.04);
        var times = p.Times();
        Require(times["read"] == (1, 200, false) && times["lift"] == (1, 100, false),
            "A stage that ended: all of it, and its time from its start to the next one's: " + string.Join(", ", times));
        Require(times["compile"].Progress == 0.25 && times["compile"].Seconds == 60, "The running stage: how far it is, its time until now.");
        Require(times["finish"] == (0, 0, false), "A stage not started: none of it, no time.");
        Feed(p, Event("error", ("code", "tool_failed"), ("text", "compile failed")));
        at = at.AddSeconds(30);
        Require(p.Times()["compile"].Seconds == 60, "A stage the build stopped in: its time stops with it.");

        // (a stage the builder runs again: its runs added up, not the stages between them)
        var q = new BuildProgress(download: false, shaders: false) { Now = () => at };
        Feed(q, StageEvent("read"));
        at = at.AddSeconds(50);
        Feed(q, StageEvent("lift"));
        at = at.AddSeconds(100);
        Feed(q, StageEvent("read"));
        at = at.AddSeconds(20);
        Feed(q, StageEvent("lift"));
        at = at.AddSeconds(10);
        var again = q.Times();
        Require(again["read"].Seconds == 70 && again["lift"].Seconds == 110,
            "A stage run again: its runs added up, the stages between not counted in it: " + string.Join(", ", again));

        Require(SystemInfo.CpuName("processor\t: 0\nmodel name\t: AMD Ryzen 7  6800H with Radeon Graphics\nflags\t: fpu\n") ==
                "AMD Ryzen 7 6800H with Radeon Graphics", "The CPU's name from /proc/cpuinfo, spaces made single.");
        Require(SystemInfo.CpuName("processor\t: 0\nBogoMIPS\t: 48.00\n") == null, "No model name (ARM): no CPU name.");

        var stages = new Dictionary<string, (double, double, bool)> { ["read"] = (1, 212.4, true), ["compile"] = (0.37, 401, false) };
        var failure = new BuildReports.Failure("v3.4.0", "v2.4.1", "Release", BuildReports.Update, "Linux", "32 GB, 16 threads", "workers automatic",
            "the build stopped", "error: x", ["line 1"], BuildReports.Terminated, "AMD Ryzen 7 6800H", stages);
        var sent = JsonNode.Parse(BuildReports.Json(failure, home))!;
        Require(sent["client"]!.GetValue<string>() == BuildReports.Client && BuildReports.Client == "Longshot",
            "Every report names its client, apart from the launcher's version.");
        Require(sent["outcome"]!.GetValue<string>() == "terminated" && sent["cpu"]!.GetValue<string>() == "AMD Ryzen 7 6800H" &&
                !sent["auto"]!.GetValue<bool>() && sent["log"]!.AsArray().Count == 1 && sent["why"]!.GetValue<string>() == "the build stopped",
            "Send's report: how the build ended, the CPU, the log and why.");
        var compile = sent["stages"]!["compile"]!;
        Require(compile["progress"]!.GetValue<double>() == 0.37 && compile["seconds"]!.GetValue<double>() == 401 &&
                !compile["kept"]!.GetValue<bool>() && sent["stages"]!["read"]!["kept"]!.GetValue<bool>(),
            "Each stage by its id: its progress, its seconds and whether it kept an earlier build's work.");
        var summary = JsonNode.Parse(BuildReports.Json(failure with { Outcome = BuildReports.Cancelled, Auto = true }, home))!;
        Require(summary["auto"]!.GetValue<bool>() && summary["outcome"]!.GetValue<string>() == "cancelled" &&
                summary["log"]!.AsArray().Count == 0 && summary["why"]!.GetValue<string>() == "" &&
                summary["error"]!.GetValue<string>() == "" && summary["stages"]!["compile"] != null,
            "The statistics' summary of a cancelled build: how it ended and the stages, no log, why or error line.");
        var failed = failure with
        {
            Outcome = BuildReports.Failed, Auto = true, Why = $"lift failed in {home}/Longshot/build/romfs/Actor.sarc",
            Error = @"error: can't open C:\Games\Longshot\data.nup",
        };
        var auto = JsonNode.Parse(BuildReports.Json(failed, home))!;
        Require(auto["why"]!.GetValue<string>() == "lift failed in " + (OperatingSystem.IsWindows() ? Privacy.WindowsHome : Privacy.LinuxHome) + "/Longshot/build/romfs/[game file]" &&
                auto["error"]!.GetValue<string>() == @"error: can't open C:\Games\Longshot\[game file]" && auto["log"]!.AsArray().Count == 0,
            "A failed build's summary says why and its error line, its paths with no user name and no game file's name: " +
            auto["why"] + " / " + auto["error"]);
        Require(BuildReports.Diagnostic("compile stopped: Main.bfstm missing") == "compile stopped: [game file] missing" &&
                BuildReports.Diagnostic("  ") == "" && BuildReports.Diagnostic(new string('x', 400)).Length == BuildReports.TextLength,
            "A diagnostic loses game files' names and is cut to the server's limit.");
        var done = JsonNode.Parse(BuildReports.Json(failed with { Outcome = BuildReports.Completed }, home))!;
        Require(done["why"]!.GetValue<string>() == "" && done["error"]!.GetValue<string>() == "",
            "A completed build's summary carries no error line, even one left from earlier in the build.");
        var same = JsonNode.Parse(BuildReports.Json(failed with { Why = "RuntimeError: boom", Error = "RuntimeError: boom" }, home))!;
        Require(same["why"]!.GetValue<string>() == "RuntimeError: boom" && same["error"]!.GetValue<string>() == "",
            "A summary whose error line only says why again sends it once, as why.");
        var stopped = JsonNode.Parse(BuildReports.Json(failed with
        {
            Outcome = BuildReports.Terminated, Why = BuildReports.LauncherStopped, Error = "warning: an earlier line",
        }, home))!;
        Require(stopped["why"]!.GetValue<string>() == BuildReports.LauncherStopped && BuildReports.LauncherStopped.Contains("SIGTERM") &&
                stopped["error"]!.GetValue<string>() == "",
            "The launcher's SIGTERM: the summary says why the build stopped, and no earlier error line: " + stopped["why"] + " / " + stopped["error"]);
        // (the user's name through the JSON that is sent: names with several spaces, on other drives, under Linux's /media,
        // and the user's own name beside the home folder's; everything else in the paths stays)
        foreach (var (h, why, error, users, kept) in new (string, string, string, string[]?, string)[]
                 {
                     (@"C:\Users\Jane Ann Doe", @"cannot open D:\Users\Jane Ann Doe\tools\make_game.py",
                      @"C:\Users\JANEAN~1\AppData\Local\Temp\x: denied", null, @"D:\Users\<user>\tools\make_game.py"),
                     ("/home/alex", "/media/alex/My Very Private Drive/tools/make_game.py: not found",
                      "cannot write /run/media/alex/x and /home/alex/Longshot/work", null, "/media/<user>/My Very Private Drive/tools/make_game.py"),
                     (@"C:\Users\Ana", @"cannot open E:\Backup\ana.b\saves", @"C:\Users\Ana\x", ["ana.b"], @"E:\Backup\<user>\saves"),
                 })
        {
            var sentJson = BuildReports.Json(failed with { Why = why, Error = error }, h, users);
            var names = (users ?? []).Append(h.Split('\\', '/')[^1]).Select(n => n.Split(' ')[^1]);
            Require(names.All(n => !sentJson.Contains(n, StringComparison.OrdinalIgnoreCase)) && !sentJson.Contains("JANEAN~1") &&
                    JsonNode.Parse(sentJson)!["why"]!.GetValue<string>().Contains(kept, StringComparison.Ordinal),
                "A summary's JSON names no user, and keeps the rest of the path: " + sentJson);
        }
        // (the placeholders as the game writes them, readable in the JSON that is sent, a summary's and Send's: never \u003C)
        foreach (var on in new[] { true, false })
        {
            var both = BuildReports.Json(failed with { Auto = on, Why = @"cannot open C:\Users\Ana\Longshot\x", Error = @"D:\Users\Ana\y is full" },
                                         @"C:\Users\Ana");
            var values = JsonNode.Parse(both)!;
            Require(both.Contains(Privacy.WindowsHome, StringComparison.Ordinal) && both.Contains(Privacy.User, StringComparison.Ordinal) &&
                    !both.Contains(@"\u003C", StringComparison.OrdinalIgnoreCase) && !both.Contains(@"\u003E", StringComparison.OrdinalIgnoreCase) &&
                    values["why"]!.GetValue<string>() == @"cannot open %USERPROFILE%\Longshot\x" &&
                    values["error"]!.GetValue<string>() == @"D:\Users\<user>\y is full",
                $"The JSON sent writes {Privacy.WindowsHome} and {Privacy.User} as they are ({(on ? "a summary" : "Send's report")}): " + both);
        }

        // (the launcher's SIGTERM: the summary sent once, whichever thread ends the build first, and made on the window's thread,
        // so the signal's thread reads none of the window's data; nothing it does throws, so the build is always stopped)
        var once = new BuildReports.Once();
        var claims = Enumerable.Range(0, 64).AsParallel().Count(_ => once.Claim());
        Require(claims == 1 && !once.Claim(), "A build's summary claimed once only, by one of many threads at once.");
        Require(BuildReports.OnWindow<string>(run => run(), () => "made", TimeSpan.FromSeconds(1)) == "made",
            "Made on the window's thread: what it made.");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        Require(BuildReports.OnWindow<string>(_ => { }, () => "made", TimeSpan.FromMilliseconds(50)) == null && waited.Elapsed < TimeSpan.FromSeconds(5),
            "A window's thread that doesn't make it in time: none, and no wait past the time given.");
        Require(BuildReports.OnWindow<string>(run => run(), () => throw new InvalidOperationException("Collection was modified"), TimeSpan.FromSeconds(1)) == null,
            "A summary that throws on the window's thread: none, nothing thrown to the signal's thread.");
        Require(BuildReports.OnWindow<string>(_ => throw new InvalidOperationException("no window"), () => "made", TimeSpan.FromSeconds(1)) == null,
            "A window's thread that can't be posted to: none, nothing thrown.");
    }

    /// What a summary's diagnostic takes out (every game file's name, a link's query and password) and what it leaves (the rest of
    /// a path, prose with a slash in it, a link's path). The user's name goes before it (Json: Privacy.Scrub, PrivacyChecks)
    static void DiagnosticChecks()
    {
        foreach (var (text, want) in new[]
        {
            (@"can't open D:\Jane Doe\Longshot\x", @"can't open D:\Jane Doe\Longshot\x"),
            (@"denied: C:\Program Files (x86)\Longshot Tools\make_game.exe.", @"denied: C:\Program Files (x86)\Longshot Tools\make_game.exe."),
            ("/media/data/My Passport/dumps/x: no space left", "/media/data/My Passport/dumps/x: no space left"),
            (@"can't read C:\Games\The Legend Dump v2.nup", @"can't read C:\Games\The Legend Dump [game file]"),
            (@"open 'E:\Some Folder\game file.nx' failed", @"open 'E:\Some Folder\[game file]' failed"),
            ("open \"The Game v2.nsz\" failed", "open \"[game file]\" failed"),
            ("error: can't read MyGame[0100ABCD][v131072].nx", "error: can't read [game file]"),
            ("could not decrypt main.nso with prod.keys", "could not decrypt [game file] with [game file]"),
            ("missing Link.bfres, Actor.pack.zs and Shader.Product.bntx", "missing [game file], [game file] and [game file]"),
            (@"lift: romfs\Actor\Link.sbfres", @"lift: romfs\Actor\[game file]"),
            ("cache build/romfs/Pack is stale", "cache build/romfs/Pack is stale"),
            ("compiled 3/10 shaders, N/A and/or skipped", "compiled 3/10 shaders, N/A and/or skipped"),
            ("struct.pack() and d.keys() failed", "struct.pack() and d.keys() failed"),
            ("HTTP 404 from https://git.magicbean.dev/MagicBeanDev/Longshot/releases", "HTTP 404 from https://git.magicbean.dev/MagicBeanDev/Longshot/releases"),
            ("HTTP 404 from https://example.com/u/data/x", "HTTP 404 from https://example.com/u/data/x"),
            ("HTTP 401 from https://bob:hunter2@git.magicbean.dev/x?token=abc#top", "HTTP 401 from https://git.magicbean.dev/x"),
            ("HTTP 404 from https://example.com/dl/Main.bfstm", "HTTP 404 from https://example.com/dl/[game file]"),
            ("~/Longshot/work is full", "~/Longshot/work is full"),
        })
            Require(BuildReports.Diagnostic(text) == want, $"A diagnostic: \"{text}\" sent as \"{BuildReports.Diagnostic(text)}\", not \"{want}\".");
        foreach (var e in GameFiles.Extensions)
            Require(BuildReports.Diagnostic("read x" + e + " failed") == "read [game file] failed",
                $"Every dump the launcher takes ({e}) is a game file to a diagnostic.");
        var waited = System.Diagnostics.Stopwatch.StartNew();
        Require(BuildReports.Diagnostic(string.Concat(Enumerable.Repeat("a=b.", 25_000))).Length == BuildReports.TextLength &&
                waited.Elapsed < TimeSpan.FromSeconds(2),
            $"A 100 KB word is cut before the patterns run: {waited.ElapsedMilliseconds} ms.");
    }

    /// The window's side of the summaries, in its source: why always given (the launcher's SIGTERM says so), and in English (a
    /// translated fallback would count one failure under a signature per language)
    static void BuildSummarySourceChecks(string repo)
    {
        var build = File.ReadAllText(Path.Combine(repo, "App", "ViewModels", "BuildStepViewModel.cs"));
        Require(build.Contains("void Summarize(string outcome, string why)") && build.Contains("string? Summary(string outcome, string why)"),
            "A build's summary is always given why it ended.");
        Require(build.Contains("died = progress.Error == null && errorCode == null;") && build.Contains("if (died) Summarize(outcome, reported ?? why);")
                && !build.Contains("Summarize(BuildReports.Completed") && !build.Contains("Summarize(BuildReports.Cancelled"),
            "The launcher summarizes only a builder that died without an error event; a build's own end is the builder's to send.");
        Require(build.Contains("Summarize(BuildReports.Terminated, BuildReports.LauncherStopped)"),
            "The launcher's SIGTERM: the summary says the launcher was stopped.");
        Require(build.Contains("Shutdown.Track(BuildReports.SendAsync") &&
                build.Contains("Shutdown.Terminate(close => CloseAfterFinalizing = close"),
            "Build attempts track their sends and hold the shutdown callback for finalization.");
        var program = File.ReadAllText(Path.Combine(repo, "App", "Program.cs"));
        Require(program.Contains("if (ViewModels.BuildStepViewModel.Shutdown.Terminating) return;"),
            "Another SIGTERM cannot bypass the held shutdown after the current build is cleared.");
        Require(!System.Text.RegularExpressions.Regex.IsMatch(build, @"Summar(?:ize|y)\([^;\n]*Tr\.[TFP]\("),
            "No summary is given a translated why (Tr.T, Tr.F, Tr.P).");
        Require(build.Contains("? Tr.N(\"longshot-builder was stopped by a signal (SIGTERM) from outside the launcher.\")") &&
                build.Contains(": English(\"longshot-builder stopped with exit code {0} without saying why.\", code));") &&
                build.Contains("Fail(why, outcome, reported, errorCode);"),
            "A build that stopped without saying why: the summary says so in English, the page in the player's language.");
    }
}
