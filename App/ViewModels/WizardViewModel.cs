using System.Collections.ObjectModel;
using System.Diagnostics;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

public enum JobKind { Build, Rebuild, Update }

/// One run of the wizard: what is built (a new version, one you have again, an update of a build), from which game files, into
/// which folder; the version's tools, downloaded in step 1; and the pages of its steps, kept for Back
public sealed class BuildJob
{
    public BuildJob(MainViewModel main, JobKind kind, string channel, string version, BuildEntry? replaces)
    {
        Main = main;
        Kind = kind;
        Channel = channel;
        Version = version;
        Replaces = replaces;
        From = kind == JobKind.Update ? replaces?.Version : null;
        Older = kind == JobKind.Update && Versions.Newer(From, version);
        InstallDir = "";
        Place();
    }

    /// Where it is built, from the builds folder (BuildRegistry.FolderOf: builds/&lt;track&gt;/&lt;version&gt;), and the rename an update's or
    /// a go-back's folder takes to its new version's name when the build starts (InstanceFolders.Rename). Again when the builds
    /// folder changes in step 2, and once its tools are known: a release of longshot-builder's kind builds in its version folder
    /// (BuilderVersions: versions/&lt;v&gt;/builds/default, never renamed), for the current profile
    public void Place()
    {
        var s = Main.Service;
        if (Builder is LongshotBuilder && s.VersionFolderFor(Version) is { } version)
        {
            InstallDir = BuilderVersions.GameFolderOf(version);
            renameFrom = renameTo = null;
            return;
        }
        InstallDir = s.Registry.FolderOf(Kind == JobKind.Build ? null : Replaces, s.BuildsFolder, Channel, Version, s.Platform);
        renameFrom = renameTo = null;
        if (Kind != JobKind.Build && Replaces != null && !BuildRegistry.SameFolder(Replaces.Folder, InstallDir))
        {
            renameFrom = Replaces.Folder;
            renameTo = InstallDir;
        }
    }

    public MainViewModel Main { get; }
    public LauncherService Service => Main.Service;
    /// One Vulkan probe shared by this wizard's steps, fresh for each build job.
    public Task<IReadOnlyList<GpuRequirement>?> VulkanGpus => vulkanGpus ??= Probe();
    Task<IReadOnlyList<GpuRequirement>?>? vulkanGpus;
    async Task<IReadOnlyList<GpuRequirement>?> Probe()
    {
        var gpus = await Service.VulkanProbe().ConfigureAwait(false);
        LauncherLog.Add(SystemRequirements.LogLine(gpus));   // (the estimate and the list, to set beside the game log's "vk:" lines)
        return gpus;
    }
    public IReadOnlyList<GpuRequirement>? VulkanSnapshot => VulkanGpus is { IsCompletedSuccessfully: true } t ? t.Result : null;
    public IReadOnlyList<Gpu> GameGpuCards => SystemInfo.GameCards(Service.GpuCards, VulkanSnapshot);
    public JobKind Kind { get; }
    public string Channel { get; }
    public string Version { get; }
    /// The build an update or a rebuild takes the place of
    public BuildEntry? Replaces { get; }
    /// The version an update starts from, read when the job is made: the update takes Replaces' row in its place
    /// (BuildRegistry.Replace), its version then the new one
    public string? From { get; }
    /// An update to a version older than the one it replaces (the Version dropdown on Home): it goes back, and says so
    public bool Older { get; }
    /// The version and the one it replaces as players see them (a nightly: "Nightly 2029 · c544828f", Versions.Shown)
    public string Shown => Versions.Shown(Version);
    public string? ShownFrom => From == null ? null : Versions.Shown(From);
    public string InstallDir { get; set; }
    string? renameFrom, renameTo;
    /// The folder an update or a go-back renames to InstallDir when its build starts (BuildRegistry.FolderOf: builds/&lt;track&gt;/&lt;version&gt;,
    /// renamed to the new version's name); null when it builds in its folder as it is
    public string? RenameFrom => renameTo != null && BuildRegistry.SameFolder(renameTo, InstallDir) ? renameFrom : null;
    public FoundGameFile? Picked { get; set; }
    public Toolchain? Tools
    {
        get => tools;
        set
        {
            tools = value;
            Place();
        }
    }
    Toolchain? tools;
    /// The builder of these tools (Builders.For); null until they are installed
    public LongshotBuilder? Builder => Tools is { } t ? Builders.For(t, Service.Platform, Service.Home, LauncherService.LauncherPath) : null;
    /// The .nup this build's game read its files from, which is not there any more (Play found it gone: LongshotBuilder.MissingNup):
    /// step 1 says so, and the game is built again from where the player picks it now
    public string? MovedNup { get; set; }
    /// The tools were downloaded in step 1 (not already on this PC), and how long that took
    public bool ToolsDownloaded { get; set; }
    /// The tools' download started before step 1, after the update check (ToolsPrefetch): "Downloaded in the background"
    public bool ToolsEarly { get; set; }
    public TimeSpan ToolsTime { get; set; }
    public bool AdvancedOpen { get; set; }
    /// A completely cold build (Advanced, or the problem page's Try a completely cold build): before the builder starts, the
    /// build files and both stores go (Disk.FreeWork), and this build's folder's shader caches with their pipeline notes
    /// (ShaderCaches.Clear); no caches are taken from the build it replaces, nor game files from another build. For this build only
    public bool Cold { get; set; }
    public readonly CancellationTokenSource Cancel = new();

    public FilesStepViewModel? Files { get; set; }
    public SettingsStepViewModel? Settings { get; set; }

    public string Verb => Kind switch { JobKind.Update => Older ? "Go back to " : "Update to ", JobKind.Rebuild => "Rebuild ", _ => "Build " };
    /// The start button's words: Build, Rebuild, Update to or Go back to, then the version
    public string StartText => Tr.F(Kind switch
    {
        JobKind.Update => Older ? "Go back to {0}" : "Update to {0}",
        JobKind.Rebuild => "Rebuild {0}",
        _ => "Build {0}",
    }, Shown);

    /// Everything of step 1 stops (the download, the checks): the wizard was left
    public void Stop()
    {
        try { Cancel.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

/// One game file found on this PC (or browsed, or dropped), and what longshot-builder check said of it
public sealed class GameFileViewModel : ViewModelBase
{
    readonly FilesStepViewModel step;
    GameCheck? check;

    public GameFileViewModel(FilesStepViewModel step, FoundGameFile file)
    {
        this.step = step;
        File = file;
        CheckAgainCommand = new Command(() => step.CheckAgain(this), () => Dim);
        CopyReportCommand = step.Job.Main.CopyReportCommand(() => "The game file was not taken: " + check?.Text,
            () => new[] { $"game file: {File.Name}, {(File.Folders ? "romfs and exefs folders" : $"{File.Size} bytes")}",
                          $"check: {check?.Status}", $"tools: {step.Job.Version} ({step.Job.Channel})" });
    }

    public FoundGameFile File { get; }
    public string Name => File.Folders ? Tr.F("{0} (romfs and exefs)", File.Name) : File.Name;
    public string Where => File.Where == "Dropped on the window" ? Tr.T("Dropped on the window") : ShownPath.Of(Path.GetDirectoryName(File.Path) ?? File.Path);
    public string? WhereFull => File.Where == "Dropped on the window" ? null : ShownPath.Of(File.Path);

    public GameCheck? Check
    {
        get => check;
        set
        {
            if (!Set(ref check, value)) return;
            RaiseAll(nameof(Checking), nameof(Ok), nameof(Old), nameof(Bad), nameof(ChipText), nameof(Dim), nameof(CanPick), nameof(Tip),
                     nameof(Why), nameof(HasWhy));
            CheckAgainCommand.Changed();
        }
    }

    public bool Checking => check == null;
    public bool Ok => check?.Status == GameCheckStatus.Supported;
    public bool Old => check?.Status == GameCheckStatus.OtherVersion;
    public bool Bad => check?.Status is GameCheckStatus.Unreadable or GameCheckStatus.CheckFailed;
    public string ChipText => check?.Status switch
    {
        GameCheckStatus.Supported => Tr.T("Supported version"),
        GameCheckStatus.OtherVersion => Tr.T("Not the supported version"),
        GameCheckStatus.Unreadable => Tr.T("Can't be read"),
        GameCheckStatus.CheckFailed => Tr.T("Not checked"),
        _ => Tr.T("Checking…"),
    };
    public string? Tip => check?.Text;
    /// Under the file, for one that can't be used: what is wrong and what to do (the chip alone said "Can't be read", and its
    /// reason only showed on hover, in English)
    public string? Why => check?.Why;
    public bool HasWhy => Why != null;
    /// Check again: after copying the file again, or fixing what stopped the check
    public Command CheckAgainCommand { get; }
    public Command CopyReportCommand { get; }
    public bool Dim => check != null && !Ok;
    public bool CanPick => !Dim;

    public bool IsPicked
    {
        get => step.Picked == this;
        set
        {
            if (value && CanPick) step.Picked = this;
            else Raise();
        }
    }

    public void PickedChanged() => Raise(nameof(IsPicked));
}

/// Step 1, the game files: the version's tools download (Toolchains) while the game files found on this PC are listed, then each
/// is checked with longshot-builder check from those tools. Next once both are done and a supported file is picked
public sealed class FilesStepViewModel : ViewModelBase, IWizardPage
{
    GameFileViewModel? picked;
    double toolsFraction;
    long toolsTotal;
    bool toolsDone, toolsFailed, searching = true;
    string? toolsProblem;
    readonly Stopwatch toolsClock = new();
    bool checking;

    /// tools: start the tools' download (the screenshots show it at a set point instead); search: look for game files in this PC's
    /// folders (the screenshots don't: nothing of the PC they are drawn on shows)
    public FilesStepViewModel(BuildJob job, bool tools = true, bool search = true)
    {
        Job = job;
        job.Files = this;
        BackCommand = new Command(Back);
        NextCommand = new Command(Next, () => CanNext);
        BrowseCommand = new Command(() => _ = BrowseAsync());
        FoldersCommand = new Command(() => _ = FoldersAsync());
        // (only after a failure: a second click while it downloads again starts nothing)
        RetryCommand = new Command(StartTools, () => toolsFailed);
        CopyToolsReportCommand = job.Main.CopyReportCommand(() => ToolsNote, () => new[] { $"tools: {Job.Version} ({Job.Channel})" });
        RequirementsCommand = new Command(() => Main.Host?.Open(SystemRequirements.Url));
        // (the same probe is shared with the other steps of this wizard)
        var gpus = job.VulkanGpus;
        _ = Task.Run(async () => SystemRequirements.Check(job.InstallDir, await gpus)).ContinueWith(task => Ui.Post(() =>
        {
            if (task.IsCompletedSuccessfully) requirements = task.Result;
            else requirements = new(RequirementStatus.Gray, Tr.T("This PC could not be checked"), Tr.T("See the system requirements before building."));
            RaiseAll(nameof(RequirementsSummary), nameof(RequirementsDetail), nameof(RequirementsGreen), nameof(RequirementsYellow), nameof(RequirementsRed), nameof(RequirementsGray));
        }));
        if (tools) StartTools();
        if (search) StartSearch();
        else searching = false;
    }

    RequirementResult requirements = new(RequirementStatus.Gray, Tr.T("Checking this PC…"), Tr.T("Checking OS, memory, storage and Vulkan support."));
    public string RequirementsSummary => requirements.Summary;
    public string RequirementsDetail => requirements.Detail;
    public bool RequirementsGray => requirements.Status == RequirementStatus.Gray;
    public bool RequirementsGreen => requirements.Status == RequirementStatus.Green;
    public bool RequirementsYellow => requirements.Status == RequirementStatus.Yellow;
    public bool RequirementsRed => requirements.Status == RequirementStatus.Red;
    public Command RequirementsCommand { get; }

    public BuildJob Job { get; }
    MainViewModel Main => Job.Main;
    LauncherService Service => Job.Service;
    public int Step => 1;
    public bool Finished => false;

    public ObservableCollection<GameFileViewModel> Files { get; } = new();

    /// (an update builds the game again from the game files: when the ones it was built from moved, it says so rather than that
    /// they are picked. A .nup moved since its build, Play found: picked, the game is pointed at it, not built again, Next)
    public string Sub => Job.MovedNup is { } moved
            ? Tr.F("Longshot reads the game's files from your .nup, which is no longer at {0}. Pick it where it is now, and the game reads it from there.",
                   ShownPath.Of(moved))
        : Job.Kind != JobKind.Update ? Tr.T("Pick your dump of the game. Longshot reads it on this PC; nothing is uploaded.")
        : Remembered() != null
            ? Tr.F(Job.Older ? "Going back from {0} to {1}. The file you built it from is picked; pick another if it moved."
                             : "Updating {0} to {1}. The file you built it from is picked; pick another if it moved.", Job.ShownFrom, Job.Shown)
            : Tr.F(Job.Older ? "Going back from {0} to {1}. The file you built it from is no longer where it was: pick it again, since the game is built from it again."
                             : "Updating {0} to {1}. The file you built it from is no longer where it was: pick it again, since the game is built from it again.", Job.ShownFrom, Job.Shown);

    // ---- the tools ----

    public string ToolsTitle => Tr.F("Builder tools for {0}", Job.Shown);
    public double ToolsFraction { get => toolsFraction; private set => Set(ref toolsFraction, value); }
    public bool ToolsDone => toolsDone;
    public bool ToolsFailed => toolsFailed;
    public bool ToolsBusy => !toolsDone && !toolsFailed;
    /// Downloading with its size known: a bar; before that (finding the version's tools) a spinner, not a bar that does not move
    public bool ToolsMeasured => ToolsBusy && toolsTotal > 0;
    public bool ToolsLooking => ToolsBusy && toolsTotal == 0;

    public string ToolsNote
    {
        get
        {
            if (toolsFailed) return Tr.F("The download stopped: {0}", toolsProblem);
            if (toolsDone)
                return Job.ToolsDownloaded ? Tr.F("Downloaded, {0}. Kept for this version's updates.", Durations.Size(toolsTotal))
                    : Tr.T("Already on this PC. Kept for this version's updates.");
            if (toolsTotal == 0) return Tr.T("Looking for this version's tools…");
            var have = (long)(toolsFraction * toolsTotal);
            var text = Tr.F("{0} of {1}", have >> 20, Durations.Size(toolsTotal));
            var secs = toolsClock.Elapsed.TotalSeconds;
            if (toolsFraction > 0.02 && secs > 2)
            {
                var left = secs / toolsFraction * (1 - toolsFraction);
                text += left < 90 ? Tr.F(", about {0} s left", Math.Max(1, (int)Math.Round(left))) : Tr.F(", about {0} min left", (int)Math.Round(left / 60));
            }
            return text;
        }
    }

    void StartTools()
    {
        toolsFailed = false;
        toolsProblem = null;
        toolsTotal = 0;
        ToolsFraction = 0;
        RaiseTools();
        toolsClock.Restart();
        var token = Job.Cancel.Token;
        // (the launcher may have started this version's tools already, after the update check: Service.Prefetch; this joins that
        // download, or starts it. Leaving the wizard stops it, as before, whoever started it: the player left, so no download goes
        // on unseen; the next Get starts it again)
        var fetch = Service.Prefetch.Get(Job.Version);
        Job.ToolsEarly = fetch.Early;
        var watch = fetch.Watch(p => Ui.Post(() => Progress(p)));
        token.Register(() =>
        {
            watch.Dispose();
            if (!fetch.Task.IsCompleted) fetch.Cancel();
        });
        Task.Run(async () =>
        {
            try
            {
                var t = await fetch.Task.WaitAsync(token);
                watch.Dispose();
                Ui.Post(() => ToolsReady(t));
            }
            catch (OperationCanceledException) { }
            catch (Exception e) when (e is FetchException or IOException or InvalidDataException or FormatException or UnauthorizedAccessException
                                          or ArgumentException)
            {
                LauncherLog.Add($"tools for {Job.Version} failed: {e.GetType().Name}: {e.Message}");
                Ui.Post(() =>
                {
                    toolsFailed = true;
                    toolsProblem = e.Message;
                    RaiseTools();
                });
            }
        }, token);
    }

    void Progress(ToolsDownloadProgress p)
    {
        if (p.Total > 0)
        {
            toolsTotal = p.Total;
            Job.ToolsDownloaded = true;
        }
        ToolsFraction = p.Fraction;
        RaiseAll(nameof(ToolsNote), nameof(ToolsMeasured), nameof(ToolsLooking));
    }

    void ToolsReady(Toolchain t)
    {
        Job.Tools = t;
        Job.ToolsTime = toolsClock.Elapsed;
        toolsDone = true;
        ToolsFraction = 1;
        RaiseTools();
        _ = CheckAllAsync();
    }

    void RaiseTools()
    {
        RaiseAll(nameof(ToolsDone), nameof(ToolsFailed), nameof(ToolsBusy), nameof(ToolsMeasured), nameof(ToolsLooking), nameof(ToolsNote));
        RetryCommand.Changed();
        RaiseNext();
    }

    /// For the screenshots: the tools as if this far into their download
    public void ShowToolsAt(double fraction, long total)
    {
        toolsTotal = total;
        Job.ToolsDownloaded = true;
        toolsDone = fraction >= 1;
        ToolsFraction = fraction;
        RaiseTools();
    }

    /// For the screenshots: the tools already on this PC (no check is started: no tools are there)
    public void ShowToolsDone()
    {
        toolsDone = true;
        ToolsFraction = 1;
        RaiseTools();
    }

    /// For the screenshots: the tools' download failed, saying why
    public void ShowToolsFailed(string why)
    {
        toolsFailed = true;
        toolsProblem = why;
        RaiseTools();
    }

    /// For the screenshots: a file listed with its check's answer
    public void ShowFile(FoundGameFile file, GameCheck check) => Add(file, pick: false, first: false).Check = check;

    // ---- the game files ----

    public bool Searching => searching && Files.Count == 0;
    public bool NothingFound => !searching && Files.Count == 0;

    void StartSearch()
    {
        var remembered = Remembered();
        var token = Job.Cancel.Token;
        Task.Run(() =>
        {
            var found = GameFiles.Find(GameFiles.Places(Service.Platform));
            Ui.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                searching = false;
                if (remembered != null) Add(remembered, pick: true, first: true);
                // (a .nup moved since its build: one found under its name is checked first, so it is the one picked when it is
                // supported, and Next points the game at it)
                var moved = Job.MovedNup is { } m ? Path.GetFileName(m) : null;
                foreach (var f in found.OrderBy(f => !string.Equals(f.Name, moved, StringComparison.OrdinalIgnoreCase)))
                    Add(f, pick: false, first: false);
                RaiseAll(nameof(Searching), nameof(NothingFound));
                if (Job.Tools != null) _ = CheckAllAsync();
            });
        }, token);
    }

    /// The game files this build (an update) or the last build was made from
    FoundGameFile? Remembered()
    {
        var e = Job.Replaces;
        var s = Service.Settings;
        var nup = e?.Nup ?? (e?.Romfs == null ? s.Nup : null);
        if (nup != null && File.Exists(nup)) return GameFiles.FromPath(nup, "Last used");
        var (romfs, exefs) = e?.Romfs != null ? (e.Romfs, e.Exefs) : (s.Romfs, s.Exefs);
        return romfs != null && exefs != null ? GameFiles.FromFolders(romfs, exefs, "Last used") : null;
    }

    GameFileViewModel Add(FoundGameFile f, bool pick, bool first)
    {
        var have = Files.FirstOrDefault(x => BuildRegistry.SameFolder(x.File.Path, f.Path) && x.File.Exefs == f.Exefs);
        if (have == null)
        {
            have = new GameFileViewModel(this, f);
            if (first) Files.Insert(0, have);
            else Files.Add(have);
        }
        if (pick) Picked = have;
        RaiseAll(nameof(Searching), nameof(NothingFound));
        return have;
    }

    /// Each file checked in turn (the check unpacks into one probe folder); the first supported one is picked when none is
    async Task CheckAllAsync()
    {
        if (checking || Job.Builder is not { } builder) return;
        checking = true;
        var token = Job.Cancel.Token;
        try
        {
            while (!token.IsCancellationRequested && Files.FirstOrDefault(f => f.Check == null) is { } next)
            {
                // (longshot-builder check)
                var source = next.File.Source;
                var result = await Task.Run(() => builder.CheckAsync(source, Service.Work, token), token);
                LauncherLog.Add($"game file check, {next.File.Name}: {result.Status}: {result.Text}");
                next.Check = result;
                if (picked == next && !next.Ok) Picked = null;
                if (picked == null && next.Ok) Picked = next;
                RaiseNext();
            }
        }
        catch (OperationCanceledException) { }
        finally { checking = false; }
    }

    /// The file checked again (Check again under it)
    public void CheckAgain(GameFileViewModel file)
    {
        file.Check = null;
        _ = CheckAllAsync();
    }

    public GameFileViewModel? Picked
    {
        get => picked;
        set
        {
            var was = picked;
            if (!Set(ref picked, value)) return;
            was?.PickedChanged();
            value?.PickedChanged();
            Job.Picked = value?.File;
            RaiseNext();
        }
    }

    // ---- Browse, the folders, a drop ----

    async Task BrowseAsync()
    {
        if (Main.Host is not { } host) return;
        var path = await host.PickFileAsync(Tr.T("Your game file (.nup or .nx)"), new[] { "*.nup", "*.nx" });
        if (path == null) return;
        if (GameFiles.FromPath(path, "Browsed") is { } f) Picked = Add(f, pick: true, first: true);
        else Main.ShowToast(Tr.T("That is not a .nup or .nx file."));
        _ = CheckAllAsync();
    }

    async Task FoldersAsync()
    {
        if (Main.Host is not { } host) return;
        var first = await host.PickFolderAsync(Tr.T("The game's romfs folder, or the folder holding romfs and exefs"), null);
        if (first == null) return;
        var f = GameFiles.FromPath(first, "Picked");
        if (f == null)
        {
            var exefs = await host.PickFolderAsync(Tr.T("The game's exefs folder"), Path.GetDirectoryName(first));
            if (exefs == null) return;
            f = GameFiles.FromFolders(first, exefs, "Picked");
        }
        if (f != null) Add(f, pick: true, first: true);
        else Main.ShowToast(Tr.T("Those are not the game's romfs and exefs folders."));
        _ = CheckAllAsync();
    }

    public void Dropped(IReadOnlyList<string> paths)
    {
        var any = false;
        foreach (var p in paths)
            if (GameFiles.FromPath(p, "Dropped on the window") is { } f)
            {
                Add(f, pick: true, first: true);
                any = true;
            }
        if (!any) Main.ShowToast(Tr.T("Drop a .nup or .nx file, or the folder holding romfs and exefs."));
        _ = CheckAllAsync();
    }

    // ---- Back and Next ----

    public bool CanNext => toolsDone && picked?.Ok == true;

    public string Hint => CanNext ? Tr.T("Ready for the settings.")
        : !toolsDone && picked?.Ok != true ? Tr.T("Next waits for the tools download and a supported game file.")
        : !toolsDone ? Tr.T("Next waits for the tools download.")
        : Tr.T("Pick a supported game file to go on.");

    void RaiseNext()
    {
        RaiseAll(nameof(CanNext), nameof(Hint));
        NextCommand.Changed();
    }

    public Command BackCommand { get; }
    public Command NextCommand { get; }
    public Command BrowseCommand { get; }
    public Command FoldersCommand { get; }
    public Command RetryCommand { get; }
    /// Copy for a bug report, under the tools' failure: what the download said (cut short on the line), and the launcher's log
    public Command CopyToolsReportCommand { get; }

    void Back()
    {
        Job.Stop();
        Main.ShowHome();
    }

    void Next()
    {
        if (!CanNext) return;
        // (a game whose .nup moved, Play found: the .nup picked is pointed at in seconds, longshot-builder index, rather than the game
        // built again. Only another .nup than the one it was built from goes on to the build: RepointViewModel)
        if (Job.MovedNup != null && Job.Replaces is { } entry && Job.Builder is LongshotBuilder builder && picked?.File.Source is BuildSource.Nup nup)
        {
            Main.Modal = new RepointViewModel(this, builder, entry, nup.Path);
            return;
        }
        ToSettings();
    }

    /// Step 2, the build's settings
    public void ToSettings() => Main.Navigate(Job.Settings ?? new SettingsStepViewModel(Job));
}

/// Over step 1, for a game whose .nup moved (BuildJob.MovedNup): the .nup picked where it is now pointed at by longshot-builder index
/// (LongshotBuilder.RepointAsync: seconds, the game folder's record and romfs index, then the row and the game-file setting), not
/// the game built again. Cancel stops it. Another .nup than the one the game was built from (not_the_same_nup) goes on to the
/// build; any other stop says why, with Rebuild instead
public sealed class RepointViewModel : ViewModelBase
{
    readonly FilesStepViewModel step;
    readonly CancellationTokenSource cancel = new();
    string? problem;

    /// start: run index now (the screenshots show it without)
    public RepointViewModel(FilesStepViewModel step, LongshotBuilder builder, BuildEntry entry, string nup, bool start = true)
    {
        this.step = step;
        Nup = nup;
        CancelCommand = new Command(Cancel);
        RebuildCommand = new Command(() => { Main.Modal = null; step.ToSettings(); });
        if (start) _ = RunAsync(builder, entry);
    }

    MainViewModel Main => step.Job.Main;
    public string Nup { get; }
    public string Sub => Tr.F("From {0}. This takes a few seconds.", ShownPath.Of(Nup));

    public string? Problem
    {
        get => problem;
        set
        {
            if (Set(ref problem, value)) RaiseAll(nameof(HasProblem), nameof(Working));
        }
    }

    public bool HasProblem => problem != null;
    public bool Working => problem == null;
    public Command CancelCommand { get; }
    public Command RebuildCommand { get; }

    void Cancel()
    {
        try { cancel.Cancel(); }
        catch (ObjectDisposedException) { }
        if (Main.Modal == this) Main.Modal = null;
    }

    async Task RunAsync(LongshotBuilder builder, BuildEntry entry)
    {
        var s = step.Job.Service;
        IndexResult result;
        // (on the window's thread: the row and the setting change there once index is done, RepointAsync)
        try { result = await builder.RepointAsync(entry, Nup, s.Registry, s.Settings, s.Home.Settings, s.Work, cancel.Token); }
        catch (OperationCanceledException)
        {
            LauncherLog.Add($"moved .nup: pointing {entry.Folder} at {Nup} cancelled");
            return;
        }
        LauncherLog.Add($"moved .nup: {entry.Folder} pointed at {Nup}: {result.Outcome}{(result.Code is { } c ? " (" + c + ")" : "")}" +
                        (result.Text is { } t ? ": " + t : ""));
        if (cancel.IsCancellationRequested) return;
        switch (result.Outcome)
        {
            case IndexOutcome.Done:
                step.Job.Stop();
                Main.ShowHome();
                Main.ShowToast(result.Text ?? Tr.F("The game now reads its files from {0}.", ShownPath.Of(Nup)));
                break;
            case IndexOutcome.NotTheSameNup:
                Main.Modal = null;
                step.ToSettings();
                Main.ShowToast(Tr.T("This .nup is not the one the game was built from, so the game is built again from it."));
                break;
            default:
                Problem = Tr.F("The game could not be pointed at this .nup: {0}", result.Text);
                break;
        }
    }
}
