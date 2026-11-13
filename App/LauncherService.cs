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
    /// This launcher's own version (SelfUpdate.OwnTag), and the one its channel (LauncherChannel) offers, if any
    public string OwnTag { get; } = SelfUpdate.OwnTag();
    public ReleaseInfo? LauncherUpdate { get; private set; }
    public string LauncherChannel => SelfUpdate.ChannelOf(OwnTag, Settings.LauncherChannel);
    /// The channel new builds start on: the player's pick, else the launcher's own (LauncherSettings.BuildChannel)
    public string DefaultChannel => Settings.BuildChannel(OwnTag);
    /// The save folders are worked out from this (the screenshots point it into a temporary folder)
    public SaveEnvironment SaveEnv { get; set; }
    /// The makers of this PC's graphics cards (Linux only for now: SystemInfo.GpuVendors)
    public IReadOnlyList<string> Gpus { get; set; }
    /// Whether the update check has answered (or was not asked: offline)
    public bool Checked { get; set; }

    readonly Dictionary<string, string?> newest = new();

    LauncherService(Platform platform, LauncherHome home)
    {
        Platform = platform;
        Home = home;
        Registry = BuildRegistry.Load(home.Builds);
        Settings = LauncherSettings.Load(home.Settings);
        // (release files and packages through git-cdn.magicbean.dev first: CdnFetcher; LONGSHOT_LAUNCHER_NO_CDN=1 turns it off)
        var cdn = CdnFetcher.EnabledByEnvironment();
        Fetcher = new CdnFetcher(Slow(new HttpFetcher(TimeSpan.FromSeconds(60))), cdn);
        Tools = new Toolchains(home.Toolchains, Fetcher);
        // (its own timeout, shorter: the check at start should not keep the window waiting on a server that does not answer)
        var check = new CdnFetcher(Slow(new HttpFetcher(TimeSpan.FromSeconds(20))), cdn);
        var keys = ReleaseKeys.Current();
        Releases = new Releases(check, platform, keys, product: Product.Longshot);
        // (the launcher's own releases: its own versions on its own three channels, chosen apart from the builds' in Settings)
        LauncherReleases = new Releases(check, platform, keys, product: Product.Launcher);
        SaveEnv = SaveEnvironment.Current() with { Platform = platform, BackupFolder = home.Portable ? home.PortableSaveBackups : null };
        Gpus = SystemInfo.GpuVendors(platform);
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

    /// This launcher's path, for the game folders' stamp (builder=): the AppImage on Linux, else this program
    public static string LauncherPath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath ?? "LongshotLauncher";

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
        if (await LauncherReleases.ChannelAsync(LauncherChannel, cancel) is { } own
            && SelfUpdate.UpdateTo(OwnTag, own, Platform, LauncherChannel, Program.Layout.UpdateKey) != null)
            LauncherUpdate = own;
        Checked = true;
        return any;
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
            step?.Invoke("Checking that the new launcher starts…");
            if (!await SelfUpdate.SelfCheckAsync(staged, release.Version, TimeSpan.FromSeconds(60)))
                throw new InvalidDataException($"The new launcher ({release.Version}) did not start on this PC, so it was not put in place.");
            step?.Invoke("Putting the new launcher in place…");
            SelfUpdate.Swap(current, staged, Platform);
        }
        finally
        {
            File.Delete(staged);
        }
        // (this one lets go of the home first: the new one takes it as it starts)
        step?.Invoke("Starting the new launcher…");
        Program.ReleaseLock();
        using var _ = Process.Start(new ProcessStartInfo(current) { UseShellExecute = false });
    }

    public string? UpdateFor(BuildEntry b) => UpdatePolicy.UpdateTo(b, newest);

    public string? NewestOf(string channel) => newest.TryGetValue(channel, out var v) ? v : null;

    /// What a channel's newest is, as the update check found it (also the screenshots' stand-in for the server)
    public void SetNewest(string channel, string? tag) => newest[channel] = tag;

    /// The build's game, from its folder (no window of the launcher needed)
    public void Play(BuildEntry b)
    {
        using var _ = Process.Start(GamePlay.StartInfo(b.Folder, Platform));
    }

    public void SetDefault(BuildEntry b)
    {
        Registry.SetDefault(b.Id);
        Registry.Save();
    }

    /// What deleting a build's folder must leave: the other builds' folders and the launcher's home (BuildRegistry.DeleteFolder)
    public IReadOnlyList<string> KeepOf(BuildEntry b) => Registry.Builds.Where(x => x.Id != b.Id).Select(x => x.Folder).Append(Home.Root).ToList();

    /// The build out of the list; with deleteFiles, its game folder from the disk too (only a game folder: BuildRegistry.DeleteFolder)
    public void Remove(BuildEntry b, bool deleteFiles)
    {
        if (deleteFiles) BuildRegistry.DeleteFolder(b.Folder, Platform, KeepOf(b));
        Registry.Remove(b.Id);
        Registry.Save();
        PruneTools();
    }

    /// The tools of versions no build uses any more go
    public void PruneTools()
    {
        try { Tools.Prune(Registry.Builds.Select(x => x.Version).OfType<string>()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Each build's folder measured in the background, once (its size is kept in builds.json); then done runs on the UI thread
    public void Measure(Action done)
    {
        var todo = Registry.Builds.Where(b => b.Bytes == null && Directory.Exists(b.Folder)).Select(b => (b, b.Folder)).ToList();
        if (todo.Count == 0) return;
        Task.Run(() =>
        {
            var sizes = todo.Select(t => (t.b, Bytes: Disk.FolderBytes(t.Folder))).ToList();
            ViewModels.Ui.Post(() =>
            {
                foreach (var (b, bytes) in sizes) b.Bytes = bytes;
                try { Registry.Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                done();
            });
        });
    }

    /// The saves of a build: its own folder's on Windows, the shared ones on Linux
    public IReadOnlyList<SaveSlot> SavesOf(BuildEntry b)
    {
        var set = Saves.ForBuild(b.Folder, SaveEnv);
        return set.Found ? Saves.Describe(set.Profiles[0].Path) : Array.Empty<SaveSlot>();
    }
}
