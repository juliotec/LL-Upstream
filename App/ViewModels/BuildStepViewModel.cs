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

/// Step 3: the version's builder run with the step's settings (LongshotBuilder: its events),
/// its output driving one overall bar and the stages' rows (BuildProgress, BuildClock); the log under Show details; Cancel stops
/// it and everything it started
public sealed class BuildStepViewModel : ViewModelBase, IWizardPage
{
    static readonly Dictionary<string, string> Names = new()
    {
        ["download"] = Tr.N("Download tools"), ["read"] = Tr.N("Read the game files"), ["lift"] = Tr.N("Translate"), ["compile"] = Tr.N("Compile"),
        ["finish"] = Tr.N("Finish"), [BuildProgress.ShadersStage] = Tr.N("Prepare shaders"),
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
    public bool ShowPercent => progress.Current != BuildProgress.ShadersStage || clock.SecondsLeft(progress) != null;
    public string Left => clock.SecondsLeft(progress) is double secs ? Tr.F("{0} left", Durations.About(secs))
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
    string? workId;   // (the row's id longshot-builder's work folder is named for: Run, Complete)
    /// The version's builder (Builders.For), from when the build starts; null before, and in the screenshots' replays
    LongshotBuilder? builder;
    /// The profile a version folder's build is for (build --profile), whose game folder its builder's info then says
    string? buildProfile;

    void Run()
    {
        var job = Job;
        var s = job.Service.Settings;
        var file = job.Picked!;
        // (a release of longshot-builder's kind: its version folder, versions/<v>, holds its builder as Longshot.exe, which builds
        // the current profile's game in builds/default and writes the profile's game copy and upscalers. BuilderVersions)
        job.Place();
        builder = null;
        string? profile = null;
        if (job.Service.VersionFolderFor(job.Version) is { } version
            && Builders.For(job.Tools!, job.Service.Platform, job.Service.Home, LauncherService.LauncherPath) is var release)
            try
            {
                BuilderVersions.Install(version, release.Exe, job.Service.Platform);
                builder = release.In(version);
                profile = buildProfile = job.Service.ProfileFolder;
                Directory.CreateDirectory(ProfileStore.SavesOf(profile));
                job.Service.SaveSettings();   // (its profile.json there, the builder's to write its game copy into)
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(e.Message);
                return;
            }
        // (an update's or a go-back's folder renamed to its new version's first, so the builder builds in the folder the row will have)
        if (!RenameFolder()) return;
        // (the row this build is of, whose id names longshot-builder's work folder: the same however an update renames its folder,
        // so the update finds its last build's work. A new row takes the id made here: Complete)
        workId = job.Replaces?.Id ?? job.Service.Registry.FindByFolder(job.InstallDir)?.Id ?? workId ?? BuildRegistry.NewId();
        // (DLSS, FSR 4 and XeSS as the player chose them, whatever this PC's cards: Upscalers.Chosen)
        var request = Upscalers.Chosen(
            new BuildRequest(file.Source, job.InstallDir, s.Language, job.Service.Work, SystemInfo.Workers(s.BuildWorkers), s.MemoryGb, true,
                             LowPriority: SystemInfo.LowPriority(s.BuildWorkers), IntelCompiles: job.Service.IntelCompilesApply ? s.IntelCompiles : GamePlay.IntelCompilesAuto,
                             IntelLongerLimits: s.IntelLongerLimits && job.Service.IntelCompilesApply, WorkId: workId, Profile: profile,
                             SendStatistics: s.SendPerformanceStatistics),
            s, job.Service.Platform);
        buildWork = job.Service.Work;
        MakeGameCommand command;
        try
        {
            builder ??= Builders.For(job.Tools!, job.Service.Platform, job.Service.Home, LauncherService.LauncherPath);
            var run = builder.Build(request);
            command = run.Command;
            (dlss, fsr, xess) = (run.Dlss, run.Fsr, run.Xess);
            log.Add($"builder: longshot-builder ({command.FileName})");
            foreach (var note in run.Notes) log.Add(note);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.InstallDir))!);
            // (Longshot#1010: tools under a folder Windows lets no program list stop at their first compile, unexplained)
            if (MakeGameRun.Blocked(command, job.Service.Platform == Platform.Windows) is { } blocked)
            {
                PutBack();
                Fail(blocked);
                return;
            }
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
    /// before the builder starts. The builder keeps a cache/ it finds in its folder, as a rebuild keeps its own, and its
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

    /// A completely cold build's start (BuildJob.Cold, ColdBuild.Clear): what earlier builds kept deleted before the builder
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
        // (longshot-builder's events, as the progress takes them)
        var parsed = LongshotBuilder.Read(line);
        if (parsed != null)
        {
            progress.Apply(parsed);
            switch (parsed)
            {
                case MakeGameLine.Plain p:
                    log.Add(p.Text);
                    break;
                case MakeGameLine.Stage st:
                    log.Add($"[{Names.GetValueOrDefault(st.Id, st.Id)}] {st.Text}");
                    break;
                case MakeGameLine.Error e:
                    log.Add("error: " + (e.Code != null ? $"{e.Text} ({e.Code})" : e.Text));
                    break;
                case MakeGameLine.Warning w:
                    log.Add($"warning: {w.Text} ({w.Code})");
                    break;
                case MakeGameLine.NotPrepared n:
                    log.Add("warning: " + n.Reason);
                    break;
                case MakeGameLine.Done { Seconds: double secs } d:
                    log.Add(BuildEvents.DoneLine(secs, d.CompileSeconds ?? 0, d.PreviousSeconds));
                    break;
            }
        }
        Update();
    }

    void ErrorLine(string line)
    {
        // (the message a stopped build quotes: a line the details show by default, not a game's debug or trace line; the last, or
        // longshot-builder's own line of why it did not start, before its usage: LongshotBuilder.WhyFrom)
        if (!string.IsNullOrWhiteSpace(line) && LogLevels.Of(line) <= LogLevel.Info)
            lastError = LongshotBuilder.WhyFrom(lastError, line);
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
        // cancel. A step the system killed is the builder's own error, and a failure)
        var terminated = code == 128 + 15;
        // (the page says why in the player's language; the summary in English, so one failure is one signature in every language)
        var told = progress.Error ?? lastError;
        // (longshot-builder's error code: the launcher's own words for it on the problem page, Problems.FromCode)
        var errorCode = progress.ErrorCode;
        var why = told ?? (terminated
            ? Tr.T("longshot-builder was stopped by a signal (SIGTERM) from outside the launcher.")
            : Tr.F("longshot-builder stopped with exit code {0} without saying why.", code));
        var reported = told ?? (terminated
            ? Tr.N("longshot-builder was stopped by a signal (SIGTERM) from outside the launcher.")
            : English("longshot-builder stopped with exit code {0} without saying why.", code));
        if (errorCode != null) reported = $"{errorCode}: {reported}";
        var outcome = terminated ? BuildReports.Terminated : BuildReports.Failed;
        // (the builder's own end is its to report: only a builder that died without saying why, no error event, is the launcher's
        // to summarize, BuildReports' fallback)
        died = progress.Error == null && errorCode == null;
        if (seeded == null && renamed == null)
        {
            Fail(why, outcome, reported, errorCode);
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
            Fail(why, outcome, reported, errorCode);
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
        // (game files picked in the folder itself: read from where they are now, or the builder finds nothing at the old path.
        // InstanceFolders.Follow)
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
                        : st.Id == BuildProgress.ShadersStage && frac == 0 ? "" : $"{(int)(frac * 100)}%");
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
    /// itself when it is the builder's own line
    /// code: longshot-builder's error code, whose problem the page says in the launcher's words (Problems.FromCode)
    void Fail(string why, string outcome = BuildReports.Failed, string? reported = null, string? code = null)
    {
        SetRunning(false);
        ticker?.Stop();
        if (died) Summarize(outcome, reported ?? why);
        var at = progress.Stages.FirstOrDefault(s => s.Id == progress.Current) ?? progress.Stages.LastOrDefault(s => s.State == StageState.Done);
        var name = at != null ? Tr.T(Names.GetValueOrDefault(at.Id, at.Title)) : Tr.T("the build");
        var pct = (int)Math.Floor(Overall * 100);
        Job.Main.Navigate(new ProblemViewModel(this, why, name, pct, outcome, code));
    }

    async void Complete()
    {
        Finalizing = true;
        Job.Main.Modal = null;
        CancelCommand.Changed();
        var job = Job;
        var service = job.Service;
        var registry = service.Registry;
        var file = job.Picked!;
        var s = service.Settings;
        var wasEmpty = registry.Builds.Count == 0;
        // (an update of a builder's version into another version folder: the one it leaves is kept for going back to it, and the
        // ones before it that nothing uses go once this one is saved, LauncherService.PruneVersions)
        var left = job.Kind == JobKind.Update && job.Replaces is { } was ? service.VersionFolderOf(was) : null;
        BuildEntry entry = job.Kind == JobKind.Update && job.Replaces != null && registry.Find(job.Replaces.Id) != null
            ? registry.Replace(job.Replaces.Id, job.InstallDir, job.Version, job.Channel)
            : registry.Add(job.InstallDir, job.Version, job.Channel, name: job.Version, makeDefault: wasEmpty, id: workId);
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
        // (the upscalers it built in, which an update of a folder may keep from before, not only what was asked: as the version
        // folder's builder says (info, which Home's state reads too, BuilderInfos), else the record of a folder outside one)
        var versionFolder = service.VersionFolderFor(job.Version);
        if (versionFolder != null) BuilderInfos.Forget(versionFolder);
        var info = builder != null && versionFolder != null && buildProfile != null ? await builder.InfoAsync(buildProfile) : null;
        if (info != null) BuilderInfos.Set(versionFolder!, info);
        var built = info?.Build.Upscalers ?? (builder != null ? LongshotBuilder.ReadRecord(job.InstallDir)?.Upscalers : null);
        if (built != null)
            (dlss, fsr, xess) = (built.Contains("dlss"), built.Contains("fsr4"), built.Contains("xess") && !job.Main.Linux);
        entry.Dlss = dlss;
        entry.Fsr = fsr;
        entry.Xess = xess;
        // (longshot-builder's game reads its files from the .nup where it is: the Ready page says to keep it there, and Play checks
        // it is, LongshotBuilder.MissingNup)
        var inPlace = builder != null && file.Source is BuildSource.Nup;
        entry.NupInPlace = inPlace ? true : null;
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
        // (longshot-builder's warnings the player should see once it is done, in the launcher's words: Problems.FromWarning)
        foreach (var code in progress.Warnings)
            if (Problems.FromWarning(code) is { } warned) problem = problem == null ? Tr.T(warned) : problem + "\n" + Tr.T(warned);
        service.PruneTools();
        if (left != null && service.VersionFolderOf(entry) is { } now && !BuildRegistry.SameFolder(left, now))
        {
            s.RollbackVersion = Path.GetFileName(left);
            try { service.SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            await service.PruneVersions();
        }
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
        job.Main.Navigate(new ReadyViewModel(job.Main, entry, job.Kind, job.From, clock.Elapsed + job.ToolsTime, problem, job.Older,
                                             inPlace ? file.Path : null));
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

    /// longshot-builder's work folder of the build running (LongshotBuilder.WorkFolder), which a change of the build files folder
    /// leaves alone (LauncherService.LeaveWork); null with none running
    public string? RunningWorkFolder => running && buildWork != null && workId != null ? LongshotBuilder.WorkFolder(buildWork, workId) : null;
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
    /// The builder ended without an error event (Exited): it died, and its summary is the launcher's to send
    bool died;

    /// With Settings' "Send performance statistics" ticked: how the build ended, sent on its own (BuildReports, Auto), once a
    /// build, only when the builder process itself died (no error event: a crash, a signal) or the launcher was stopped while it
    /// built. A build's own end is the builder's to send (play's --send-build-statistics). Not for the screenshots' replays. On
    /// the window's thread
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
    // one only shows where there is a Steam to add to (SteamTile.Present) and a version folder's
    // builder to make the tile, and starts from the player's last answer when there is one (steam,
    // in the constructor)
    bool desktop = true, menu = true, steam;
    bool steamAsked;                        // whether the user ever moved the Steam tick: only then may a Remove be written
    LongshotBuilder? steamBuilder;          // the version folder's builder that makes the tile (null: nothing to add to)
    bool steamBusy;                         // a steam-shortcut runs: the tick waits for its answer

    /// keepNup: the .nup the game reads its files from where it is (longshot-builder's), which the page asks the player to keep there
    public ReadyViewModel(MainViewModel main, BuildEntry entry, JobKind kind, string? from, TimeSpan took, string? problem, bool older = false,
                          string? keepNup = null)
    {
        Main = main;
        KeepNup = keepNup == null ? null
            : Tr.F("The game reads its files from your .nup, {0}. Keep it there: if you move or delete it, the game can't start until you pick it again.",
                   ShownPath.Of(keepNup));
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
        // it starts ticked on a launcher that owns its shortcuts; a portable one writes nothing by itself
        steam = Main.Service.Settings.SteamTile ?? made;
        if (made)
        {
            desktop = menu = true;
            WriteShortcuts();
        }
        else
        {
            (desktop, menu) = OwnShortcuts();
            // (a portable launcher writes nothing by itself, but the player's yes to the tile stands: it follows this version)
            if (Main.Service.Settings.SteamTile == true) WriteSteamTile();
        }
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
    public string Sub => Durations.Done(Took, ShownPath.Of(Entry.Folder), Kind == JobKind.Update ? From : null, Older);
    public bool HasProblem => Problem != null;
    /// Keep the .nup where it is (longshot-builder's game reads its files from it); null for a game that has its own copy
    public string? KeepNup { get; }
    public bool HasKeepNup => KeepNup != null;
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
    public bool ShowSteamTick => steamBuilder != null;
    public bool SteamTickVisible => ShowSteamTick;

    public bool Steam
    {
        get => steam;
        set
        {
            if (steamBuilder == null || steamBusy)
            {
                Raise(nameof(Steam));
                return;
            }
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
    static byte[]? logoBytes;

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
        var path = Shortcuts.LogoPath();
        try { if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(png)) return path; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return Shortcuts.TryWriteLogo(png) ? path : null;
    }

    static byte[] ReadEmbeddedLogo()
    {
        using var src = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LongshotLauncher/Assets/longshot.png"));
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

    /// The Steam tile, made or taken away with its tick, by the version folder's builder (SteamTile: steam-shortcut --profile <the
    /// profile's folder>, or --remove): it only ever touches its own tile, so a tile the user made in Steam's own dialog under the
    /// same name is left as it is (steam_name_taken). Steam holds its library in memory and writes the file back when it quits; the
    /// builder writes anyway and says Steam runs, and this screen never stops Steam on its own (downloads and all).
    ///
    /// The tick starts like the desktop and menu ticks: on, because on a Deck the tile is how the game gets played from the couch,
    /// and the whole point is that installing leaves something to press. The tile plays this version's folder with the current
    /// profile, so it is made again from each Ready screen of the default version (an update's new folder takes it over)
    async void TrySteamWrite()
    {
        if (steamBuilder is not { } builder || steamBusy) return;
        var add = steam;
        // Remove is only ever written when the user asked: a failed Add must not leave a later desktop-or-menu checkbox change
        // taking a tile away that sits in the library right now
        if (!add && !steamAsked) return;
        steamBusy = true;
        SteamTile.Result result;
        try { result = await SteamTile.RunAsync(builder, Main.Service.ProfileFolder, remove: !add, name: SteamTile.NameFor(Main.Service.Profile)); }
        finally { steamBusy = false; }
        if (!result.Done)
        {
            // the tick goes back to what the library holds: an add refused left no tile, a remove refused left it
            steam = !add;
            Raise(nameof(Steam));
            Main.ShowToast(result.Code == "steam_name_taken"
                ? Tr.T("Steam already has a shortcut called Longshot that this version did not make (yours, or an older launcher's), so it stays as it is. To get this one, remove that shortcut in Steam, then tick it again.")
                : Tr.F("The Steam shortcut could not be changed: {0}", result.Text ?? ""));
            return;
        }
        // the tile's capsule artwork: only missing files are written, a capsule the user chose in Steam stays; runs whether the
        // tile was just made or already sat there
        if (result.InLibrary && result.Library is { Length: > 0 } library) WriteTileArt(library, result.AppId);
        // Steam reads its library when it starts, checked on the Deck 2026-11-15: while Steam runs, Gaming mode shows what it
        // loaded at its own start, and a change made here waits for a Steam restart to show. The notice says what to do rather
        // than doing it, and never talks over a failure of the desktop or menu shortcuts
        if (result.Action is "added" or "updated" or "removed" && result.SteamRunning && !shortcutsFailed)
            Main.ShowToast(Tr.T("Restart the console to see Steam shortcut changes in Gaming mode."));
    }

    /// The tile's capsule artwork beside the library the builder wrote, in Steam's grid/ folder keyed by the tile's appid. Only
    /// files that are not there get written. A fault is silent: a tile without art beats no tile
    void WriteTileArt(string library, uint appId)
    {
        try { SteamTile.WriteArtwork(library, appId, Capsules()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// The builder that can make this version's tile, found once, quietly: on Linux, with a Steam to add to (SteamTile.Present),
    /// the version folder's builder of a version that has one. No Steam, or no version folder, means no tick and no fuss
    void LocateSteam()
    {
        if (!Main.Linux || steamBuilder != null) return;
        try
        {
            // (a builder whose info says it has no steam-shortcut offers none; one not asked yet is tried, and says so if not)
            if (SteamTile.Present() && Main.Service.VersionFolderOf(Entry) is { } version && BuilderVersions.Is(version, Main.Service.Platform)
                && BuilderInfos.OfVersion(version)?.Can("steam_shortcut") != false)
                steamBuilder = Main.Service.BuilderOf(version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { steamBuilder = null; }
    }

    void WriteSteamTile()
    {
        if (!Main.Linux) return;
        LocateSteam();
        Raise(nameof(ShowSteamTick));
        // (made again from the default version's Ready screen, or when the player moved the tick)
        if (steamBuilder != null && (steamAsked || Entry.Default)) TrySteamWrite();
    }

    public Command PlayCommand { get; }
    public Command FolderCommand { get; }
    public Command HomeCommand { get; }
}

/// The build stopped: what happened, what to do, Copy for a bug report, Build again
public sealed class ProblemViewModel : ViewModelBase, IWizardPage
{
    readonly BuildStepViewModel build;
    readonly string outcome;   // (failed, or terminated: the builder ended by SIGTERM, BuildReports)
    bool showLog;

    /// code: longshot-builder's error code: its problem in the launcher's words (Problems.FromCode), else the builder's text as said
    public ProblemViewModel(BuildStepViewModel build, string why, string stage, int percent, string outcome = BuildReports.Failed, string? code = null)
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
        Code = code;
        Problem = Problems.FromCode(code) ?? Problems.FromBuild(why);
        // (Build again is the screen's own button; the others go to the step that has the choice)
        Offer = Problem.Fixes.FirstOrDefault(f => f.Kind is FixKind.PickOtherFiles or FixKind.ChooseOtherFolder or FixKind.LowerBuildMemory
                                                  or FixKind.AcceptLicence);
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

    /// What went wrong in plain words (launcher/Core/Problems.cs), from the builder's error code or last message
    public Problem Problem { get; }
    /// longshot-builder's error code (null when it said none)
    public string? Code { get; }
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
    public string Head => Known ? Tr.T(Problem.Headline) : Tr.T("The build stopped");
    /// What the builder said: the file, the folder, the build ids it names
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
            // (longshot-builder's license_not_accepted: the licence its error names shown over step 2, and the build again once
            // accepted; declined, step 2 stays, where the upscaler can be turned off)
            case FixKind.AcceptLicence:
                var step = job.Settings ?? new SettingsStepViewModel(job);
                job.Main.Navigate(step);
                if (LongshotBuilder.LicenceOf(Why) is { } vendor)
                    step.AskLicence(vendor switch { "nvidia" => LicenceOf.Nvidia, "amd" => LicenceOf.Amd, _ => LicenceOf.Intel },
                                    () => job.Main.Navigate(new BuildStepViewModel(job)));
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

