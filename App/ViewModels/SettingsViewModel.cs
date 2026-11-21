using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The launcher's settings: the channel new builds start on, where builds go, the build files, the update check. Each version
/// keeps its own build settings (step 2)
public sealed class SettingsViewModel : ViewModelBase, ILeavingPage
{
    readonly LauncherSettings s;
    long? workBytes, cacheBytes;
    bool measuring;

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
        ChangeBuildsCommand = new Command(() => _ = ChangeBuildsAsync());
        OpenBuildsCommand = new Command(() => Main.Host?.Open(Main.Service.BuildsFolder), () => Directory.Exists(Main.Service.BuildsFolder));
        ChangeWorkCommand = new Command(() => _ = ChangeWorkAsync());
        DefaultWorkCommand = new Command(() => { s.Work = null; Save(); RaiseAll(nameof(Work)); MeasureWork(); });
        StorageCommand = new Command(() =>
        {
            Save();
            Main.ShowStorage();
        });
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
            RaiseAll(nameof(OpenBuildsTip));
            OpenBuildsCommand.Changed();
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

    public string WorkNote => measuring ? Tr.T("Measuring the build files…")
        : workBytes is long b && b > 0 ? Tr.F("{0} kept to make updates quicker.", Durations.Size(b)) : Tr.T("Nothing kept yet.");

    /// What is kept and why, under the folder: the build files here, the shader caches in each version's folder
    /// The builds folder: what lives in it, with a path as it would be (nightly\nightly-171-…), in this system's separator
    public string BuildsHint => Tr.F("Your instances live here, one folder per track and version ({0}). An update or a go-back renames its folder to its new version.",
                                     Path.Combine(Versions.Nightly, "nightly-171-…"));

    public string WorkHint => Tr.Sentences(
        Tr.T("Working files Longshot makes while building, reused by every build. Not the game: they can be on another drive."),
        Tr.T("The most recent build keeps its compiled code and translation here, so the next update redoes only what changed."));

    /// Beside Manage storage: what the page shows, and the shader caches' size once measured
    public string StorageHint => Tr.Sentences(
        Tr.T("See what Longshot keeps on this PC, by what it is for, and free it one part at a time."),
        cacheBytes is long c && c > 0 ? Tr.F("Your versions' shader caches take another {0} in their own folders.", Durations.Size(c)) : "");

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

    public Command ChangeBuildsCommand { get; }

    /// The builds folder in the system's file manager; off until there is one (the first build makes it: a folder typed here is
    /// not made just to be shown, as it may be mistyped)
    public Command OpenBuildsCommand { get; }
    public string OpenBuildsTip => Directory.Exists(Main.Service.BuildsFolder) ? Tr.T("Open builds folder")
        : Tr.T("Not made yet: the first build makes it");
    public Command ChangeWorkCommand { get; }
    public Command DefaultWorkCommand { get; }
    /// The Storage page: what Longshot keeps on this PC, by what it is for, and freeing it one item at a time
    public Command StorageCommand { get; }
    public Command ReleaseNotesCommand { get; }

    /// The game folders of the builds listed, whose shader caches the hint counts: only folders make_game.py made
    internal IReadOnlyList<string> GameFolders() =>
        Main.Service.Registry.Builds.Select(b => b.Folder).Where(f => GameFolder.IsGameFolder(f, Main.Platform)).ToList();

    void MeasureWork()
    {
        var folder = Main.Service.Work;
        var games = GameFolders();
        measuring = true;
        Raise(nameof(WorkNote));
        Task.Run(() =>
        {
            var bytes = Disk.WorkBytes(folder);
            var caches = games.Sum(g => ShaderCaches.ClearableBytes(g));
            Ui.Post(() =>
            {
                workBytes = bytes;
                cacheBytes = caches;
                measuring = false;
                RaiseAll(nameof(WorkNote), nameof(WorkHint), nameof(StorageHint));
            });
        });
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

    /// Left by a title bar tab (no Done: the tabs move between the pages): what was chosen saved
    public void Left() => Save();
}
