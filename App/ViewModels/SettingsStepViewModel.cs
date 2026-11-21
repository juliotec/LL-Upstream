using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

public sealed record Choice(int? Value, string Label);

public sealed record LanguageChoice(string Code, string Name);

/// Step 2, the build settings (LauncherSettings, remembered for the next build): the saves it shares, DLSS, FSR 4 and (Windows)
/// Intel XeSS with their licences, the game folder and the space it needs, the language, and under Advanced the build's
/// memory, workers and build files
public sealed class SettingsStepViewModel : ViewModelBase, IWizardPage
{
    readonly LauncherSettings s;
    long? free;

    public SettingsStepViewModel(BuildJob job)
    {
        Job = job;
        job.Settings = this;
        s = job.Service.Settings;
        var memory = SystemInfo.MemoryGb();
        var threads = SystemInfo.Threads();
        MemoryGb = memory;
        Threads = threads;
        // (the 2.x Builder's choices: Automatic, then every 4 GB of the PC's, every CPU thread)
        MemoryChoices = new[] { new Choice(null, Tr.T("Automatic")) }
            .Concat(Enumerable.Range(1, Math.Max(1, (memory + 3) / 4)).Select(i => new Choice(i * 4, Tr.F("{0} GB", i * 4)))).ToList();
        // (Workers: Automatic keeps some threads free, at low priority; Every thread takes them all at normal priority, the fastest)
        WorkerChoices = new[] { new Choice(null, Tr.T("Automatic")), new Choice(LauncherSettings.EveryThread, Tr.T("Every thread, fastest")) }
            .Concat(Enumerable.Range(1, Math.Max(1, threads)).Select(i => new Choice(i, Tr.F("{0} at once", i)))).ToList();
        LanguageChoices = Languages.All.Select(l => new LanguageChoice(l.Code, Tr.T(l.Name))).ToList();
        BackCommand = new Command(() => job.Main.Navigate(job.Files ?? new FilesStepViewModel(job)));
        StartCommand = new Command(Start, () => !Short);
        ChangeBuildsCommand = new Command(() => _ = ChangeBuildsAsync());
        ChangeWorkCommand = new Command(() => _ = ChangeWorkAsync());
        AdvancedCommand = new Command(() => AdvancedOpen = !AdvancedOpen);
        ChooseFsrLibraryCommand = new Command(() => _ = ChooseFsrLibraryAsync());
        Measure();
        if (job.VulkanGpus is { IsCompleted: false } vulkan)
            vulkan.ContinueWith(_ => Ui.Post(RaiseGameGpu), TaskScheduler.Default);
    }

    public BuildJob Job { get; }
    MainViewModel Main => Job.Main;
    public int Step => 2;
    public bool Finished => false;
    public bool Linux => Main.Linux;
    public bool Windows => !Main.Linux;
    public int MemoryGb { get; }
    public int Threads { get; }

    void Save()
    {
        try { Job.Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- saves ----

    /// (every build uses the same saves: said once there is a build, so nobody looks for a way to move them; not for a rebuild)
    public bool ShowSharedSaves => Job.Kind != JobKind.Rebuild && Job.Service.Registry.Builds.Count > 0;

    public string SharedDesc => Tr.F("Every version uses the same saves: {0}.", SaveText.Long(Job.Service.SaveSlots(), DateTime.Now));

    // ---- upscaling ----

    // (each row judges the estimated Auto GPU, or all adapters when it is unknown)
    public bool DlssYourGpu => Job.GameGpuCards.Any(SystemInfo.DlssRuns);
    public bool FsrYourGpu => Job.GameGpuCards.Any(SystemInfo.FsrRuns);

    /// The card the game renders on does not run it (Upscalers.Unsupported): its line says so in red. The switch still turns on, and
    /// the build fetches its files all the same (for OptiScaler)
    public bool DlssUnsupported => Upscalers.Unsupported(Job.GameGpuCards, SystemInfo.DlssRuns);
    public bool FsrUnsupported => Upscalers.Unsupported(Job.GameGpuCards, SystemInfo.FsrRuns);

    /// Refresh the rows when this wizard's Vulkan probe answers.
    void RaiseGameGpu() =>
        RaiseAll(nameof(DlssYourGpu), nameof(FsrYourGpu), nameof(XessYourGpu), nameof(DlssUnsupported), nameof(FsrUnsupported),
                 nameof(DlssLine), nameof(FsrLine), nameof(ShowDlssLine), nameof(ShowFsrLine));

    public bool Dlss
    {
        get => s.Dlss;
        set
        {
            if (value == Dlss) return;
            if (value && !s.DlssLicense)
            {
                Main.Modal = new LicenceViewModel(Main, LicenceOf.Nvidia, Linux, () => { s.DlssLicense = true; s.Dlss = true; Save(); RaiseUpscalers(); },
                                                  RaiseUpscalers);
                Raise();
                return;
            }
            s.Dlss = value;
            Save();
            RaiseUpscalers();
        }
    }

    /// AMD's licence (Windows) or the Linux notice (FsrLinuxNotice), whichever this platform asks for before FSR 4 is on
    bool FsrKnown => Linux ? s.FsrLinuxNotice : s.FsrLicense;

    /// (Linux) This version's tools can build FSR 4: they carry AMD's FSR API headers (MakeGameCommand.FsrBuilds). Unknown
    /// tools (not found yet) are taken as able: the build says so in its log if they were not
    public bool FsrBuildable => Windows || Job.Tools is not { } tools || MakeGameCommand.FsrBuilds(tools, Platform.Linux);

    public bool Fsr
    {
        get => s.Fsr && FsrKnown && FsrBuildable;
        set
        {
            if (value == Fsr || (value && !FsrEnabled)) return;
            if (value && !FsrKnown)
            {
                Main.Modal = new LicenceViewModel(Main, LicenceOf.Amd, Linux, () =>
                {
                    if (Linux) s.FsrLinuxNotice = true;
                    else s.FsrLicense = true;
                    s.Fsr = true;
                    Save();
                    RaiseUpscalers();
                }, RaiseUpscalers);
                Raise();
                return;
            }
            s.Fsr = value;
            Save();
            RaiseUpscalers();
        }
    }

    /// The FSR 4 switch can be turned on: this version's tools build it
    public bool FsrEnabled => FsrBuildable;

    public bool DlssAccepted => Dlss;
    /// The line under the switch: hidden once the licence's tick shows, unless it says this PC has no card for it (in red)
    public bool ShowDlssLine => !DlssAccepted || DlssUnsupported;
    public string DlssLine
    {
        get
        {
            var snapshot = Job.VulkanSnapshot;
            return Upscalers.Unsupported(SystemInfo.GameCards(Job.Service.GpuCards, snapshot), SystemInfo.DlssRuns)
                ? (SystemInfo.EstimatedGpu(snapshot) != null ? Tr.T("The estimated Auto GPU is not a GeForce RTX card.") : Tr.T("This PC has no GeForce RTX card."))
                : Dlss ? "" : s.DlssLicense ? Tr.T("Licence accepted earlier.") : Tr.T("Shows NVIDIA's licence first.");
        }
    }
    // (Linux: nothing of AMD's is downloaded, so there is no licence to accept; the player's own library goes beside the game)
    public bool FsrAccepted => Fsr && Windows;
    public bool ShowFsrLine => !FsrAccepted || FsrUnsupported;
    public string FsrLine
    {
        get
        {
            var snapshot = Job.VulkanSnapshot;
            return Upscalers.Unsupported(SystemInfo.GameCards(Job.Service.GpuCards, snapshot), SystemInfo.FsrRuns)
                ? (SystemInfo.EstimatedGpu(snapshot) != null ? Tr.T("The estimated Auto GPU is not a Radeon RX 6000 or newer.") : Tr.T("This PC has no Radeon RX 6000 or newer."))
                : Linux
                ? (!FsrBuildable ? Tr.T("This version can't build it on Linux yet.") : Tr.T("Uses your Vulkan FSR 4 library."))
                : Fsr ? "" : s.FsrLicense ? Tr.T("Licence accepted earlier.") : Tr.T("Shows AMD's licence first.");
        }
    }

    // ---- (Windows) Intel XeSS ----

    // (every Windows build of the game has XeSS, which runs once Intel's libxess.dll is beside the game: the switch has the build
    // fetch it under Intel's licence. Intel publishes no XeSS library for Linux, so the row is Windows' alone. As DLSS and FSR 4,
    // it turns on whatever this PC's cards. Unlike theirs its line is never red: XeSS runs on other makers' cards too where their
    // Vulkan driver has what it asks for, which only the game can tell, at its start: SystemInfo.XessIntel)
    public bool XessYourGpu => Job.GameGpuCards.Any(SystemInfo.XessIntel);

    /// This version's tools fetch Intel's library (MakeGameCommand.XessBuilds). Unknown tools (not found yet) are taken as able:
    /// the build says so in its log if they were not
    public bool XessBuildable => Job.Tools is not { } tools || MakeGameCommand.XessBuilds(tools);

    public bool Xess
    {
        get => Windows && s.Xess && s.XessLicense && XessBuildable;
        set
        {
            if (value == Xess || (value && !XessEnabled)) return;
            if (value && !s.XessLicense)
            {
                Main.Modal = new LicenceViewModel(Main, LicenceOf.Intel, Linux, () => { s.XessLicense = true; s.Xess = true; Save(); RaiseUpscalers(); },
                                                  RaiseUpscalers);
                Raise();
                return;
            }
            s.Xess = value;
            Save();
            RaiseUpscalers();
        }
    }

    /// The XeSS switch can be turned on: Windows, and this version's tools fetch Intel's library
    public bool XessEnabled => Windows && XessBuildable;
    public bool XessAccepted => Xess;
    public bool ShowXessLine => !XessAccepted;
    public string XessLine => !XessBuildable ? Tr.T("This version can't fetch it yet.")
        : Xess ? "" : s.XessLicense ? Tr.T("Licence accepted earlier.") : Tr.T("Shows Intel's licence first.");

    // ---- (Linux) the player's FSR 4 library ----

    /// The library row, under FSR 4 while it is on (Linux: AMD publishes no runtime there, so the library is the player's)
    public bool ShowFsrLibrary => Linux && Fsr;
    public bool FsrLibraryChosen => s.FsrLibraryPath != null;
    public string FsrLibraryText => s.FsrLibraryPath is { } p ? ShownPath.Of(p) : Tr.T("None chosen");
    public string FsrLibraryFull => ShownPath.Of(s.FsrLibraryPath);
    public string FsrLibraryDesc => s.FsrLibraryPath != null
        ? Tr.T("Copied into the game folder with each build.")
        : Tr.T("Choose your amd_fidelityfx_upscaler_vk.so, or put it beside the game yourself.");
    public Command ChooseFsrLibraryCommand { get; }

    async Task ChooseFsrLibraryAsync()
    {
        if (Main.Host is not { } host) return;
        var picked = await host.PickFileAsync(Tr.T("Your Vulkan FSR 4 library"), new[] { "*.so", "*.so.*" });
        if (picked == null) return;
        if (FsrLibrary.Check(picked) is { } why)
        {
            Main.ShowToast(why);
            return;
        }
        s.FsrLibraryPath = Path.GetFullPath(picked);
        Save();
        RaiseAll(nameof(FsrLibraryChosen), nameof(FsrLibraryText), nameof(FsrLibraryFull), nameof(FsrLibraryDesc));
    }

    // ---- (Windows, Intel graphics) Intel shader compiles ----

    /// Shown where it applies: Windows with Intel graphics (LauncherService.IntelCompilesApply)
    public bool ShowIntelCompiles => Job.Service.IntelCompilesApply;

    /// (in the game's order and numbers, GamePlay.IntelCompilesAuto...: Auto leaves the game's own row to choose)
    public IReadOnlyList<Choice> IntelCompileChoices { get; } = new[]
    {
        new Choice(GamePlay.IntelCompilesAuto, Tr.T("Auto")), new Choice(GamePlay.IntelCompilesSideBySide, Tr.T("Side by side")),
        new Choice(GamePlay.IntelCompilesOneAtATime, Tr.T("One at a time")),
    };

    public Choice IntelCompiles
    {
        get => IntelCompileChoices.FirstOrDefault(c => c.Value == s.IntelCompiles) ?? IntelCompileChoices[0];
        set
        {
            if (value?.Value is not int v || v == s.IntelCompiles) return;
            s.IntelCompiles = v;
            Save();
            RaiseAll(nameof(IntelCompiles), nameof(IntelCompilesDesc));
        }
    }

    // (Longshot's README, Intel laptops: the driver faulted or froze the PC with two compiles at once, issue 496; Auto is the
    // game's intel_compiles.h, side by side where it was measured. Side by side and One at a time fix the game's own row)
    public string IntelCompilesDesc => s.IntelCompiles switch
    {
        GamePlay.IntelCompilesSideBySide =>
            Tr.T("Faster shader builds, but Intel's driver can crash or freeze the PC. For the build and the game, over its own setting."),
        GamePlay.IntelCompilesOneAtATime =>
            Tr.T("Slower, but safe for Intel's driver. For the build and the game, over its own setting."),
        _ => Tr.T("Recommended: the game's own choice, side by side on the Arc B-series and one at a time on other Intel graphics. "
                  + "Changeable in the game."),
    };

    public IReadOnlyList<Choice> IntelLimitChoices { get; } = new[] { new Choice(0, Tr.T("Normal")), new Choice(1, Tr.T("Longer")) };

    public Choice IntelLimits
    {
        get => IntelLimitChoices[s.IntelLongerLimits ? 1 : 0];
        set
        {
            if (value == null || (value.Value == 1) == s.IntelLongerLimits) return;
            s.IntelLongerLimits = value.Value == 1;
            Save();
            RaiseAll(nameof(IntelLimits), nameof(IntelLimitsDesc));
        }
    }

    // (GamePlay.IntelLongerLimits: the preparation's 5 minutes and the last pipelines' 30 s)
    public string IntelLimitsDesc => s.IntelLongerLimits
        ? Tr.T("Waits up to 30 minutes on a slow driver, 3 for the last pipelines. A real hang stops later.")
        : Tr.T("Gives up after 5 minutes with no progress. Longer if the build says the GPU driver stopped responding.");

    void RaiseUpscalers() =>
        RaiseAll(nameof(Dlss), nameof(Fsr), nameof(ShowFsrLibrary), nameof(DlssAccepted), nameof(ShowDlssLine), nameof(DlssLine), nameof(FsrAccepted),
                 nameof(ShowFsrLine), nameof(FsrLine), nameof(FsrBuildable), nameof(FsrEnabled),
                 nameof(Xess), nameof(XessAccepted), nameof(ShowXessLine), nameof(XessLine), nameof(XessBuildable), nameof(XessEnabled),
                 nameof(Estimate));

    // ---- install ----

    public string InstallDir => ShownPath.Of(Job.InstallDir);
    public string InstallDirFull => ShownPath.Of(Job.InstallDir);

    /// The builds folder: Settings' own (LauncherSettings.BuildsFolder), so step 2 and Settings never disagree. Every new instance
    /// goes in it, in its track's folder and named after its version (BuildRegistry.FolderOf)
    public string BuildsFolder => ShownPath.Of(Job.Service.BuildsFolder);
    public string BuildsFolderFull => ShownPath.Of(Job.Service.BuildsFolder);

    /// Where this build goes: builds/&lt;track&gt;/&lt;version&gt;; an update or a go-back whose folder takes its new version's name when the
    /// build starts (BuildJob.RenameFrom): the old path and what follows it
    public string Destination => Job.RenameFrom is { } from
        ? Tr.Sentences(Tr.F("{0} becomes {1} when the build starts, so its folder names its version.", InBuilds(from), InBuilds(Job.InstallDir)),
                       Tr.T("Saves, settings, shader caches and mods go with it. A shortcut or Steam entry you made to its old path needs making again."))
        : Tr.F("Builds into {0}", InBuilds(Job.InstallDir));

    /// A folder as the line under the builds folder shows it: from the builds folder on (nightly/nightly-171-…), else whole
    string InBuilds(string folder) =>
        BuildRegistry.Holds(Job.Service.BuildsFolder, folder) && !BuildRegistry.SameFolder(Job.Service.BuildsFolder, folder)
            ? Path.GetRelativePath(Job.Service.BuildsFolder, folder)
            : ShownPath.Of(folder);

    /// Change: another builds folder, the same setting as Settings' (said under it), and this build's folder worked out again
    async Task ChangeBuildsAsync()
    {
        if (Main.Host is not { } host) return;
        var picked = await host.PickFolderAsync(Tr.T("Where builds go"), Job.Service.BuildsFolder);
        if (picked == null) return;
        Job.Service.Settings.BuildsFolder = Path.GetFullPath(picked);
        try { Job.Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast(Tr.F("The settings could not be saved: {0}", e.Message)); }
        Job.Place();
        RaiseAll(nameof(BuildsFolder), nameof(BuildsFolderFull), nameof(InstallDir), nameof(InstallDirFull), nameof(Destination));
        Measure();
    }

    /// The free space where the game goes (null: unknown, taken as enough)
    public long? Free
    {
        get => free;
        set
        {
            free = value;
            RaiseAll(nameof(Free), nameof(Short), nameof(MeterText), nameof(MeterFraction), nameof(Estimate), nameof(SpaceWarning), nameof(ShowEstimate));
            StartCommand.Changed();
        }
    }

    public string Drive => Disk.DriveName(Job.InstallDir);
    void Measure() => Free = Disk.FreeBytes(Job.InstallDir);

    public bool Short => free is long f && f < Disk.BuildBytes;
    public double MeterFraction => free is long f && f > 0 ? Math.Min(1, Disk.BuildBytes / (double)f) : 0;
    public string MeterText => free is not long f ? Tr.F("Needs {0} GB", Disk.BuildBytes >> 30)
        : Short ? Tr.F("Needs {0} GB, only {1} GB free on {2}", Disk.BuildBytes >> 30, f >> 30, Drive)
        : Tr.F("Needs {0} GB, {1} GB free on {2}", Disk.BuildBytes >> 30, f >> 30, Drive);

    // ---- the game ----

    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public LanguageChoice Language
    {
        get => LanguageChoices.FirstOrDefault(l => l.Code == Languages.Normalize(s.Language)) ?? LanguageChoices[0];
        set
        {
            if (value == null) return;
            s.Language = value.Code;
            Save();
            Raise();
        }
    }

    // ---- advanced ----

    public bool AdvancedOpen
    {
        get => Job.AdvancedOpen;
        set
        {
            Job.AdvancedOpen = value;
            RaiseAll(nameof(AdvancedOpen), nameof(AdvancedDesc));
        }
    }

    public IReadOnlyList<Choice> MemoryChoices { get; }
    public IReadOnlyList<Choice> WorkerChoices { get; }

    public Choice Memory
    {
        get => MemoryChoices.FirstOrDefault(c => c.Value == s.MemoryGb) ?? MemoryChoices[0];
        set
        {
            if (value == null) return;
            s.MemoryGb = value.Value;
            Save();
            RaiseAll(nameof(Memory), nameof(MemoryDesc), nameof(AdvancedDesc));
        }
    }

    public Choice Workers
    {
        get => WorkerChoices.FirstOrDefault(c => c.Value == s.BuildWorkers) ?? WorkerChoices[0];
        set
        {
            if (value == null) return;
            s.BuildWorkers = value.Value;
            Save();
            RaiseAll(nameof(Workers), nameof(WorkersDesc), nameof(AdvancedDesc));
        }
    }

    // (Automatic: the compilers take what memory is free (nx2/build.py MemoryGate); every CPU thread but those kept free
    // (SystemInfo.AutomaticWorkers), as it allows. Every setting but Every thread runs the build at low priority)
    public string MemoryDesc => s.MemoryGb == null ? Tr.F("Whatever is free of {0} GB, leaving room for your other programs.", MemoryGb)
        : Tr.T("The compilers may use this much, even if other programs need it.");
    public string WorkersDesc => s.BuildWorkers switch
    {
        null when SystemInfo.KeptFree(Threads) is int kept and > 0 =>
            Tr.F("{0} of {1} at once, as the memory allows. {2} left free, so your PC stays usable.", SystemInfo.AutomaticWorkers(Threads), Threads, kept),
        null => Tr.F("{0} of {1} at once, as the memory allows.", SystemInfo.AutomaticWorkers(Threads), Threads),
        LauncherSettings.EveryThread => Tr.F("All {0} at normal priority: the fastest build, but your PC may be slow to use until it ends.", Threads),
        _ => Tr.T("Compilers running at once. Each needs about 2 GB."),
    };
    public string Work => ShownPath.Of(Job.Service.Work);
    public string WorkFull => Job.Service.Work;

    public string AdvancedDesc => AdvancedOpen ? Tr.T("For a PC that slows down or runs out of memory while building.")
        : Job.Cold ? Tr.T("A completely cold build: nothing kept from earlier builds")
        : Tr.F("Memory {0}, {1}, build files on {2}",
               s.MemoryGb is int m ? Tr.F("{0} GB", m) : Tr.F("automatic of {0} GB", MemoryGb),
               s.BuildWorkers is int w ? Tr.P(SystemInfo.Workers(w, Threads), "1 worker", "{0} workers")
                   : Tr.F("{0} of {1} workers (automatic)", SystemInfo.AutomaticWorkers(Threads), Threads),
               Disk.DriveName(WorkFull));

    /// A completely cold build, for this build only (BuildJob.Cold)
    public bool Cold
    {
        get => Job.Cold;
        set
        {
            Job.Cold = value;
            RaiseAll(nameof(Cold), nameof(ColdDesc), nameof(AdvancedDesc));
        }
    }

    public string ColdDesc => Tr.Sentences(
        Tr.T("For a build that keeps failing, or a game that misbehaves after one."),
        Tr.T("First deletes the build files, both stores and this version's shader caches, so nothing kept from an earlier build is used."),
        Tr.T("It takes as long as a first build, and switching to your other versions builds slower afterwards. Your saves and settings stay."));

    async Task ChangeWorkAsync()
    {
        if (Main.Host is not { } host) return;
        var picked = await host.PickFolderAsync(Tr.T("Where the build files go"), WorkFull);
        if (picked == null) return;
        s.Work = Path.GetFullPath(picked);
        Save();
        RaiseAll(nameof(Work), nameof(WorkFull), nameof(AdvancedDesc));
    }

    // ---- the footer ----

    public bool ShowEstimate => !Short;
    public string SpaceWarning => Tr.F("{0} is too full for the build. Change the builds folder.", Drive);
    // (this PC's estimate from its CPU threads: BuildForecast, measured on nightly-56; the build step then corrects it as it goes)
    public string Estimate => Tr.F("{0} on this PC, shaders included",
                                   Durations.About(BuildForecast.Total(download: true, true, BuildThreads(s))));

    /// The threads a build may use: the Build workers setting's (SystemInfo.Workers: Automatic keeps some free)
    public static int BuildThreads(LauncherSettings settings) => SystemInfo.Workers(settings.BuildWorkers);
    public string StartText => Job.StartText;
    /// The start button's arrow: up for an update, not for going back to an older version
    public bool IsUpdate => Job.Kind == JobKind.Update && !Job.Older;

    public Command BackCommand { get; }
    public Command StartCommand { get; }
    public Command ChangeBuildsCommand { get; }
    public Command ChangeWorkCommand { get; }
    public Command AdvancedCommand { get; }

    void Start()
    {
        if (Short || Job.Picked == null || Job.Tools == null) return;
        // (a build replaces the game its folder has, an update's or a rebuild's always: it waits for the game to be closed, as
        // Delete does. On Windows make_game.py would build for minutes and stop at its last step, the swap, while one runs. One
        // started during the build is found there by make_game.py itself, and shown as Problems' GameRunning)
        if (GameFolder.IsBuilt(Job.InstallDir, Main.Platform) && Saves.Running(Main.Platform).Contains(Saves.Longshot.Display))
        {
            Main.ShowToast(Tr.T("Save and close Longshot first: this replaces the game in its folder."));
            return;
        }
        // (a cold build clears the shader caches, which a running game holds: ShaderCaches.Clear refuses then)
        if (Job.Cold && Saves.Running(Main.Platform).Contains(Saves.Longshot.Display))
        {
            Main.ShowToast(Tr.T("Save and close Longshot first: a completely cold build clears the shader caches."));
            return;
        }
        var file = Job.Picked;
        // (the game files are remembered for the next build)
        if (file.Folders && file.Source is BuildSource.Folders f)
        {
            s.Nup = null;
            s.Romfs = f.Romfs;
            s.Exefs = f.Exefs;
        }
        else
        {
            s.Nup = file.Path;
            s.Romfs = s.Exefs = null;
        }
        Save();
        Main.Navigate(new BuildStepViewModel(Job));
    }
}

/// Whose licence a LicenceViewModel shows: NVIDIA's for DLSS, AMD's for FSR 4 (on Linux, its notice), Intel's for XeSS
public enum LicenceOf { Nvidia, Amd, Intel }

/// NVIDIA's licence for DLSS, AMD's for FSR 4 or Intel's for XeSS, shown when the switch is turned on: Accept keeps it on
/// (and is remembered)
public sealed class LicenceViewModel : ViewModelBase
{
    readonly Action accepted, declined;

    public LicenceViewModel(MainViewModel main, LicenceOf of, bool linux, Action accepted, Action declined)
    {
        Main = main;
        Of = of;
        this.accepted = accepted;
        this.declined = declined;
        Linux = linux;
        Text = of switch
        {
            LicenceOf.Nvidia => DlssText(linux),
            LicenceOf.Intel => XessText,
            _ => linux ? FsrLinuxText : FsrText,
        };
        AcceptCommand = new Command(() => { Main.Modal = null; this.accepted(); });
        DeclineCommand = new Command(() => { Main.Modal = null; this.declined(); });
    }

    public MainViewModel Main { get; }
    public LicenceOf Of { get; }
    public bool Linux { get; }
    /// (AMD on Linux: a notice, as nothing of AMD's is downloaded there, so nothing to accept)
    bool FsrNotice => Of == LicenceOf.Amd && Linux;
    public string AcceptText => FsrNotice ? Tr.T("Turn on") : Tr.T("Accept");
    public string Title => Of switch
    {
        LicenceOf.Nvidia => Tr.T("NVIDIA's licence for DLSS"),
        LicenceOf.Intel => Tr.T("Intel's licence for XeSS"),
        _ => Linux ? Tr.T("AMD FSR 4 on Linux") : Tr.T("AMD's licence for FSR 4"),
    };
    public string Sub => FsrNotice
        ? Tr.T("The build compiles FSR 4 into the game. Nothing of AMD's comes with Longshot.")
        : Of == LicenceOf.Nvidia ? Tr.T("The build downloads NVIDIA's DLSS SDK under this licence. Nothing of NVIDIA's comes with Longshot.")
        : Of == LicenceOf.Intel ? Tr.T("The build downloads Intel's XeSS library under this licence. Nothing of Intel's comes with Longshot.")
        : Tr.T("The build downloads AMD's FSR 4 runtime under this licence. Nothing of AMD's comes with Longshot.");
    public string Text { get; }
    public Command AcceptCommand { get; }
    public Command DeclineCommand { get; }

    // (the 2.x Builders' texts: builder/LongshotBuilder/MainWindow.xaml.cs DlssLicense and FsrLicense, builder/linux DLSS_LICENSE)
    // (the licences' addresses and the files' names stay as they are in every language)
    static string DlssText(bool linux) =>
        Tr.F("NVIDIA DLSS is NVIDIA's, not Longshot's. With it on, the build downloads NVIDIA's DLSS SDK (release {0}) from " +
             "{1}, under NVIDIA's own license, the NVIDIA RTX SDKs license:", "v310.9.16", "github.com/NVIDIA/DLSS") + "\n\n" +
        "https://github.com/NVIDIA/DLSS/blob/9981c6350dc58c0ce21f024fd65e31e851c31689/LICENSE.txt\n\n" +
        Tr.F("Its files stay on this PC (in the launcher's {0} folder, and {1} in the game folder's {2} folder). " +
             "Longshot never includes or shares them: never share a game folder built with them.", "ngx-sdk",
             linux ? "libnvidia-ngx-dlss.so, libnvidia-ngx-dlssg.so" : "nvngx_dlss.dll, nvngx_dlssg.dll", "ngx");

    static string FsrText =>
        Tr.F("AMD FSR 4 is AMD's, not Longshot's. With it on, the build downloads AMD's FSR runtime for Vulkan ({0}: {1}, signed by AMD) " +
             "from {2}, each file checked against its SHA-256, under AMD's FSR SDK license:", "FidelityFX SDK v2.4.0",
             "amd_fidelityfx_loader_vk.dll, amd_fidelityfx_upscaler_vk.dll", "github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK") + "\n\n" +
        "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/blob/7539ae8b6568ad29ed78a753404351daf3b5cca7/Kits/FidelityFX/docs/license.md\n\n" +
        Tr.F("Its files stay on this PC (in the launcher's {0} folder, and the two DLLs with {1} in the game folder).", "ffx-sdk", "LICENSE-AMD-FSR-SDK.md");

    // (Longshot's scripts/fetch.py xess, scripts/xess_sdk.py before nightly-611: libxess.dll from Intel's XeSS SDK repository at
    // the commit Longshot's XeSS API headers come from, with Intel's licence and third-party notices beside it;
    // tests/test_launcher_core.py checks the commit against it)
    static string XessText =>
        Tr.F("Intel XeSS is Intel's, not Longshot's. With it on, the build downloads Intel's XeSS Super Resolution library ({0}, " +
             "signed by Intel) from {1}, each file checked against its SHA-256, under the Intel Simplified Software License:",
             "libxess.dll 2.0.2.68", "github.com/intel/xess") + "\n\n" +
        "https://github.com/intel/xess/blob/de0fb9c1c510661c571164e1418ceca8101dab69/LICENSE.txt\n\n" +
        Tr.F("Its files stay on this PC (in the launcher's {0} folder, and {1} with {2} in the game folder).", "xess-sdk",
             "libxess.dll", "LICENSE-INTEL-XESS-SDK.txt");

    // (Linux: Longshot's README, AMD FSR 4. The build compiles the Vulkan back end with AMD's MIT FSR API headers; the library
    // is the player's, loaded by the game at its start: AMD publishes its signed runtime for Windows only)
    static string FsrLinuxText =>
        Tr.F("AMD FSR 4 is AMD's, not Longshot's. On Linux the build compiles FSR 4 into the game's Vulkan renderer (a few minutes " +
             "longer), and the game runs it through a Vulkan FSR 4 library, {0}. AMD publishes its own runtime for Windows only and " +
             "Longshot can't include AMD's files, so the library is yours to provide, under its own terms.",
             "amd_fidelityfx_upscaler_vk.so") + "\n\n" +
        Tr.T("Choose it in the build settings and the launcher copies it into the game folder with each build. Without it, the " +
             "game's Upscaler row says AMD's files were not found and offers the other upscalers.");
}
