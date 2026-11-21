using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// A page of the build wizard: its step in the title bar's pill (1 game files, 2 settings, 3 build), and whether step 3 is done
public interface IWizardPage
{
    int Step { get; }
    bool Finished { get; }
}

/// One step of the title bar's pill
public sealed class StepItem(int number, string title) : ViewModelBase
{
    bool done, now;

    public int Number => number;
    public string Title => Tr.T(title);
    public bool IsDone { get => done; set { if (Set(ref done, value)) Raise(nameof(IsTodo)); } }
    public bool IsNow { get => now; set { if (Set(ref now, value)) Raise(nameof(IsTodo)); } }
    public bool IsTodo => !done && !now;
}

/// A page that does something when it is left, however it is (a title bar tab, Back, a controller's B): Settings saves,
/// the NFC page stops its scan
public interface ILeavingPage
{
    void Left();
}

/// The window: one page at a time (home, the wizard's steps, settings, saves, NFC), a dialog over it, a toast at the bottom
public sealed class MainViewModel : ViewModelBase
{
    object page;
    object? modal;
    string? toast;
    int toastId;
    bool dropping;

    public MainViewModel(LauncherService service, StartRequest start)
    {
        Service = service;
        Home = new HomeViewModel(this);
        page = Home;
        Home.Open(start);
        MinimizeCommand = new Command(() => Host?.Minimize());
        CloseCommand = new Command(() => Host?.Close());
        // (the title bar's tabs: the one shown does nothing)
        GoHomeCommand = new Command(() => { if (!IsHome) ShowHome(); });
        GoSavesCommand = new Command(() => { if (Shown != Section.Saves) ShowSaves(); });
        GoNfcCommand = new Command(() => { if (Shown != Section.Nfc) ShowNfc(); });
        StorageCommand = new Command(() => { if (Shown != Section.Storage) ShowStorage(); });
        SettingsCommand = new Command(() => { if (Shown != Section.Settings) ShowSettings(); });
        if (start.Kind == StartKind.Window) OfferCrash();
    }

    /// How long a crash stays news: an older log is not offered (a build last played weeks ago, the launcher not opened since)
    static readonly TimeSpan CrashNews = TimeSpan.FromDays(7);

    /// A build whose last run closed unexpectedly (launcher/Core/Problems.cs CrashCheck): its problem page, once per log. Not at
    /// the first start with the check (the logs already there are old news), and not while a game runs (its log is the live run's)
    void OfferCrash()
    {
        var s = Service.Settings;
        var now = DateTime.UtcNow.Ticks;
        if (s.CrashChecksSince is not { } since)
        {
            s.CrashChecksSince = now;
            SaveSettingsQuietly();
            return;
        }
        try
        {
            if (Saves.Running(Service.Platform).Contains(Saves.Longshot.Display)) return;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return; }
        var notBefore = Math.Max(since, now - CrashNews.Ticks);
        // (the log names the GPU the game ran on; this is for a log without that line: only one maker here, else none guessed)
        var vendor = Service.Gpus.Count == 1 ? Service.Gpus[0] : null;
        foreach (var b in Service.Registry.Builds)
        {
            var found = CrashCheck.Find(b.Folder, s.SeenCrashLogs.GetValueOrDefault(b.Folder), notBefore, vendor);
            if (found == null) continue;
            s.SeenCrashLogs[b.Folder] = found.WriteTicks;
            SaveSettingsQuietly();
            page = new ProblemPageViewModel(this, found.Problem, ShowHome, crashed: true);
            return;
        }
    }

    void SaveSettingsQuietly()
    {
        try { Service.SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public LauncherService Service { get; }
    /// The launcher's own version, small beside its name in the title bar
    public string LauncherVersion => Versions.Shown(Service.OwnTag);
    public HomeViewModel Home { get; }
    public IWindowHost? Host { get; set; }
    public Platform Platform => Service.Platform;
    public bool Linux => Service.Platform == Platform.Linux;

    public Command MinimizeCommand { get; }
    public Command CloseCommand { get; }
    /// The title bar's tabs, in their order: Home, Saves, NFC tags, Storage, Settings
    public Command GoHomeCommand { get; }
    public Command GoSavesCommand { get; }
    public Command GoNfcCommand { get; }
    public Command StorageCommand { get; }
    public Command SettingsCommand { get; }

    /// The pages the title bar's tabs move between; None: another page (the wizard, a build, a transfer's steps), which keeps
    /// its own way back and shows no tabs
    public enum Section { None, Home, Saves, Nfc, Storage, Settings }

    public Section Shown => page switch
    {
        HomeViewModel => Section.Home,
        SavesViewModel => Section.Saves,
        NfcViewModel => Section.Nfc,
        StorageViewModel => Section.Storage,
        SettingsViewModel => Section.Settings,
        _ => Section.None,
    };

    /// The tabs, on their pages only; the one shown lit
    public bool ShowsTabs => Shown != Section.None;
    public bool IsSavesShown => Shown == Section.Saves;
    public bool IsNfcShown => Shown == Section.Nfc;
    public bool IsStorageShown => Shown == Section.Storage;
    public bool IsSettingsShown => Shown == Section.Settings;

    public IReadOnlyList<StepItem> Steps { get; } = new[] { new StepItem(1, Tr.N("Game files")), new StepItem(2, Tr.N("Settings")), new StepItem(3, Tr.N("Build")) };

    public object Page
    {
        get => page;
        private set
        {
            var was = page;
            if (!Set(ref page, value)) return;
            // (however it is left: a tab, Back, a controller's B)
            if (was is ILeavingPage left) left.Left();
            RefreshSteps();
            RaiseAll(nameof(IsHome), nameof(InWizard), nameof(AcceptsDrop), nameof(DropText), nameof(Shown), nameof(ShowsTabs),
                     nameof(IsSavesShown), nameof(IsNfcShown), nameof(IsStorageShown), nameof(IsSettingsShown));
        }
    }

    public void RefreshSteps()
    {
        var w = page as IWizardPage;
        foreach (var s in Steps)
        {
            s.IsDone = w != null && (s.Number < w.Step || (s.Number == 3 && w.Finished));
            s.IsNow = w != null && s.Number == w.Step && !(s.Number == 3 && w.Finished);
        }
    }

    public bool IsHome => page is HomeViewModel;
    public bool InWizard => page is IWizardPage;

    public object? Modal
    {
        get => modal;
        set
        {
            if (Set(ref modal, value)) Raise(nameof(HasModal));
        }
    }

    public bool HasModal => modal != null;

    public string? Toast
    {
        get => toast;
        private set
        {
            if (Set(ref toast, value)) Raise(nameof(HasToast));
        }
    }

    public bool HasToast => toast != null;

    /// A short line at the bottom of the window, for a few seconds
    public void ShowToast(string text)
    {
        var id = ++toastId;
        Toast = text;
        _ = Task.Delay(2800).ContinueWith(_ => Ui.Post(() => { if (toastId == id) Toast = null; }), TaskScheduler.Default);
    }

    // ---- Copy for a bug report ----

    /// A bug report (BugReport): what happened, as the screen said it, this launcher and PC, what the screen adds (more), and the
    /// launcher's log of this run
    public string Report(string what, IEnumerable<string>? more = null)
    {
        var layout = Program.Layout.Kind switch { LauncherKind.Installed => "installed", LauncherKind.Portable => "portable", _ => "not installed" };
        var facts = new ReportFacts(
            $"Longshot Launcher {Service.OwnTag} ({layout}), launcher channel {Service.LauncherChannel}, builds from {Service.DefaultChannel}",
            BugReport.System(),
            BugReport.Network(Service.Fetcher is CdnFetcher { Enabled: true }, HttpFetcher.ProxyFor(new Uri(Product.Server))));
        return BugReport.Make(facts, what, more);
    }

    /// The report on the clipboard, and a toast saying what it holds
    public async Task CopyReportAsync(string what, IEnumerable<string>? more = null)
    {
        if (Host is { } host) await host.CopyAsync(Report(what, more));
        ShowToast(Tr.T("Copied for a bug report: what happened, the launcher's version, this PC's system and the launcher's log."));
    }

    /// Copy for a bug report, for a screen that shows a failure: what() is what it says now, more() what it adds (null for nothing)
    public Command CopyReportCommand(Func<string> what, Func<IEnumerable<string>>? more = null) =>
        new(() => _ = CopyReportAsync(what(), more?.Invoke()));

    /// A file dragged over the window in step 1 or on the NFC page: DropText over the page
    public bool Dropping
    {
        get => dropping;
        set => Set(ref dropping, value);
    }

    /// The pages that take files dropped on the window: step 1 (the game's files) and NFC (tag dumps, or folders of them), but not
    /// while it is still adding the last ones (NfcViewModel.Adding): it would not take them
    public bool AcceptsDrop => page is FilesStepViewModel or NfcViewModel { Adding: false };
    /// The NFC page started or finished adding: AcceptsDrop changed
    public void RaiseAcceptsDrop() => Raise(nameof(AcceptsDrop));
    public string DropText => page is NfcViewModel ? Tr.T("Drop NFC tag dumps, or folders of them, to add them") : Tr.T("Drop your game file to check it");

    public void Drop(IReadOnlyList<string> paths)
    {
        Dropping = false;
        if (page is FilesStepViewModel files) files.Dropped(paths);
        else if (page is NfcViewModel nfc && paths.Count > 0) nfc.AddDropped(paths);
    }

    public void Navigate(object to)
    {
        Modal = null;
        Page = to;
    }

    public void ShowHome()
    {
        Home.Refresh();
        Navigate(Home);
    }

    /// The window active again: on the home, a build whose folder was deleted outside the launcher meanwhile leaves the list
    /// (HomeViewModel.Refresh); nothing is redone when every folder is still there
    public void HomeAgain()
    {
        if (page == Home && Modal == null && Service.Registry.Builds.Any(b => BuildRegistry.Gone(b.Folder))) Home.Refresh();
    }

    public void ShowSettings() => Navigate(new SettingsViewModel(this));

    /// The Storage page's last measure, kept while the launcher is open so that coming back to the page shows it at once (its
    /// refresh measures again); forgotten after a build, and not used once the places it measured changed (StorageViewModel)
    StorageViewModel.Snapshot? storageMeasured;
    public long StorageGeneration { get; set; }
    public StorageViewModel.Snapshot? StorageMeasured
    {
        get => storageMeasured;
        set { storageMeasured = value; if (value == null) StorageGeneration++; }
    }

    /// What Longshot keeps on this PC, by what it is for, and freeing it: the title bar's Storage tab, or Settings > Manage storage
    public void ShowStorage() => Navigate(new StorageViewModel(this));

    public void ShowSaves() => Navigate(new SavesViewModel(this));

    public void ShowNfc() => Navigate(new NfcViewModel(this));

    /// The wizard for a version: a new build, building one you have again, or an update of a build (replaces)
    public FilesStepViewModel StartWizard(JobKind kind, string channel, string version, BuildEntry? replaces = null, bool tools = true)
    {
        var job = new BuildJob(this, kind, channel, version, replaces);
        var step = new FilesStepViewModel(job, tools);
        Navigate(step);
        return step;
    }
}
