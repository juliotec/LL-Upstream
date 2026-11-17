using System.Diagnostics;
using LongshotLauncher.Core;

namespace LongshotLauncher;

/// The launcher's state for the window and the command line: its home, its list of builds, its settings, and what git.magicbean.dev said
/// is newest on each channel
public sealed class LauncherService
{
    public Platform Platform { get; }
    public LauncherHome Home { get; }
    public BuildRegistry Registry { get; private set; }
    public LauncherSettings Settings { get; private set; }
    public IReadOnlyDictionary<string, string?> Newest => newest;
    public IFetcher Fetcher { get; }
    /// Longshot's signed releases on git.magicbean.dev (the builder of each version), checked against the keys this launcher trusts
    public Releases Releases { get; }
    /// The launcher's own signed releases (MagicBeanDev/LongshotLauncher), checked the same way
    public Releases LauncherReleases { get; }
    public Toolchains Tools { get; }
    /// Each version's tools, fetched once: started early for the likely next build (PrefetchLikely), joined by the wizard's step 1
    public ToolsPrefetch Prefetch { get; }
    /// This launcher's own version (SelfUpdate.OwnTag), and the one its channel (LauncherChannel) offers, if any
    public string OwnTag { get; } = SelfUpdate.OwnTag();
    public ReleaseInfo? LauncherUpdate { get; private set; }
    public string LauncherChannel => SelfUpdate.ChannelOf(OwnTag, Settings.LauncherChannel);
    /// The channel new builds start on: the player's pick, else the launcher's own (LauncherSettings.BuildChannel)
    public string DefaultChannel => Settings.BuildChannel(OwnTag);
    /// The save folders are worked out from this (the screenshots point it into a temporary folder)
    public SaveEnvironment SaveEnv { get; set; }
    /// This PC's graphics cards: named on Windows (WindowsGpus), only by maker on Linux (SystemInfo.GpuVendors)
    public IReadOnlyList<Gpu> GpuCards { get; set; }
    /// The makers of this PC's graphics cards, each once
    public IReadOnlyList<string> Gpus => GpuCards.Select(g => g.Vendor).Distinct().ToList();
    /// This PC's CPU name (WindowsCpu, SystemInfo.CpuName), read once, the first time it is asked for; "" when the system names none
    public string Cpu => cpu ??= (OperatingSystem.IsWindows() ? WindowsCpu.Read() : SystemInfo.CpuName()) ?? "";
    string? cpu;
    /// The Intel shader compiles choice applies on this PC (SystemInfo.IntelCompilesApply): Windows with Intel graphics
    public bool IntelCompilesApply => SystemInfo.IntelCompilesApply(Platform, GpuCards);
    /// Whether the update check has answered (or was not asked: offline)
    public bool Checked { get; set; }

    readonly Dictionary<string, string?> newest = new();

    LauncherService(Platform platform, LauncherHome home)
    {
        Platform = platform;
        Home = home;
        Registry = BuildRegistry.Load(home.Builds);
        // (issue 140: a portable launcher's folder renamed, or one above it: its builds' paths follow it)
        if (home.Portable && Registry.FollowHome())
            try { Registry.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Settings = LauncherSettings.Load(home.Settings);
        // (release lists, release files and packages through git-cdn.magicbean.dev first: CdnFetcher; LONGSHOT_LAUNCHER_NO_CDN=1 turns it off)
        var cdn = CdnFetcher.EnabledByEnvironment();
        Fetcher = new CdnFetcher(Slow(new HttpFetcher(TimeSpan.FromSeconds(60))), cdn);
        Tools = new Toolchains(home.Toolchains, Fetcher);
        // (its own timeout, shorter: the check at start should not keep the window waiting on a server that does not answer)
        var check = new CdnFetcher(Slow(new HttpFetcher(TimeSpan.FromSeconds(20))), cdn);
        var keys = ReleaseKeys.Current();
        Releases = new Releases(check, platform, keys, product: Product.Longshot);
        // (the launcher's own releases: its own versions on its own three channels, chosen apart from the builds' in Settings)
        LauncherReleases = new Releases(check, platform, keys, product: Product.Launcher);
        Prefetch = new ToolsPrefetch((tag, progress, cancel) => ToolsSource.EnsureAsync(Tools, Releases, tag, progress, cancel));
        // (a prune put off while a fetch ran, once none does: PruneTools)
        Prefetch.Idle += () => ViewModels.Ui.Post(() => { if (Interlocked.Exchange(ref pruneLater, 0) == 1) PruneTools(); });
        SaveEnv = SaveEnvironment.Current() with
        {
            Platform = platform,
            BackupFolder = home.Portable ? home.PortableSaveBackups : null,
            UserFolder = home.Portable ? home.PortableUser : null,
        };
        GpuCards = OperatingSystem.IsWindows() ? WindowsGpus.Read() : SystemInfo.GpuVendors(platform).Select(v => new Gpu(v)).ToList();
    }

    /// A Debug build only: LONGSHOT_LAUNCHER_DEBUG_SLOW=<ms> waits that long before each answer and after each MB of a download, to
    /// see the screens' busy states (a release build never reads it)
    static IFetcher Slow(IFetcher fetcher)
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("LONGSHOT_LAUNCHER_DEBUG_SLOW"), out var ms) && ms > 0)
            return new SlowFetcher(fetcher, TimeSpan.FromMilliseconds(ms));
#endif
        return fetcher;
    }

#if DEBUG
    sealed class SlowFetcher(IFetcher inner, TimeSpan wait) : IFetcher
    {
        public async Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel)
        {
            await Task.Delay(wait, cancel).ConfigureAwait(false);
            return await inner.GetTextAsync(url, accept, cancel).ConfigureAwait(false);
        }

        public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
        {
            await Task.Delay(wait, cancel).ConfigureAwait(false);
            long mb = 0;
            await inner.DownloadAsync(url, to, have =>
            {
                for (; mb < have >> 20; mb++) Thread.Sleep(wait / 10);
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke(have);
            }, cancel).ConfigureAwait(false);
        }
    }
#endif

    /// The launcher's home (Program.Home: a portable launcher's is beside it), opened
    public static LauncherService Open() => Open(Platforms.Current, Program.Home);

    /// platform: the screenshots draw the Windows screens on Linux too; home: null for LauncherHome.Resolve
    public static LauncherService Open(Platform platform, LauncherHome? at)
    {
        var home = at ?? LauncherHome.Resolve(platform);
        Directory.CreateDirectory(home.Root);
        var service = new LauncherService(platform, home);
        return service;
    }

    /// This launcher's path, for the game folders' stamp (builder=) and the shortcuts (SelfUpdate.StartPath)
    public static string LauncherPath => SelfUpdate.StartPath();

    public void SaveSettings() => Settings.Save(Home.Settings);

    /// Where new builds go: the folder chosen, else ~/Games/Longshot, or a portable launcher's own builds folder
    public string BuildsFolder => string.IsNullOrWhiteSpace(Settings.BuildsFolder) && Home.Portable ? Home.PortableBuilds : Settings.BuildsFolderOrDefault();
    public string Work => string.IsNullOrWhiteSpace(Settings.Work) ? Home.Work : Settings.Work;

    /// A folder the command line named (--launch), listed if it is not yet
    public BuildEntry Ensure(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (Registry.FindByFolder(full) is { } known) return known;
        var version = GameFolder.Version(full);
        var entry = Registry.Add(full, version, Versions.TrackOf(version));
        Registry.Save();
        return entry;
    }

    public bool IsBuilt(BuildEntry b) => GameFolder.IsBuilt(b.Folder, Platform);

    /// The newest version of each channel a build follows, and the one new builds start on; and whether the launcher's own channel
    /// has a newer launcher. False: the server answered nothing, or no file passed its checks (Releases.Problem says why); the old
    /// answers are kept
    public async Task<bool> CheckReleasesAsync(CancellationToken cancel = default)
    {
        var any = false;
        // (each product's release list once per check: Longshot's three channels from one list, the launcher's from its own)
        foreach (var (channel, info) in await Releases.ChannelsAsync(Versions.Channels, cancel))
        {
            if (info == null) continue;
            newest[channel] = info.Version;
            any = true;
        }
        LauncherUpdate = null;
        // (only a launcher started from its own file can replace itself: not a Flatpak's, which Flatpak updates)
        if (Program.Layout.File != null && await LauncherReleases.ChannelAsync(LauncherChannel, cancel) is { } own
            && SelfUpdate.UpdateTo(OwnTag, own, Platform, LauncherChannel, Program.Layout.UpdateKey) != null)
            LauncherUpdate = own;
        Checked = true;
        if (any) PrefetchLikely();
        return any;
    }

    /// The tools of the version the player will most likely build next, started in the background (ToolsPrefetch.Likely), so a
    /// first build, or an update when the player chose that in Settings (DownloadUpdatesEarly), doesn't wait on them in step 1.
    /// Off with LONGSHOT_LAUNCHER_NO_PREFETCH=1
    public void PrefetchLikely()
    {
        if (!ToolsPrefetch.EnabledBy(Environment.GetEnvironmentVariable)) return;
        if (ToolsPrefetch.Likely(Registry.Builds, NewestOf(DefaultChannel), UpdateFor, Tools.IsInstalled, Settings.DownloadUpdatesEarly) is { } tag)
            Prefetch.Get(tag, early: true);
    }

    /// The launcher's update, downloaded, checked and put in place; then the new launcher is started and this one should close.
    /// The update keeps the launcher's kind and name (Program.Layout): an installed one stays the installed program, a portable one
    /// takes the program out of the portable zip and stays beside its "portable" file. Throws with a reason a player can read.
    /// progress: the download (0..1); step: each step after it, for the screen ("Checking the new launcher…"). cancel stops the
    /// download and its check, never the swap
    public async Task UpdateLauncherAsync(Action<double>? progress = null, CancellationToken cancel = default, Action<string>? step = null)
    {
        var release = LauncherUpdate ?? throw new InvalidOperationException("There is no launcher update.");
        var layout = Program.Layout;
        var current = layout.File
                      ?? throw new InvalidOperationException("This launcher was not started from its own file, so it cannot replace itself.");
        var file = release.Launcher.TryGetValue(layout.UpdateKey, out var f) ? f
            : throw new InvalidDataException($"{Versions.Shown(release.Version)} has no launcher of this kind ({layout.UpdateKey}).");
        var entry = layout.UpdateKey == ReleaseManifest.WindowsPortable ? LauncherLayout.ProgramName : null;
        var staged = await SelfUpdate.DownloadAsync(Fetcher, file, current, progress, cancel, entry);
        try
        {
            step?.Invoke(Tr.T("Checking that the new launcher starts…"));
            if (!await SelfUpdate.SelfCheckAsync(staged, release.Version, TimeSpan.FromSeconds(60)))
                throw new InvalidDataException(Tr.F("The new launcher ({0}) did not start on this PC, so it was not put in place.", release.Version));
            step?.Invoke(Tr.T("Putting the new launcher in place…"));
            SelfUpdate.Swap(current, staged, Platform);
        }
        finally
        {
            File.Delete(staged);
        }
        // (this one lets go of the home first: the new one takes it as it starts)
        step?.Invoke(Tr.T("Starting the new launcher…"));
        Program.ReleaseLock();
        using var _ = Process.Start(new ProcessStartInfo(current) { UseShellExecute = false });
    }

    public string? UpdateFor(BuildEntry b) => UpdatePolicy.UpdateTo(b, newest);

    /// The versions a build can be changed to (Other versions), from the list the update check read: none before it answered
    public IReadOnlyList<OtherVersion> OtherVersionsOf(BuildEntry b) => OtherVersions.Of(b, Releases.Listed, Product.Longshot);

    /// What's new from a build's version to the one offered, from the list the update check read (ReleaseNotes)
    public IReadOnlyList<ReleaseNote> NotesFor(string? have, string offered) => ReleaseNotes.Between(Releases.Listed, have, offered, Product.Longshot);

    /// What's new from this launcher to the one its channel offers; empty when none is offered
    public IReadOnlyList<ReleaseNote> LauncherNotes() =>
        LauncherUpdate is { } u ? ReleaseNotes.Between(LauncherReleases.Listed, OwnTag, u.Version, Product.Launcher) : Array.Empty<ReleaseNote>();

    public string? NewestOf(string channel) => newest.TryGetValue(channel, out var v) ? v : null;

    /// What a channel's newest is, as the update check found it (also the screenshots' stand-in for the server)
    public void SetNewest(string channel, string? tag) => newest[channel] = tag;

    /// The launcher the update check offers (the screenshots' stand-in for the server)
    public void SetLauncherUpdate(ReleaseInfo? release) => LauncherUpdate = release;

    /// The build's game, from its folder (no window of the launcher needed). Returns what the window says first, if anything: the
    /// saves brought together before it started (PlayFolder)
    public string? Play(BuildEntry b) => PlayFolder(b.Folder);

    /// A game folder's game, with the saves and the settings every build shares: the old builds' saves brought in first
    /// (GatherSaves), the player's settings brought to a build that has none (CarrySettings), and the shared user folder given to
    /// it (GamePlay.UserFolder, StartInfo). Returns what those did or why the gather could not (SaveText.Gathered), null when
    /// there was nothing to do
    public string? PlayFolder(string folder)
    {
        var said = GatherSaid(GatherSaves());
        var user = GamePlay.UserFolder(SaveEnv, folder);
        if (user != null)
        {
            // (the game makes it too; one it cannot make is the game's to report)
            try { Directory.CreateDirectory(user); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        if (CarrySettings(folder) is { } carried) said = said == null ? carried : said + " " + carried;
        if (OperatingSystem.IsWindows())
            foreach (var line in CarryDriverSettings(folder)) said = said == null ? line : said + " " + line;
        // (the performance statistics: the folder's tick made the player's answer, and what the last sessions left sent)
        PerfReports.BeforePlay(folder, Settings.SendPerformanceStatistics, Settings.KeepSentPerformanceStatistics, Tools);
        // (the crash reports: the folder's file made the player's answer, for the crash helper to read after a crash)
        CrashReports.BeforePlay(folder, Settings.SendCrashReportsAutomatically);
        using var _ = Process.Start(GamePlay.StartInfo(folder, Platform, user: user,
                                                       intelCompiles: IntelCompilesApply ? Settings.IntelCompiles : GamePlay.IntelCompilesAuto,
                                                       intelLongerLimits: Settings.IntelLongerLimits && IntelCompilesApply));
        return said;
    }

    /// (Windows) What Windows and the NVIDIA driver keep for the other builds' programs, by their paths, given to this build's
    /// program when it has none of its own (DriverSettings): Windows' graphics preference and an NVIDIA program profile. The
    /// sources: the other builds' games, the most recently updated first (three at most: each NVIDIA lookup loads the driver's
    /// settings). What the window says; a failure (the driver refusing) is left out, and the game starts as before
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public IReadOnlyList<string> CarryDriverSettings(string folder)
    {
        var said = new List<string>();
        var target = GameFolder.ExePath(folder, Platform);
        var sources = Registry.Builds.OrderByDescending(b => b.Updated).Select(b => b.Folder)
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            .Select(f => GameFolder.ExePath(f, Platform)).Take(3).ToList();
        if (sources.Count == 0) return said;
        try
        {
            if (DriverSettings.GpuPreference(new UserRegistry(), target, sources) != null)
                said.Add(Tr.T("Windows' graphics choice for your other versions now applies to this one too."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try
        {
            if (NvApiProfiles.Open() is { } nvidia && DriverSettings.Nvidia(nvidia, target, sources) is { } profile)
                said.Add(Tr.F("Your NVIDIA settings for your other versions (the {0} profile) now apply to this one too.", profile));
        }
        catch (NvidiaProblem) { }
        return said;
    }

    /// The player's game settings brought to the build in folder when its own file has none (SettingsCarry.Fill): the newest of
    /// the shared user folder's and the other builds' (BuildFolders). What the window says, null when nothing was brought. A
    /// failure is left out: the game starts with its own file, as before
    public string? CarrySettings(string folder)
    {
        try
        {
            return SettingsCarry.Fill(folder, SaveEnv.LongshotUser, BuildFolders()) != null
                ? Tr.T("Your game settings from your other versions now apply to this one too.") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// The settings file a build reads when the launcher starts it: the shared user folder's once there is one (a Longshot that
    /// keeps its settings there made it, Longshot's host/options_path.c), else the build's own. Hookshot's settings are imported
    /// into it
    public string OptionsFileOf(string folder) =>
        GamePlay.UserFolder(SaveEnv, folder) is { } user && File.Exists(Path.Combine(user, SettingsCarry.FileName))
            ? Path.Combine(user, SettingsCarry.FileName) : Path.Combine(folder, SettingsCarry.FileName);

    /// The game folders the builds kept their own saves and settings in: every build's, every Longshot game folder in the builds
    /// folder (a version an update replaced before it took its saves along is no longer listed, its files still there; only
    /// Longshot's, never another game's such as Hookshot's), and the 2.x Builder's (not for a portable launcher)
    IReadOnlyList<string> BuildFolders()
    {
        IEnumerable<string> inBuilds;
        try
        {
            inBuilds = Directory.Exists(BuildsFolder)
                ? Directory.EnumerateDirectories(BuildsFolder).Where(d => GameFolder.IsGameFolder(d, Platform)).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { inBuilds = Enumerable.Empty<string>(); }
        var all = Registry.Builds.Select(b => b.Folder).Concat(inBuilds);
        return (Home.Portable ? all : all.Append(LauncherSettings.DefaultBuildsFolder(SaveEnv.Getenv))).ToList();
    }

    /// What the window says of a gather (GatherSaves): which save every version uses now and where the backup is, or why it
    /// could not; null when there was nothing to do
    public static string? GatherSaid((SaveGathered? Done, string? Problem) g) =>
        g.Done != null ? SaveText.Gathered(g.Done) : g.Problem != null ? Tr.F("Your saves were not brought together: {0}", g.Problem) : null;

    /// (Windows) The saves the builds kept in their own folders (BuildFolders) brought into the shared one (Saves.Gather): when
    /// the window opens and before a game starts. Done: what it did (null: nothing to bring, or Longshot runs); Problem: why it
    /// could not, with nothing changed
    public (SaveGathered? Done, string? Problem) GatherSaves()
    {
        if (Platform != Platform.Windows) return (null, null);
        try
        {
            return (Saves.Gather(SaveEnv, Saves.OldUserFolders(SaveEnv, BuildFolders(), null)), null);
        }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException) { return (null, e.Message); }
    }

    public void SetDefault(BuildEntry b)
    {
        Registry.SetDefault(b.Id);
        Registry.Save();
    }

    /// The old versions' folders an update left in the builds folder, with nothing of the player's in them (Leftovers.Find)
    public IReadOnlyList<Leftover> FindLeftovers() =>
        Leftovers.Find(BuildsFolder, Registry.Builds.Select(b => b.Folder), Platform, Settings.KeptFolders, new[] { Home.Root });

    /// The rows that lost the game their update built in a new folder (Leftovers.LostUpdate) given it back, saved, and returned
    public IReadOnlyList<BuildEntry> RelistLostUpdates()
    {
        var moved = new List<BuildEntry>();
        foreach (var b in Registry.Builds.ToList())
        {
            var listed = Registry.Builds.Select(x => x.Folder).Append(Home.Root);
            if (Leftovers.LostUpdate(BuildsFolder, b, listed, Platform) is not { } found) continue;
            LauncherLog.Add($"{b.Version} in {b.Folder} has no game; its update's {found.Version} in {found.Folder} is listed in its place");
            moved.Add(Registry.Replace(b.Id, found.Folder, found.Version, b.Channel));
        }
        if (moved.Count == 0) return moved;
        remeasure = true;
        try { Registry.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        PruneTools();
        return moved;
    }

    /// What deleting them frees: their files with no hard link outside them (Disk.Measure: a game file a build was linked to
    /// stays with that build). Seconds for a game folder: off the window's thread
    public static long LeftoverFrees(IReadOnlyList<Leftover> leftovers) =>
        Disk.Measure(leftovers.Select(l => l.Folder).ToList()).Sum(s => s.Frees);

    /// An old version's folder deleted (BuildRegistry.DeleteFolder: a game folder only, never one that holds a build's or the
    /// launcher's home)
    public void DeleteLeftover(Leftover leftover) =>
        BuildRegistry.DeleteFolder(leftover.Folder, Platform, Registry.Builds.Select(b => b.Folder).Append(Home.Root).ToList());

    /// What deleting a build's folder must leave: the other builds' folders and the launcher's home (BuildRegistry.DeleteFolder)
    public IReadOnlyList<string> KeepOf(BuildEntry b) => Registry.Builds.Where(x => x.Id != b.Id).Select(x => x.Folder).Append(Home.Root).ToList();

    /// The build out of the list; with deleteFiles, its game folder from the disk too (only a game folder: BuildRegistry.DeleteFolder)
    public void Remove(BuildEntry b, bool deleteFiles)
    {
        if (deleteFiles) BuildRegistry.DeleteFolder(b.Folder, Platform, KeepOf(b));
        Registry.Remove(b.Id);
        remeasure = true;   // (the game files it shared with another build are that one's alone now: Measure)
        Registry.Save();
        PruneTools();
    }

    /// The builds whose folder was deleted outside the launcher, out of the list (BuildRegistry.ForgetGone) and builds.json; the
    /// tools only they used go too. keep: the builds the launcher is deleting itself (their row goes when their folder has)
    public IReadOnlyList<BuildEntry> ForgetGone(IReadOnlyCollection<string> keep)
    {
        var gone = Registry.ForgetGone(keep);
        if (gone.Count == 0) return gone;
        remeasure = true;   // (the game files it shared with another build are that one's alone now: Measure)
        try { Registry.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        PruneTools();
        return gone;
    }

    /// The tools of versions no build uses any more go, but not the ones fetched this session for a build to come. While a fetch
    /// runs the prune waits (it would take the fetch's staging folder and downloads) and runs when the last one ends (Idle)
    public void PruneTools()
    {
        if (Prefetch.Busy)
        {
            Interlocked.Exchange(ref pruneLater, 1);
            if (Prefetch.Busy) return;
            // (the last fetch ended between the two looks, its Idle maybe before the flag: prune now)
            Interlocked.Exchange(ref pruneLater, 0);
        }
        try { Tools.Prune(Registry.Builds.Select(x => x.Version).OfType<string>().Concat(Prefetch.Tags)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    bool measuring, remeasure;
    int pruneLater;

    /// The builds' folders measured together in the background (Disk.Measure: a game file hard-linked into several builds counted
    /// once), when one is not measured yet or a build went; the sizes are kept in builds.json. Then done runs on the UI thread.
    /// Asked while a measure runs, it measures again after it
    public void Measure(Action done)
    {
        if (measuring) return;   // (what changed meanwhile is measured after it)
        var todo = Registry.Builds.Where(b => Directory.Exists(b.Folder)).Select(b => (b, b.Folder, b.Updated)).ToList();
        if (!remeasure && todo.All(t => t.b.Bytes != null && t.b.Counted != null && t.b.Frees != null)) return;
        if (todo.Count == 0) { remeasure = false; return; }
        measuring = true;
        remeasure = false;
        Task.Run(() =>
        {
            IReadOnlyList<Disk.FolderSize> sizes;
            try { sizes = Disk.Measure(todo.Select(t => t.Folder).ToList()); }
            catch (Exception) { sizes = Array.Empty<Disk.FolderSize>(); }
            ViewModels.Ui.Post(() =>
            {
                measuring = false;
                // (a row whose folder changed, or that was built again in its folder, while it was measured keeps its null, and is
                // measured again below: Add and Replace move its Updated)
                for (var i = 0; i < sizes.Count; i++)
                    if (todo[i].b.Folder == todo[i].Folder && todo[i].b.Updated == todo[i].Updated && Registry.Builds.Contains(todo[i].b))
                        (todo[i].b.Bytes, todo[i].b.Counted, todo[i].b.Frees) = (sizes[i].Bytes, sizes[i].Counted, sizes[i].Frees);
                try { Registry.Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                done();
                // (a build went, or one was added or rebuilt while this measured: again. Not after a failure, which would repeat)
                if (sizes.Count == todo.Count && (remeasure || Registry.Builds.Any(b => b.Frees == null && Directory.Exists(b.Folder)))) Measure(done);
            });
        });
    }

    /// Longshot's save slots, the same for every build (Saves.Shared)
    public IReadOnlyList<SaveSlot> SaveSlots()
    {
        var set = Saves.Shared(SaveEnv);
        return set.Found ? Saves.Describe(set.Profiles[0].Path) : Array.Empty<SaveSlot>();
    }
}
