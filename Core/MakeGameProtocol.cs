using System.Globalization;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// A line of make_game.py's stdout (its docstring: the @@ lines a builder reads, and the plain "the shaders were not prepared
/// now (why): ..." line)
public abstract record MakeGameLine
{
    public sealed record Stage(string Id, string Text) : MakeGameLine;
    public sealed record Progress(double Fraction) : MakeGameLine;
    public sealed record Plan(int Changed, int Total) : MakeGameLine;
    public sealed record File(string Name, double Seconds, string Kind) : MakeGameLine;
    public sealed record Eta(double Seconds) : MakeGameLine;
    public sealed record Times(long Total, long Compile, long Previous) : MakeGameLine;
    public sealed record Check(bool Ok, string Text) : MakeGameLine;
    public sealed record Done(string Folder) : MakeGameLine;
    public sealed record Error(string Text) : MakeGameLine;
    public sealed record NotPrepared(string Reason) : MakeGameLine;
    public sealed record Plain(string Text) : MakeGameLine;
}

public static class MakeGameProtocol
{
    public const string ShadersStage = "shaders";
    static readonly Regex NotPreparedLine = new(@"^the shaders were not prepared now \((.*)\): ", RegexOptions.CultureInvariant);

    static bool Number(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    static bool Whole(string s, out long v) => long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    /// The line read; an @@ line that does not have its shape is Plain (shown in the details, as the Builders do)
    public static MakeGameLine Parse(string line)
    {
        line = line.TrimEnd('\r');
        if (!line.StartsWith("@@", StringComparison.Ordinal))
            return NotPreparedLine.Match(line) is { Success: true } m ? new MakeGameLine.NotPrepared(m.Groups[1].Value) : new MakeGameLine.Plain(line);
        var space = line.IndexOf(' ');
        var word = space < 0 ? line[2..] : line[2..space];
        var rest = space < 0 ? "" : line[(space + 1)..];
        var p = rest.Split(' ');
        switch (word)
        {
            case "stage" when p[0].Length > 0:
                return new MakeGameLine.Stage(p[0], p.Length > 1 ? rest[(p[0].Length + 1)..] : "");
            case "progress" when p.Length == 1 && Number(p[0], out var f):
                return new MakeGameLine.Progress(Math.Clamp(f, 0, 1));
            case "plan" when p.Length == 2 && Whole(p[0], out var changed) && Whole(p[1], out var total):
                return new MakeGameLine.Plan((int)changed, (int)total);
            case "file" when p.Length == 3 && Number(p[1], out var secs):
                return new MakeGameLine.File(p[0], secs, p[2]);
            case "eta" when p.Length == 1 && Number(p[0], out var eta):
                return new MakeGameLine.Eta(eta);
            case "times" when p.Length == 3 && Whole(p[0], out var all) && Whole(p[1], out var compile) && Whole(p[2], out var previous):
                return new MakeGameLine.Times(all, compile, previous);
            case "check" when p[0] is "ok" or "bad":
                return new MakeGameLine.Check(p[0] == "ok", p.Length > 1 ? rest[(p[0].Length + 1)..] : "");
            case "done":
                return new MakeGameLine.Done(rest);
            case "error":
                return new MakeGameLine.Error(rest);
        }
        return new MakeGameLine.Plain(line);
    }
}

public enum StageState { Waiting, Running, Done, Skipped }

public sealed class BuildStage
{
    public string Id { get; }
    public string Title { get; }
    public StageState State { get; internal set; }
    /// Under the row: the compile's files, or why the shaders were left to the first start
    public string? Note { get; internal set; }
    /// It kept what an earlier build did (the read or the translation skipped, a compile of the changed files only): its time is
    /// no measure of this PC's pace
    public bool Kept { get; internal set; }

    internal double From, Size;
    /// When its latest run started and ended (BuildProgress.Now): null before it started, and while it runs for the end
    internal DateTime? Began, Ended;
    /// The seconds of its earlier runs, when make_game ran it again
    internal double Earlier;

    /// How long it ran, in seconds, its runs added up (not the stages between them): until now while it runs, 0 when it never
    /// started
    public double Seconds(DateTime now) => Began is { } b ? Earlier + Math.Max(0, ((Ended ?? now) - b).TotalSeconds) : 0;

    /// Its share of the overall bar, 0..1 (the stages this build has add up to 1)
    public double Weight => Size;
    /// Where it starts on the overall bar, 0..1
    public double Start => From;

    internal BuildStage(string id, string title, double from, double size)
    {
        Id = id;
        Title = title;
        From = from;
        Size = size;
    }
}

/// One overall 0..1 for the whole build, each stage's share of it its forecast time on this PC (BuildForecast at the build's
/// threads: with fewer threads the compile and the shaders take more of the bar, as they take more of the time): the tools'
/// download first when the version's tools are not installed, then make_game.py's stages. Only the stages this build has count,
/// scaled to fill the bar; the bar never goes back (make_game may run a stage again), and it is 1 once make_game says @@done.
public sealed class BuildProgress
{
    static readonly (string Id, string Title)[] Titles =
    {
        ("download", "Download the build tools"),
        ("read", "Read your game files"),
        ("lift", "Translate the game to PC code"),
        ("compile", "Compile"),
        ("finish", "Put the game together"),
        (MakeGameProtocol.ShadersStage, "Prepare the shaders"),
    };
    // (make_game's plain lines when a stage keeps what an earlier build did: its time then says nothing of this PC's pace)
    static readonly (string Stage, string Line)[] KeptLines =
    {
        ("read", "the game was read from this .nup before"),
        ("lift", "the game was translated with these tools before"),
    };

    readonly List<BuildStage> stages = new();
    BuildStage? current;

    /// The clock the stages' times are read from (a test's own)
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// download: the tools are downloaded first; shaders: this build prepares the shaders; update: make_game --update (its
    /// read step reads the files the folder keeps); threads: the CPU threads the build may use (the stages' shares of the bar)
    public BuildProgress(bool download, bool shaders, bool update = false, int threads = BuildForecast.ReferenceThreads)
    {
        var included = Titles.Where(t => (download || t.Id != "download") && (shaders || t.Id != MakeGameProtocol.ShadersStage)).ToList();
        var sum = included.Sum(t => BuildForecast.Seconds(t.Id, threads));
        var from = 0.0;
        foreach (var (id, title) in included)
        {
            var size = BuildForecast.Seconds(id, threads) / sum;
            // (an update's read copies the files the folder kept: no measure of reading a dump on this PC)
            stages.Add(new BuildStage(id, update && id == "read" ? "Read the game files kept in the folder" : title, from, size)
                { Kept = update && id == "read" });
            from += size;
        }
    }

    public IReadOnlyList<BuildStage> Stages => stages;
    public double Overall { get; private set; }
    public string? Current => current?.Id;
    /// The compile's time left in seconds (@@eta), as build.py worked it out when it said it; null before it says
    public double? Eta { get; private set; }
    /// How many @@eta lines came: build.py says one only as a file ends (minutes apart for the biggest), so BuildClock counts
    /// down from the last one
    public int EtaLines { get; private set; }
    public int? FilesChanged { get; private set; }
    public int? FilesTotal { get; private set; }
    public int FilesCompiled { get; private set; }
    public bool Finished { get; private set; }
    public string? Error { get; private set; }
    public string? Folder { get; private set; }

    void Set(double f)
    {
        f = Math.Clamp(f, 0, 1);
        if (f > Overall) Overall = f;
    }

    void Begin(string id)
    {
        var stage = stages.FirstOrDefault(s => s.Id == id);
        if (stage == null) return;   // (a stage this build does not show: the bar stays where it is)
        var now = Now();
        if (current != null && current != stage && current.State == StageState.Running) End(current, now);
        // (stages before it that never said so are done too: an older make_game, or one with nothing to do)
        foreach (var s in stages.TakeWhile(s => s != stage))
            if (s.State is StageState.Waiting or StageState.Running) End(s, now);
        current = stage;
        stage.State = StageState.Running;
        // (make_game may run a stage again: its earlier runs are kept, and this one timed from its own start, so the stages between
        // are not counted in it and the stages' seconds add up to no more than the build took)
        if (stage.Began is { } began && stage.Ended is { } ended)
        {
            stage.Earlier += Math.Max(0, (ended - began).TotalSeconds);
            stage.Began = now;
        }
        stage.Began ??= now;
        stage.Ended = null;
        Set(stage.From);
    }

    /// The tools' download, 0..1
    public void Download(double fraction)
    {
        if (stages.FirstOrDefault(s => s.Id == "download") is not { } d) return;
        if (current != d) Begin("download");
        Set(d.From + d.Size * Math.Clamp(fraction, 0, 1));
        if (fraction >= 1) End(d, Now());   // (downloaded: none of it is left to count down)
    }

    // (the read's unpacking says no @@progress: nx2/nup.py says the title's size first, "title 0: ... romfs 186587 files, 26.24 GB",
    // then "  494.6 MiB written (4329 files completed)" every 2 s. The unpacking is about 95% of the read's time, the checks after it
    // the rest)
    static readonly Regex ReadSize = new(@"^title \d+: .* romfs \d+ files, ([\d.]+) GB", RegexOptions.CultureInvariant);
    static readonly Regex ReadWritten = new(@"^\s+([\d.]+) MiB written \(\d+ files completed\)", RegexOptions.CultureInvariant);
    const double ReadUnpacking = 0.95;
    double readBytes;

    public MakeGameLine Feed(string line)
    {
        var parsed = MakeGameProtocol.Parse(line);
        Apply(parsed);
        return parsed;
    }

    public void Apply(MakeGameLine line)
    {
        switch (line)
        {
            case MakeGameLine.Stage s:
                Begin(s.Id);
                break;
            case MakeGameLine.Progress p when current != null:
                Set(current.From + current.Size * p.Fraction);
                break;
            case MakeGameLine.Plan plan:
                FilesChanged = plan.Changed;
                FilesTotal = plan.Total;
                FilesCompiled = 0;
                // (some of the files compiled, the rest from the cache: no measure of a whole compile on this PC)
                if (stages.FirstOrDefault(s => s.Id == "compile") is { } compile) compile.Kept = plan.Changed < plan.Total;
                Note();
                break;
            case MakeGameLine.File f when f.Kind is "new" or "again":
                FilesCompiled++;
                Note();
                break;
            case MakeGameLine.Eta e:
                Eta = Math.Max(0, e.Seconds);
                EtaLines++;
                break;
            case MakeGameLine.Plain plain when current is { Id: "read" } && ReadSize.Match(plain.Text) is { Success: true } size:
                if (double.TryParse(size.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0)
                    readBytes += gb * 1e9;   // (each title's, for a .nup of more than one)
                break;
            case MakeGameLine.Plain plain when current is { Id: "read" } && readBytes > 0 && ReadWritten.Match(plain.Text) is { Success: true } w:
                if (double.TryParse(w.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib))
                    Set(current.From + current.Size * ReadUnpacking * Math.Clamp(mib * (1 << 20) / readBytes, 0, 1));
                break;
            case MakeGameLine.Plain plain when current != null:
                foreach (var (stage, kept) in KeptLines)
                    if (current.Id == stage && plain.Text.StartsWith(kept, StringComparison.Ordinal)) current.Kept = true;
                break;
            case MakeGameLine.NotPrepared n:
                if (stages.FirstOrDefault(s => s.Id == MakeGameProtocol.ShadersStage) is { } shaders)
                {
                    shaders.State = StageState.Skipped;
                    shaders.Note = "Left to the game's first start: " + n.Reason;
                }
                break;
            case MakeGameLine.Done d:
                Folder = d.Folder;
                Complete();
                break;
            case MakeGameLine.Error e:
                Error = e.Text;
                if (current != null && current.State == StageState.Running)
                {
                    current.State = StageState.Waiting;
                    current.Ended ??= Now();   // (where it stopped: its time stops with it)
                }
                break;
        }
    }

    /// A stage done, its time ended (a stage that never started has none)
    static void End(BuildStage s, DateTime now)
    {
        s.State = StageState.Done;
        if (s.Began != null) s.Ended ??= now;
    }

    /// How far a stage got, 0..1: a done one 1, one not started 0, else where the bar is within its share
    public double Fraction(BuildStage s) => s.State switch
    {
        StageState.Done => 1,
        _ when s.Began == null || s.Size <= 0 => 0,
        _ => Math.Clamp((Overall - s.From) / s.Size, 0, 1),
    };

    /// What the build reports say of each stage, by its id: how far it got (Fraction), how long it ran and whether it kept an
    /// earlier build's work (its time then says nothing of this PC's pace), each as of now
    public IReadOnlyDictionary<string, (double Progress, double Seconds, bool Kept)> Times()
    {
        var now = Now();
        return stages.ToDictionary(s => s.Id, s => (Math.Round(Fraction(s), 3), Math.Round(s.Seconds(now), 1), s.Kept));
    }

    void Note()
    {
        if (stages.FirstOrDefault(s => s.Id == "compile") is not { } c || FilesChanged is not int changed || FilesTotal is not int total) return;
        c.Note = changed == 0 ? "Everything is already built"
            : changed >= total ? $"{FilesCompiled} of {changed} files"
            : $"{FilesCompiled} of {changed} changed files";
    }

    /// make_game said @@done: every stage is done (the shaders' row skipped when they were not prepared) and the bar is full
    public void Complete()
    {
        foreach (var s in stages)
            if (s.State != StageState.Skipped)
            {
                // (a build that prepares shaders, whose game never said @@stage shaders: left to the first start, ShaderStep.Skipped)
                if (s.Id == MakeGameProtocol.ShadersStage && s.State == StageState.Waiting)
                {
                    s.State = StageState.Skipped;
                    s.Note ??= "Left to the game's first start";
                }
                else End(s, Now());
            }
        current = null;
        Finished = true;
        Overall = 1;
    }
}
