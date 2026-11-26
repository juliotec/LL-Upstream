using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// A build reported to the Longshot team, at Longshot-api's /telemetry/build (Address), two ways:
///
/// - Send on the problem page (only when the player presses it): the failed build in full, which the server writes as one comment
///   per failure on the build issue, counting its repeats there: an issue only the team can read. The page shows what is sent before it is
///   (Json): the launcher's and the game's versions, the channel, what the build was, the system, this PC's CPU, memory, threads
///   and graphics cards, the build's settings, how it ended, why it stopped and its last error line, each stage's progress and
///   time, and the build log's last LogLines lines, with no user name (Privacy.Scrub: the home folder as %USERPROFILE% or ~). No game file, no
///   save.
/// - With Settings' "Send performance statistics" ticked (the player's yes to statistics, off by default), every build's end on
///   its own (Auto), with the same fields but no log. The builder sends those itself: play is given --send-build-statistics
///   (BuilderVersions.PlayArguments). The launcher sends one (Auto) only as the fallback for a build whose builder process died
///   without an error event (a crash, a signal: terminated) or that the launcher's own SIGTERM stopped: it says why it stopped
///   and its last error line, with no user name and every game file's name taken out (Diagnostic), so the dashboards tell its
///   failures apart. The server only counts those, for Prometheus: no comment.
///
/// Every report names its client (Client), apart from the launcher's version.
public static partial class BuildReports
{
    public const string Address = "https://data-services.magicbean.dev/telemetry/build";
    /// Who sent the report, beside the launcher's version (the server counts a report without one as Longshot's)
    public const string Client = "Longshot";
    /// The build log's lines a report carries (the server keeps as many)
    public const int LogLines = 200;
    /// Where a sent report's comment may be: a link the answer gives elsewhere is not offered (the reports' issue is the team's
    /// own now, so a player could not open it: its links never start with this)
    public const string IssuePrefix = "https://git.magicbean.dev/MagicBeanDev/Longshot/issues/";

    /// How a build ended (Failure.Outcome): it finished; make_game.py stopped on an error; the player pressed Stop; make_game.py or
    /// the launcher was stopped by a signal (SIGTERM: the PC shutting down, a task manager)
    public const string Completed = "completed", Failed = "failed", Cancelled = "cancelled", Terminated = "terminated";

    /// Why a build's summary says it stopped when the launcher itself was sent SIGTERM (BuildStepViewModel.Terminated): in
    /// English, as every summary's why is, so the dashboards count it under one signature
    public const string LauncherStopped = "The launcher was stopped by a signal (SIGTERM) while it built: the PC shutting down, or a task manager.";

    /// What the build was (Failure.Kind), the same whatever the buttons say: a version built into a folder of its own, an update to
    /// a newer version, an update back to an older one (the Version dropdown on Home), or a version built again in its folder
    public const string FirstBuild = "first build", Update = "update", GoBack = "go back", Rebuild = "rebuild";

    /// The kind from the wizard's job (BuildJob.Kind and Older): update, rebuild, else a first build
    public static string KindOf(bool update, bool rebuild, bool older) =>
        update ? older ? GoBack : Update : rebuild ? Rebuild : FirstBuild;

    /// What a report says of a build. Kind: KindOf. Stages: each stage's progress (0..1), seconds and whether it kept an earlier
    /// build's work (BuildProgress.Times). Auto: sent on its own, with the statistics ticked (no log; why and the error line only for a
    /// failed or terminated build, through Diagnostic)
    public sealed record Failure(string Launcher, string Version, string Channel, string Kind, string System, string Pc,
                                 string Settings, string Why, string? Error, IReadOnlyList<string> Log,
                                 string Outcome = Failed, string Cpu = "",
                                 IReadOnlyDictionary<string, (double Progress, double Seconds, bool Kept)>? Stages = null,
                                 bool Auto = false);

    /// The longest each text may be, in UTF-16 code units, as the server keeps it (Longshot-api's limits): the versions and the
    /// channel, the kind, the OS, the CPU, the PC's line, the settings' line, why and the error line, and each log line
    public const int VersionLength = 40, KindLength = 20, SystemLength = 200, CpuLength = 120, PcLength = 300, SettingsLength = 400,
                     TextLength = 300, LogLineLength = 300;
    /// The most a report may be, in UTF-8 bytes (the server answers a bigger one 413): the log's oldest lines are left out until it
    /// fits, which only a log of long lines in a script of 3 bytes a character needs
    public const int MaxBytes = 128 * 1024;

    /// text cut to at most max UTF-16 code units, on a whole character: never half of a surrogate pair
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var end = max > 0 && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..Math.Max(0, end)];
    }

    /// The names a game file or one made from it may end in, as a diagnostic loses them (GameFile): the dumps the launcher takes
    /// (GameFiles.Extensions: a check keeps them in this list), other dump and key formats, and the game's own files
    const string GameExtensions = "nup|nx|nsp|nsz|xci|xcz|nca|nso|keys|romfs|exefs|bfres|sbfres|byml|bntx|pack|bars|sarc|msbt|bfstm|bfsar|zs|dat";

    /// A quoted text, its quote neither part of a word (can't) nor followed by one: 'C:\x y\z.nup' or "x y.nup", as a whole
    [GeneratedRegex("""(?<![\w])(['"])([^'"\r\n]+)\1(?![\w])""")]
    private static partial Regex Quoted();

    /// A quoted text's last name that is a game file's, spaces and all (The Game v2.nup)
    [GeneratedRegex("""^[^\\/]*\.(?i:""" + GameExtensions + """)$""")]
    private static partial Regex QuotedName();

    /// A link other than file:// (Link)
    [GeneratedRegex("""(?i)\bhttps?://[^\s'"<>]+""")]
    private static partial Regex Url();

    /// A game file's name, a word of its own (Main.bfstm, [0100ABCD][v0].nx), not a call (d.keys(), struct.pack())
    [GeneratedRegex("""(?<![\w.-])[^\s'"<>|\\/]*\.(?i:""" + GameExtensions + """)\b(?!\()""")]
    private static partial Regex GameFile();

    /// A diagnostic as a summary sent on its own carries it (the player saw nothing before it went), cut to the server's limit.
    /// The user's name is already out of it (Json: Privacy.Scrub); the rest of a path stays, so the dashboards see where a build
    /// failed. Every game file's name goes, as [game file] (a quoted one whole, spaces and all); a link keeps its scheme, host and
    /// path, not a user and password before the host, a query or a fragment.
    /// What it can't know: the words before a game file's name with spaces that is neither quoted nor alone
    public static string Diagnostic(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // (cut first too: a long line costs the patterns time, and only its start is sent)
        var t = Cut(text.Trim(), 4 * TextLength);
        t = Quoted().Replace(t, m =>
        {
            var q = m.Groups[2].Value;
            var name = q.LastIndexOfAny(['\\', '/']) + 1;
            return QuotedName().IsMatch(q[name..]) ? m.Groups[1].Value + q[..name] + "[game file]" + m.Groups[1].Value : m.Value;
        });
        t = Url().Replace(t, m => Link(m.Value));
        return Cut(GameFile().Replace(t, "[game file]"), TextLength);
    }

    /// A link as a diagnostic keeps it: its scheme, host and path (a token in a query, a password before the host never go)
    static string Link(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : url;
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// The report as it is sent, and as the page shows it: JSON with no user name (Privacy.Scrub: this PC's home folder and user
    /// unless a test gives them), each text cut to the server's limit (the name taken out first, so no cut leaves part of it)
    public static string Json(Failure f, string? home, IEnumerable<string>? users = null)
    {
        var names = users?.ToList();
        string Clean(string? text) => Privacy.Scrub(text ?? "", home, names);
        string Kept(string? text, int max) => Cut(Clean(text), max);
        // (on its own, only a build that failed says why: a completed or cancelled one may still hold an earlier error line)
        string Reason(string? text) => !f.Auto ? Kept(text, TextLength)
            : f.Outcome is Failed or Terminated ? Diagnostic(Clean(text)) : "";
        // (a summary's error line, unless it only says why again, or the launcher itself was stopped, whose last error line is
        // only what the tools printed earlier: the dashboards would count one failure under many. Send's report keeps both, as
        // the page shows them)
        var why = Reason(f.Why);
        var error = Reason(f.Error) is var e && f.Auto && (e == why || f.Why == LauncherStopped) ? "" : e;
        List<string> log = f.Auto ? [] : (f.Log.Count > LogLines ? f.Log.Skip(f.Log.Count - LogLines) : f.Log)
            .Select(l => Kept(l, LogLineLength)).ToList();
        string Written(IEnumerable<string> lines)
        {
            var stages = new JsonObject();
            foreach (var (id, t) in f.Stages ?? new Dictionary<string, (double, double, bool)>())
                stages[id] = new JsonObject { ["progress"] = t.Progress, ["seconds"] = t.Seconds, ["kept"] = t.Kept };
            var report = new JsonObject
            {
                ["v"] = 1,
                ["client"] = Client,
                ["launcher"] = Kept(f.Launcher, VersionLength),
                ["version"] = Kept(f.Version, VersionLength),
                ["channel"] = Kept(f.Channel, VersionLength),
                ["kind"] = Kept(f.Kind, KindLength),
                ["outcome"] = f.Outcome,
                ["auto"] = f.Auto,
                ["os"] = Kept(f.System, SystemLength),
                ["cpu"] = Kept(f.Cpu, CpuLength),
                ["pc"] = Kept(f.Pc, PcLength),
                ["settings"] = Kept(f.Settings, SettingsLength),
                ["why"] = why,
                ["error"] = error,
                ["stages"] = stages,
                ["log"] = new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            };
            return Clean(report.ToJsonString(Options));
        }
        var json = Written(log);
        for (var skip = 10; Encoding.UTF8.GetByteCount(json) > MaxBytes && skip - 10 < log.Count; skip += 10)
            json = Written(log.Skip(skip));
        return json;
    }

    /// The launcher's client for the reports: 30 s at most, its own user agent
    public static readonly HttpClient Http = MakeHttp();

    static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LongshotLauncher");
        return http;
    }

    public enum Outcome { Sent, Busy, Refused, Unreachable }

    /// What became of a report: Sent with its comment (Link, null when the answer named none on the build issue), or why not
    public sealed record Answer(Outcome Outcome, string? Link = null, string? Detail = null);

    /// Give a summary already being sent a bounded window, without holding the window's thread or delaying rollback.
    public static async Task WaitForSendAsync(Task send, TimeSpan wait)
    {
        await Task.WhenAny(send, Task.Delay(wait));
    }

    /// Tracks summaries across build attempts and holds shutdown until finalization and the bounded send window both finish.
    /// Called on the window's thread.
    public sealed class Shutdown
    {
        readonly List<Task> sends = [];
        public bool Terminating { get; private set; }

        public void Track(Task send)
        {
            sends.RemoveAll(t => t.IsCompleted);
            sends.Add(send);
        }

        public void Terminate(Action<Action> hold, Action shutdown, TimeSpan wait)
        {
            if (Terminating) return;
            Terminating = true;
            var window = WaitForSendAsync(Task.WhenAll(sends), wait);
            hold(async () =>
            {
                await window;
                shutdown();
            });
        }
    }

    /// Posts the report (Json) to address, Address unless a test says otherwise
    public static async Task<Answer> SendAsync(HttpClient http, string json, CancellationToken cancel, string address = Address)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(address, content, cancel);
            var body = await response.Content.ReadAsStringAsync(cancel);
            LauncherLog.Add($"build report: {(int)response.StatusCode} from {address}");
            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                return new Answer(Outcome.Sent, Field(body, "comment") is { } link && link.StartsWith(IssuePrefix, StringComparison.Ordinal) ? link : null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new Answer(Outcome.Busy);
            if ((int)response.StatusCode is >= 400 and < 500) return new Answer(Outcome.Refused, Detail: Field(body, "error") ?? $"HTTP {(int)response.StatusCode}");
            return new Answer(Outcome.Unreachable, Detail: $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            LauncherLog.Add($"build report: not sent: {e.GetType().Name}: {e.Message}");
            return new Answer(Outcome.Unreachable, Detail: e.Message);
        }
    }

    static string? Field(string body, string name)
    {
        try
        {
            return JsonNode.Parse(body)?[name]?.GetValue<string>();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// A build's summary, sent once (BuildStepViewModel.Summarize): the window's thread and the signal's (the launcher's SIGTERM,
    /// as make_game.py gets the same one when the system stops the session) may both end a build at once, and only the first sends
    public sealed class Once
    {
        int claimed;

        /// True for the first caller only, whatever its thread
        public bool Claim() => Interlocked.Exchange(ref claimed, 1) == 0;
    }

    /// What make makes on the window's thread (post: Ui.Post), waited for from another thread, at most wait: that thread then reads
    /// none of the window's data (the build log, the stages) while the window's thread changes it. Null when the window's thread
    /// hasn't made it in time, or make or post threw: nothing is thrown to the thread that waits
    public static T? OnWindow<T>(Action<Action> post, Func<T?> make, TimeSpan wait) where T : class
    {
        var made = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            post(() =>
            {
                try { made.TrySetResult(make()); }
                catch (Exception e) { made.TrySetException(e); }
            });
            return made.Task.Wait(wait) ? made.Task.Result : null;
        }
        catch (Exception e)
        {
            LauncherLog.Add($"build report: not made: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }
}
