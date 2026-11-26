using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// An event of longshot-builder's as the progress takes it (BuildEvents.ToLine)
public abstract record MakeGameLine
{
    public sealed record Stage(string Id, string Text) : MakeGameLine;
    /// Of: the stage it is of; null: the current one
    public sealed record Progress(double Fraction, string? Of = null) : MakeGameLine;
    public sealed record Plan(int Changed, int Total) : MakeGameLine;
    public sealed record File(string Name, double Seconds, string Kind) : MakeGameLine;
    public sealed record Eta(double Seconds) : MakeGameLine;
    public sealed record Check(bool Ok, string Text) : MakeGameLine;
    /// Seconds, CompileSeconds, PreviousSeconds: the done event's times (the build's, its compile's, the last build's in this
    /// work folder)
    public sealed record Done(string Folder, double? Seconds = null, double? CompileSeconds = null, double? PreviousSeconds = null) : MakeGameLine;
    public sealed record Error(string Text, string? Code = null) : MakeGameLine;
    public sealed record Warning(string Code, string Text) : MakeGameLine;
    public sealed record NotPrepared(string Reason) : MakeGameLine;
    public sealed record Plain(string Text) : MakeGameLine;
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
    internal double Reported;
    /// When its latest run started and ended (BuildProgress.Now): null before it started, and while it runs for the end
    internal DateTime? Began, Ended;
    /// The seconds of its earlier runs, when the builder ran it again
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
/// download first when the version's builder is not installed, then the builder's stages. Only the stages this build has count,
/// scaled to fill the bar; the bar never goes back (the builder may run a stage again), and it is 1 once the builder says done.
public sealed class BuildProgress
{
    /// The shaders' stage: prepared after the game is put together, or left to its first start
    public const string ShadersStage = "shaders";

    static readonly (string Id, string Title)[] Titles =
    {
        ("download", "Download the build tools"),
        ("read", "Read your game files"),
        ("lift", "Translate the game to PC code"),
        ("compile", "Compile"),
        ("finish", "Put the game together"),
        (ShadersStage, "Prepare the shaders"),
    };
    readonly List<BuildStage> stages = new();
    BuildStage? current;

    /// The clock the stages' times are read from (a test's own)
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    /// download: the tools are downloaded first; shaders: this build prepares the shaders; update: an update of a game folder (its
    /// read step reads the files the folder keeps); threads: the CPU threads the build may use (the stages' shares of the bar)
    public BuildProgress(bool download, bool shaders, bool update = false, int threads = BuildForecast.ReferenceThreads)
    {
        var included = Titles.Where(t => (download || t.Id != "download") && (shaders || t.Id != ShadersStage)).ToList();
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
    /// The compile's time left in seconds (its eta event), as the builder worked it out when it said it; null before it says
    public double? Eta { get; private set; }
    /// How many eta events came: the builder says one only as a file ends (minutes apart for the biggest), so BuildClock counts
    /// down from the last one
    public int EtaLines { get; private set; }
    public int? FilesChanged { get; private set; }
    public int? FilesTotal { get; private set; }
    public int FilesCompiled { get; private set; }
    public bool Finished { get; private set; }
    public string? Error { get; private set; }
    /// The error's stable code (Problems.FromCode); null when the error said none
    public string? ErrorCode { get; private set; }
    /// The codes of the warnings the build said, each once, in the order said:
    /// the Ready page says those with the launcher's words (Problems.FromWarning)
    public IReadOnlyList<string> Warnings => warnings;
    readonly List<string> warnings = new();
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
        // (stages before it that never said so are done too: ones with nothing to do)
        foreach (var s in stages.TakeWhile(s => s != stage))
            if (s.State is StageState.Waiting or StageState.Running) End(s, now);
        if (current != stage)
        {
            stage.Reported = 0;
            if (id == "compile")
            {
                Eta = null;
                FilesChanged = FilesTotal = null;
                FilesCompiled = 0;
                stage.Note = null;
                stage.Kept = false;
            }
            else if (id == "lift") stage.Kept = false;
        }
        current = stage;
        stage.State = StageState.Running;
        // (the builder may run a stage again: its earlier runs are kept, and this one timed from its own start, so the stages between
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
        Report(d, fraction);
        if (fraction >= 1) End(d, Now());   // (downloaded: none of it is left to count down)
    }

    void Report(BuildStage stage, double fraction)
    {
        stage.Reported = Math.Max(stage.Reported, Math.Clamp(fraction, 0, 1));
        Set(stage.From + stage.Size * stage.Reported);
    }

    public void Apply(MakeGameLine line)
    {
        switch (line)
        {
            case MakeGameLine.Stage s:
                Begin(s.Id);
                break;
            // (a progress of another stage than the one running: none of the bar's)
            case MakeGameLine.Progress p when current != null && (p.Of == null || p.Of == current.Id):
                Report(current, p.Fraction);
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
            case MakeGameLine.Eta e when current is { Id: "compile" }:
                Eta = Math.Max(0, e.Seconds);
                EtaLines++;
                break;
            case MakeGameLine.Warning w:
                if (!warnings.Contains(w.Code)) warnings.Add(w.Code);
                break;
            case MakeGameLine.NotPrepared n:
                if (stages.FirstOrDefault(s => s.Id == ShadersStage) is { } shaders)
                {
                    shaders.Ended ??= Now();
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
                ErrorCode = e.Code;
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

    /// How far a stage got, 0..1, from its own progress events or completion.
    public double Fraction(BuildStage s) => s.State switch
    {
        StageState.Done => 1,
        _ => s.Reported,
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

    /// The builder said done: every stage is done (the shaders' row skipped when they were not prepared) and the bar is full
    public void Complete()
    {
        foreach (var s in stages)
            if (s.State != StageState.Skipped)
            {
                // (a build that prepares shaders, whose builder never said its shaders stage: left to the first start, ShaderStep.Skipped)
                if (s.Id == ShadersStage && s.State == StageState.Waiting)
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
