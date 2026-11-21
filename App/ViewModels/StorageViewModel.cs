using Avalonia.Media;
using Avalonia.Threading;
using LongshotLauncher.Core;

namespace LongshotLauncher.ViewModels;

/// The Storage page (the title bar's Storage tab, or Settings > Manage storage): what Longshot keeps on this PC by what it is for (Core/Storage.cs),
/// as one bar of its groups, each group's items below with what removing one costs, asked before it goes. Measured off the
/// window's thread when the page opens and after each removal
public sealed class StorageViewModel : ViewModelBase
{
    public MainViewModel Main { get; }
    /// Read again at each measure: a version deleted since leaves the list, and its rows with it
    Storage.Places places;
    IReadOnlyList<Storage.Item>? items;
    Storage.Group selected = Storage.Group.FasterUpdates;
    Storage.Item? confirming;
    bool confirmingAll, removing;

    /// The groups' colours, as the mockup's (the launcher's palette: Brand, its light ink, Warn, Go, InkSoft, Ink), each a
    /// lightness of its own so they are told apart without their hue
    static readonly Dictionary<Storage.Group, (string Fill, string Ink)> Colours = new()
    {
        [Storage.Group.GameFiles] = ("#3D63E6", "#FFFFFF"),
        [Storage.Group.Games] = ("#93AAFF", "#14161A"),
        [Storage.Group.ShaderCaches] = ("#E2B86B", "#14161A"),
        [Storage.Group.FasterUpdates] = ("#5FB08A", "#06150E"),
        [Storage.Group.BuildTools] = ("#A2A9B3", "#14161A"),
        [Storage.Group.YourData] = ("#EDEFF2", "#14161A"),
    };

    readonly long? shownFree;

    /// The drive shown (null: all of them, each item summed across its drives) and the dropdown's choices, made once measured
    string? drive;
    IReadOnlyList<DriveOption> driveOptions = Array.Empty<DriveOption>();
    /// Whether Longshot runs and a download runs, as last looked (Watch): what turns the Remove buttons off, and their tips
    bool seenRunning, seenDownloading, watching;
    readonly DispatcherTimer watch;

    /// What the page is doing (null: nothing), pinned under the legend for as long as it takes: removing, with how much of what
    /// it frees is gone so far (the drive's free space against its start: busyDrive, busyFreeStart, busyExpect), or measuring
    string? busyText, busyDrive;
    long busyFreeStart = -1, busyExpect;
    double busyFraction;
    readonly DispatcherTimer busyTick;

    /// measured, free, running: the items, the drive's free space and whether Longshot runs as the screenshots show them, no
    /// disk read nor look at what runs; null: measured and looked at here
    public StorageViewModel(MainViewModel main, IReadOnlyList<Storage.Item>? measured = null, long? free = null, Storage.Group? show = null,
                            bool? running = null, string? onDrive = null)
    {
        Main = main;
        shownFree = free;
        if (show is { } g) selected = g;
        var s = main.Service;
        places = new Storage.Places(s.Work, s.Tools.Root, s.Home.Logs, Array.Empty<string>(), Array.Empty<Storage.Game>(), Array.Empty<string>());
        CancelCommand = new Command(() => { confirming = null; confirmingAll = false; RaiseItems(); });
        AskAllCommand = new Command(() => { confirmingAll = true; confirming = null; RaiseItems(); }, () => CanClearAll);
        RemoveAllCommand = new Command(() => _ = RemoveAsync(Clearable().ToList()), () => !IsBusy);
        seenRunning = running ?? true;
        seenDownloading = running.HasValue ? false : DownloadingNow;
        // (a game or a download that ends while the page is open turns its buttons back on: looked at every 2 s, off the
        // window's thread, until the page is left)
        watch = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Watch());
        busyTick = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Tick());
        if (running == null) watch.Start();
        RefreshCommand = new Command(Measure, () => !IsBusy);
        if (measured != null)
        {
            items = measured;
            MakeDriveOptions();
            drive = onDrive;
        }
        // (the worker checks whether the last measure was of these same places)
        else Measure();
    }

    void Watch()
    {
        if (!ReferenceEquals(Main.Page, this))
        {
            watch.Stop();
            return;
        }
        if (watching) return;
        watching = true;
        Task.Run(() => (RunningNow, DownloadingNow)).ContinueWith(t => Ui.Post(() =>
        {
            watching = false;
            if (!t.IsCompletedSuccessfully || t.Result == (seenRunning, seenDownloading)) return;
            (seenRunning, seenDownloading) = t.Result;
            RaiseItems();
        }));
    }

    /// Where Longshot keeps things on this PC: the build files folder, the builders, the logs, the SDKs, every listed game
    /// folder make_game made, the game folders in the builds folder no row lists (Leftovers.All: an update left them, or a row
    /// went with its files kept), and the saves with their backups
    static Storage.Places PlacesOf(MainViewModel main, IReadOnlyList<Storage.Game> games)
    {
        var s = main.Service;
        return new Storage.Places(s.Work, s.Tools.Root, s.Home.Logs, new[] { s.Home.NgxSdk, s.Home.FfxSdk, s.Home.XessSdk }, games,
                                  new[] { s.SaveEnv.SaveFolder }.Concat(s.SaveEnv.BackupRoots).Distinct().ToList(), s.SaveEnv.ShaderStore,
                                  new[] { s.SaveEnv.LongshotRoot, s.SaveEnv.LongshotUser }.Distinct().ToList());
    }

    /// A game folder no row lists: its id on the page (no row's id has it)
    const string UnlistedId = "unlisted:";

    /// Everything measured again, off the window's thread; the Remove buttons stay off until it is done (the sizes shown
    /// are the last ones until then)
    /// A measure kept by the launcher (MainViewModel.StorageMeasured): of which places (Key), what, and when
    public sealed record Snapshot(string Key, IReadOnlyList<Storage.Item> Items, DateTimeOffset At, Storage.Places Places, bool Partial = false);

    /// The places as one text: another version, a folder moved, and a kept measure is not theirs
    static string KeyOf(Storage.Places p, string buildsFolder) => string.Join("\n",
        new[] { buildsFolder, p.Work, p.Toolchains, p.LauncherLogs, p.ShaderStore ?? "" }.Concat(p.Sdks).Concat(p.Games.Select(g => g.Id + "=" + g.Folder)).Concat(p.SaveFolders).Concat(p.SettingsFolders ?? Array.Empty<string>()));

    DateTimeOffset? measuredAt;
    bool partialMeasure;

    /// Beside the drives: measure again, with when the sizes shown were measured
    public Command RefreshCommand { get; }
    public string RefreshTip => measuredAt is { } at
        ? partialMeasure ? Tr.F("Measure again (partial measurement at {0})", at.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture))
                         : Tr.F("Measure again (measured at {0})", at.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture))
        : Tr.T("Measure again");

    void Measure()
    {
        var gamesSnapshot = Main.Service.Registry.Builds.Select(b => new Storage.Game(b.Id, b.Version is { } v ? Versions.Shown(v) : b.Name, b.Folder)).ToList();
        var buildsFolder = Main.Service.BuildsFolder;
        var kept = Main.StorageMeasured;
        var useKept = items == null;
        var home = Main.Service.Home.Root;
        var platform = Main.Platform;
        var saves = Main.Service.SaveEnv;
        var registryProblem = Main.Service.Registry.Problem != null;
        var generation = ++Main.StorageGeneration;
        Busy(items == null ? Tr.T("Measuring what Longshot keeps…") : Tr.T("Updating the sizes…"));
        Task.Run(() =>
        {
            IReadOnlyList<Storage.Item>? measured = null;
            Storage.Places? discovered = null;
            string? key = null;
            var running = true;
            var downloading = true;
            var partial = false;
            try
            {
                var at = PlacesOf(Main, gamesSnapshot);
                key = KeyOf(at, buildsFolder);
                running = RunningNow;
                downloading = DownloadingNow;
                if (useKept && kept != null && kept.Key == key)
                {
                    discovered = kept.Places;
                    measured = kept.Items;
                    partial = kept.Partial;
                }
                else
                {
                    var games = at.Games.Where(g => GameFolder.IsGameFolder(g.Folder, platform, () => partial = true)).ToList();
                    games.AddRange(Leftovers.All(buildsFolder, at.Games.Select(g => g.Folder), platform, new[] { home }, saves, () => partial = true)
                        .Select(l => new Storage.Game(UnlistedId + l.Folder, l.Shown, l.Folder, Unlisted: true, l.HoldsYours || registryProblem)));
                    discovered = at with { Games = games };
                    measured = Storage.Measure(discovered, () => partial = true);
                }
            }
            catch (Exception e) { LauncherLog.Add("storage: not measured: " + e.Message); }
            Ui.Post(() =>
            {
                try
                {
                    if (measured != null && generation == Main.StorageGeneration)
                    {
                        seenRunning = running;
                        seenDownloading = downloading;
                        places = discovered!;
                        items = measured;
                        measuredAt = ReferenceEquals(measured, kept?.Items) ? kept!.At : DateTimeOffset.Now;
                        partialMeasure = partial;
                        Main.StorageMeasured = new Snapshot(key!, measured, measuredAt.Value, discovered!, partial);
                        if (partial) Main.ShowToast(Tr.T("Storage was only partly measured; some folders could not be read."));
                    }
                    else if (measured == null) Main.ShowToast(items == null ? Tr.T("Storage could not be measured.") : Tr.T("Storage could not be measured. The last successful sizes are shown."));
                    MakeDriveOptions();
                    if (drive != null && !driveOptions.Any(o => Storage.SameDrive(o.Drive, drive))) drive = null;
                }
                finally
                {
                    removing = false;
                    Busy(null);
                    RaiseEverything();
                }
            });
        });
    }

    /// Pinned: what the page does now (null: nothing). drive, expect: a removal on that drive that frees about expect bytes,
    /// whose bar fills as the drive's free space grows (never past 99% until it is done: another program may write meanwhile)
    void Busy(string? text, string? drive = null, long expect = 0)
    {
        busyText = text;
        busyDrive = drive;
        busyExpect = text == null ? 0 : expect;
        busyFraction = 0;
        busyFreeStart = drive != null && expect > 0 ? Disk.FreeBytes(drive) ?? -1 : -1;
        if (busyFreeStart >= 0) busyTick.Start();
        else busyTick.Stop();
        RaiseAll(nameof(IsBusy), nameof(BusyText), nameof(BusyDetail), nameof(BusyFraction), nameof(HasFraction), nameof(SweepBusy), nameof(RefreshTip));
        RefreshCommand.Changed();
    }

    void Tick()
    {
        if (busyDrive == null || busyFreeStart < 0 || busyExpect <= 0 || Disk.FreeBytes(busyDrive) is not long free) return;
        busyFraction = Math.Max(busyFraction, Math.Clamp((double)(free - busyFreeStart) / busyExpect, 0, 0.99));
        RaiseAll(nameof(BusyFraction), nameof(BusyDetail), nameof(DriveText));
    }

    /// The screenshots' pinned progress: text, with fraction of expect freed (no drive looked at)
    public StorageViewModel ShownBusy(string text, long expect, double fraction)
    {
        busyText = text;
        busyExpect = expect;
        busyFreeStart = 0;
        busyFraction = fraction;
        removing = true;
        RaiseEverything();
        return this;
    }

    /// The screenshots' first measure: nothing measured yet, the progress in the bar's place
    public StorageViewModel ShownFirstMeasure()
    {
        items = null;
        measuredAt = null;
        driveOptions = Array.Empty<DriveOption>();
        busyText = Tr.T("Measuring what Longshot keeps…");
        busyExpect = 0;
        busyFreeStart = -1;
        RaiseEverything();
        return this;
    }

    public bool IsBusy => busyText != null;
    public bool SweepBusy => IsBusy && !HasFraction;
    public string BusyText => busyText ?? "";
    public bool HasFraction => busyFreeStart >= 0 && busyExpect > 0;
    public double BusyFraction => busyFraction;
    public string BusyDetail => HasFraction ? Tr.F("{0} of {1} freed", Durations.Size((long)(busyFraction * busyExpect)), Durations.Size(busyExpect)) : "";

    /// Home's delete of a version began: pinned, with how much of its folder is gone
    void Deleting(BuildItemViewModel v)
    {
        removing = true;
        confirming = null;
        confirmingAll = false;
        var e = v.Entry;
        Busy(Tr.F("Deleting {0}…", v.Version), e.Folder,
             e.Frees ?? e.Bytes ?? items?.Where(i => i.Game?.Id == e.Id).Sum(i => i.Frees) ?? 0);
        RaiseItems();
    }

    /// Home's delete of a version ended: its rows out at once when it went, then everything measured again (the game files
    /// it shared, the builders no version uses now)
    void Deleted(bool deleted, string id)
    {
        if (!deleted && !removing) return;
        if (deleted && items != null) items = items.Where(i => i.Game?.Id != id).ToList();
        RaiseEverything();
        Measure();
    }

    /// How the pinned progress names an item
    static string NameOf(Storage.Item i) => i.Kind == Storage.Kind.ShaderCache && i.Game is { } g ? Tr.F("the shader caches of {0}", g.Name)
        : i.Game is { } u ? u.Name : Tr.T(Storage.Info(i.Kind).Name);

    /// The measured items on the drive shown (all of them: null)
    IEnumerable<Storage.Item> Raw => (items ?? Array.Empty<Storage.Item>()).Where(i => drive == null || Storage.SameDrive(i.Drive, drive));

    /// What the page shows: one item a kind (a version's: one a version), summed across the drives shown
    IReadOnlyList<Storage.Item> View => Raw.GroupBy(i => (i.Kind, i.Game?.Id))
        .Select(g => g.Skip(1).Any() ? g.First() with { Bytes = g.Sum(i => i.Bytes), Frees = g.Sum(i => i.Frees), Drive = null, HasClearableFiles = g.Any(i => i.HasClearableFiles) } : g.First())
        .ToList();

    /// The measured items one shown item sums: what its removal lets go (only the drive shown's)
    IReadOnlyList<Storage.Item> PartsOf(Storage.Item shown) => Raw.Where(i => i.Kind == shown.Kind && i.Game?.Id == shown.Game?.Id).ToList();

    public bool Measuring => items == null;
    long Total => View.Sum(i => i.Bytes);
    long BytesOf(Storage.Group g) => View.Where(i => i.Group == g).Sum(i => i.Bytes);

    /// One choice of the drive dropdown: all drives (Drive null), or one with what Longshot keeps on it and its free space
    public sealed record DriveOption(string? Drive, string Name, string Detail);

    /// The dropdown's choices from what was measured: all drives, then each drive Longshot keeps something on, the fullest first
    void MakeDriveOptions()
    {
        var drives = (items ?? Array.Empty<Storage.Item>()).Where(i => i.Drive != null && i.Bytes > 0)
            .GroupBy(i => i.Drive!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(g => (Drive: g.Key, Bytes: g.Sum(i => i.Bytes))).OrderByDescending(d => d.Bytes).ToList();
        driveOptions = new[] { new DriveOption(null, Tr.T("All drives"), Tr.F("{0} on {1} drives", Durations.Size(drives.Sum(d => d.Bytes)), drives.Count)) }
            .Concat(drives.Select(d => new DriveOption(d.Drive, d.Drive,
                FreeOf(d.Drive) is long free ? Tr.F("{0} used · {1} free", Durations.Size(d.Bytes), Durations.Size(free)) : Tr.F("{0} used", Durations.Size(d.Bytes)))))
            .ToList();
    }

    /// A drive's free space (the screenshots': shownFree)
    long? FreeOf(string drive) => shownFree ?? Disk.FreeBytes(OperatingSystem.IsWindows() && !drive.EndsWith('\\') ? drive + "\\" : drive);

    /// The dropdown, when Longshot keeps things on more than one drive; else the one line of free space
    public bool ShowDrives => driveOptions.Count > 2;
    public IReadOnlyList<DriveOption> DriveOptions => driveOptions;

    /// The drive picked: the bar, the groups' sizes, the rows and their Remove all only that drive's
    public DriveOption? PickedDrive
    {
        get => driveOptions.FirstOrDefault(o => drive == null ? o.Drive == null : Storage.SameDrive(o.Drive, drive));
        set
        {
            var to = value?.Drive;
            if (to == null ? drive == null : Storage.SameDrive(to, drive)) return;
            drive = to;
            confirming = null;
            confirmingAll = false;
            RaiseEverything();
        }
    }

    /// Beside the heading once measured (until then the pinned progress says it is measuring)
    public string TotalText => Measuring ? "" : Durations.Size(Total);

    /// The one drive Longshot keeps things on (before measuring: the build files'), its free space
    public string DriveText
    {
        get
        {
            var on = driveOptions.FirstOrDefault(o => o.Drive != null)?.Drive ?? Disk.DriveName(places.Work);
            return FreeOf(on) is long free ? Tr.F("{0} free on {1}", Durations.Size(free), on) : "";
        }
    }

    /// The build files on another drive than the builds: the one Longshot fills that the player may not have chosen
    public bool WorkElsewhere => !string.Equals(Disk.DriveName(places.Work), Disk.DriveName(Main.Service.BuildsFolder),
                                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public string WorkElsewhereText => Tr.F("Build files are on {0}, not with your games on {1}. Choosing a folder on {1} in Settings keeps {0} free.",
                                            Disk.DriveName(places.Work), Disk.DriveName(Main.Service.BuildsFolder));
    public string BuildsFolder => ShownPath.Of(Main.Service.BuildsFolder);
    public string Work => ShownPath.Of(places.Work);

    /// One of the bar's groups (and its legend card): its name, size, share and colour
    public sealed record Segment(Storage.Group Group, string Name, string Short, string SizeText, string ShareText, double Share,
                                 IBrush Fill, IBrush Ink, bool IsSelected, Command Pick)
    {
        /// The group shown at full strength, the others dimmer
        public double Strength => IsSelected ? 1 : 0.5;
        public string Tip => Name + ", " + SizeText;
    }

    public IReadOnlyList<Segment> Segments => Storage.Groups.Select(g =>
    {
        var bytes = BytesOf(g.Group);
        var share = Total > 0 ? (double)bytes / Total : 0;
        var (fill, ink) = Colours[g.Group];
        return new Segment(g.Group, Tr.T(g.Name), share > 0.09 ? Tr.T(g.Name) : "", Durations.Size(bytes),
                           Tr.F("{0}% of what Longshot uses", Math.Round(share * 100)), Math.Max(share, 0.0001), Brush(fill), Brush(ink),
                           g.Group == selected, new Command(() => { selected = g.Group; confirming = null; confirmingAll = false; RaiseEverything(); }));
    }).ToList();

    /// The bar's segments: the groups that hold something (on the drive picked); the legend keeps all of them
    public IReadOnlyList<Segment> BarSegments => Segments.Where(s => s.Share > 0.0001).ToList();

    static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    Storage.GroupInfo Info => Storage.Groups.First(g => g.Group == selected);
    public string GroupName => Tr.T(Info.Name);
    public string GroupSize => Durations.Size(BytesOf(selected));
    public string GroupAbout => Tr.T(Info.About);
    public string? GroupNote => Info.Note is { } n ? Tr.T(n) : null;
    public bool HasGroupNote => Info.Note != null;
    public IBrush GroupFill => Brush(Colours[selected].Fill);
    public bool IsFasterUpdates => selected == Storage.Group.FasterUpdates;

    /// Keep the most recent build's files (LauncherSettings.KeepBuildFiles): off, they go once each build is done
    public bool KeepBuildFiles
    {
        get => Main.Service.Settings.KeepBuildFiles;
        set
        {
            Main.Service.Settings.KeepBuildFiles = value;
            try { Main.Service.SaveSettings(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Main.ShowToast(Tr.F("The settings could not be saved: {0}", e.Message)); }
            RaiseAll(nameof(KeepBuildFiles), nameof(Items));
        }
    }

    public string KeepText => Tr.F("Keep build files and stores ({0})",
                                   Durations.Size(View.Where(i => i.Kind is Storage.Kind.RecentCode or Storage.Kind.RecentTranslation or Storage.Kind.OlderCode or Storage.Kind.OlderTranslations).Sum(i => i.Bytes)));
    public string KeepHint => Tr.T("The next update redoes only what changed. Capped stores also keep work for switching versions.");

    /// One item of the group shown: what it is, its size, whether it is kept, and its removal (asked first)
    /// ShowRemove: it is one the page can let go; CanRemove: it can now, else RemoveTip says why (Longshot or a download runs)
    public sealed record Row(string Name, string About, string SizeText, string? Tag, IBrush? TagInk, IBrush? TagFill, bool ShowRemove,
                             bool CanRemove, string? RemoveTip, bool Confirming, string ConfirmText, Command Ask, Command Remove,
                             Command Cancel, string RemoveName)
    {
        public bool HasTag => Tag != null;
    }

    public IReadOnlyList<Row> Items => View.Where(i => i.Group == selected).Select(i =>
    {
        var info = Storage.Info(i.Kind);
        var name = i.Kind == Storage.Kind.GameFiles ? CopyName(i) : i.Game is { } g ? g.Name : Tr.T(info.Name);
        // (a version: Home's delete asks, with its saves and the default, and the page measures again once it is done)
        var version = i.Kind == Storage.Kind.Programs ? VersionRow(i) : null;
        string? tag = null;
        IBrush? tagInk = null, tagFill = null;
        if (i.Kind == Storage.Kind.Unlisted)
        {
            tag = Tr.T("Not in your versions");
            tagInk = Brush("#E2B86B");
            tagFill = Brush("#24E2B86B");
        }
        if (selected == Storage.Group.FasterUpdates)
        {
            var kept = KeepBuildFiles && i.Kind is Storage.Kind.RecentCode or Storage.Kind.RecentTranslation or Storage.Kind.OlderCode or Storage.Kind.OlderTranslations;
            tag = kept ? Tr.T("Kept") : Tr.T("Goes after the next build");
            tagInk = Brush(kept ? "#5FB08A" : "#E2B86B");
            tagFill = Brush(kept ? "#265FB08A" : "#24E2B86B");
        }
        var refusal = Storage.CanRemove(i, seenRunning, seenDownloading);
        var confirm = Tr.Sentences(Tr.F("Frees {0}.", Durations.Size(i.Frees)), info.Cost is { } c ? Tr.T(c) : "");
        // (a version's row says what else goes with it: its shader caches, in its folder)
        // (a folder no row lists: where it is, in the builds folder (Where it goes shows that one))
        var about = i.Kind == Storage.Kind.GameFiles ? CopyAbout(i)
            : i.Kind == Storage.Kind.Unlisted && i.Game is { } at
            ? Tr.Sentences(Tr.F("In your builds folder: {0}.", InBuilds(at.Folder)),
                           i.Frees - i.Bytes is var own && own > 0 ? Tr.F("Removing it also frees {0} of game files only it has", Durations.Size(own)) : "")
            : Tr.T(info.About);
        if (version != null && View.FirstOrDefault(c => c.Kind == Storage.Kind.ShaderCache && c.Game?.Id == i.Game!.Id) is { Bytes: > 0 } cache)
            about = Tr.F("Its program and shader list. Removing it also deletes its {0} of shader caches", Durations.Size(cache.Bytes));
        var show = refusal != Storage.Refusal.NotRemovable && (i.Bytes > 0 || i.HasClearableFiles) && confirming != i
                   && (i.Kind != Storage.Kind.Programs || version != null);
        return new Row(name, about, Durations.Size(i.Bytes), tag, tagInk, tagFill,
                       show, show && refusal == Storage.Refusal.None && !IsBusy,
                       removing ? Tr.T("Wait until what is being removed is gone.") : WhyNot(i.Kind, refusal), confirming == i, confirm,
                       version != null ? new Command(() => Main.Home.AskDelete(version, d => Deleted(d, version.Entry.Id), () => Deleting(version)))
                       : new Command(() => { confirming = i; confirmingAll = false; RaiseItems(); }),
                       new Command(() => _ = RemoveAsync(new[] { i })), CancelCommand, Tr.F("Remove {0}", name));
    }).ToList();

    /// A copy of the game files: named by the versions that use it
    static string CopyName(Storage.Item i) => i.Users is { Count: > 0 } users
        ? Tr.F("Used by {0}", string.Join(Tr.T(", "), users.Select(u => u.Name))) : Tr.T(Storage.Info(i.Kind).Name);

    /// What freeing a copy takes: removing every version that uses it, or more when folders the page doesn't measure (a
    /// developer's base, another program's) hold links to its files too
    static string CopyAbout(Storage.Item i)
    {
        var n = i.Users?.Count ?? 0;
        var what = Tr.T("Unpacked game files.");
        // (a few files linked elsewhere are no reason to say so: within 1%)
        if (i.Frees * 100 < i.Bytes * 99)
        {
            var said = Tr.Sentences(what, Tr.F("Other folders use these files too, so removing {0} frees only {1}.",
                                               n == 1 ? i.Users![0].Name : Tr.T("these versions"), Durations.Size(i.Frees)));
            if (i.Elsewhere is not { Count: > 0 } others) return said;
            // (where they can be told: the first few, one a line)
            const int Shown = 4;
            var lines = others.Take(Shown).Select(ShownPath.Of).ToList();
            if (others.Count > Shown) lines.Add(Tr.F("and {0} more", others.Count - Shown));
            return said + "\n" + Tr.T("Also used by:") + "\n" + string.Join("\n", lines);
        }
        return Tr.Sentences(what, n == 1 ? Tr.F("Freed when {0} is removed.", i.Users![0].Name)
                                  : n > 1 ? Tr.T("Freed once all of these versions are removed.") : "");
    }

    /// A folder in the builds folder as a path from it (its full path when it is not in it)
    string InBuilds(string folder)
    {
        var rel = Path.GetRelativePath(Main.Service.BuildsFolder, folder);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? ShownPath.Of(folder) : rel;
    }

    /// Home's row of a version, for its delete; null when it is not listed there (any more)
    BuildItemViewModel? VersionRow(Storage.Item i) => i.Game is { } g ? Main.Home.Builds.FirstOrDefault(b => b.Entry.Id == g.Id) : null;

    /// Why a Remove is off, for its tip: what holds it now; null when nothing does
    static string? WhyNot(Storage.Kind kind, Storage.Refusal refusal) => refusal switch
    {
        Storage.Refusal.Running => kind switch
        {
            Storage.Kind.Programs => Tr.T("Close Longshot first: no version is deleted while a game runs."),
            Storage.Kind.Unlisted => Tr.T("Close Longshot first: no game folder is deleted while a game runs."),
            Storage.Kind.Logs => Tr.T("Close Longshot first: it writes its logs while it runs."),
            _ => Tr.T("Close Longshot first: it uses its shader caches while it runs."),
        },
        Storage.Refusal.Downloading => Tr.T("A download is running: its files stay until it ends."),
        Storage.Refusal.HoldsYours => Tr.T("It holds mods of yours (or a link you made): move them out of its folder first."),
        _ => null,
    };

    /// What Remove all would let go, whatever holds it now: never a version (each is deleted on its own, as Home's delete asks)
    IEnumerable<Storage.Item> Candidates() => View
        .Where(i => i.Group == selected && (i.Bytes > 0 || i.HasClearableFiles) && i.Kind != Storage.Kind.Programs && i.Removable);

    /// What Remove all lets go now: the candidates nothing holds
    IEnumerable<Storage.Item> Clearable() => Candidates().Where(i => Storage.CanRemove(i, seenRunning, seenDownloading) == Storage.Refusal.None);

    /// Remove all: shown for a group with more than one thing to let go; off, with its tip, while what holds them runs
    public bool ShowClearAll => !confirmingAll && Candidates().Count() > 1;
    public bool CanClearAll => ShowClearAll && !IsBusy && Clearable().Any();
    public string? ClearAllTip => ShowClearAll && !Clearable().Any() && Candidates().FirstOrDefault() is { } held
        ? WhyNot(held.Kind, Storage.CanRemove(held, seenRunning, seenDownloading)) : null;
    /// (in Installed games only the folders no row lists go: never a version in the list)
    public string ClearAllText
    {
        get
        {
            var size = Durations.Size((Clearable().Any() ? Clearable() : Candidates()).Sum(i => i.Frees));
            return selected == Storage.Group.Games ? Tr.F("Remove those not in your versions {0}", size) : Tr.F("Remove all {0}", size);
        }
    }
    public bool ConfirmingAll => confirmingAll;
    public string ConfirmAllText => Tr.Sentences(Tr.F("Frees {0}.", Durations.Size(Clearable().Sum(i => i.Frees))),
        selected == Storage.Group.ShaderCaches ? Tr.T("Only recommended if you won't be playing again soon: every version has to prepare its shaders again before you can play it, several minutes each.")
        : selected == Storage.Group.FasterUpdates ? Tr.T("The next update builds everything from scratch.")
        : selected == Storage.Group.Games ? Tr.T("Deletes the folders of the versions that aren't in your list. A save still in one is backed up first.")
        : Tr.T("Each is made again when it is next needed."));

    /// Longshot runs: it holds its shader caches and logs; a download runs: it holds the tools' staging. Looked at now (Watch
    /// keeps seenRunning and seenDownloading)
    bool RunningNow => Saves.Running(Main.Platform).Contains(Saves.Longshot.Display);
    bool DownloadingNow => Main.Service.Prefetch.Busy;
    public string BusyNote => Tr.T("Nothing is removed while a build is running or Longshot is open.");

    public Command CancelCommand { get; }
    public Command AskAllCommand { get; }
    public Command RemoveAllCommand { get; }

    void RaiseItems()
    {
        RaiseAll(nameof(Items), nameof(ShowClearAll), nameof(CanClearAll), nameof(ClearAllTip), nameof(ConfirmingAll), nameof(ConfirmAllText),
                 nameof(ClearAllText));
        AskAllCommand.Changed();
        RemoveAllCommand.Changed();
    }

    /// Every binding again, the commands' Can too (a command asks only when told: Command.Changed), as after a measure
    void RaiseEverything()
    {
        RaiseAll(string.Empty);
        AskAllCommand.Changed();
        RemoveAllCommand.Changed();
    }

    /// The items let go in turn, off the window's thread, then measured again; the toast says what was freed (or why not)
    async Task RemoveAsync(IReadOnlyList<Storage.Item> what)
    {
        if (IsBusy || what.Count == 0) return;
        removing = true;
        Main.StorageMeasured = null;
        confirming = null;
        confirmingAll = false;
        // (what each shown item sums, on the drive shown: never another drive's)
        var parts = what.SelectMany(PartsOf).ToList();
        var capturedPlaces = places;
        var drives = parts.Select(i => i.Drive).Distinct().ToList();
        Busy(what.Count == 1 ? Tr.F("Removing {0}…", NameOf(what[0])) : Tr.F("Removing {0} items…", what.Count),
             drives.Count == 1 ? drives[0] : null, parts.Sum(i => i.Frees));
        RaiseItems();
        var running = RunningNow;
        var own = LauncherLog.FilePath;
        long freed = 0;
        var refused = Storage.Refusal.None;
        string? problem = null;
        var backups = new List<string>();
        int logsRemoved = 0, logsSkipped = 0, logsFailed = 0;
        int foldersSkipped = 0, foldersFailed = 0;
        var service = Main.Service;
        // (a game folder no row lists: the launcher's delete, as Home's offer does it, its save backed up first)
        void DeleteFolder(Storage.Game g)
        {
            var root = Path.GetFullPath(service.BuildsFolder);
            var relative = Path.GetRelativePath(root, Path.GetFullPath(g.Folder));
            if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new IOException("The folder is no longer inside the builds folder.");
            if (service.DeleteLeftover(new Leftover(g.Folder, null)) is { } backup) backups.Add(backup);
        }
        try
        {
            await Task.Run(() =>
            {
                foreach (var i in parts)
                {
                    var r = Storage.Refusal.None;
                    var incomplete = false;
                    void Remove() => r = Storage.Remove(i, capturedPlaces, running, false, own, deleteFolder: DeleteFolder,
                        logOutcome: (removed, skipped, failed) => { logsRemoved += removed; logsSkipped += skipped; logsFailed += failed; incomplete = skipped > 0 || failed > 0 || removed == 0; },
                        folderOutcome: (removed, skipped, failed) => { foldersSkipped += skipped; foldersFailed += failed; incomplete = skipped > 0 || failed > 0 || removed == 0; });
                    if (i.Kind == Storage.Kind.UnfinishedDownloads)
                    {
                        if (!service.Prefetch.CleanStaging(Remove)) r = Storage.Refusal.Downloading;
                    }
                    else Remove();
                    if (r == Storage.Refusal.None && !incomplete) freed += i.Frees;
                    else if (r != Storage.Refusal.None) refused = r;
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            problem = e.Message;
            if (e is FolderNotDeleted { Backup: { } backup }) backups.Add(backup);
        }
        // (a folder Home offers to delete may be gone now: its offer looked at again)
        if (parts.Any(i => i.Kind == Storage.Kind.Unlisted)) Main.Home.OfferLeftovers();
        if (backups.Count > 0) Main.Home.Notice = Tr.F("A save from a folder being removed is backed up in {0}.", string.Join(", ", backups));
        // (removing stays on until the sizes are measured again: Measure)
        Main.ShowToast(problem != null ? Tr.F("Not everything could be removed: {0}", problem)
            : refused == Storage.Refusal.Running ? Tr.T("Close Longshot first: it holds its shader caches and logs while it runs.")
            : refused == Storage.Refusal.Downloading ? Tr.T("A download is running: its files stay until it ends.")
            : foldersFailed > 0 || foldersSkipped > 0 ? Tr.F("Some storage targets were missing or could not be removed ({0} skipped, {1} failed). Earlier estimate for other items: up to {2}.", foldersSkipped, foldersFailed, Durations.Size(freed))
            : logsFailed > 0 || logsSkipped > 0 ? Tr.F("Removed {0} logs; kept {1} and could not remove {2}. Earlier estimate: up to {3}.", logsRemoved, logsSkipped, logsFailed, Durations.Size(freed))
            : Tr.F("Removal finished. Earlier estimate: up to {0}.", Durations.Size(freed)));
        Measure();
    }
}
