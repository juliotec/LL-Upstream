using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The launcher's settings: the channel new builds start on, where builds go, the build files, the update check. Each version
/// keeps its own build settings (step 2)
public sealed class SettingsViewModel : ViewModelBase
{
    readonly LauncherSettings s;
    long? workBytes, cacheBytes;
    bool measuring;
    // (what the confirmation asked for: the shader caches too)
    bool clearCaches;

    public sealed class ChannelOption(SettingsViewModel owner, string id, string name, string about, string? version) : ViewModelBase
    {
        public string Id => id;
        public string Name => Tr.T(name);
        public string About => Tr.T(about);
        public string Version => version == null ? Tr.T("Not checked yet") : Versions.Shown(version);

        public bool IsPicked
        {
            get => owner.Channel == id;
            set
            {
                if (value) owner.Channel = id;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    /// One of the launcher's own channels (where it updates itself from)
    public sealed class LauncherChannelOption(SettingsViewModel owner, string id, string name) : ViewModelBase
    {
        public string Id => id;
        public string Name => Tr.T(name);

        public bool IsPicked
        {
            get => owner.LauncherChannel == id;
            set
            {
                if (value) owner.LauncherChannel = id;
            }
        }

        public void Changed() => Raise(nameof(IsPicked));
    }

    public SettingsViewModel(MainViewModel main)
    {
        Main = main;
        s = main.Service.Settings;
        Channels = ChannelPickerViewModel.All.Select(c => new ChannelOption(this, c.Id, c.Name, c.About, main.Service.NewestOf(c.Id))).ToList();
        LauncherChannels = ChannelPickerViewModel.All.Select(c => new LauncherChannelOption(this, c.Id, c.Name)).ToList();
        DoneCommand = new Command(Done);
        ChangeBuildsCommand = new Command(() => _ = ChangeBuildsAsync());
        ChangeWorkCommand = new Command(() => _ = ChangeWorkAsync());
        DefaultWorkCommand = new Command(() => { s.Work = null; Save(); RaiseAll(nameof(Work)); MeasureWork(); });
        FreeingCommand = new BusyCommand(FreeSpaceAsync, null, e => Main.ShowToast(Tr.F("The build files could not all be deleted: {0}", e.Message)));
        FreeSpaceCommand = new Command(() => Main.Modal = new FreeSpaceViewModel(this),
                                       () => (workBytes > 0 || cacheBytes > 0) && !measuring && !FreeingCommand.IsBusy);
        ReleaseNotesCommand = new Command(() => Main.Host?.Open(Releases.Repo + "/releases"));
        AddToMenuCommand = new Command(AddToMenu, () => Program.Layout.File != null);
        RemoveFromMenuCommand = new Command(RemoveFromMenu);
        Languages = new[] { new UiLanguageChoice(null, Tr.T("System language")) }
            .Concat(Tr.All.Select(l => new UiLanguageChoice(l.Code, l.Name))).ToList();
        MeasureWork();
    }

    // ---- the launcher's language: the system's until one is picked; a pick changes the window at once ----

    public sealed record UiLanguageChoice(string? Code, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<UiLanguageChoice> Languages { get; }

    public UiLanguageChoice Language
    {
        get => Languages.FirstOrDefault(l => l.Code == s.UiLanguage) ?? Languages[0];
        set
        {
            if (value == null || value.Code == s.UiLanguage) return;
            s.UiLanguage = value.Code;
            Save();
            Tr.Use(s.UiLanguageOr(Controls.SystemLanguage.Name()));
        }
    }

    // ---- the launcher itself: how it is kept, and on Linux its entry in the applications menu ----

    /// Where this launcher is and how (installed, portable), under the launcher's channels
    public string LauncherHint => Tr.Sentences(Tr.T("The channel the launcher updates itself from. Its releases are its own, apart from Longshot's."), Program.Layout.Kind switch
    {
        LauncherKind.Installed => Tr.F("Installed for you in {0}; uninstall it from Apps & features.", ShownPath.Of(Path.GetDirectoryName(Program.Layout.File))),
        LauncherKind.Portable => Tr.F("Portable: everything it keeps is in {0}.", ShownPath.Of(Main.Service.Home.Root)),
        _ => "",
    });

    /// The AppImage's own entry in the applications menu (Linux): added, moved along when the AppImage moved, removed
    public bool ShowMenu => Main.Linux;
    string? MenuTarget => Shortcuts.LauncherInMenu();
    public bool InMenu => MenuTarget != null;
    public string AddToMenuText => MenuTarget == null ? Tr.T("Add to applications menu")
        : MenuTarget != Program.Layout.File ? Tr.T("Point the menu entry here") : Tr.T("Write the menu entry again");
    public string MenuHint => Program.Layout.File == null ? Tr.T("Only the AppImage can be added to the menu.")
        : MenuTarget == null ? Tr.T("Adds Longshot Launcher to your desktop's applications menu, starting this AppImage where it is now.")
        : MenuTarget != Program.Layout.File ? Tr.F("The menu entry starts {0}, not this AppImage.", ShownPath.Of(MenuTarget))
        : Tr.T("Longshot Launcher is in the applications menu. Moved the AppImage? Add it again from its new place.");
    public Command AddToMenuCommand { get; }
    public Command RemoveFromMenuCommand { get; }

    void AddToMenu()
    {
        if (Program.Layout.File is not { } appImage) return;
        try
        {
            using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://LongshotLauncher/Assets/longshot.png"));
            using var bytes = new MemoryStream();
            icon.CopyTo(bytes);
            Shortcuts.AddLauncherToMenu(appImage, bytes.ToArray());
            Main.ShowToast(Tr.T("Longshot Launcher is in the applications menu."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast(Tr.F("The menu entry could not be written: {0}", e.Message)); }
        RaiseAll(nameof(InMenu), nameof(AddToMenuText), nameof(MenuHint));
    }

    void RemoveFromMenu()
    {
        try
        {
            Shortcuts.RemoveLauncherFromMenu();
            Main.ShowToast(Tr.T("Longshot Launcher is out of the applications menu."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast(Tr.F("The menu entry could not be removed: {0}", e.Message)); }
        RaiseAll(nameof(InMenu), nameof(AddToMenuText), nameof(MenuHint));
    }

    public MainViewModel Main { get; }
    public IReadOnlyList<ChannelOption> Channels { get; }
    public IReadOnlyList<LauncherChannelOption> LauncherChannels { get; }

    /// The channel the launcher updates itself from; a change is checked at once
    public string LauncherChannel
    {
        get => Main.Service.LauncherChannel;
        set
        {
            if (value == Main.Service.LauncherChannel || !Versions.IsChannel(value)) return;
            s.LauncherChannel = value;
            Save();
            foreach (var c in LauncherChannels) c.Changed();
            // (new builds follow the launcher's channel while none was picked for them)
            foreach (var c in Channels) c.Changed();
            Raise(nameof(ChannelHint));
            _ = Main.Home.CheckReleasesAsync();
        }
    }

    void Save()
    {
        try { Main.Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast(Tr.F("The settings could not be saved: {0}", e.Message)); }
    }

    public string Channel
    {
        get => Main.Service.DefaultChannel;
        set
        {
            if (value == s.DefaultChannel || !Versions.IsChannel(value)) return;
            s.DefaultChannel = value;
            Save();
            foreach (var c in Channels) c.Changed();
            Raise(nameof(ChannelHint));
        }
    }

    public string ChannelHint =>
        Tr.Sentences(ChannelPickerViewModel.AboutOf(Main.Service, Channel, ChannelPickerViewModel.All.FirstOrDefault(c => c.Id == Channel).About ?? ""),
                     Tr.T("Builds you have keep their own channel."));

    public string BuildsFolder
    {
        get => ShownPath.Of(Main.Service.BuildsFolder);
        set
        {
            s.BuildsFolder = string.IsNullOrWhiteSpace(value) ? null : ShownPath.Typed(value.Trim());
            Save();
            Raise();
        }
    }

    public string Work
    {
        get => ShownPath.Of(Main.Service.Work);
        set
        {
            s.Work = string.IsNullOrWhiteSpace(value) ? null : ShownPath.Typed(value.Trim());
            Save();
            Raise();
            MeasureWork();
        }
    }

    public string WorkNote => FreeingCommand.IsBusy ? Tr.T("Deleting the build files…")
        : measuring ? Tr.T("Measuring the build files…")
        : workBytes is long b && b > 0 ? Tr.F("{0} kept to make updates quicker.", Durations.Size(b)) : Tr.T("Nothing kept yet.");

    /// What is kept and why, under the folder: the build files here, the shader caches in each version's folder
    public string WorkHint => Tr.Sentences(
        Tr.T("Builds keep their compiled code and translations here, so an update, a rebuild or going back to a version you built before is quicker."),
        cacheBytes is long c && c > 0 ? Tr.F("Your versions' shader caches take another {0} in their own folders.", Durations.Size(c)) : "",
        Tr.T("Free up space lists what it deletes first."));

    /// The update check that a change of the launcher's channel starts (HomeViewModel.CheckCommand): a spinner beside the channels
    public BusyCommand CheckCommand => Main.Home.CheckCommand;

    public bool CheckUpdates
    {
        get => s.CheckUpdates;
        set
        {
            s.CheckUpdates = value;
            Save();
            Raise();
        }
    }

    /// An update's build tools downloaded as soon as it's out (LauncherSettings.DownloadUpdatesEarly); the update still waits for the
    /// player. Taking effect from the next update check
    public bool DownloadUpdatesEarly
    {
        get => s.DownloadUpdatesEarly;
        set
        {
            s.DownloadUpdatesEarly = value;
            Save();
            Raise();
        }
    }

    /// The player's answer for every build; each folder takes it before its game next starts (PerfReports.BeforePlay)
    public bool SendPerformanceStatistics
    {
        get => s.SendPerformanceStatistics;
        set
        {
            s.SendPerformanceStatistics = value;
            Save();
            Raise();
        }
    }

    /// Keep a copy of what was sent, in each game folder's perf_sent/ (PerfReports.BeforePlay writes it with the tick)
    public bool KeepSentPerformanceStatistics
    {
        get => s.KeepSentPerformanceStatistics;
        set
        {
            s.KeepSentPerformanceStatistics = value;
            Save();
            Raise();
        }
    }

    /// Send crash reports automatically, without the game's crash window; each folder takes it before its game next starts
    /// (CrashReports.BeforePlay)
    public bool SendCrashReportsAutomatically
    {
        get => s.SendCrashReportsAutomatically;
        set
        {
            s.SendCrashReportsAutomatically = value;
            Save();
            Raise();
        }
    }

    public string Version => "Longshot Launcher " + Versions.Shown(Main.Service.OwnTag);

    public Command DoneCommand { get; }
    public Command ChangeBuildsCommand { get; }
    public Command ChangeWorkCommand { get; }
    public Command DefaultWorkCommand { get; }
    /// Opens the confirmation (FreeSpaceViewModel), which lists what goes
    public Command FreeSpaceCommand { get; }
    /// The deleting itself, once confirmed: its spinner on the link
    public BusyCommand FreeingCommand { get; }
    public Command ReleaseNotesCommand { get; }

    /// The game folders of the builds listed, whose shader caches Free up space may clear: only folders make_game.py made
    internal IReadOnlyList<string> GameFolders() =>
        Main.Service.Registry.Builds.Select(b => b.Folder).Where(f => GameFolder.IsGameFolder(f, Main.Platform)).ToList();

    void MeasureWork()
    {
        var folder = Main.Service.Work;
        var games = GameFolders();
        measuring = true;
        Raise(nameof(WorkNote));
        FreeSpaceCommand.Changed();
        Task.Run(() =>
        {
            var bytes = Disk.WorkBytes(folder);
            var caches = games.Sum(g => ShaderCaches.ClearableBytes(g));
            Ui.Post(() =>
            {
                workBytes = bytes;
                cacheBytes = caches;
                measuring = false;
                RaiseAll(nameof(WorkNote), nameof(WorkHint));
                FreeSpaceCommand.Changed();
            });
        });
    }

    /// Free up space, as confirmed: caches, the shader caches of every version too
    internal void Free(bool caches)
    {
        clearCaches = caches;
        FreeingCommand.Execute(null);
    }

    /// The build files deleted: the next build or update makes them again. Only what make_game made there (Disk.WorkParts): the
    /// folder may be one the player chose, holding other things. With clearCaches, each version's shader caches too
    /// (ShaderCaches.Clear: never while Longshot runs, and never the pipeline notes the build shipped). Gigabytes: off the
    /// window's thread, the link off meanwhile
    async Task FreeSpaceAsync(BusyCommand busy, CancellationToken cancel)
    {
        var folder = Main.Service.Work;
        var games = clearCaches ? GameFolders() : Array.Empty<string>();
        var freed = workBytes ?? 0;
        Raise(nameof(WorkNote));
        FreeSpaceCommand.Changed();
        try
        {
            var running = false;
            var caches = await Task.Run(() =>
            {
                Disk.FreeWork(folder);
                long sum = 0;
                foreach (var g in games)
                    if (ShaderCaches.Clear(g) is long b) sum += b;
                    else running = true;
                return sum;
            });
            Main.ShowToast(running ? Tr.F("Freed {0} of build files. Longshot is running, so the shader caches stay.", Durations.Size(freed))
                : caches > 0 ? Tr.F("Freed {0}: the build files and the shader caches. The next build makes the build files again, and each version prepares its shaders again when it starts.", Durations.Size(freed + caches))
                : Tr.F("Freed {0} of build files. The next update makes them again.", Durations.Size(freed)));
        }
        finally
        {
            MeasureWork();
        }
    }

    async Task ChangeBuildsAsync()
    {
        if (Main.Host is not { } host) return;
        if (await host.PickFolderAsync(Tr.T("Where builds go"), Main.Service.BuildsFolder) is { } picked) BuildsFolder = Path.GetFullPath(picked);
    }

    async Task ChangeWorkAsync()
    {
        if (Main.Host is not { } host) return;
        if (await host.PickFolderAsync(Tr.T("Where the build files go"), Main.Service.Work) is { } picked) Work = Path.GetFullPath(picked);
    }

    void Done()
    {
        Save();
        Main.ShowHome();
    }
}

/// Free up space, confirmed first: each part of the build files it deletes with its size and what it was kept for, what the next
/// build then costs, and the shader caches as an option (unticked: preparing them again takes minutes at each version's next start)
public sealed class FreeSpaceViewModel : ViewModelBase
{
    readonly SettingsViewModel settings;
    IReadOnlyList<long>? groups;
    long caches;
    int versions;
    bool clearCaches;

    public sealed record Part(string Name, string About, string Size);

    /// measured: the sizes as the screenshots show them (Disk.WorkGroups' bytes, the caches', the versions with caches), no disk
    /// read; null: measured here, off the window's thread
    public FreeSpaceViewModel(SettingsViewModel settings, (IReadOnlyList<long> Groups, long Caches, int Versions)? measured = null)
    {
        this.settings = settings;
        // (the screenshots: not this PC's games)
        Running = measured == null && Saves.Running(settings.Main.Platform).Contains(Saves.Longshot.Display);
        KeepCommand = new Command(() => settings.Main.Modal = null);
        DeleteCommand = new Command(Delete, () => groups != null && (WorkTotal > 0 || clearCaches && caches > 0));
        if (measured is { } m)
        {
            (groups, caches, versions) = m;
            return;
        }
        var work = settings.Main.Service.Work;
        var games = settings.GameFolders();
        Task.Run(() =>
        {
            var measured = Disk.GroupBytes(work);
            var withCaches = games.Select(g => ShaderCaches.ClearableBytes(g)).ToList();
            Ui.Post(() =>
            {
                groups = measured;
                caches = withCaches.Sum();
                versions = withCaches.Count(b => b > 0);
                RaiseAll(nameof(Measuring), nameof(Parts), nameof(HasParts), nameof(Total), nameof(CacheText), nameof(HasCaches), nameof(CanClearCaches));
                DeleteCommand.Changed();
            });
        });
    }

    public bool Measuring => groups == null;
    long WorkTotal => groups?.Sum() ?? 0;

    /// The parts that hold something, in Disk.WorkGroups' order
    public IReadOnlyList<Part> Parts => groups == null ? Array.Empty<Part>()
        : Disk.WorkGroups.Select((g, i) => (g, b: groups[i])).Where(p => p.b > 0)
              .Select(p => new Part(Tr.T(p.g.Name), Tr.T(p.g.About), Durations.Size(p.b))).ToList();
    public bool HasParts => WorkTotal > 0;
    public string Total => groups == null ? "" : WorkTotal > 0
        ? Tr.F("{0} in all. The next build or update makes them again, so it takes longer: up to as long as a first build.", Durations.Size(WorkTotal))
        : Tr.T("No build files to delete.");

    /// Longshot runs: it holds its shader caches, so they cannot be cleared now (ShaderCaches.Clear refuses too)
    public bool Running { get; }
    public bool HasCaches => caches > 0;
    public bool CanClearCaches => HasCaches && !Running;
    public string CacheText => Tr.P(versions, "Also clear the shader caches of your version ({1})", "Also clear the shader caches of your {0} versions ({1})",
                                    Durations.Size(caches));
    public string CacheHint => Running ? Tr.T("Close Longshot to clear the shader caches: it holds them while it runs.")
        : Tr.T("Each version then prepares its shaders again when it next starts: several minutes, and it may stutter at first.");

    public bool ClearCaches
    {
        get => clearCaches;
        set
        {
            if (!Set(ref clearCaches, value && CanClearCaches)) return;
            DeleteCommand.Changed();
        }
    }

    public Command KeepCommand { get; }
    public Command DeleteCommand { get; }

    void Delete()
    {
        settings.Main.Modal = null;
        settings.Free(clearCaches && CanClearCaches);
    }
}
