using System.Diagnostics;
using System.Globalization;

namespace LongshotLauncher.Core;

/// Unreadable: the game file itself could not be read (damaged, incomplete, not a game dump, the drive); CheckFailed: the check
/// did not get as far as the file (this PC's build tools, or an error the builder did not expect), so nothing is known of it
public enum GameCheckStatus { Supported, OtherVersion, Unreadable, CheckFailed }

/// make_game.py --check's answer, in plain words: the supported version, another version of the game, files it could not read,
/// or a check that could not run. Text: make_game.py's own words (English), for the tooltip and a bug report. ThisPc: a check that
/// could not run because of this PC (its build tools, compiler or Windows SDK), not an error nobody expected
public sealed record GameCheck(GameCheckStatus Status, string Text, bool ThisPc = false)
{
    public bool Ok => Status == GameCheckStatus.Supported;

    /// make_game.py's failures that are the file's: nup.py's (it reads the .nup), nxar.py's (it reads a .nx: MagicBeanDev/Longshot#685),
    /// the game modules' checks and the drive's. Its check tests this PC's compiler first (check_windows_sdk), and those failures, like
    /// one it did not expect, are not the file's
    static readonly string[] FileFailures =
    [
        "nup.py stopped", "not a NUP package", ".nup file", "nxar.py stopped", "not a .nx archive", ".nx file",
        "is not a game executable", "is not a game module", "Your drive could not read",
    ];

    /// What an @@error of the check says of the file: Unreadable when it is the file's, CheckFailed otherwise
    public static GameCheckStatus Of(string error) =>
        FileFailures.Any(f => error.Contains(f, StringComparison.OrdinalIgnoreCase)) ? GameCheckStatus.Unreadable : GameCheckStatus.CheckFailed;

    /// The answer in the player's words, shown under the file (Text stays in the tooltip and the report): what is wrong and what to
    /// do. Null for a supported file
    public string? Why => Status switch
    {
        GameCheckStatus.Supported => null,
        // (the words the build's own stop says for it: the version Longshot needs, and what to do)
        GameCheckStatus.OtherVersion => Problems.FromBuild(Text) is { Kind: ProblemKind.WrongGameVersion } p ? Tr.T(p.Detail) : Text,
        // (a .nx whose first bytes are not NXAR goes to nup.py too: make_game.py tells the two apart by them, not by the name)
        GameCheckStatus.Unreadable when Has("not a NUP package") || Has("not a .nx archive") =>
            Tr.T("This file is not a .nup or .nx package, or its beginning is damaged. Pick the .nup or .nx file of your dump, or copy it again."),
        // (a .nx made by nxconvert pack from a DNSP or DXCI: the game as NCA files, which make_game.py does not read)
        GameCheckStatus.Unreadable when Has("as NCA files") =>
            Tr.T("This .nx file holds the game as NCA files, which Longshot can't read. Make the .nx from your .nup with nxconvert pack-nup, or pick the .nup itself."),
        GameCheckStatus.Unreadable when Has("incomplete") || Has("ends early") =>
            Tr.T("This file is incomplete: it is smaller than it should be. Copy or download it again, then check it again."),
        GameCheckStatus.Unreadable when Has("is not a game executable") || Has("is not a game module") =>
            Tr.T("This dump's game code is not in a form Longshot can read. Try another dump of the game."),
        GameCheckStatus.Unreadable when Has("Your drive could not read") => Text,
        GameCheckStatus.Unreadable =>
            Tr.T("This file is damaged and could not be read. Copy or download it again, or try another dump of the game."),
        _ when ThisPc => Tr.F("The file could not be checked, because of this PC rather than the file: {0}", Text),
        // (an error the builder did not expect, or one it did not say: neither the file's nor this PC's, as far as is known)
        _ => Tr.F("The check stopped before it could tell anything about this file: {0}", Text),
    };

    bool Has(string words) => Text.Contains(words, StringComparison.OrdinalIgnoreCase);

    /// The answer from the check's stdout lines, the last line it wrote to stderr and its exit code (the 2.x Builder's
    /// Runner.Check): @@check ok, @@check bad (another version), @@error (the file could not be read, or the check could not
    /// run: Of), or nothing said (the check could not run: the stderr line says why)
    public static GameCheck Read(IEnumerable<string> stdout, string? lastError, int exitCode)
    {
        GameCheck? answer = null;
        foreach (var line in stdout)
            switch (MakeGameProtocol.Parse(line))
            {
                case MakeGameLine.Check { Ok: true } c:
                    answer = new GameCheck(GameCheckStatus.Supported, c.Text);
                    break;
                case MakeGameLine.Check c:
                    answer = new GameCheck(GameCheckStatus.OtherVersion, c.Text);
                    break;
                case MakeGameLine.Error e:
                    // (make_game.py's own failures, other than the file's, are this PC's: its prerequisites; not one it did not expect)
                    answer = new GameCheck(Of(e.Text), e.Text, ThisPc: !e.Text.Contains("did not expect", StringComparison.Ordinal));
                    break;
            }
        return answer ?? new GameCheck(GameCheckStatus.CheckFailed,
            string.IsNullOrWhiteSpace(lastError) ? Tr.F("The build tools stopped without saying why (exit code {0}).", exitCode) : lastError.Trim());
    }
}

/// make_game.py run: its stdout and stderr line by line, and its whole process tree stopped when the run is cancelled
public static class MakeGameRun
{
    /// The program run to its end; each line handed over as it comes (from a pool thread). Cancelling kills the process and
    /// everything it started (the compilers), then throws OperationCanceledException
    public static async Task<int> RunAsync(ProcessStartInfo start, Action<string> stdout, Action<string> stderr, CancellationToken cancel = default)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var p = new Process { StartInfo = start };
        var outDone = new TaskCompletionSource();
        var errDone = new TaskCompletionSource();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) outDone.TrySetResult();
            else stdout(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) errDone.TrySetResult();
            else stderr(e.Data);
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (cancel.Register(() =>
               {
                   try { p.Kill(entireProcessTree: true); }
                   catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
               }))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(outDone.Task, errDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
        cancel.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    /// (Longshot#1010) Why the build tools can't start on Windows, said before they are run: they are under a folder Windows lets
    /// no program list (Disk.Unlistable), where the compiler stops at once and the builder could only say "could not build a test
    /// program". Null when nothing is in the way, and always on Linux. The words are English, as make_game.py's, and Problems
    /// matches them (BlockedFolder)
    public static string? Blocked(MakeGameCommand command, bool windows, Func<string, bool>? listable = null) =>
        windows && Disk.Unlistable(command.WorkingDirectory, listable) is { } folder
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1}, a folder above Longshot's build tools, so they can't start. Move " +
                            @"Longshot's folder out of it (for example to C:\Longshot) and start Longshot from there.", Problems.BlockedFolderWords, folder)
            : null;

    /// make_game.py --check run, and its answer. windows: the build tools are Windows' (Blocked)
    public static async Task<GameCheck> CheckAsync(MakeGameCommand command, bool windows, CancellationToken cancel = default)
    {
        if (Blocked(command, windows) is { } blocked) return new GameCheck(GameCheckStatus.CheckFailed, blocked, ThisPc: true);
        var lines = new List<string>();
        string? lastError = null;
        try
        {
            var code = await RunAsync(command.StartInfo(), l => { lock (lines) lines.Add(l); },
                                      l => { if (!string.IsNullOrWhiteSpace(l)) lastError = l; }, cancel).ConfigureAwait(false);
            lock (lines) return GameCheck.Read(lines.ToList(), lastError, code);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new GameCheck(GameCheckStatus.CheckFailed, Tr.F("The build tools could not start: {0}", e.Message), ThisPc: true);
        }
    }
}

/// What a build should take on this PC before it starts, by stage. Measured on one PC (two player builds of nightly-56 on a
/// Ryzen 9 7900X, 24 threads, and an RTX 3090: Longshot's docs/development.md, "Build times"; measure again when the build
/// changes) and scaled by this PC's threads for the stages that use every thread (the lift, the compile, the shaders). The
/// others read and write files and keep the measured time. A thread past the cores is worth less than a core, so the scale
/// goes as (24 / threads)^0.85 rather than in step: 8 threads about 2.5 times as long, 32 about 0.8
public static class BuildForecast
{
    public const int ReferenceThreads = 24;
    static readonly Dictionary<string, (double Seconds, bool Parallel)> Reference = new()
    {
        ["download"] = (10, false), ["read"] = (76, false), ["lift"] = (57, true), ["compile"] = (305, true), ["finish"] = (66, false),
        [MakeGameProtocol.ShadersStage] = (311, true),
    };

    /// The stage uses every thread (the lift, the compile, the shaders): its time goes with the CPU, the others' with the disk
    public static bool Parallel(string stage) => Reference.TryGetValue(stage, out var r) && r.Parallel;

    /// A stage's seconds on a PC with this many threads (0 for a stage it does not know)
    public static double Seconds(string stage, int threads)
    {
        if (!Reference.TryGetValue(stage, out var r)) return 0;
        return r.Parallel ? r.Seconds * Math.Pow(ReferenceThreads / (double)Math.Clamp(threads, 1, 256), 0.85) : r.Seconds;
    }

    /// The whole build's seconds: download, the tools' download first; shaders, the shaders prepared now
    public static double Total(bool download, bool shaders, int threads) =>
        new[] { "download", "read", "lift", "compile", "finish", MakeGameProtocol.ShadersStage }
            .Where(id => (download || id != "download") && (shaders || id != MakeGameProtocol.ShadersStage))
            .Sum(id => Seconds(id, threads));
}

/// A build's times: how long each stage took (or has run), and how long the whole build has left, from BuildProgress and a clock.
/// It says nothing until this PC has shown its pace (Settled): the forecast alone is one PC's times scaled by threads, which
/// is far off on a PC whose cores are slower or faster than the reference's, or whose disk is. Then the stages still to come
/// are the forecast scaled by this PC's pace, one for the stages that use every thread (the CPU's: the lift, the compile, the
/// shaders) and one for the others (the disk's: the read, the finish), each from how long its finished stages took against
/// theirs and how fast its running stage moves (a stage that kept an earlier build's work, BuildStage.Kept, says nothing of
/// the pace and is left out). A stage known to keep an earlier build's work is forecast at the share of it it still does (the
/// compile's changed files). A running stage counts down from the rate its own progress moves at, from when it began to move
/// (the read's progress is only its last check, after minutes of unpacking; the shaders' begins once the game has started),
/// and the compile counts down from make_game's own estimate (@@eta, from what earlier builds measured on this PC), which
/// build.py says only as a file ends
public sealed class BuildClock
{
    /// The forecast seconds of measured work a pace needs before it counts
    public const double Evidence = 30;
    /// How long make_game runs before the time left is said at all, and how long before the forecast is said anyway when
    /// nothing has shown this PC's pace yet (a build of kept stages, a compile that has not said @@eta)
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(20), Patience = TimeSpan.FromMinutes(2);
    /// The share of a stage's forecast a stage that keeps an earlier build's work is forecast at (the read or the translation
    /// skipped), and the least share of a compile of some files (the link, the checks)
    const double KeptShare = 0.1, LeastCompile = 0.03;

    readonly Dictionary<string, TimeSpan> started = new();
    readonly Dictionary<string, TimeSpan> took = new();
    readonly Dictionary<string, TimeSpan> still = new();   // (a running stage's last moment at no progress: its progress moves from there)
    readonly int threads;
    TimeSpan begun = TimeSpan.MinValue;
    TimeSpan making = TimeSpan.MinValue;   // (when make_game's first stage began: the download before it is the network's)
    TimeSpan last;
    int etaLines;
    TimeSpan etaAt;
    bool settled;

    /// threads: the CPU threads the build may use (0: this PC's)
    public BuildClock(int threads = 0) => this.threads = threads > 0 ? threads : SystemInfo.Threads();

    static double Fraction(BuildProgress p, BuildStage s) => s.Weight > 0 ? Math.Clamp((p.Overall - s.Start) / s.Weight, 0, 1) : 0;

    /// The progress seen at now (a monotonic time: a Stopwatch's elapsed)
    public void Observe(BuildProgress p, TimeSpan now)
    {
        if (begun == TimeSpan.MinValue) begun = now;
        last = now;
        if (p.EtaLines != etaLines)
        {
            etaLines = p.EtaLines;
            etaAt = now;
        }
        foreach (var s in p.Stages)
        {
            if (s.State == StageState.Running && !started.ContainsKey(s.Id)) started[s.Id] = now;
            if (s.State == StageState.Running && s.Id != "download" && making == TimeSpan.MinValue) making = now;
            if (s.State == StageState.Running && Fraction(p, s) == 0) still[s.Id] = now;
            if (s.State is StageState.Done or StageState.Skipped && started.TryGetValue(s.Id, out var from) && !took.ContainsKey(s.Id))
                took[s.Id] = now - from;
        }
        // (once said, the time left is not taken back)
        if (!settled && making != TimeSpan.MinValue && now - making >= Warmup)
            settled = now - making >= Patience || p.Eta != null || Pace(p, true) != null || Pace(p, false) != null || Moving(p) != null;
    }

    /// The time left is said: make_game has run a while (Warmup) and this PC has shown its pace (a pace, make_game's @@eta, a
    /// running stage's rate), or it has run long enough (Patience) that the forecast is better than nothing; or the build is done
    public bool Settled(BuildProgress p) => settled || p.Finished;

    /// A stage's time: what it took, or how long it has run; null before it started
    public TimeSpan? Of(string id) =>
        took.TryGetValue(id, out var t) ? t : started.TryGetValue(id, out var from) ? last - from : null;

    public TimeSpan Elapsed => begun == TimeSpan.MinValue ? TimeSpan.Zero : last - begun;

    /// The running stage whose own progress tells its rest, and that rest in seconds: it has moved a tenth of the way, for ten
    /// seconds at least (the compile from its start: its files work from there)
    (BuildStage Stage, double Ran, double Remaining)? Moving(BuildProgress p)
    {
        if (p.Stages.FirstOrDefault(s => s.State == StageState.Running) is not { } s) return null;
        var ran = Of(s.Id)?.TotalSeconds ?? 0;
        var fraction = Fraction(p, s);
        var moving = s.Id != "compile" && still.TryGetValue(s.Id, out var from) ? (last - from).TotalSeconds : ran;
        return fraction >= 0.1 && moving >= 10 ? (s, ran, moving / fraction * (1 - fraction)) : null;
    }

    /// This PC's pace against the forecast for one kind of stage (cpu: the threaded ones, BuildForecast.Parallel; else the
    /// others): the finished stages' time over their forecast, with the running stage's own rate counted as far as it has gone.
    /// Left out: the download (the network's, not the PC's), and a stage that kept an earlier build's work (seconds where the
    /// forecast has minutes). Null until Evidence forecast seconds have been measured; kept within 0.2..5
    public double? Pace(BuildProgress p, bool cpu)
    {
        double actual = 0, forecast = 0;
        foreach (var s in p.Stages)
        {
            if (s.Id == "download" || s.Kept || BuildForecast.Parallel(s.Id) != cpu) continue;
            if (s.State == StageState.Done && took.TryGetValue(s.Id, out var t))
            {
                actual += t.TotalSeconds;
                forecast += BuildForecast.Seconds(s.Id, threads);
            }
        }
        // (the running stage, by the share of it done: its whole time at its rate against its forecast)
        if (Moving(p) is { } m && !m.Stage.Kept && BuildForecast.Parallel(m.Stage.Id) == cpu)
        {
            var done = Fraction(p, m.Stage);
            actual += (m.Ran + m.Remaining) * done;
            forecast += BuildForecast.Seconds(m.Stage.Id, threads) * done;
        }
        return forecast >= Evidence ? Math.Clamp(actual / forecast, 0.2, 5) : null;
    }

    /// A stage's pace: its kind's, else 1 (the forecast as it is: the disk's pace says nothing of the CPU's)
    double PaceOf(BuildProgress p, BuildStage s) => Pace(p, BuildForecast.Parallel(s.Id)) ?? 1;

    /// The share of a whole stage's work this build does in it: a compile of some files their share (a little at least, the
    /// link), a stage that keeps an earlier build's work a tenth, any other all of it
    static double Share(BuildProgress p, BuildStage s) =>
        s.Id == "compile" && p.FilesChanged is int changed && p.FilesTotal is int total && total > 0
            ? Math.Max(LeastCompile, Math.Min(1, changed / (double)total))
            : s.Kept ? KeptShare : 1;

    double Expected(BuildProgress p, BuildStage s) => BuildForecast.Seconds(s.Id, threads) * PaceOf(p, s) * Share(p, s);

    /// The seconds left; null until Settled
    public double? SecondsLeft(BuildProgress p)
    {
        if (p.Finished) return 0;
        if (!Settled(p)) return null;
        var moving = Moving(p);
        double left = 0;
        foreach (var s in p.Stages)
        {
            if (s.State is StageState.Done or StageState.Skipped) continue;
            var expected = Expected(p, s);
            if (s.State != StageState.Running)
            {
                left += expected;
                continue;
            }
            var ran = Of(s.Id)?.TotalSeconds ?? 0;
            // (the compile: build.py's @@eta less the time since it said it. Run out (the link after the last file, or a PC slower
            // than the times it knows), the compile's own progress says the rest)
            var countdown = s.Id == "compile" && p.Eta is double eta ? eta - (last - etaAt).TotalSeconds : 0;
            if (countdown > 0) left += countdown;
            else if (moving is { } m && m.Stage == s) left += m.Remaining;
            else if (s.Id == "compile" && p.Eta != null) left += expected * 0.1;   // (its countdown out, its progress not yet telling)
            else left += Math.Max(expected - ran, expected * 0.1);
        }
        return left;
    }

    /// A stage that has not started, or is running: about how long it takes in all on this PC, at its kind's pace so far; null
    /// until Settled
    public double? Estimate(BuildProgress p, BuildStage stage) => Settled(p) ? Expected(p, stage) : null;
}

/// Times and sizes as the window writes them
public static class Durations
{
    /// m:ss (h:mm:ss past an hour)
    public static string Clock(TimeSpan t)
    {
        var s = (long)Math.Round(Math.Max(0, t.TotalSeconds));
        return s >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    /// "Under a minute", "About 12 min", "About 1 h 20 min"
    public static string About(double seconds)
    {
        if (seconds < 60) return Tr.T("Under a minute");
        var m = (long)Math.Round(seconds / 60);
        if (m < 60) return Tr.F("About {0} min", m);
        return m % 60 == 0 ? Tr.F("About {0} h", m / 60) : Tr.F("About {0} h {1} min", m / 60, m % 60);
    }

    /// "~4 min" for a stage still to come
    public static string Roughly(double seconds) => Tr.F("~{0} min", Math.Max(1, (long)Math.Round(seconds / 60)));

    /// When something was saved or built, as the home writes it: "today 18:02", "yesterday 22:14", "6 Nov", "6 Nov 2025"
    public static string When(DateTime t, DateTime now)
    {
        var day = t.Date;
        if (day == now.Date) return Tr.F("today {0}", t.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (day == now.Date.AddDays(-1)) return Tr.F("yesterday {0}", t.ToString("HH:mm", CultureInfo.InvariantCulture));
        return Date(t, now);
    }

    static readonly string[] Months =
    {
        Tr.N("Jan"), Tr.N("Feb"), Tr.N("Mar"), Tr.N("Apr"), Tr.N("May"), Tr.N("Jun"),
        Tr.N("Jul"), Tr.N("Aug"), Tr.N("Sep"), Tr.N("Oct"), Tr.N("Nov"), Tr.N("Dec"),
    };

    /// "6 Nov", or "6 Nov 2025" in another year, in the language in use (its order and its month's name)
    static string Date(DateTime t, DateTime now)
    {
        var month = Tr.T(Months[t.Month - 1]);
        return t.Year == now.Year ? Tr.F("{0} {1}", t.Day, month) : Tr.F("{0} {1} {2}", t.Day, month, t.Year);
    }

    /// The day only: "today", "yesterday", "3 Nov"
    public static string Day(DateTime t, DateTime now)
    {
        if (t.Date == now.Date) return Tr.T("today");
        if (t.Date == now.Date.AddDays(-1)) return Tr.T("yesterday");
        return Date(t, now);
    }

    /// "38.4 GB", "340 MB"
    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes) >> 20} MB");
}
