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

    readonly string name;

    public StageRowViewModel(string id, string name)
    {
        Id = id;
        this.name = name;
    }

    public string Id { get; }
    public string Name => Tr.T(name);
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
        ["download"] = Tr.N("Download tools"), ["read"] = Tr.N("Read the game files"), ["lift"] = Tr.N("Translate"), ["compile"] = Tr.N("Compile"),
        ["finish"] = Tr.N("Finish"), [MakeGameProtocol.ShadersStage] = Tr.N("Prepare shaders"),
    };

    readonly BuildProgress progress;
    readonly BuildClock clock;
    readonly Stopwatch watch = new();
    readonly BuildLog log = new();
    readonly CancellationTokenSource cancel = new();
    readonly DispatcherTimer? ticker;
    string? lastError;
    string? seeding;   // (what the first row after the download says while an update's shader caches are taken: Seed)
    TimeSpan? fixedTime;
    bool showLog, running;
    LogLevelChoice level = LogLevelChoice.Of(LogLevel.Info);
    string search = "";
    bool following = true;
    bool fsr;   // (AMD FSR 4 in this build: asked for, and these tools build it; MakeGameCommand.FsrBuilds)
    bool dlss;  // (NVIDIA DLSS in this build: asked for, and licence accepted)
    bool xess;  // (Intel XeSS's library fetched by this build: asked for, licence accepted, and these tools fetch it; Windows)

    static string Upscaler(bool on, string name, Func<Gpu, bool> runs, IReadOnlyList<Gpu> cards, bool auto, bool pending) =>
        !on ? "" : ", " + name + (pending ? " (Auto GPU check pending)" : Upscalers.Unsupported(cards, runs)
            ? auto ? " (estimated Auto GPU does not run it)" : " (no card here runs it)" : "");

    public BuildStepViewModel(BuildJob job, bool start = true)
    {
        Job = job;
        var s = job.Service.Settings;
        var threads = SettingsStepViewModel.BuildThreads(s);
        progress = new BuildProgress(download: true, shaders: true, update: job.Picked?.Source is BuildSource.Update, threads: threads);
        clock = new BuildClock(threads);
        progress.Download(1);
        Rows = progress.Stages.Select(st => new StageRowViewModel(st.Id, Names.GetValueOrDefault(st.Id, st.Title))).ToList();
        ToggleLogCommand = new Command(() => ShowLog = !ShowLog);
        CancelCommand = new Command(() => { if (!Finalizing) Job.Main.Modal = new StopBuildViewModel(this); }, () => !Finalizing);
        log.Add($"Longshot Launcher {job.Service.OwnTag}: {job.Verb.Trim().ToLowerInvariant()} {job.Version} ({job.Channel})");
        var probe = job.VulkanGpus;
        var pending = !probe.IsCompleted;
        var snapshot = probe.IsCompletedSuccessfully ? probe.Result : null;
        var cards = SystemInfo.GameCards(job.Service.GpuCards, snapshot);
        var auto = SystemInfo.EstimatedGpu(snapshot) != null;
        log.Add($"settings: memory {(s.MemoryGb is int m ? m + " GB" : "automatic")}, workers {Workers(s)}, " +
                $"{Languages.NameOf(s.Language)}{Upscaler(s.Dlss, "DLSS", SystemInfo.DlssRuns, cards, auto, pending)}{Upscaler(s.Fsr, "FSR 4", SystemInfo.FsrRuns, cards, auto, pending)}" +
                $"{(s.Xess && !job.Main.Linux ? ", XeSS" : "")}");
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
    public BuildProgress Progress => progress;
    public BuildLog Log => log;

    public string Title => Job.Kind switch
    {
        JobKind.Update => Tr.F(Job.Older ? "Going back to Longshot {0}" : "Updating to Longshot {0}", Job.Shown),
        JobKind.Rebuild => Tr.F("Rebuilding Longshot {0}", Job.Shown),
        _ => Tr.F("Building Longshot {0}", Job.Shown),
    };

    public double Overall => clock.Overall;
    public string Percent => (int)Math.Floor(Overall * 100) + "%";
    public bool ShowPercent => progress.Current != MakeGameProtocol.ShadersStage || clock.SecondsLeft(progress) != null;
    public string Left => progress.Current == MakeGameProtocol.ShadersStage && progress.ShaderTiming is { Saving: true }
        ? Tr.T("Saving the pipeline cache")
        : clock.SecondsLeft(progress) is double secs ? Tr.F("{0} left", Durations.About(secs))
        : clock.SecondsBeforeShaders(progress) is double before ? Tr.F("{0} left before shaders", Durations.About(before))
        : Tr.T("Working out the time left");

    public bool ShowLog
    {
        get => showLog;
        set
        {
            if (Set(ref showLog, value)) RaiseAll(nameof(ShowStages), nameof(LogButton), nameof(LogText));
        }
    }

    public bool ShowStages => !showLog;
    public string LogButton => showLog ? Tr.T("Hide details") : Tr.T("Show details");
    public string LogText => string.Join("\n", log.Tail(level.Level, search, 2000));

    /// The details' lines with their levels: the view writes each with its level's coloured prefix (BuildStepView, LogColors)
    public IReadOnlyList<(LogLevel Level, string Text)> LogLines => log.TailWithLevels(level.Level, search, 2000);

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

    string? buildWork;
    MakeGameCommand? started;   // (the build's make_game.py, for what its failure says of its folder: MakeGameRun.TooDeep)

    void Run()
    {
        var job = Job;
        var s = job.Service.Settings;
        var file = job.Picked!;
        // (an update's or a go-back's folder renamed to its new version's first, so make_game.py builds in the folder the row will have)
        if (!RenameFolder()) return;
        // (a version you have already built from this .nup gives the new one its game files: BuildRegistry.SameGameFiles. Not
        // for a cold build: they are read from the .nup again)
        var romfsFrom = !job.Cold && file.Source is BuildSource.Nup nup ? job.Service.Registry.SameGameFiles(nup.Path, job.InstallDir)?.Folder : null;
        // (DLSS, FSR 4 and XeSS as the player chose them, whatever this PC's cards: Upscalers.Chosen)
        var request = Upscalers.Chosen(
            new BuildRequest(file.Source, job.InstallDir, s.Language, job.Service.Work, SystemInfo.Workers(s.BuildWorkers), s.MemoryGb, true,
                             RomfsFrom: romfsFrom, LowPriority: SystemInfo.LowPriority(s.BuildWorkers), IntelCompiles: job.Service.IntelCompilesApply ? s.IntelCompiles : GamePlay.IntelCompilesAuto,
                             IntelLongerLimits: s.IntelLongerLimits && job.Service.IntelCompilesApply),
            s, job.Service.Platform);
        buildWork = job.Service.Work;
        dlss = request.Dlss && request.DlssLicense;
        MakeGameCommand command;
        try
        {
            command = MakeGameCommand.For(job.Tools!, request, job.Service.Platform, job.Service.Home, LauncherService.LauncherPath);
            fsr = command.Arguments.Contains("--fsr");
            if (request.Fsr && request.FsrLicense && !fsr)   // (Linux only: MakeGameCommand.FsrBuilds is always true on Windows)
                log.Add("AMD FSR 4: this version's tools carry no AMD FSR API headers, so it is built without FSR 4.");
            xess = command.Arguments.Contains("--fetch-xess-runtime");
            if (request.Xess && request.XessLicense && !xess && !job.Main.Linux)   // (MakeGameCommand.XessBuilds)
                log.Add("Intel XeSS: this version's tools can't fetch Intel's library, so the game folder gets none from this build.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.InstallDir))!);
            // (Longshot#1010: tools under a folder Windows lets no program list stop at their first compile, unexplained)
            if (MakeGameRun.Blocked(command, job.Service.Platform == Platform.Windows) is { } blocked)
            {
                PutBack();
                Fail(blocked);
                return;
            }
            started = command;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PutBack();
            Fail(e.Message);
            return;
        }
        SetRunning(true);
        Current = this;
        watch.Start();
        ticker?.Start();
        Task.Run(async () =>
        {
            if (job.Cold && !ClearForCold())
            {
                PutBack();
                Ui.Post(StoppedIfRequested);
                return;
            }
            Seeded? seeded;
            try { seeded = Seed(); }
            catch (OperationCanceledException)   // (stopped before the build started: Keep left nothing taken)
            {
                PutBack();
                Ui.Post(StoppedIfRequested);
                return;
            }
            // (Windows: room in NVIDIA's shader cache before the build prepares the shaders into it, LauncherService.RoomInNvidiaCache)
            if (OperatingSystem.IsWindows() && job.Service.RoomInNvidiaCache() is { } room) Ui.Post(() => log.Add(room));
            int code;
            try
            {
                cancel.Token.ThrowIfCancellationRequested();   // (stopped as the caches were taken: no build to start)
                code = await MakeGameRun.RunAsync(command.StartInfo(), line => Ui.Post(() => Line(line)),
                                                  line => Ui.Post(() => ErrorLine(line)), cancel.Token, command.LowPriority);
            }
            catch (OperationCanceledException)
            {
                // (already on a pool thread: Undo may wait a moment for a file a killed compiler or game still holds)
                try { seeded?.Undo(); PutBack(); }
                finally { Ui.Post(StoppedIfRequested); }
                return;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                var back = seeded?.Undo();
                PutBack();
                Ui.Post(() =>
                {
                    if (cancel.IsCancellationRequested) { StoppedIfRequested(); return; }
                    if (back is { } n) log.Add(Seeded.Said(n));
                    Fail(Tr.F("The build tools could not start: {0}", e.Message),
                         reported: English("The build tools could not start: {0}", e.Message));
                });
                return;
            }
            Ui.Post(() => Exited(code, seeded));
        });
    }

    /// The shader caches an update took from the build it replaces, before its build (Seed): put back when the build does
    /// not finish (ShaderCaches.Undo), so the build still listed keeps them
    sealed record Seeded(string From, string To, CachesResult Kept)
    {
        public int Undo() => ShaderCaches.Undo(From, To, Kept);
        public static string Said(int files) => $"shader caches: {files} put back, the build did not finish";
    }

    /// An update starts with the shader caches of the build it replaces (ShaderCaches.Keep: all of them when both versions translate
    /// shaders the same way with the same compiler, else the pipeline side): moved, or copied from another drive, into the new folder's cache/
    /// before make_game.py starts. make_game.py keeps a cache/ it finds in its folder, as a rebuild keeps its own, and its
    /// prepare (Prepare shaders, which every build runs: no switch leaves it to the game's first start) then finds them
    /// and has little left to do; after the build it would find caches of its own there already. Run on a pool thread;
    /// null: none taken (said in the log). Cancel stops a copy (OperationCanceledException), and the build is not started
    Seeded? Seed()
    {
        var job = Job;
        if (job.Kind != JobKind.Update || job.Replaces is not { } old || job.Tools is not { } tools) return null;
        if (job.Cold)
        {
            Ui.Post(() => log.Add("shader caches: none taken, a completely cold build"));
            return null;
        }
        // (an update in its build's own folder, BuildRegistry.FolderOf, has them where they are already)
        if (BuildRegistry.SameFolder(old.Folder, job.InstallDir))
        {
            Ui.Post(() => log.Add("shader caches: kept where they are, in the build's own folder"));
            return null;
        }
        var fromTools = old.Version is { } was ? job.Service.Tools.Find(was) : null;
        Ui.Post(() =>
        {
            seeding = Tr.T("Keeping the prepared shaders");
            Update();
        });
        string said;
        CachesResult? kept = null;
        try
        {
            var last = -1;
            kept = ShaderCaches.Keep(old.Folder, job.InstallDir, fromTools, tools, cancel: cancel.Token, progress: (done, all) =>
            {
                var pct = all > 0 ? (int)(done * 100 / all) : 100;
                if (pct == Interlocked.Exchange(ref last, pct)) return;
                Ui.Post(() =>
                {
                    if (seeding == null) return;
                    seeding = Tr.F("Keeping the prepared shaders: {0}%", pct);
                    Update();
                });
            });
            said = kept.What switch
            {
                CachesKept.Kept when kept.PipelinesOnly =>
                    $"shader caches: the pipelines and notes of {old.Version} {(kept.Moved ? "moved" : "copied")} ({kept.Files} files, " +
                    $"{kept.Bytes >> 20} MB); its programs are not kept (another translator, none translated, or its tools are gone) " +
                    "and are translated again",
                CachesKept.Kept => $"shader caches: {(kept.Moved ? "moved" : "copied")} from {old.Version} ({kept.Files} files, {kept.Bytes >> 20} MB)",
                CachesKept.NoCaches => $"shader caches: {old.Version} has none to keep",
                CachesKept.HasCaches => "shader caches: this build's folder has its own already",
                CachesKept.OtherTranslator => $"shader caches: not kept, {old.Version} translates or compiles shaders differently (or its tools are gone)",
                CachesKept.Running => "shader caches: not kept, Longshot is running",
                _ => $"shader caches: not kept, no room for a copy ({kept.Bytes >> 20} MB) and the build",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            said = "shader caches: not kept: " + e.Message;
        }
        finally
        {
            Ui.Post(() =>
            {
                seeding = null;
                Update();
            });
        }
        Ui.Post(() => log.Add(said));
        return kept is { What: CachesKept.Kept } taken ? new Seeded(old.Folder, job.InstallDir, taken) : null;
    }

    /// A completely cold build's start (BuildJob.Cold, ColdBuild.Clear): what earlier builds kept deleted before make_game.py
    /// starts, said in the log. Run on a pool thread; false: the build fails, nothing started (Longshot runs, or a file could not
    /// be deleted)
    bool ClearForCold()
    {
        var job = Job;
        Ui.Post(() =>
        {
            seeding = Tr.T("Deleting what earlier builds kept");
            Update();
        });
        string? failed = null;
        try
        {
            if (ColdBuild.Clear(job.Service.Work, job.InstallDir) is { } cleared)
                Ui.Post(() => log.Add($"cold build: deleted {cleared.Work >> 20} MB of build files and stores, {cleared.Caches >> 20} MB of shader caches"));
            else failed = Tr.T("Save and close Longshot first: a completely cold build clears the shader caches.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed = Tr.F("A completely cold build could not delete what earlier builds kept: {0}", e.Message);
        }
        Ui.Post(() =>
        {
            seeding = null;
            Update();
            if (failed != null && !cancel.IsCancellationRequested) Fail(failed);
        });
        return failed == null;
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

    void Exited(int code, Seeded? seeded = null)
    {
        // Stop can win while the process has exited but its callback is still queued.
        if (cancel.IsCancellationRequested)
        {
            _ = Task.Run(() => { try { seeded?.Undo(); PutBack(); } finally { Ui.Post(StoppedIfRequested); } });
            return;
        }
        ticker?.Stop();
        Update();
        if (progress.Finished && code == 0)
        {
            Complete();
            return;
        }
        // (.NET gives a child a signal ended 128 + the signal: 143 is SIGTERM, from outside the launcher, whose own Stop is a
        // cancel. A step the system killed is make_game's own error, "the system stopped it", and a failure)
        var terminated = code == 128 + 15;
        // (the page says why in the player's language; the summary in English, so one failure is one signature in every language)
        var told = progress.Error ?? lastError;
        // (#205: Python could not load its own parts, a folder too deep for Windows: said as such, with the path measured)
        if (told != null && started != null && MakeGameRun.TooDeep(started, told, Job.Service.Platform == Platform.Windows) is { } deep) told = deep;
        var why = told ?? (terminated
            ? Tr.T("make_game.py was stopped by a signal (SIGTERM) from outside the launcher.")
            : Tr.F("make_game.py stopped with exit code {0} without saying why.", code));
        var reported = told ?? (terminated
            ? Tr.N("make_game.py was stopped by a signal (SIGTERM) from outside the launcher.")
            : English("make_game.py stopped with exit code {0} without saying why.", code));
        var outcome = terminated ? BuildReports.Terminated : BuildReports.Failed;
        if (seeded == null && renamed == null)
        {
            Fail(why, outcome, reported);
            return;
        }
        // (put back on a pool thread: Undo may wait up to ShaderCaches.UndoWait for a file something the build started still
        // holds, too long for the window's thread, and the folder's rename too. The problem page shows once they are back, so
        // that building again finds the old build's caches in their place, in its folder of its version's name)
        Finalizing = true;
        CancelCommand.Changed();
        Task.Run(() =>
        {
            var n = seeded?.Undo();
            PutBack();
            return n;
        }).ContinueWith(t => Ui.Post(() =>
        {
            Finalizing = false;
            if (CloseAfterFinalizing != null) { SetRunning(false); CloseAfterFinalizing(); return; }
            if (t.IsCompletedSuccessfully && t.Result is { } n) log.Add(Seeded.Said(n));
            Fail(why, outcome, reported);
        }), TaskScheduler.Default);
    }

    /// The rename this build made when it started (RenameFolder), undone when it does not finish (PutBack)
    InstanceFolders.Move? renamed;

    /// An update's or a go-back's folder renamed to its new version's name (BuildJob.RenameFrom: builds/nightly/nightly-171-… to
    /// builds/nightly/nightly-175-…), on the window's thread before anything else of the build: the same folder, so its saves,
    /// settings, shader caches and mods go with it, and the list saved with it. False: refused, nothing changed and nothing
    /// started (Longshot runs, or a file in the folder is held open: a File Explorer window, an editor), and the page says why
    bool RenameFolder()
    {
        var job = Job;
        if (job.RenameFrom is not { } from || job.Replaces is not { } old) return true;
        var move = new InstanceFolders.Move(old, from, job.InstallDir);
        try
        {
            InstanceFolders.Rename(job.Service.Registry, move, job.Service.Platform, job.Service.SaveEnv);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(Tr.F("Its folder could not be renamed to {0}, so the build did not start: {1} Close what has a file open in {2} (a File Explorer window, a text editor), then build again.",
                      job.InstallDir, e.Message, from),
                 reported: English("Its folder could not be renamed to {0}, so the build did not start: {1} Close what has a file open in {2} (a File Explorer window, a text editor), then build again.",
                                   job.InstallDir, e.Message, from));
            return false;
        }
        old.Folder = job.InstallDir;   // (the row the job holds, if the list was read again since)
        renamed = move;
        log.Add($"instance folder: {from} renamed to {job.InstallDir}");
        // (game files picked in the folder itself, its romfs and .longshot_exefs: read from where they are now, or make_game.py
        // finds nothing at the old path. InstanceFolders.Follow)
        if (job.Picked is { } picked && picked.Follow(from, job.InstallDir) is var moved && moved != picked)
        {
            job.Picked = moved;
            log.Add($"game files: in the renamed folder, read from {(moved.Source is BuildSource.Folders f ? f.Romfs : moved.Path)}");
        }
        job.Service.FolderMoved(from, job.InstallDir);
        return true;
    }

    /// The rename undone, on a pool thread (a stopped build's files may be held a moment: InstanceFolders.MoveBack waits for
    /// them): the folder back to its old name, as the row's version still is, said in the log
    void PutBack()
    {
        if (renamed is not { } m) return;
        renamed = null;
        var back = InstanceFolders.MoveBack(Job.Service.Registry, m, TimeSpan.FromSeconds(10));
        if (back) m.Build.Folder = m.From;
        Ui.Post(() =>
        {
            log.Add(back ? $"instance folder: back to {m.From}" : $"instance folder: left at {m.To}, it could not go back to {m.From}");
            if (!back) return;
            Job.Picked = Job.Picked?.Follow(m.To, m.From);   // (a retry reads them at their old place again)
            Job.Service.FolderMoved(m.To, m.From);
        });
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
                row.Detail = !Job.ToolsDownloaded ? Tr.T("Already on this PC")
                    : Job.ToolsEarly ? Tr.T("Downloaded in the background") : Tr.T("Downloaded in step 1");
                row.Time = Job.ToolsDownloaded ? Durations.Clock(Job.ToolsTime) : "";
                row.TimeFaint = "";
                continue;
            }
            var took = clock.Of(st.Id);
            switch (st.State)
            {
                case StageState.Done:
                    row.State = "done";
                    row.Detail = st.Note ?? Tr.T("Done");
                    row.Time = took is { } t ? Durations.Clock(t) : "";
                    row.TimeFaint = "";
                    break;
                case StageState.Running:
                    row.State = "run";
                    var frac = progress.Fraction(st);
                    row.Detail = st.Note ?? (st.Id == "finish" ? Tr.T("Writing the game folder")
                        : st.Id == MakeGameProtocol.ShadersStage && frac == 0 ? "" : $"{(int)(frac * 100)}%");
                    row.Time = took is { } r ? Durations.Clock(r) : "";
                    row.TimeFaint = clock.Estimate(progress, st) is double est ? " / " + Durations.Roughly(est) : "";
                    break;
                case StageState.Skipped:
                    row.State = "skip";
                    row.Detail = st.Note ?? Tr.T("At the game's first start");
                    row.Time = "";
                    row.TimeFaint = Tr.T("Skipped");
                    break;
                default:
                    row.State = progress.Error != null && progress.Current == st.Id ? "fail" : "wait";
                    row.Detail = "";
                    row.Time = "";
                    row.TimeFaint = clock.Estimate(progress, st) is double e ? Durations.Roughly(e) : "";
                    break;
            }
        }
        // (before the build: the first row after the download runs while an update's shader caches are taken, Seed)
        if (seeding != null && Rows.FirstOrDefault(r => r.Id != "download") is { } first)
        {
            first.State = "run";
            first.Detail = seeding;
        }
        RaiseAll(nameof(Overall), nameof(Percent), nameof(Left), nameof(ShowPercent));
        if (showLog && following) Raise(nameof(LogText));
    }

    /// Cancel: the build and everything it started stopped, back home
    public void Stop()
    {
        if (Finalizing) return;
        ticker?.Stop();
        Finalizing = true;
        CancelCommand.Changed();
        Summarize(BuildReports.Cancelled, "");
        cancel.Cancel();
    }

    void StoppedIfRequested()
    {
        if (!cancel.IsCancellationRequested || !Finalizing) return;
        if (Current == this) Current = null;
        SetRunning(false);
        Finalizing = false;
        if (CloseAfterFinalizing != null) { CloseAfterFinalizing(); return; }
        Job.Main.ShowHome();
        Job.Main.ShowToast(Tr.T("Build stopped."));
    }

    /// The problem page, why in the player's language; reported: why as the statistics' summary says it, in English (Tr.N), why
    /// itself when it is make_game's own line
    void Fail(string why, string outcome = BuildReports.Failed, string? reported = null)
    {
        SetRunning(false);
        ticker?.Stop();
        Summarize(outcome, reported ?? why);
        var at = progress.Stages.FirstOrDefault(s => s.Id == progress.Current) ?? progress.Stages.LastOrDefault(s => s.State == StageState.Done);
        var name = at != null ? Tr.T(Names.GetValueOrDefault(at.Id, at.Title)) : Tr.T("the build");
        var pct = (int)Math.Floor(Overall * 100);
        Job.Main.Navigate(new ProblemViewModel(this, why, name, pct, outcome));
    }

    async void Complete()
    {
        Finalizing = true;
        Job.Main.Modal = null;
        CancelCommand.Changed();
        Summarize(BuildReports.Completed, "");
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
        entry.Dlss = dlss;
        entry.Fsr = fsr;
        entry.Xess = xess;
        entry.Unmeasured();
        string? problem = null;
        try { registry.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = Tr.F("The list of builds could not be saved: {0}", e.Message); }
        // (Linux, AMD FSR 4: the player's own library into the game folder, where the game looks for it)
        if (fsr && job.Main.Linux && s.FsrLibraryPath is { } library)
        {
            try { log.Add("AMD FSR 4: your library copied to " + FsrLibrary.Install(library, job.InstallDir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log.Add("AMD FSR 4: your library was not copied: " + e.Message);
                problem ??= Tr.F("Your FSR 4 library was not copied into the game folder: {0}", e.Message);
            }
        }
        service.PruneTools();
        // (Keep the build's work and capped stores, or remove them when keeping is off. Off the window's thread.)
        var work = buildWork ?? service.Work;
        var keep = s.KeepBuildFiles;
        await Task.Run(() => Storage.AfterBuild(work, keep));
        // (what the Storage page measured before this build is not what is there now)
        Job.Main.StorageMeasured = null;
        SetRunning(false);
        Finalizing = false;

        if (CloseAfterFinalizing != null)
        {
            CloseAfterFinalizing();
            return;
        }

        // (no saves to move: every build shares them, SaveEnvironment.LongshotUser)
        job.Main.Navigate(new ReadyViewModel(job.Main, entry, job.Kind, job.From, clock.Elapsed + job.ToolsTime, problem, job.Older));
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
    public bool Finalizing { get; private set; }
    public Action? CloseAfterFinalizing { get; set; }

    IDisposable? awake;

    /// The build running or not: while it runs the PC is kept from sleeping (KeepAwake), and let go however the build ends
    void SetRunning(bool on)
    {
        running = on;
        if (on) awake ??= KeepAwake.Hold("Building Longshot");
        else
        {
            if (Current == this) Current = null;
            awake?.Dispose();
            awake = null;
        }
    }

    /// A bug report (MainViewModel.Report): why it stopped, then the build, this PC's memory and GPU, the settings and the build log's
    /// last 200 lines, then the launcher's log
    public string Report(string why)
    {
        var more = new List<string>
        {
            $"build: {Job.Verb.Trim()} {Job.Version} ({Job.Channel})",
            "PC: " + PcLine(),
            "settings: " + SettingsLine(),
            "",
            "build log, its last 200 lines:",
        };
        more.AddRange(log.Last(200));
        return Job.Main.Report("the build stopped: " + why, more);
    }

    /// The report Send on the problem page sends (BuildReports), with the build's last error line; auto: the statistics' summary
    public BuildReports.Failure Failure(string why, string outcome = BuildReports.Failed, bool auto = false) =>
        new(Job.Service.OwnTag, Job.Version, Job.Channel,
            BuildReports.KindOf(update: Job.Kind == JobKind.Update, rebuild: Job.Kind == JobKind.Rebuild, older: Job.Older),
            BugReport.System(), PcLine(), SettingsLine(), why,
            lastError, auto ? [] : log.Last(BuildReports.LogLines).ToList(), outcome, Job.Service.Cpu, Stages(), auto);

    /// An English text with its parts filled in, as a summary carries it (Tr.N marks it for the catalogs where it is shown)
    static string English(string english, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, Tr.N(english), args);

    /// Each stage's progress and time (BuildProgress.Times), the tools' download timed by step 1, where it ran (kept: they were
    /// already on this PC)
    IReadOnlyDictionary<string, (double Progress, double Seconds, bool Kept)> Stages()
    {
        var times = progress.Times().ToDictionary(kv => kv.Key, kv => kv.Value);
        if (times.ContainsKey("download"))
            times["download"] = (1, Math.Round(Job.ToolsDownloaded ? Job.ToolsTime.TotalSeconds : 0, 1), !Job.ToolsDownloaded);
        return times;
    }

    /// The build that is running, if any: what the launcher's SIGTERM reports (Terminated)
    public static BuildStepViewModel? Current { get; private set; }
    public static BuildReports.Shutdown Shutdown { get; } = new();
    readonly BuildReports.Once summarized = new();

    /// With Settings' "Send performance statistics" ticked: how the build ended, sent on its own (BuildReports, Auto), once a
    /// build. Not for the screenshots' replays. On the window's thread
    void Summarize(string outcome, string why)
    {
        if (Summary(outcome, why) is { } json) Shutdown.Track(BuildReports.SendAsync(BuildReports.Http, json, CancellationToken.None));
    }

    /// The summary to send, made on the window's thread (it reads the stages); null when none is sent: the statistics unticked, a
    /// screenshot's replay, or the build already summarized (once only, whichever thread ends it first: BuildReports.Once)
    string? Summary(string outcome, string why)
    {
        if (fixedTime != null || !Job.Service.Settings.SendPerformanceStatistics || !summarized.Claim()) return null;
        return BuildReports.Json(Failure(why, outcome, auto: true), null);
    }

    /// SIGTERM on the window's thread: report it and close only after cancellation or finalization has put files back.
    public void Terminated(Action shutdown)
    {
        if (Shutdown.Terminating) return;
        Summarize(BuildReports.Terminated, BuildReports.LauncherStopped);
        Shutdown.Terminate(close => CloseAfterFinalizing = close, shutdown, TimeSpan.FromSeconds(2));
        if (!Finalizing) Stop();
    }

    /// The Build workers setting, for the log and the report: "automatic (12, low priority)", "every thread (16, normal priority)", "6 (low priority)"
    static string Workers(LauncherSettings s) =>
        (s.BuildWorkers switch { null => "automatic ", LauncherSettings.EveryThread => "every thread ", _ => "" }) +
        $"({SystemInfo.Workers(s.BuildWorkers)}, {(SystemInfo.LowPriority(s.BuildWorkers) ? "low" : "normal")} priority)";

    /// This PC's memory, threads and graphics cards
    string PcLine() =>
        $"{SystemInfo.MemoryGb()} GB, {SystemInfo.Threads()} threads" +
        (Job.Service.Gpus.Count > 0 ? ", " + string.Join(" and ", Job.Service.Gpus) + " GPU" : "");

    /// The build's memory, workers and upscalers
    string SettingsLine()
    {
        var s = Job.Service.Settings;
        return $"memory {(s.MemoryGb is int m ? m + " GB" : "automatic")}, workers {Workers(s)}, " +
               $"DLSS {(dlss ? "on" : "off")}, FSR 4 {(fsr ? "on" : s.Fsr ? "asked, not built" : "off")}, " +
               $"XeSS {(xess ? "on" : s.Xess ? "asked, not fetched" : "off")}" + (Job.Cold ? ", cold build" : "");
    }
}

/// "Stop the build?"
public sealed class StopBuildViewModel(BuildStepViewModel build) : ViewModelBase
{
    public string Sub => Tr.F("The tools and the files read so far are kept, so building {0} again starts faster.", build.Job.Shown);
    public Command KeepCommand { get; } = new(() => build.Job.Main.Modal = null);
    public Command StopCommand { get; } = new(() => { build.Job.Main.Modal = null; build.Stop(); });
}

/// The build finished: Play, Open folder, the shortcuts, whether Play starts this version
public sealed class ReadyViewModel : ViewModelBase, IWizardPage
{
    // the three shortcuts start ticked: an install should leave something to press. The Steam
    // one only shows where there is a Steam to add to (SteamShortcuts.Locate says so), and starts
    // from the player's last answer when there is one (steam, in the constructor)
    bool desktop = true, menu = true, steam;
    bool steamAsked;                        // whether the user ever moved the Steam tick: only then may a Remove be written
    SteamShortcuts.Placement? steamPlacement;   // the tile this screen can make (null: nothing to add to)

    public ReadyViewModel(MainViewModel main, BuildEntry entry, JobKind kind, string? from, TimeSpan took, string? problem, bool older = false)
    {
        Main = main;
        Entry = entry;
        Kind = kind;
        From = from;
        Older = older;
        Took = took;
        Problem = problem;
        PlayCommand = new Command(() =>
        {
            try
            {
                var said = Main.Service.Play(Entry);
                Main.ShowHome();
                if (said != null) Main.Home.Notice = said;
                Main.ShowToast(Tr.F("Starting Longshot, {0}…", Version));
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Main.ShowToast(Tr.F("{0} could not start: {1}", Version, e.Message));
            }
        });
        FolderCommand = new Command(() => Main.Host?.Open(Entry.Folder));
        HomeCommand = new Command(Main.ShowHome);
        // (both ticked and made as it opens, by the launcher that owns the shortcuts; a portable one's ticks are the shortcuts that
        // start it, made or removed only when the player ticks, so it writes nothing of its own accord: Shortcuts.MadeAtReady, #161)
        var made = Shortcuts.MadeAtReady(Program.Layout);
        // Steam is looked for whatever the launcher's kind, so the tick shows wherever there is a Steam to add to
        LocateSteam();
        // the Steam tick starts from the player's last answer when there is one: an untick that took
        // the tile away is not undone by the next build (LauncherSettings.SteamTile). With no answer
        // it starts ticked on a launcher that owns its shortcuts, and from what Steam's library
        // already holds on a portable one, which writes nothing by itself
        steam = Main.Service.Settings.SteamTile
                ?? (made || (steamPlacement != null && SteamShortcuts.InLibrary(steamPlacement)));
        if (made)
        {
            desktop = menu = true;
            WriteShortcuts();
        }
        else (desktop, menu) = OwnShortcuts();
    }

    public MainViewModel Main { get; }
    public BuildEntry Entry { get; }
    public JobKind Kind { get; }
    public string? From { get; }
    /// It went back to an older version (BuildJob.Older)
    public bool Older { get; }
    public TimeSpan Took { get; }
    public string? Problem { get; }
    public int Step => 3;
    public bool Finished => true;

    public string Version => Entry.Version ?? Entry.Name;
    public string Heading => Tr.F("{0} is ready", Version);
    public string Sub => Kind == JobKind.Update && From != null
        ? Tr.F(Older ? "Went back from {0} in {1} min, in {2}." : "Updated from {0} in {1} min, in {2}.",
               From, Math.Max(1, (int)Math.Round(Took.TotalMinutes)), ShownPath.Of(Entry.Folder))
        : Tr.F("Built in {0} min, in {1}.", Math.Max(1, (int)Math.Round(Took.TotalMinutes)), ShownPath.Of(Entry.Folder));
    public bool HasProblem => Problem != null;
    public string MenuLabel => Main.Linux ? Tr.T("Applications menu entry") : Tr.T("Start menu entry");
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

    /// The Deck and any Linux desktop with Steam: the tile that makes the game playable in Gaming
    /// mode. Found once, quietly: no Steam to add to means no tick and no fuss
    public bool ShowSteamTick => steamPlacement != null;
    public bool SteamTickVisible => ShowSteamTick;

    public bool Steam
    {
        get => steam;
        set
        {
            if (steamPlacement == null) return;
            if (Set(ref steam, value)) { steamAsked = true; RememberSteamTile(value); WriteShortcuts(); }
        }
    }

    /// The player moved the tick: that answer stands for every later Ready screen, so a tile
    /// taken away does not come back on the next build (LauncherSettings.SteamTile)
    void RememberSteamTile(bool ticked)
    {
        try { Main.Service.Settings.SteamTile = ticked; Main.Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public string SteamLabel => Tr.T("Steam shortcut (Gaming mode)");

    public bool IsDefault
    {
        get => Entry.Default;
        set
        {
            if (value)
            {
                Main.Service.SetDefault(Entry);
                Main.ShowToast(Tr.F("Play now starts {0}.", Version));
            }
            else if (Main.Service.Registry.Builds.FirstOrDefault(b => b.Id != Entry.Id) is { } other)
            {
                Main.Service.SetDefault(other);
                Main.ShowToast(Tr.F("Play now starts {0}.", other.Version ?? other.Name));
            }
            Raise();
        }
    }

    /// The shortcuts that start this launcher already (a portable one's ticks)
    (bool, bool) OwnShortcuts()
    {
        if (Main.Host == null) return (false, false);
        try { return Main.Linux ? Shortcuts.LinuxOwn(LauncherService.LauncherPath) : WindowsShortcuts.Own(LauncherService.LauncherPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (false, false); }
    }

    /// The shortcuts start the launcher's Play of the default build, so they follow "Play starts this version" and updates. One
    /// that starts another launcher is left as it is, unless this one takes over (Shortcuts.TakesOver), and the toast says so
    void WriteShortcuts()
    {
        if (Main.Host == null) return;
        // Linux shortcuts find their icon by path; the Windows .lnk takes it from the program's own
        // resources. The .png feeds the desktop and menu files; the Steam tile names the .ico (Steam
        // ignores .png in its icon field; both files sit in the launcher's home)
        shortcutsFailed = false;
        var logo = EnsureLogo();
        steamLogoIco = EnsureLogoIco();
        try
        {
            var left = Main.Linux ? Shortcuts.WriteLinux(LauncherService.LauncherPath, null, desktop, menu, logo)
                : WindowsShortcuts.Write(LauncherService.LauncherPath, desktop, menu);
            if (left.Count > 0)
                Main.ShowToast(Tr.F("{0} starts another Longshot Launcher, so it was left as it is.", string.Join(", ", left.Select(ShownPath.Of))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            shortcutsFailed = true;
            Main.ShowToast(Tr.F("The shortcuts could not be made: {0}", e.Message));
        }
        WriteSteamTile();
    }

    bool shortcutsFailed;       // a failed desktop/menu write this pass: it outranks the restart notice
    string? steamLogoIco;       // the .ico the Steam tile names; null: the write failed, art stays as it is
    static byte[]? logoBytes;
    static byte[]? logoIcoBytes;

    /// The logo the desktop and applications-menu shortcuts point at: the embedded Longshot.png
    /// written into the launcher's own home, where the desktop and the menu can read it (KDE draws
    /// a .png path correctly, checked on the Deck 2026-11-15). Null when there is nothing to write
    /// to: a shortcut without a logo beats no shortcut. The file is rewritten whenever it differs
    /// from the embedded logo, so a truncated file or a newer logo from a launcher update gets
    /// repaired rather than kept forever
    string? EnsureLogo()
    {
        if (!Main.Linux) return null;
        byte[] png;
        try { png = logoBytes ??= ReadEmbeddedLogo(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var path = SteamShortcuts.LogoPath();
        try { if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(png)) return path; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return SteamShortcuts.TryWriteLogo(png) ? path : null;
    }

    /// The logo the Steam Gaming-mode tile points at: the embedded longshot.ico. Steam ignores a
    /// .png in the icon field — the tile naming longshot.png showed the blank default on the Deck
    /// after a clean Steam restart (hardware, 2026-11-15) — while every tile in the Deck's own
    /// library that draws art names a .ico. Same repair rule as EnsureLogo
    string? EnsureLogoIco()
    {
        if (!Main.Linux) return null;
        byte[] ico;
        try { ico = logoIcoBytes ??= ReadEmbeddedLogoIco(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var path = SteamShortcuts.LogoIcoPath();
        try { if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(ico)) return path; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return SteamShortcuts.TryWriteLogoIco(ico) ? path : null;
    }

    static byte[] ReadEmbeddedLogo()
    {
        using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LongshotLauncher/Assets/longshot.png"));
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] ReadEmbeddedLogoIco()
    {
        using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LongshotLauncher/Assets/longshot.ico"));
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] ReadEmbeddedAsset(string name)
    {
        using var src = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://LongshotLauncher/Assets/steam/{name}"));
        using var ms = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    // the tile's capsule artwork, read once from the program's own resources
    static (string suffix, string ext, byte[] bytes)[]? capsuleBytes;
    static (string suffix, string ext, byte[] bytes)[] Capsules() => capsuleBytes ??=
    [
        ("", "png", ReadEmbeddedAsset("capsule_920x430.png")),      // landscape: the recently-played row
        ("p", "png", ReadEmbeddedAsset("capsulep_600x900.png")),    // portrait: the library tile
        ("_hero", "png", ReadEmbeddedAsset("hero_1920x620.png")),   // the detail page's background
        ("_logo", "png", ReadEmbeddedAsset("logo_998x323.png")),    // the detail page's logo
    ];

    /// The Steam tile, made or taken away with its tick. The core only ever touches the tile whose
    /// appid its own launcher path computes (or the id its marker recorded): a tile the user made in
    /// Steam's own dialog is another id and is never in reach, whatever it is called. Steam holds its
    /// library in memory and writes the file back when it quits, so a caller that asked the user may
    /// pass takeDown; this screen never stops Steam on its own (downloads and all). The placement is
    /// looked up once: no Steam on this machine means the tick was never offered.
    ///
    /// The tick starts like the desktop and menu ticks: on, because on a Deck the tile is how the
    /// game gets played from the couch, and the whole point is that installing leaves something to
    /// press. It is the one shortcut on this screen whose file Steam also owns, so the write goes
    /// through Steam's own id rule rather than "whatever is called Longshot".
    bool TrySteamWrite(bool asked = false)
    {
        if (steamPlacement is not { } p) return false;
        // Remove is only ever written when the user asked: a failed Add must not leave a later
        // desktop-or-menu checkbox change taking a tile away that sits in the library right now
        steamAsked |= asked;
        try
        {
            var wrote = false;
            if (steam)
            {
                // Add false with no tile of ours in the library means it refused: a tile called
                // Longshot the user made themselves, which the launcher leaves exactly as it is.
                // The tick goes back to the truth and says why
                wrote = SteamShortcuts.Add(p);
                if (!wrote && !SteamShortcuts.InLibrary(p))
                {
                    steam = false;
                    Raise(nameof(Steam));
                    Main.ShowToast(Tr.T("Steam already has a shortcut called Longshot that this launcher did not make, so it stays exactly as you made it."));
                }
            }
            else if (steamAsked)
            {
                wrote = SteamShortcuts.Remove(p);    // false: there was never ours to take away
            }
            // the tile's capsule artwork: only missing files are written, a capsule the user
            // chose in Steam stays; runs whether the tile was just made or already sat there
            if (steam) WriteTileArt(p);
            // Steam reads its library when it starts, checked on the Deck 2026-11-15: while Steam
            // runs, Gaming mode shows what it loaded at its own start, and a change made here waits
            // for a Steam restart to show. The launcher never restarts Steam itself (downloads and
            // all), so the notice says what to do rather than doing it. It never talks over a
            // failure it should have seen instead: one toast shows at a time, and an error about
            // shortcuts that could not be written outranks a note about restarting
            if (wrote && SteamShortcuts.SteamUp() && !shortcutsFailed)
                Main.ShowToast(Tr.T("Restart the console to see Steam shortcut changes in Gaming mode."));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // the tick goes back to what the library actually holds, whatever the write did or did not do
            steam = steamPlacement != null && SteamShortcuts.InLibrary(steamPlacement);
            Raise(nameof(Steam));
            Main.ShowToast(Tr.F("The Steam shortcut could not be changed: {0}", e.Message));
            return false;
        }
    }

    /// The tile's capsule artwork beside its shortcuts.vdf, in Steam's grid/ folder keyed by the
    /// tile's appid read unsigned. Only files that are not there get written: a capsule the user
    /// chose in Steam's own UI, or one a previous run put down, is left exactly as it is. A fault
    /// is silent — a tile without art beats no tile, and the tile itself is the thing that matters.
    /// The hero slot goes to the game folder's own banner (SteamShortcuts.GameHero) when that
    /// build's folder has one: what nx2/game_hero.py made there from the player's own copy.
    void WriteTileArt(SteamShortcuts.Placement p)
    {
        try
        {
            var folder = SteamShortcuts.GameHero(Entry.Folder);
            SteamShortcuts.WriteArtwork(p.Vdf, SteamShortcuts.TileAppId(p),
                                        SteamShortcuts.WithGameHero(Capsules(), Entry.Folder));
            // an install from before the marked hero kept this launcher's generated logo in the
            // grid, and WriteArtwork never overwrites: beside a folder hero that file is a second
            // mark over the first, so retire our own copy of it (and only our own: a capsule the
            // user chose in Steam's UI stays, whosever bytes it is)
            if (folder != null)
                SteamShortcuts.RetireGeneratedLogo(p.Vdf, SteamShortcuts.TileAppId(p),
                                                   Capsules().First(c => c.suffix == "_logo").bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// The tile this launcher can make, found once, quietly: no Steam to add to means no tick and
    /// no fuss. Locate touches the folders Steam owns and can fault for ordinary reasons, so a
    /// fault here hides the tick rather than taking the Ready screen down
    void LocateSteam()
    {
        if (!Main.Linux || steamPlacement != null) return;
        try
        {
            // the icon path is offered only when the .ico really landed: naming a file that was
            // never written would point the tile at nothing, and an iconless tile beats a broken one
            steamPlacement = steamLogoIco is { } ico
                ? SteamShortcuts.Locate(LauncherService.LauncherPath, ico)
                : SteamShortcuts.Locate(LauncherService.LauncherPath);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            steamPlacement = null;
        }
    }

    void WriteSteamTile()
    {
        if (!Main.Linux) return;
        // Locate and the first InLibrary read run outside the write's catch: they touch the same
        // folders Steam owns and can fault for the same ordinary reasons, and a fault here would
        // take the whole Ready screen down instead of just hiding the tick
        try
        {
            LocateSteam();
            if (steamPlacement is not { } offered) return;
            // the cached placement can predate this screen's .ico: EnsureLogoIco failing once and
            // succeeding now must retarget the offered tile at the file that actually landed —
            // naming an .ico that was never written is the broken tile the line above exists to
            // avoid. An .ico offered by an earlier attempt stays offered: that write put its file
            // on disk, and the tile names that path
            if (steamLogoIco is { } now && offered.Entry.Icon != now)
                steamPlacement = offered with { Entry = offered.Entry with { Icon = now } };
            Raise(nameof(ShowSteamTick));
            TrySteamWrite();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            steamPlacement = null;      // no tick offered; the desktop and menu shortcuts stand alone
            Raise(nameof(ShowSteamTick));
            Main.ShowToast(Tr.F("The Steam shortcut could not be made: {0}", e.Message));
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
    readonly string outcome;   // (failed, or terminated: make_game.py ended by SIGTERM, BuildReports)
    bool showLog;

    public ProblemViewModel(BuildStepViewModel build, string why, string stage, int percent, string outcome = BuildReports.Failed)
    {
        this.build = build;
        this.outcome = outcome;
        Why = why;
        Stage = stage;
        Percent = percent;
        ToggleLogCommand = new Command(() => ShowLog = !ShowLog);
        CopyCommand = new Command(() => _ = Copy());
        SendCommand = new Command(() => _ = Send(), () => CanSend);
        ToggleSentCommand = new Command(() => ShowSent = !ShowSent);
        SeeCommand = new Command(() => { if (answer?.Link is { } link) build.Job.Main.Host?.Open(link); });
        BackCommand = new Command(() => build.Job.Main.Navigate(build.Job.Settings ?? new SettingsStepViewModel(build.Job)));
        RetryCommand = new Command(() => build.Job.Main.Navigate(new BuildStepViewModel(build.Job)));
        Problem = Problems.FromBuild(why);
        // (Build again is the screen's own button; the others go to the step that has the choice)
        Offer = Problem.Fixes.FirstOrDefault(f => f.Kind is FixKind.PickOtherFiles or FixKind.ChooseOtherFolder or FixKind.LowerBuildMemory);
        OfferCommand = new Command(TakeOffer, () => Offer != null);
        ColdCommand = new Command(TryCold, () => CanCold);
    }

    /// Try a completely cold build: offered unless this one was cold already
    public bool CanCold => !build.Job.Cold;
    public Command ColdCommand { get; }

    /// Step 2 with a completely cold build ticked and Advanced open, so the player reads what it deletes before starting it
    void TryCold()
    {
        var job = build.Job;
        var settings = job.Settings ?? new SettingsStepViewModel(job);
        settings.Cold = true;
        settings.AdvancedOpen = true;
        job.Main.Navigate(settings);
    }

    /// What went wrong in plain words (launcher/Core/Problems.cs), from make_game.py's last message
    public Problem Problem { get; }
    /// The fix this screen has a button for besides Build again: step 1 for other game files, step 2 for another folder or less
    /// build memory (its Advanced opened); null for none
    public Fix? Offer { get; }

    public int Step => 3;
    public bool Finished => false;
    public string Why { get; }
    public string Stage { get; }
    public int Percent { get; }
    public bool Known => Problem.Kind != ProblemKind.Unknown;
    /// The general advice, under a message the launcher doesn't know (a known one has its own)
    public bool Unknown => !Known;
    public bool Memory => Unknown && Why.Contains("memory", StringComparison.OrdinalIgnoreCase);
    public string Head => Known ? Tr.T(Problem.Headline) : Tr.T("make_game.py stopped");
    /// What make_game.py said: the file, the folder, the build ids it names
    public string Said => Tr.F("Building {0} stopped in {1}, {2}% of the way through. It said: {3}", build.Job.Shown, Stage, Percent, Why);
    public string Body => Known ? Tr.T(Problem.Detail) : Said;
    public string OfferLabel => Offer is { } offer ? Tr.T(offer.Label) : "";
    public bool ShowLog { get => showLog; set { if (Set(ref showLog, value)) RaiseAll(nameof(LogButton), nameof(LogTail)); } }
    public string LogButton => showLog ? Tr.T("Hide details") : Tr.T("Show details");
    public string LogTail => string.Join("\n", build.Log.Tail(LogLevel.Info, null, 6));

    public Command ToggleLogCommand { get; }
    public Command CopyCommand { get; }
    /// Send to the team: the report (SentText) to Longshot-api's /telemetry/build (BuildReports)
    public Command SendCommand { get; }
    public Command ToggleSentCommand { get; }
    /// See it: the report's comment on the build issue
    public Command SeeCommand { get; }
    public Command BackCommand { get; }
    public Command RetryCommand { get; }
    public Command OfferCommand { get; }

    void TakeOffer()
    {
        var job = build.Job;
        switch (Offer?.Kind)
        {
            case FixKind.PickOtherFiles:
                job.Main.Navigate(job.Files ?? new FilesStepViewModel(job));
                break;
            case FixKind.ChooseOtherFolder:
                job.Main.Navigate(job.Settings ?? new SettingsStepViewModel(job));
                break;
            case FixKind.LowerBuildMemory:
                var settings = job.Settings ?? new SettingsStepViewModel(job);
                settings.AdvancedOpen = true;
                job.Main.Navigate(settings);
                break;
        }
    }

    async Task Copy()
    {
        if (build.Job.Main.Host is { } host) await host.CopyAsync(build.Report(Why));
        build.Job.Main.ShowToast(Tr.T("Copied: the version, this PC's memory and GPU, the settings and the log's last 200 lines."));
    }

    /// Everything Send sends, as it sends it (BuildReports.Json)
    public string SentText => sentText ??= BuildReports.Json(build.Failure(Why, outcome), null);
    string? sentText;
    bool showSent, sending;
    BuildReports.Answer? answer;

    public bool ShowSent { get => showSent; set { if (Set(ref showSent, value)) Raise(nameof(SentButton)); } }
    public string SentButton => showSent ? Tr.T("Hide what is sent") : Tr.T("Show what is sent");
    /// Send is there until the report was sent (once a page), and not while it is being sent
    public bool CanSend => !sending && answer?.Outcome != BuildReports.Outcome.Sent;
    public string SendLabel => sending ? Tr.T("Sending…") : answer?.Outcome == BuildReports.Outcome.Sent ? Tr.T("Sent") : Tr.T("Send to the team");
    /// What became of Send, under it ("" before it is pressed)
    public string SendSaid => answer switch
    {
        null => Tr.T("Sends the report below to the Longshot team. Only the team can read it. Nothing is sent until you press it."),
        { Outcome: BuildReports.Outcome.Sent } => Tr.T("Sent. Thank you: the team sees how often this happens and on which PCs."),
        { Outcome: BuildReports.Outcome.Busy } => Tr.T("The server has had many new reports this hour. Try again later."),
        { Outcome: BuildReports.Outcome.Refused } a => Tr.F("The server refused the report: {0}", a.Detail ?? ""),
        { } a => Tr.F("The report could not be sent: {0}", a.Detail ?? ""),
    };
    public bool HasLink => answer?.Link != null;

    async Task Send()
    {
        sending = true;
        RaiseAll(nameof(CanSend), nameof(SendLabel));
        SendCommand.Changed();
        try
        {
            answer = await BuildReports.SendAsync(BuildReports.Http, SentText, CancellationToken.None);
        }
        finally
        {
            sending = false;
            RaiseAll(nameof(CanSend), nameof(SendLabel), nameof(SendSaid), nameof(HasLink));
            SendCommand.Changed();
        }
    }
}

/// One of the build log's levels as its picker shows it
public sealed record LogLevelChoice(LogLevel Level, string Name)
{
    /// The levels it shows, least detailed first: each choice adds its own to the ones before it (the picker's stacked swatches)
    public IReadOnlyList<Avalonia.Media.IBrush> Shows => LogLevels.All.Where(l => l <= Level).Select(Controls.LogColors.Of).ToList();
    public static IReadOnlyList<LogLevelChoice> All { get; } = LogLevels.All.Select(l => new LogLevelChoice(l, LogLevels.Name(l))).ToList();
    public static LogLevelChoice Of(LogLevel level) => All.First(c => c.Level == level);
    public override string ToString() => Name;
}
