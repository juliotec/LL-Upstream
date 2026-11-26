using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// Storage's "Remove everything…" (#200): what goes and what stays, with their sizes from the page's last measure, a separate
/// tick for the saves (off: they stay), then everything Longshot put on this PC removed (Core/RemoveEverything.cs), the shortcuts
/// it made too, and on Windows the installed launcher itself. Then the launcher closes: its own data is gone
public sealed class RemoveEverythingViewModel : ViewModelBase
{
    readonly IReadOnlyList<Storage.Item> items;
    readonly bool? shownRunning;
    bool saves, removing;
    RemoveEverything.Done? done;
    string? problem;
    /// Steam's tile was taken away while Steam runs: it shows until Steam starts again
    bool steamWaits;

    /// items: the Storage page's measure (all drives); running: the screenshots' (no look at what runs)
    public RemoveEverythingViewModel(MainViewModel main, IReadOnlyList<Storage.Item> items, bool? running = null)
    {
        Main = main;
        this.items = items;
        shownRunning = running;
        KeepCommand = new Command(() => Main.Modal = null, () => !removing);
        RemoveCommand = new Command(() => _ = RemoveAsync(), () => !removing && done == null && WhyNot == null);
        CloseCommand = new Command(() => Main.Host?.Close());
    }

    public MainViewModel Main { get; }

    /// The saves, their backups, the game's settings and the NFC tags removed too: off unless the player ticks it
    public bool Saves
    {
        get => saves;
        set
        {
            saves = value;
            RaiseAll(nameof(Saves), nameof(Lines), nameof(TotalText));
        }
    }

    public sealed record LineRow(string Name, string SizeText, string State, bool Goes);

    public IReadOnlyList<LineRow> Lines => RemoveEverything.Summary(items, saves)
        .Select(s => new LineRow(Tr.T(RemoveEverything.Names[s.Line]), Durations.Size(s.Bytes), s.Goes ? Tr.T("Removed") : Tr.T("Kept"), s.Goes))
        .ToList();

    public string TotalText => Tr.F("Frees about {0}.", Durations.Size(RemoveEverything.Summary(items, saves).Where(s => s.Goes).Sum(s => s.Bytes)));

    static bool InstalledOnWindows => OperatingSystem.IsWindows() && Program.Layout.Kind == LauncherKind.Installed;

    public string Title => done == null ? Tr.T("Remove everything Longshot put on this PC?")
        : done.Problems.Any() ? Tr.T("Longshot is removed, but not all of it") : Tr.T("Longshot is removed");

    public string Sub => Tr.Sentences(
        Tr.T("Also removed: the launcher's settings and its list of versions, the game's caches, and the shortcuts it made (desktop, applications menu, Steam)."),
        InstalledOnWindows ? Tr.T("The launcher uninstalls itself too.") : "",
        Tr.T("A folder you chose keeps whatever else is in it."));

    public string SavesLabel => Tr.T("Also delete my saves, their backups, the game's settings and NFC tags");
    public string SavesWarning => Tr.T("They go with no copy kept and can't be brought back. To keep a copy, export them first in Saves.");

    /// Why Remove is off now: Longshot or a build (this launcher's, or longshot-builder writing a listed version's folder:
    /// Saves.BuilderRunning) or a download runs, or the list of versions could not be read
    string? WhyNot => shownRunning == true || (shownRunning == null && Core.Saves.Running(Main.Platform).Contains(Core.Saves.Longshot.Display))
        ? Tr.T("Close Longshot first: nothing is removed while it runs.")
        : shownRunning == null && (BuildStepViewModel.Current != null || Main.Service.Prefetch.Busy
                                   || Core.Saves.BuilderRunning(Main.Platform, Main.Service.Registry.Builds.Select(b => b.Folder)))
            ? Tr.T("Wait for the build or download to finish first.")
        : shownRunning == null && Main.Service.Registry.Problem != null ? Tr.T("The list of versions could not be read, so nothing is removed.")
        : null;

    public string? Problem => problem ?? (done == null ? WhyNot : null);
    public bool HasProblem => Problem != null;

    public bool Asking => done == null;
    public bool IsDone => done != null;
    public string RemoveLabel => removing ? Tr.T("Removing…") : Tr.T("Remove everything");

    /// What is left, one a line, and what the player still does (the launcher's own file, which it cannot delete while it runs)
    public string DoneText
    {
        get
        {
            if (done == null) return "";
            var lines = new List<string>();
            foreach (var k in done.Kept)
                lines.Add(k.Reason switch
                {
                    RemoveEverything.Reason.Yours => Tr.F("Kept, yours: {0}", k.Path),
                    RemoveEverything.Reason.Others => Tr.F("Kept, it holds other things: {0}", k.Path),
                    _ => Tr.F("Could not be removed: {0} ({1})", k.Path, k.Why ?? ""),
                });
            if (steamWaits) lines.Add(Tr.T("Steam shows its Longshot tile until it is restarted."));
            if (done.Backups.Count > 0) lines.Add(Tr.F("A save found in a version's folder is backed up in {0}.", string.Join(", ", done.Backups)));
            if (!InstalledOnWindows && Program.Layout.File is { } file)
                lines.Add(Tr.F("To finish, delete the launcher itself: {0}", Program.Layout.Kind == LauncherKind.Portable && Main.Platform == Platform.Windows
                                                                                 ? Path.GetDirectoryName(file)! : file));
            return string.Join("\n", lines);
        }
    }

    public Command KeepCommand { get; }
    public Command RemoveCommand { get; }
    public Command CloseCommand { get; }

    /// The screenshots' finished removal
    public RemoveEverythingViewModel ShownDone(RemoveEverything.Done shown)
    {
        done = shown;
        RaiseAll(string.Empty);
        return this;
    }

    async Task RemoveAsync()
    {
        if (removing || WhyNot != null)
        {
            problem = WhyNot;
            RaiseAll(nameof(Problem), nameof(HasProblem));
            return;
        }
        removing = true;
        problem = null;
        Changed();
        var s = Main.Service;
        var platform = s.Platform;
        var nfcChosen = Environment.GetEnvironmentVariable(NfcTags.Variable) is { Length: > 0 };
        var places = new RemoveEverything.Places(s.SaveEnv, s.Home, s.Registry.Builds.Select(b => b.Folder).ToList(), s.BuildsFolder,
                                                 !string.IsNullOrWhiteSpace(s.Settings.BuildsFolder), s.Work, !string.IsNullOrWhiteSpace(s.Settings.Work),
                                                 nfcChosen ? null : NfcTags.Folder(platform), s.Settings.EarlierWork.ToList(), LauncherService.LauncherPath);
        var launcher = LauncherService.LauncherPath;
        // (any version folder's builder takes the tile out: its note beside Steam's library names the tile whichever made it)
        var steamBuilder = s.Tools.Installed().Select(t => s.Tools.Find(t)).OfType<Toolchain>().Select(t => s.BuilderOf(t.Folder)).FirstOrDefault();
        // (the default profile's tile, Longshot, and each other profile's own: SteamTile.NameFor)
        var tiles = new List<string?> { null };
        try { tiles.AddRange(s.Profiles.Select(SteamTile.NameFor).Where(n => n != null)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var withSaves = saves;
        try
        {
            done = await Task.Run(() =>
            {
                // (the shortcuts first, while the logo they name is still there: only the ones that start this launcher)
                steamWaits = Shortcuts(launcher, platform, steamBuilder, tiles);
                var d = RemoveEverything.Run(places, withSaves, () => Core.Saves.Running(platform),
                                             building: () => Core.Saves.BuilderRunning(platform, places.Versions));
                // (the installed Windows launcher's shortcut, entry and program: deleted once the window has closed)
                if (OperatingSystem.IsWindows() && InstalledOnWindows)
                {
                    try
                    {
                        var removed = Installer.Uninstall(InstallPlaces.Of(), new UserRegistry(), new ShellShortcuts(), false, Array.Empty<string>(), Environment.ProcessPath);
                        if (removed.Pending != null) WindowsInstall.DeleteAfterExit(removed.Pending);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                                  or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        d = d with { Kept = d.Kept.Append(new RemoveEverything.Left(InstallPlaces.Of().Program, RemoveEverything.Reason.Problem, e.Message)).ToList() };
                    }
                }
                return d;
            });
            Program.ReleaseLock();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = Tr.F("Nothing was removed: {0}", e.Message);
        }
        finally
        {
            removing = false;
            Main.StorageMeasured = null;
            Changed();
        }
    }

    /// The shortcuts the launcher made that start it or its versions: Linux's desktop and menu entries and Steam's tile (taken out
    /// by a version folder's builder, which only ever removes its own: SteamTile), Windows' desktop and Start menu Longshot.lnk
    /// (one starting another launcher stays). A failure leaves that shortcut, never the rest. Returns whether Steam's tile went
    /// while Steam runs
    static bool Shortcuts(string launcher, Platform platform, LongshotBuilder? steamBuilder, IReadOnlyList<string?> tiles)
    {
        void Try(Action act)
        {
            try { act(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                LauncherLog.Add("remove everything: a shortcut was not removed: " + e.Message);
            }
        }
        if (platform != Platform.Linux)
        {
            Try(() => WindowsShortcuts.Write(launcher, false, false));
            return false;
        }
        Try(() => RemoveEverything.RemoveLinuxShortcuts(launcher));
        var steam = false;
        if (steamBuilder != null && SteamTile.Present())
            Try(() =>
            {
                foreach (var name in tiles)
                {
                    var r = SteamTile.RunAsync(steamBuilder, null, remove: true, name: name).GetAwaiter().GetResult();
                    if (!r.Done) LauncherLog.Add($"remove everything: the Steam shortcut {name ?? "Longshot"} was not removed: " + r.Text);
                    steam |= r.Action == "removed" && r.SteamRunning;
                }
            });
        return steam;
    }

    void Changed()
    {
        RaiseAll(string.Empty);
        KeepCommand.Changed();
        RemoveCommand.Changed();
    }
}
