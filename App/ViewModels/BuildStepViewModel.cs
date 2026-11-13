using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A stage's row in step 3: its state, what it is doing, and its time
public sealed class StageRowViewModel : ViewModelBase
{
    string detail = "", time = "", timeFaint = "", state = "wait";

    public StageRowViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get => detail; set => Set(ref detail, value); }
    public string Time { get => time; set => Set(ref time, value); }
    public string TimeFaint { get => timeFaint; set => Set(ref timeFaint, value); }

    /// wait, run, done, skip or fail
    public string State
    {
        get => state;
        set
        {
            if (Set(ref state, value)) RaiseAll(nameof(IsWait), nameof(IsRun), nameof(IsDone), nameof(IsSkip), nameof(IsFail));
        }
    }

    public bool IsWait => state == "wait";
    public bool IsRun => state == "run";
    public bool IsDone => state == "done";
    public bool IsSkip => state == "skip";
    public bool IsFail => state == "fail";
}

/// Step 3: make_game.py run with the step's settings (MakeGameCommand), its @@ lines driving one overall bar and the stages'
/// rows (BuildProgress, BuildClock); the log under Show details; Cancel stops it and everything it started
public sealed class BuildStepViewModel : ViewModelBase, IWizardPage
{
    static readonly Dictionary<string, string> Names = new()
    {
        ["download"] = "Download tools", ["read"] = "Read the game files", ["lift"] = "Translate", ["compile"] = "Compile",
        ["finish"] = "Finish", [MakeGameProtocol.ShadersStage] = "Prepare shaders",
    };

    readonly BuildProgress progress;
    readonly BuildClock clock;
    readonly Stopwatch watch = new();
    readonly BuildLog log = new();
    readonly CancellationTokenSource cancel = new();
    readonly DispatcherTimer? ticker;
    string? lastError;
    TimeSpan? fixedTime;
    bool showLog, running;
    LogLevelChoice level = LogLevelChoice.Of(LogLevel.Info);
    string search = "";
    bool following = true;
    bool fsr;   // (AMD FSR 4 in this build: asked for, and these tools build it; MakeGameCommand.FsrBuilds)

    public BuildStepViewModel(BuildJob job, bool start = true)
    {
        Job = job;
        var s = job.Service.Settings;
        var threads = SettingsStepViewModel.BuildThreads(s);
        progress = new BuildProgress(download: true, shaders: s.PrepareShaders, threads: threads);
        clock = new BuildClock(threads);
        progress.Download(1);
        Rows = progress.Stages.Select(st => new StageRowViewModel(st.Id, Names.GetValueOrDefault(st.Id, st.Title))).ToList();
        Notches = progress.Stages.Skip(1).Select(st => st.Start).ToList();
        ToggleLogCommand = new Command(() => ShowLog = !ShowLog);
        CancelCommand = new Command(() => Job.Main.Modal = new StopBuildViewModel(this));
        log.Add($"Longshot Launcher {typeof(BuildStepViewModel).Assembly.GetName().Version?.ToString(3)}: {job.Verb.Trim().ToLowerInvariant()} {job.Version} ({job.Channel})");
        log.Add($"settings: memory {(s.MemoryGb is int m ? m + " GB" : "automatic")}, workers {(s.BuildWorkers is int w ? w : "automatic")}, " +
                $"{Languages.NameOf(s.Language)}{(s.Dlss ? ", DLSS" : "")}{(s.Fsr ? ", FSR 4" : "")}");
        if (start)
        {
            ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Update());
            Run();
        }
        Update();
    }

    public BuildJob Job { get; }
    public int Step => 3;
    public bool Finished => false;
    public IReadOnlyList<StageRowViewModel> Rows { get; }
    public IReadOnlyList<double> Notches { get; }
    public BuildProgress Progress => progress;
    public BuildLog Log => log;

    public string Title => Job.Kind switch
    {
        JobKind.Update => "Updating to " + Job.Shown,
        JobKind.Rebuild => "Rebuilding " + Job.Shown,
        _ => "Building " + Job.Shown,
    };

    public double Overall => progress.Overall;
    public string Percent => (int)Math.Floor(progress.Overall * 100) + "%";
    public string Left => clock.SecondsLeft(progress) is double secs ? Durations.About(secs) + " left" : "Working out the time left";

    public bool ShowLog
    {
        get => showLog;
        set
        {
            if (Set(ref showLog, value)) RaiseAll(nameof(ShowStages), nameof(LogButton), nameof(LogText));
        }
    }

    public bool ShowStages => !showLog;
    public string LogButton => showLog ? "Hide details" : "Show details";
    public string LogText
    {
        get
        {
            return string.Join("\n", log.Tail(level.Level, search, 2000));
        }
    }

    /// The details' level picker (Info by default: info, warnings and errors; Debug and Trace add the game's detail lines) and
    /// search box (only the lines holding its text, any case)
    public IReadOnlyList<LogLevelChoice> Levels => LogLevelChoice.All;
    public LogLevelChoice Level
    {
        get => level;
        set { if (Set(ref level, value ?? LogLevelChoice.Of(LogLevel.Info))) Raise(nameof(LogText)); }
    }
    public string Search
    {
        get => search;
        set { if (Set(ref search, value ?? "")) Raise(nameof(LogText)); }
    }

    /// The details are at their bottom, following new lines (the view says so as it scrolls). Scrolled up to read, the text
    /// stays as it is: past the 2000 lines shown, each new line would push the lines being read up a line
    public bool Following
    {
        get => following;
        set { if (Set(ref following, value) && value && showLog) Raise(nameof(LogText)); }
    }

    public Command ToggleLogCommand { get; }
    public Command CancelCommand { get; }

    void Run()
    {
        var job = Job;
        var s = job.Service.Settings;
        var file = job.Picked!;
        var request = new BuildRequest(file.Source, job.InstallDir, s.Language, job.Service.Work, s.BuildWorkers, s.MemoryGb, s.PrepareShaders,
                                       s.Dlss, s.DlssLicense, s.Fsr, job.Main.Linux ? s.FsrLinuxNotice : s.FsrLicense);
        MakeGameCommand command;
        try
        {
            command = MakeGameCommand.For(job.Tools!, request, job.Service.Platform, job.Service.Home, LauncherService.LauncherPath);
            fsr = command.Arguments.Contains("--fsr");
            if (request.Fsr && request.FsrLicense && !fsr)   // (Linux only: MakeGameCommand.FsrBuilds is always true on Windows)
                log.Add("AMD FSR 4: this version's tools carry no AMD FSR API headers, so it is built without FSR 4.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.InstallDir))!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Fail(e.Message);
            return;
        }
        running = true;
        watch.Start();
        ticker?.Start();
        Task.Run(async () =>
        {
            int code;
            try
            {
                code = await MakeGameRun.RunAsync(command.StartInfo(), line => Ui.Post(() => Line(line)),
                                                  line => Ui.Post(() => ErrorLine(line)), cancel.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Ui.Post(() => Fail("The build tools could not start: " + e.Message));
                return;
            }
            Ui.Post(() => Exited(code));
        });
    }

    public void Line(string line)
    {
        var parsed = progress.Feed(line);
        {
            switch (parsed)
            {
                case MakeGameLine.Plain p:
                    log.Add(p.Text);
                    break;
                case MakeGameLine.Stage st:
                    log.Add($"[{Names.GetValueOrDefault(st.Id, st.Id)}] {st.Text}");
                    break;
                case MakeGameLine.Error e:
                    log.Add("error: " + e.Text);
                    break;
                case MakeGameLine.NotPrepared n:
                    log.Add(line);
                    break;
            }
        }
        Update();
    }

    void ErrorLine(string line)
    {
        // (the message a stopped build quotes: a line the details show by default, not a game's debug or trace line)
        if (!string.IsNullOrWhiteSpace(line) && LogLevels.Of(line) <= LogLevel.Info) lastError = line.Trim();
        log.Add(line);
        if (showLog && following) Raise(nameof(LogText));
    }

    void Exited(int code)
    {
        running = false;
        ticker?.Stop();
        Update();
        if (progress.Finished && code == 0) Complete();
        else Fail(progress.Error ?? lastError ?? $"make_game.py stopped with exit code {code} without saying why.");
    }

    /// The rows, the bar and the time left from the progress and the clock
    public void Update()
    {
        clock.Observe(progress, fixedTime ?? watch.Elapsed);
        foreach (var (st, row) in progress.Stages.Zip(Rows))
        {
            if (st.Id == "download")
            {
                row.State = "done";
                row.Detail = Job.ToolsDownloaded ? "Downloaded in step 1" : "Already on this PC";
                row.Time = Job.ToolsDownloaded ? Durations.Clock(Job.ToolsTime) : "";
                row.TimeFaint = "";
                continue;
            }
            var took = clock.Of(st.Id);
            switch (st.State)
            {
                case StageState.Done:
                    row.State = "done";
                    row.Detail = st.Note ?? "Done";
                    row.Time = took is { } t ? Durations.Clock(t) : "";
                    row.TimeFaint = "";
                    break;
                case StageState.Running:
                    row.State = "run";
                    var frac = st.Weight > 0 ? Math.Clamp((progress.Overall - st.Start) / st.Weight, 0, 1) : 0;
                    row.Detail = st.Note ?? (st.Id == "finish" ? "Writing the game folder" : $"{(int)(frac * 100)}%");
                    row.Time = took is { } r ? Durations.Clock(r) : "";
                    row.TimeFaint = clock.Estimate(progress, st) is double est ? " / " + Durations.Roughly(est) : "";
                    break;
                case StageState.Skipped:
                    row.State = "skip";
                    row.Detail = st.Note ?? "At the game's first start";
                    row.Time = "";
                    row.TimeFaint = "Skipped";
                    break;
                default:
                    row.State = progress.Error != null && progress.Current == st.Id ? "fail" : "wait";
                    row.Detail = "";
                    row.Time = "";
                    row.TimeFaint = clock.Estimate(progress, st) is double e ? Durations.Roughly(e) : "";
                    break;
            }
        }
        RaiseAll(nameof(Overall), nameof(Percent), nameof(Left));
        if (showLog && following) Raise(nameof(LogText));
    }

    /// Cancel: the build and everything it started stopped, back home
    public void Stop()
    {
        running = false;
        ticker?.Stop();
        cancel.Cancel();
        Job.Main.ShowHome();
        Job.Main.ShowToast("Build stopped.");
    }

    void Fail(string why)
    {
        running = false;
        ticker?.Stop();
        var at = progress.Stages.FirstOrDefault(s => s.Id == progress.Current) ?? progress.Stages.LastOrDefault(s => s.State == StageState.Done);
        var name = at != null ? Names.GetValueOrDefault(at.Id, at.Title) : "the build";
        var pct = (int)Math.Floor(progress.Overall * 100);
        Job.Main.Navigate(new ProblemViewModel(this, why, name, pct));
    }

    void Complete()
    {
        var job = Job;
        var service = job.Service;
        var registry = service.Registry;
        var file = job.Picked!;
        var s = service.Settings;
        var wasEmpty = registry.Builds.Count == 0;
        BuildEntry entry = job.Kind == JobKind.Update && job.Replaces != null && registry.Find(job.Replaces.Id) != null
            ? registry.Replace(job.Replaces.Id, job.InstallDir, job.Version, job.Channel)
            : registry.Add(job.InstallDir, job.Version, job.Channel, name: job.Version, makeDefault: wasEmpty);
        if (file.Source is BuildSource.Folders f)
        {
            entry.Nup = null;
            entry.Romfs = f.Romfs;
            entry.Exefs = f.Exefs;
        }
        else
        {
            entry.Nup = file.Path;
            entry.Romfs = entry.Exefs = null;
        }
        entry.Dlss = s.Dlss && s.DlssLicense;
        entry.Fsr = fsr;
        entry.Bytes = null;
        string? problem = null;
        try { registry.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = "The list of builds could not be saved: " + e.Message; }
        service.PruneTools();

        // (no saves to move: every build shares them, SaveEnvironment.LongshotUser)
        job.Main.Navigate(new ReadyViewModel(job.Main, entry, job.Kind, job.From, clock.Elapsed + job.ToolsTime, problem));
    }

    /// For the screenshots: the lines a build would have said so far, with the clock at elapsed
    public void Replay(IEnumerable<(string Line, TimeSpan At)> lines)
    {
        foreach (var (l, at) in lines)
        {
            fixedTime = at;
            Line(l);
        }
    }

    public void ReplayExit(int code) => Exited(code);

    public bool Running => running;

    /// A bug report: the version, this PC, the settings and the log's last 200 lines
    public string Report(string why)
    {
        var s = Job.Service.Settings;
        var sb = new StringBuilder();
        sb.AppendLine($"Longshot Launcher {typeof(BuildStepViewModel).Assembly.GetName().Version?.ToString(3)}: {Job.Verb.Trim()} {Job.Version} ({Job.Channel})");
        sb.AppendLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {SystemInfo.MemoryGb()} GB, {SystemInfo.Threads()} threads" +
                      (Job.Service.Gpus.Count > 0 ? ", " + string.Join(" and ", Job.Service.Gpus) + " GPU" : ""));
        sb.AppendLine($"settings: memory {(s.MemoryGb is int m ? m + " GB" : "automatic")}, workers {(s.BuildWorkers is int w ? w : "automatic")}, " +
                      $"shaders {(s.PrepareShaders ? "now" : "at first start")}, DLSS {(s.Dlss ? "on" : "off")}, FSR 4 {(fsr ? "on" : s.Fsr ? "asked, not built" : "off")}");
        sb.AppendLine("stopped: " + why);
        sb.AppendLine();
        foreach (var l in log.Last(200)) sb.AppendLine(l);
        return sb.ToString();
    }
}

/// "Stop the build?"
public sealed class StopBuildViewModel(BuildStepViewModel build) : ViewModelBase
{
    public string Sub => $"The tools and the files read so far are kept, so building {build.Job.Shown} again starts faster.";
    public Command KeepCommand { get; } = new(() => build.Job.Main.Modal = null);
    public Command StopCommand { get; } = new(() => { build.Job.Main.Modal = null; build.Stop(); });
}

/// The build finished: Play, Open folder, the shortcuts, whether Play starts this version
public sealed class ReadyViewModel : ViewModelBase, IWizardPage
{
    bool desktop = true, menu = true;

    public ReadyViewModel(MainViewModel main, BuildEntry entry, JobKind kind, string? from, TimeSpan took, string? problem)
    {
        Main = main;
        Entry = entry;
        Kind = kind;
        From = from;
        Took = took;
        Problem = problem;
        PlayCommand = new Command(() =>
        {
            try
            {
                var said = Main.Service.Play(Entry);
                Main.ShowHome();
                if (said != null) Main.Home.Notice = said;
                Main.ShowToast($"Starting Longshot, {Version}…");
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Main.ShowToast($"{Version} could not start: {e.Message}");
            }
        });
        FolderCommand = new Command(() => Main.Host?.Open(Entry.Folder));
        HomeCommand = new Command(Main.ShowHome);
        WriteShortcuts();
    }

    public MainViewModel Main { get; }
    public BuildEntry Entry { get; }
    public JobKind Kind { get; }
    public string? From { get; }
    public TimeSpan Took { get; }
    public string? Problem { get; }
    public int Step => 3;
    public bool Finished => true;

    public string Version => Entry.Version ?? Entry.Name;
    public string Heading => Version + " is ready";
    public string Sub => (Kind == JobKind.Update && From != null ? "Updated from " + From : "Built") +
                         $" in {Math.Max(1, (int)Math.Round(Took.TotalMinutes))} min, in {ShownPath.Of(Entry.Folder)}.";
    public bool HasProblem => Problem != null;
    public string MenuLabel => Main.Linux ? "Applications menu entry" : "Start menu entry";
    public bool ShowDefaultTick => Main.Service.Registry.Builds.Count > 1;

    public bool Desktop
    {
        get => desktop;
        set
        {
            if (Set(ref desktop, value)) WriteShortcuts();
        }
    }

    public bool Menu
    {
        get => menu;
        set
        {
            if (Set(ref menu, value)) WriteShortcuts();
        }
    }

    public bool IsDefault
    {
        get => Entry.Default;
        set
        {
            if (value)
            {
                Main.Service.SetDefault(Entry);
                Main.ShowToast($"Play now starts {Version}.");
            }
            else if (Main.Service.Registry.Builds.FirstOrDefault(b => b.Id != Entry.Id) is { } other)
            {
                Main.Service.SetDefault(other);
                Main.ShowToast($"Play now starts {other.Version ?? other.Name}.");
            }
            Raise();
        }
    }

    /// The shortcuts start the launcher's Play of the default build, so they follow "Play starts this version" and updates
    void WriteShortcuts()
    {
        if (Main.Host == null) return;
        try
        {
            if (Main.Linux) Shortcuts.WriteLinux(LauncherService.LauncherPath, null, desktop, menu);
            else WindowsShortcuts.Write(LauncherService.LauncherPath, desktop, menu);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Main.ShowToast("The shortcuts could not be made: " + e.Message);
        }
    }

    public Command PlayCommand { get; }
    public Command FolderCommand { get; }
    public Command HomeCommand { get; }
}

/// The build stopped: what happened, what to do, Copy for a bug report, Build again
public sealed class ProblemViewModel : ViewModelBase, IWizardPage
{
    readonly BuildStepViewModel build;
    bool showLog;

    public ProblemViewModel(BuildStepViewModel build, string why, string stage, int percent)
    {
        this.build = build;
        Why = why;
        Stage = stage;
        Percent = percent;
        ToggleLogCommand = new Command(() => ShowLog = !ShowLog);
        CopyCommand = new Command(() => _ = Copy());
        BackCommand = new Command(() => build.Job.Main.Navigate(build.Job.Settings ?? new SettingsStepViewModel(build.Job)));
        RetryCommand = new Command(() => build.Job.Main.Navigate(new BuildStepViewModel(build.Job)));
    }

    public int Step => 3;
    public bool Finished => false;
    public string Why { get; }
    public string Stage { get; }
    public int Percent { get; }
    public bool Memory => Why.Contains("memory", StringComparison.OrdinalIgnoreCase);
    public string Head => Memory ? "The PC ran out of memory while building" : "make_game.py stopped";
    public string Body => $"Building {build.Job.Shown} stopped in {Stage}, {Percent}% of the way through. It said: {Why}";
    public bool ShowLog { get => showLog; set { if (Set(ref showLog, value)) RaiseAll(nameof(LogButton), nameof(LogTail)); } }
    public string LogButton => showLog ? "Hide details" : "Show details";
    public string LogTail => string.Join("\n", build.Log.Tail(LogLevel.Info, null, 6));

    public Command ToggleLogCommand { get; }
    public Command CopyCommand { get; }
    public Command BackCommand { get; }
    public Command RetryCommand { get; }

    async Task Copy()
    {
        if (build.Job.Main.Host is { } host) await host.CopyAsync(build.Report(Why));
        build.Job.Main.ShowToast("Copied: the version, this PC's memory and GPU, the settings and the log's last 200 lines.");
    }
}

/// One of the build log's levels as its picker shows it
public sealed record LogLevelChoice(LogLevel Level, string Name)
{
    public static IReadOnlyList<LogLevelChoice> All { get; } = LogLevels.All.Select(l => new LogLevelChoice(l, LogLevels.Name(l))).ToList();
    public static LogLevelChoice Of(LogLevel level) => All.First(c => c.Level == level);
    public override string ToString() => Name;
}
