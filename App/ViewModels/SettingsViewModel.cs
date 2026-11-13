using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The launcher's settings: the channel new builds start on, where builds go, the build files, the update check. Each version
/// keeps its own build settings (step 2)
public sealed class SettingsViewModel : ViewModelBase
{
    readonly LauncherSettings s;
    long? workBytes;

    public sealed class ChannelOption(SettingsViewModel owner, string id, string name, string about, string? version) : ViewModelBase
    {
        public string Id => id;
        public string Name => name;
        public string About => about;
        public string Version => version == null ? "Not checked yet" : Versions.Shown(version);

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
        public string Name => name;

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
        FreeSpaceCommand = new Command(FreeSpace, () => workBytes > 0);
        ReleaseNotesCommand = new Command(() => Main.Host?.Open(Releases.Repo + "/releases"));
        AddToMenuCommand = new Command(AddToMenu, () => Program.Layout.File != null);
        RemoveFromMenuCommand = new Command(RemoveFromMenu);
        MeasureWork();
    }

    // ---- the launcher itself: how it is kept, and on Linux its entry in the applications menu ----

    /// Where this launcher is and how (installed, portable), under the launcher's channels
    public string LauncherHint => "The channel the launcher updates itself from. Its releases are its own, apart from Longshot's. " + Program.Layout.Kind switch
    {
        LauncherKind.Installed => "Installed for you in " + ShownPath.Of(Path.GetDirectoryName(Program.Layout.File)) + "; uninstall it from Apps & features.",
        LauncherKind.Portable => "Portable: everything it keeps is in " + ShownPath.Of(Main.Service.Home.Root) + ".",
        _ => "",
    };

    /// The AppImage's own entry in the applications menu (Linux): added, moved along when the AppImage moved, removed
    public bool ShowMenu => Main.Linux;
    string? MenuTarget => Shortcuts.LauncherInMenu();
    public bool InMenu => MenuTarget != null;
    public string AddToMenuText => MenuTarget == null ? "Add to applications menu"
        : MenuTarget != Program.Layout.File ? "Point the menu entry here" : "Write the menu entry again";
    public string MenuHint => Program.Layout.File == null ? "Only the AppImage can be added to the menu."
        : MenuTarget == null ? "Adds Longshot Launcher to your desktop's applications menu, starting this AppImage where it is now."
        : MenuTarget != Program.Layout.File ? $"The menu entry starts {ShownPath.Of(MenuTarget)}, not this AppImage."
        : "Longshot Launcher is in the applications menu. Moved the AppImage? Add it again from its new place.";
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
            Main.ShowToast("Longshot Launcher is in the applications menu.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast("The menu entry could not be written: " + e.Message); }
        RaiseAll(nameof(InMenu), nameof(AddToMenuText), nameof(MenuHint));
    }

    void RemoveFromMenu()
    {
        try
        {
            Shortcuts.RemoveLauncherFromMenu();
            Main.ShowToast("Longshot Launcher is out of the applications menu.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast("The menu entry could not be removed: " + e.Message); }
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
            _ = Main.Home.CheckReleasesAsync();
        }
    }

    void Save()
    {
        try { Main.Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast("The settings could not be saved: " + e.Message); }
    }

    public string Channel
    {
        get => s.DefaultChannel;
        set
        {
            if (value == s.DefaultChannel || !Versions.IsChannel(value)) return;
            s.DefaultChannel = value;
            Save();
            foreach (var c in Channels) c.Changed();
            Raise(nameof(ChannelHint));
        }
    }

    public string ChannelHint => (ChannelPickerViewModel.All.FirstOrDefault(c => c.Id == s.DefaultChannel).About ?? "") + " Builds you have keep their own channel.";

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

    public string WorkNote => workBytes is long b && b > 0 ? $"{Durations.Size(b)} kept to make updates quicker." : "Nothing kept yet.";

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

    public string Version => "Longshot Launcher " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "3.0.0");

    public Command DoneCommand { get; }
    public Command ChangeBuildsCommand { get; }
    public Command ChangeWorkCommand { get; }
    public Command DefaultWorkCommand { get; }
    public Command FreeSpaceCommand { get; }
    public Command ReleaseNotesCommand { get; }

    void MeasureWork()
    {
        var folder = Main.Service.Work;
        Task.Run(() =>
        {
            var bytes = Disk.WorkBytes(folder);
            Ui.Post(() =>
            {
                workBytes = bytes;
                Raise(nameof(WorkNote));
                FreeSpaceCommand.Changed();
            });
        });
    }

    /// The build files deleted: the next build or update makes them again. Only what make_game made there (Disk.WorkParts): the
    /// folder may be one the player chose, holding other things
    void FreeSpace()
    {
        var folder = Main.Service.Work;
        var freed = workBytes ?? 0;
        try
        {
            Disk.FreeWork(folder);
            Main.ShowToast($"Freed {Durations.Size(freed)} of build files. The next update makes them again.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Main.ShowToast("The build files could not all be deleted: " + e.Message);
        }
        MeasureWork();
    }

    async Task ChangeBuildsAsync()
    {
        if (Main.Host is not { } host) return;
        if (await host.PickFolderAsync("Where builds go", Main.Service.BuildsFolder) is { } picked) BuildsFolder = Path.GetFullPath(picked);
    }

    async Task ChangeWorkAsync()
    {
        if (Main.Host is not { } host) return;
        if (await host.PickFolderAsync("Where the build files go", Main.Service.Work) is { } picked) Work = Path.GetFullPath(picked);
    }

    void Done()
    {
        Save();
        Main.ShowHome();
    }
}
