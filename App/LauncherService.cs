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
    /// The screenshots and checks can supply a probe for each wizard.
    public Func<Task<IReadOnlyList<GpuRequirement>?>> VulkanProbe { get; set; } = () => Task.Run(SystemRequirements.ReadGpus);
    /// This PC's CPU name (WindowsCpu, SystemInfo.CpuName), read once, the first time it is asked for; "" when the system names none
    public string Cpu => cpu ??= (OperatingSystem.IsWindows() ? WindowsCpu.Read() : SystemInfo.CpuName()) ?? "";
    string? cpu;
    /// The Intel shader compiles choice applies on this PC (SystemInfo.IntelCompilesApply): Windows with Intel graphics
    public bool IntelCompilesApply => SystemInfo.IntelCompilesApply(Platform, GpuCards);
    /// Whether the update check has answered (or was not asked: offline)
    public bool Checked { get; set; }

    readonly Dictionary<string, string?> newest = new();
    /// Longshot's open pull requests as Beanstalk last listed them (PullRequests.ListAsync), and when: null before it answered
    IReadOnlyList<PullRequest>? pulls;
    DateTime pullsAt;
    /// How long a list is good for before it is asked again (Beanstalk's own copy is at most a minute old)
    static readonly TimeSpan PullsFresh = TimeSpan.FromMinutes(1);

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
        // (each version's builder in its version folder, versions/<v>; an older launcher's toolchains/ read as fetch's bases)
        Tools = new Toolchains(BuilderVersions.Root(home.Root), platform, home.Toolchains);
        // (its own timeout, shorter: the check at start should not keep the window waiting on a server that does not answer)
        var check = new CdnFetcher(Slow(new HttpFetcher(TimeSpan.FromSeconds(20))), cdn);
        var keys = ReleaseKeys.Current();
        Releases = new Releases(check, platform, keys, product: Product.Longshot);
        // (the launcher's own releases: its own versions on its own three channels, chosen apart from the builds' in Settings)
        LauncherReleases = new Releases(check, platform, keys, product: Product.Launcher);
        // (longshot-builder's releases: fetched by a builder of this PC's, each install said in the launcher's log)
        Prefetch = new ToolsPrefetch((tag, progress, cancel) => ToolsSource.EnsureAsync(Tools, Releases, tag, progress, cancel,
                                                                                        pullRequest: FindPullRequestAsync, log: LauncherLog.Add,
                                                                                        cache: Path.Combine(Work, Disk.LongshotBuildCache)));
        // (a prune put off while a fetch ran, once none does: PruneTools)
        Prefetch.Idle += () => ViewModels.Ui.Post(() => { if (Interlocked.Exchange(ref pruneLater, 0) == 1) PruneTools(); });
        SaveEnv = SaveEnvironment.Current() with
        {
            Platform = platform,
            BackupFolder = home.Portable ? home.PortableSaveBackups : null,
            UserFolder = home.Portable ? home.PortableUser : null,
        };
        // (the profiles' folders, <launcher data>/profiles: made once from what the launcher kept before, the originals left)
        SaveEnv = SaveEnv with { ProfilesRoot = ProfileStore.Root(home.Root) };
        MoveToProfileFolders();
        LauncherProfiles.Ensure(SaveEnv, KnownGame());
        if (Settings.CurrentProfile != null && LauncherProfiles.Find(SaveEnv, Settings.CurrentProfile) == null) Settings.CurrentProfile = null;
        SaveEnv = SaveEnv with { SavesHome = LauncherProfiles.SavesHome(SaveEnv, Profile.Id) };
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

    public void SaveSettings()
    {
        Settings.Save(Home.Settings);
    }

    /// The game copy the launcher knows: the game file last picked, else the default version's (its .nup or its dump folder), else
    /// the newest of any version's
    public string? KnownGame() =>
        Settings.Nup is { Length: > 0 } nup ? nup
        : Registry.Default is { } d && ProfileMigration.GameOf(d) is { } g ? g
        : Registry.Builds.OrderByDescending(b => b.Updated).Select(ProfileMigration.GameOf).FirstOrDefault(x => x != null);

    /// The move to the profiles' folders (ProfileMigration), once: every profile a launcher's settings listed (the default
    /// always), its saves copied from the --user folder, the game's settings copied, its game copy and upscalers from its version;
    /// what settings.json held of the profiles goes into their profile.json and out of settings.json, a copy of it kept beside it
    /// (settings.json.before-profile-folders). Nothing else is moved: the originals stay where they are
    void MoveToProfileFolders()
    {
        if (SaveEnv.ProfilesRoot is not { } root) return;
        try
        {
            var result = ProfileMigration.MoveFromSettings(Settings, Home.Settings, Home.Settings + ".before-profile-folders", SaveEnv,
                                                           Registry.Builds, KnownGame(), oldProfilesRoot: Path.Combine(SaveEnv.LongshotRoot, "profiles"));
            if (result != null)
            {
                LauncherLog.Add($"profiles: made in {root} ({string.Join(", ", result.Profiles)}), the saves copied; the originals stay where they were");
                foreach (var n in result.Notes) LauncherLog.Add("profiles: " + n);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Add($"profiles: the move to {root} stopped ({e.Message}); it is tried again at the next start");
        }
    }

    /// The versions longshot-builder built in a folder of their own, copied once each into their version folder
    /// (VersionFolderMigration: versions/<v>/builds/default, the builder put beside it from the installed tools), off the window's
    /// thread, then their rows pointed at the copies on it; the originals stay where they are. Not while a game runs (its folder's
    /// files may be open): then at the next start. done: told on the window's thread once rows moved. False: nothing to move
    public bool MoveToVersionFolders(Action? done = null)
    {
        var moves = VersionFolderMigration.Plan(Registry.Builds, Home.Root);
        if (moves.Count == 0) return false;
        Task.Run(() =>
        {
            try
            {
                if (Saves.Running(Platform).Count > 0)
                {
                    LauncherLog.Add("versions: a game is running: the versions move into their version folders at the next start");
                    return;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return; }
            var copied = new List<VersionFolderMigration.Move>();
            foreach (var m in moves)
                try
                {
                    var version = Path.GetDirectoryName(Path.GetDirectoryName(m.To)!)!;
                    if (Tools.Legacy(m.Build.Version!)?.BuilderExe is { } exe) BuilderVersions.Install(version, exe, Platform);
                    VersionFolderMigration.Copy(m);
                    // (the row's work folder with it, where its first Play's update finds the last build's lift and objects)
                    if (!VersionFolderMigration.CarryWork(m, Work, Platform))
                        LauncherLog.Add($"versions: the work folder of {m.From} was not moved; the first Play of {m.To} updates from the shared stores");
                    copied.Add(m);
                    LauncherLog.Add($"versions: {m.From} copied to {m.To}; the original stays where it is");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    LauncherLog.Add($"versions: {m.From} was not copied to {m.To} ({e.Message}); it is played where it is");
                }
            if (copied.Count == 0) return;
            ViewModels.Ui.Post(() =>
            {
                foreach (var m in copied)
                    if (Registry.Builds.FirstOrDefault(b => b.Id == m.Build.Id && BuildRegistry.SameFolder(b.Folder, m.From)) is { } row)
                        row.Folder = m.To;
                try { Registry.Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LauncherLog.Add("versions: the list was not saved: " + e.Message); }
                remeasure = true;
                done?.Invoke();
            });
        });
        return true;
    }

    // ---- the launcher's profiles (LauncherProfiles): the current one's saves are SaveEnv's, its folder what Play names

    /// The profiles are the folders of profiles/, read each time (LauncherProfiles): another launcher's change shows at once
    public LauncherProfile Profile => LauncherProfiles.Current(SaveEnv, Settings);
    public IReadOnlyList<LauncherProfile> Profiles => LauncherProfiles.All(SaveEnv);

    /// The save the current profile's Play starts on (null: its newest), written to its profile.json at once
    public void SetStartSlot(string? slot) => LauncherProfiles.SetStartSlot(SaveEnv, Profile.Id, slot);

    /// The current profile played this version (a BuildEntry.Id) now
    public void MarkPlayed(string version) => LauncherProfiles.MarkPlayed(SaveEnv, Profile.Id, version, DateTime.Now);

    /// The current profile's folder (ProfileStore: profile.json, settings.ini, saves/)
    public string ProfileFolder => LauncherProfiles.FolderOf(SaveEnv, Profile.Id) ?? ProfileStore.Dir(ProfileStore.Root(Home.Root), Profile.Id);

    /// The launcher on another profile: its saves become SaveEnv's (the Saves page, the save editor), its folder the one Play
    /// names; saved at once
    public void SwitchProfile(string id)
    {
        LauncherProfiles.Of(SaveEnv, id);
        Settings.CurrentProfile = id == LauncherProfiles.DefaultId ? null : id;
        SaveEnv = SaveEnv with { SavesHome = LauncherProfiles.SavesHome(SaveEnv, id) };
        // (what the builders said was of the other profile's game folders: asked again for this one's)
        BuilderInfos.Clear();
        try { SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// The version folder a version of the builder's kind is played and built in (BuilderVersions); null for a name that is no
    /// version's
    public string? VersionFolderFor(string version) =>
        Versions.IsTagName(version) ? BuilderVersions.FolderOf(Home.Root, version) : null;

    /// An instance's folder renamed (an update's or a go-back's, the home's move, or one put back): the game files last picked
    /// follow it when they were in it (LauncherSettings.Follow), saved. The list's rows follow in BuildRegistry.Moved
    public void FolderMoved(string from, string to)
    {
        if (!Settings.Follow(from, to)) return;
        try { SaveSettings(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LauncherLog.Add($"settings: not saved after {from} moved: {e.Message}"); }
    }

    /// The current settings written, or why they could not be: a Play asks immediately before it
    /// routes through Steam (round-four P2 — the arrival instance reads the settings FILE, so a
    /// Play whose save just failed must stay direct, where this window's in-memory values are
    /// already the player's current ones). Quiet: the settings page has its own toast path
    public bool TrySaveSettings()
    {
        try { Settings.Save(Home.Settings); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// Where new builds go: the folder chosen, else ~/Games/Longshot, or a portable launcher's own builds folder
    public string BuildsFolder => string.IsNullOrWhiteSpace(Settings.BuildsFolder) && Home.Portable ? Home.PortableBuilds : Settings.BuildsFolderOrDefault();
    public string Work => string.IsNullOrWhiteSpace(Settings.Work) ? Home.Work : Settings.Work;

    /// (A portable launcher with no build listed yet) the installed launcher's builds and saves on this PC, to offer
    /// (OtherLauncher.InstalledOne); null when there is none, or nothing in it
    public OtherLauncher? InstalledLauncher()
    {
        if (!Home.Portable || Registry.Builds.Count > 0) return null;
        try { return OtherLauncher.InstalledOne(Platform, Environment.GetEnvironmentVariable, Home.Root) is { HasAnything: true } o ? o : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// Another launcher's builds listed here and its saves copied in where there are none (OtherLauncher.TakeInto), saved
    public OtherTaken TakeIn(OtherLauncher other)
    {
        var taken = other.TakeInto(Registry, SaveEnv, () => Saves.Running(Platform));
        foreach (var b in taken.Added) LauncherLog.Add($"build {b.Version} in {b.Folder} listed from the launcher in {other.Home}");
        if (taken.Added.Count > 0)
        {
            remeasure = true;
            Registry.Save();
        }
        return taken;
    }

    /// "Add a build folder…": a game folder, a folder of them (a builds folder), or a launcher's data folder
    /// (LongshotLauncher-data: its builds, and its saves where there are none here) listed. The rows added (none: nothing new
    /// in it) and what it says of the saves
    public OtherTaken AddFolder(string picked)
    {
        if (OtherLauncher.At(picked, Platform, Environment.GetEnvironmentVariable) is { } other) return TakeIn(other);
        var added = new List<BuildEntry>();
        foreach (var dir in OtherLauncher.GamesIn(picked, Platform))
        {
            if (Registry.FindByFolder(dir) != null) continue;
            var version = GameFolder.Version(dir);
            var b = Registry.Add(dir, version, OtherLauncher.ChannelNamed(dir, version));
            LauncherLog.Add($"build {b.Version} in {b.Folder} added by the player");
            added.Add(b);
        }
        if (added.Count > 0)
        {
            remeasure = true;
            Registry.Save();
        }
        return new OtherTaken(added, false, null);
    }

    /// A game, or another launcher's data, in picked (AddFolder found it, though everything in it may be listed already)
    public bool Finds(string picked) =>
        OtherLauncher.At(picked, Platform, Environment.GetEnvironmentVariable) is { HasAnything: true } || OtherLauncher.GamesIn(picked, Platform).Count > 0;

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

    /// Whether a version's game is there: a version folder's, as its builder's info last said (BuilderInfos), any other's (or one
    /// whose builder was not asked yet) by its program
    public bool IsBuilt(BuildEntry b) =>
        IsBuiltOverride?.Invoke(b)
        ?? (BuilderInfos.Of(b.Folder, Platform)?.Build is { Folder: { } f } build && BuildRegistry.SameFolder(f, b.Folder) ? build.Present
            : GameFolder.IsBuilt(b.Folder, Platform));

    /// Each version folder's builder asked what it is and what the current profile's game folder is (BuilderInfos, `info`), off the
    /// window's thread, one after another; changed: told on the window's thread when an answer differs from what was known
    public void RefreshInfos(Action? changed = null)
    {
        var profile = ProfileFolder;
        var folders = Registry.Builds.Select(VersionFolderOf).OfType<string>().Distinct(StringComparer.Ordinal)
                              .Where(v => BuilderVersions.Is(v, Platform)).ToList();
        if (folders.Count == 0) return;
        _ = Task.Run(async () =>
        {
            var any = false;
            foreach (var v in folders)
                if (await BuilderOf(v).InfoAsync(profile).ConfigureAwait(false) is { } info) any |= BuilderInfos.Set(v, info);
            if (any && changed != null) ViewModels.Ui.Post(changed);
        });
    }

    /// A version folder's builder (its Longshot.exe), for its own commands
    public LongshotBuilder BuilderOf(string versionFolder) =>
        Builders.For(new Toolchain(Path.GetFileName(Path.TrimEndingDirectorySeparator(versionFolder)), versionFolder, BuilderVersions.ExeName(Platform)),
                     Platform, Home, LauncherPath);

    /// Whether a version was built with make_game.py (OldVersions): not played, built or updated here, only shown as not supported,
    /// with the way back to the stable launcher
    public bool IsOld(BuildEntry b) => IsOldOverride?.Invoke(b) ?? OldVersions.Is(b.Folder, b, Platform, Home.Root);

    /// Whether a version is an old one, without looking: the screenshots' made-up versions are not, unless a scene says
    public Func<BuildEntry, bool>? IsOldOverride { get; set; }

    /// Whether a version's game is there, without looking: the screenshots' made-up versions are built, or (a scene about it) not
    public Func<BuildEntry, bool>? IsBuiltOverride { get; set; }

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
        // (after the launcher's own, and only with a pull request's build installed: one question to Beanstalk for all of them,
        // which costs git.magicbean.dev nothing; a slow answer holds back nothing else)
        if (Registry.Builds.Any(b => Versions.IsPullRequest(b.Version))) await PullRequestsAsync(fresh: true, cancel);
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
        // (never a pull request's: nothing of its code is fetched before the player has read its warnings)
        if (ToolsPrefetch.Likely(Registry.Builds, NewestOf(DefaultChannel), b => Versions.IsPullRequest(b.Version) ? null : UpdateFor(b),
                                 Tools.IsInstalled, Settings.DownloadUpdatesEarly) is { } tag)
            Prefetch.Get(tag, early: true);
    }

    /// The launcher's update, downloaded, checked and put in place; then the new launcher is started and this one should close.
    /// The update keeps the launcher's kind and name (Program.Layout): an installed one stays the installed program, a portable one
    /// takes the program out of the portable zip and stays beside its "portable" file. Throws with a reason a player can read.
    /// progress: the download (0..1); step: each step after it, for the screen ("Checking the new launcher…"). cancel stops the
    /// download and its check, never the swap
    public Task UpdateLauncherAsync(Action<double>? progress = null, CancellationToken cancel = default, Action<string>? step = null) =>
        ReplaceLauncherAsync(LauncherUpdate ?? throw new InvalidOperationException("There is no launcher update."), progress, cancel, step);

    /// Whether this launcher can replace itself: started from its own file (not a Flatpak's, which Flatpak updates)
    public static bool CanReplaceItself => Program.Layout.File != null;

    /// The offer of the stable launcher in place of this one (StableSwitch): the current profile's saves folder and what copying
    /// them back to the --user folder would do (SavesCopyBack.Plan), worked out now, nothing changed
    public SavesCopyBack StableSavesPlan() =>
        SavesCopyBack.Plan(LauncherProfiles.SavesHome(SaveEnv, Profile.Id) ?? SaveEnv.LongshotUser, SaveEnv.LongshotUser);

    /// The replacement by the stable launcher (StableSwitch), once the player said yes: the saves copied back first when asked
    /// (copyBack: the plan shown; replaceNewer: the newer files there replaced too), the launcher's channel set to stable, then the
    /// stable channel's newest launcher downloaded, checked and put in place as an update is (ReplaceLauncherAsync), and started;
    /// this one should close. Throws with a reason a player can read; the channel is put back as it was then, the saves copied
    /// stay copied (the profile keeps its own)
    public async Task SwitchToStableAsync(SavesCopyBack? copyBack, bool replaceNewer, Action<double>? progress = null, CancellationToken cancel = default,
                                          Action<string>? step = null)
    {
        if (copyBack != null)
        {
            var copied = await Task.Run(() => copyBack.Copy(replaceNewer), cancel);
            LauncherLog.Add($"stable launcher: {copied} save files copied from {copyBack.From} to {copyBack.To}" +
                            (copyBack.Newer.Count > 0 && !replaceNewer ? $", {copyBack.Newer.Count} newer ones there kept" : ""));
        }
        var before = Settings.LauncherChannel;
        Settings.LauncherChannel = Versions.Stable;
        SaveSettings();
        try
        {
            step?.Invoke(Tr.T("Asking git.magicbean.dev for the stable launcher…"));
            var release = await LauncherReleases.ChannelAsync(Versions.Stable, cancel)
                          ?? throw new InvalidDataException(LauncherReleases.Problem ?? Tr.T("git.magicbean.dev has no stable launcher for this PC yet."));
            if (SelfUpdate.UpdateTo(OwnTag, release, Platform, Versions.Stable, Program.Layout.UpdateKey) == null)
                throw new InvalidDataException(Tr.T("git.magicbean.dev has no stable launcher for this PC yet."));
            await ReplaceLauncherAsync(release, progress, cancel, step);
        }
        catch
        {
            Settings.LauncherChannel = before;
            try { SaveSettings(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// A launcher release downloaded, checked and put in place of this one, then started (UpdateLauncherAsync, SwitchToStableAsync)
    async Task ReplaceLauncherAsync(ReleaseInfo release, Action<double>? progress, CancellationToken cancel, Action<string>? step)
    {
        var layout = Program.Layout;
        var current = layout.File
                      ?? throw new InvalidOperationException("This launcher was not started from its own file, so it cannot replace itself.");
        var file = release.Launcher.TryGetValue(layout.UpdateKey, out var f) ? f
            : throw new InvalidDataException($"{Versions.Shown(release.Version)} has no launcher of this kind ({layout.UpdateKey}).");
        var entry = layout.UpdateKey == ReleaseManifest.WindowsPortable ? LauncherLayout.ProgramName : null;
        LauncherLog.Add($"launcher update: {OwnTag} -> {release.Version} ({layout.UpdateKey}), downloading {LauncherLog.Link(file.Url)}");
        string staged;
        try { staged = await SelfUpdate.DownloadAsync(Fetcher, file, current, progress, cancel, entry); }
        catch (Exception e)
        {
            LauncherLog.Add($"launcher update: download stopped: {e.GetType().Name}: {e.Message}");
            throw;
        }
        try
        {
            LauncherLog.Add($"launcher update: downloaded and checked (SHA-256) as {staged}; starting it with {SelfUpdate.CheckArgument}");
            step?.Invoke(Tr.T("Checking that the new launcher starts…"));
            if (!await SelfUpdate.SelfCheckAsync(staged, release.Version, TimeSpan.FromSeconds(60)))
            {
                LauncherLog.Add($"launcher update: the new launcher did not answer {SelfUpdate.CheckArgument}; not put in place");
                throw new InvalidDataException(Tr.F("The new launcher ({0}) did not start on this PC, so it was not put in place.", release.Version));
            }
            step?.Invoke(Tr.T("Putting the new launcher in place…"));
            // (off the window's thread: the swap may wait a few seconds for a new launcher still held after its check, #223)
            await Task.Run(() => SelfUpdate.Swap(current, staged, Platform));
        }
        finally
        {
            // (never throws: a failure here would hide the reason the update stopped)
            SelfUpdate.Discard(staged);
        }
        // (this one lets go of the home first: the new one takes it as it starts)
        step?.Invoke(Tr.T("Starting the new launcher…"));
        LauncherLog.Add("launcher update: starting the new launcher; this one closes");
        Program.ReleaseLock();
        using var _ = Process.Start(new ProcessStartInfo(current) { UseShellExecute = false });
    }

    /// The version a build updates to, or null: its channel's newest (UpdatePolicy); for a pull request's build, the build of the
    /// pull request's newer push, while it is open and this PC can build it; none for an old version (OldVersions)
    public string? UpdateFor(BuildEntry b) =>
        IsOld(b) ? null
        : Versions.PullRequestOf(b.Version) is { } pr
            ? UpdatePolicy.UpdateTo(b.Version, Versions.PullRequest,
                                    PullRequestOf(pr.Number) is { } open && PullRequests.Problem(open, Platform) == null ? open.Tag : null)
            : UpdatePolicy.UpdateTo(b, newest);

    /// The open pull requests as Beanstalk last listed them; null before it answered
    public IReadOnlyList<PullRequest>? Pulls => pulls;

    /// A pull request from the last list (null: not open, or no list yet)
    public PullRequest? PullRequestOf(int number) => pulls?.FirstOrDefault(p => p.Number == number);

    /// The open pull requests, asked of Beanstalk again when the last list is older than a minute or fresh is set; the last list
    /// when it does not answer (null: none yet)
    public async Task<IReadOnlyList<PullRequest>?> PullRequestsAsync(bool fresh = false, CancellationToken cancel = default)
    {
        if (pulls != null && !fresh && DateTime.UtcNow - pullsAt < PullsFresh) return pulls;
        if (await PullRequests.ListAsync(Releases.Fetcher, cancel) is { } list)
        {
            pulls = list;
            pullsAt = DateTime.UtcNow;
            LauncherLog.Add($"pull requests: {list.Count} open, from Beanstalk");
        }
        else LauncherLog.Add("pull requests: Beanstalk did not answer");
        return pulls;
    }

    /// A pull request as it is now, for its build's tools (SourceBuilds): from a list at most a minute old; null when it is
    /// not open any more, or Beanstalk did not answer
    async Task<PullRequest?> FindPullRequestAsync(int number, CancellationToken cancel) =>
        (await PullRequestsAsync(cancel: cancel).ConfigureAwait(false))?.FirstOrDefault(p => p.Number == number);

    /// The pull requests the update check reads (the screenshots' stand-in for Beanstalk)
    public void SetPullRequests(IReadOnlyList<PullRequest>? list)
    {
        pulls = list;
        pullsAt = DateTime.UtcNow;
    }

    /// A track's versions for the home's Version dropdown (VersionPick.Of), from the list the update check read: only the
    /// installed ones before it answered
    public IReadOnlyList<TrackVersion> VersionsOn(string track) =>
        track == Versions.PullRequest ? VersionPick.OfPullRequests(Registry.Builds, pulls, Platform, foundPulls)
        : VersionPick.Of(track, Registry.Builds, Releases.Listed, Product.Longshot, Platform);


    /// The pull requests the player found by their number (Find a pull request): listed on the track with the trusted ones
    readonly HashSet<int> foundPulls = new();
    public void AddFound(int number) => foundPulls.Add(number);

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
    public string? Play(BuildEntry b) => PlayFolder(b.Folder, b);

    /// A game folder's game, with the saves and the settings every build shares: the old builds' saves brought in first
    /// (GatherSaves), the player's settings brought to a build that has none (CarrySettings), and the shared user folder given to
    /// (GamePlay.UserFolder, StartInfo). Returns what those did or why the gather could not (SaveText.Gathered), null when
    /// there was nothing to do. entry: the list's build of that folder (--play names a folder: the list's, if it holds it).
    public string? PlayFolder(string folder, BuildEntry? entry = null)
    {
        entry ??= Registry.Builds.FirstOrDefault(b => BuildRegistry.SameFolder(b.Folder, folder));
        // (a version built by Longshot's old builder: the stable launcher's to play, OldVersions)
        if (entry != null ? IsOld(entry) : OldVersions.Is(folder, null, Platform, Home.Root))
            throw new OldVersionException(OldVersions.NotPlayed(entry?.Version ?? GameFolder.Version(folder)));
        // (longshot-builder's game reads its files from the .nup where it is: gone, it can't start, and the window offers to pick it
        // again, or to connect its drive. Only a folder whose game longshot-builder built last: LongshotBuilder.CurrentRecord)
        if (LongshotBuilder.MissingNup(folder, entry) is { } moved) throw new NupMovedException(moved.Nup, moved.DriveMissing);
        // (a version folder's game: its builder plays it, with the profile)
        if (PlayVersion(folder, entry)) return null;
        var said = GatherSaid(GatherSaves());
        // (a folder named by --play that no list holds: its user\save the shared save too)
        if (Platform == Platform.Windows) Saves.LinkSaves(SaveEnv, new[] { folder });
        var user = GamePlay.UserFolder(SaveEnv, folder);
        if (user != null)
        {
            // (the game makes it too; one it cannot make is the game's to report)
            try { Directory.CreateDirectory(user); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
        if (CarrySettings(folder) is { } carried) said = said == null ? carried : said + " " + carried;
        if (OperatingSystem.IsWindows())
        {
            foreach (var line in CarryDriverSettings(folder)) said = said == null ? line : said + " " + line;
            if (RoomInNvidiaCache() is { } room) said = said == null ? room : said + " " + room;
        }
        // (the performance statistics: the folder's tick made the player's answer, and what the last sessions left sent)
        PerfReports.BeforePlay(folder, Settings.SendPerformanceStatistics, Settings.KeepSentPerformanceStatistics, Tools,
                               Path.Combine(Work, Disk.LongshotBuildCache));
        // (the crash reports: the folder's file made the player's answer, for the crash helper to read after a crash)
        CrashReports.BeforePlay(folder, Settings.SendCrashReportsAutomatically);
        var start = GamePlay.StartInfo(folder, Platform, user: user,
                                       intelCompiles: IntelCompilesApply ? Settings.IntelCompiles : GamePlay.IntelCompilesAuto,
                                       intelLongerLimits: Settings.IntelLongerLimits && IntelCompilesApply,
                                       gamescope: Platform == Platform.Linux && Settings.Gamescope,
                                       gamescopeOptions: Settings.GamescopeOptions);
        Process.Start(start)?.Dispose();
        return said;
    }

    /// Said when a Play did not get to the game (BuilderVersions.Ended): the message, on any thread
    public event Action<string>? PlayFailed;

    /// A version folder's game played by its builder: versions/<v>/Longshot.exe --profile <the current profile's folder>
    /// --output json --cache <the launcher's builder cache> --no-self-update (the launcher updates its versions). The builder
    /// checks the game folder is the profile's (its fast check),
    /// updates it when it is not, and starts the game with the profile; the launcher's own switches for older games are not
    /// given (BuilderVersions.OldSwitches). False for any other folder. A builder gone from the version folder (taken by an
    /// antivirus) is put back from an older launcher's tools when it kept them; else the next build of the version fetches it
    bool PlayVersion(string folder, BuildEntry? entry)
    {
        if (!BuilderVersions.InVersions(folder, Home.Root)) return false;
        var version = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar))!)!;
        if (!BuilderVersions.Is(version, Platform) && entry?.Version is { } v && Tools.Legacy(v)?.BuilderExe is { } exe)
            try { BuilderVersions.Install(version, exe, Platform); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LauncherLog.Add($"play: the builder was not put back in {version}: {e.Message}"); }
        if (!BuilderVersions.Is(version, Platform)) return false;
        // (the profile's folder, its saves/ made, and the game copy the launcher knows when it names none: what the builder plays)
        var profile = ProfileFolder;
        Directory.CreateDirectory(ProfileStore.SavesOf(profile));
        try { LauncherProfiles.EnsureGame(SaveEnv, Profile.Id, KnownGame()); }
        catch (ProfileFailure e) { LauncherLog.Add("play: the profile was not written: " + e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LauncherLog.Add("play: the profile was not written: " + e.Message); }
        PerfReports.BeforePlay(folder, Settings.SendPerformanceStatistics, Settings.KeepSentPerformanceStatistics, Tools,
                               Path.Combine(Work, Disk.LongshotBuildCache));
        CrashReports.BeforePlay(folder, Settings.SendCrashReportsAutomatically);
        // (Steam Input, the Deck's controller: outside Steam, with Steam up, Play asks Steam to start this profile's tile, which
        // runs this version's builder with the profile, so Steam brings its controller layer up for the game. No tile, a tile
        // of another version, no Steam or a failure: the builder is started here, as below. SteamLaunchRoute holds the rule)
        var route = SteamLaunchRoute.Decide(Platform, SteamShortcuts.LibraryFile(), SteamTile.NameFor(Profile),
                                            BuilderVersions.ExeOf(version, Platform), Environment.GetEnvironmentVariable);
        if (route.Route == SteamLaunchRoute.Kind.RunGameId)
        {
            LauncherLog.Add($"play: through the Steam tile, {route.Uri}: {route.Note}");
            return true;
        }
        LauncherLog.Add("play: direct, " + route.Note);
        var env = GamePlay.Environment(Platform, GamePlay.CurrentEnvironment(),
                                       IntelCompilesApply ? Settings.IntelCompiles : GamePlay.IntelCompilesAuto, Settings.IntelLongerLimits && IntelCompilesApply);
        var start = BuilderVersions.StartInfo(version, Platform, profile, Path.Combine(Work, Disk.LongshotBuildCache), env,
                                              statistics: Settings.SendPerformanceStatistics);
        // (its events read: the last error's text is what a failed Play says)
        start.RedirectStandardOutput = true;
        start.StandardOutputEncoding = System.Text.Encoding.UTF8;
        LauncherLog.Add($"play: {start.FileName} {string.Join(' ', start.ArgumentList)}");
        var builder = Process.Start(start);
        if (builder != null) _ = WatchBuilder(builder, folder);
        return true;
    }

    async Task WatchBuilder(Process builder, string folder)
    {
        using (builder)
        {
            string? error = null;
            try
            {
                // (every line read, the game's too, so its output never fills the pipe; the builder's error events kept)
                while (await builder.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    if (BuildEvents.Parse(line) is BuildEvent.Error e) error = e.Text;
                await builder.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return; }
            if (BuilderVersions.Ended(builder.ExitCode, error) is { } said)
            {
                LauncherLog.Add($"play: the builder of {folder} ended with {builder.ExitCode}");
                PlayFailed?.Invoke(said);
            }
        }
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

    /// The argument that starts the launcher as an administrator to raise NVIDIA's shader cache, and only that (Program.Main,
    /// RaiseNvidiaCacheAsAdmin)
    public const string RaiseNvidiaCacheArgument = "--raise-nvidia-shader-cache";

    /// (Windows) NVIDIA's shader cache size when the driver refused to raise it for want of an administrator (NVAPI -137): Home
    /// offers to raise it as one (RaiseNvidiaCacheAsAdmin). Null: raised, nothing to do, or not NVIDIA
    public uint? NvidiaCacheNeedsAdmin { get; private set; }

    /// (Windows) NVIDIA's shader cache given room for Longshot's pipelines before a build prepares them and before the game
    /// starts (DriverSettings.RaiseShaderCache: under 4 GB raised to 10 GB, for every program, as the control panel's Shader
    /// Cache Size). What the window says, null when nothing was done; a driver that needs an administrator to save leaves
    /// NvidiaCacheNeedsAdmin, any other refusal nothing, and the game starts as before
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public string? RoomInNvidiaCache()
    {
        NvidiaCacheNeedsAdmin = null;
        if (NvApiProfiles.Open() is not { } nvidia) return null;
        uint? short_ = null;
        try
        {
            if ((short_ = DriverSettings.ShaderCacheShort(nvidia)) is not { } was) return null;
            nvidia.SetShaderCacheMb(DriverSettings.ShaderCacheRoomMb);
            LauncherLog.Add($"NVIDIA shader cache size raised from {was} MB to {DriverSettings.ShaderCacheRoomMb} MB");
            return Tr.F("NVIDIA's shader cache was raised from {0} to {1}, so Longshot's shaders stop compiling again at every start.",
                        DriverSettings.Size(was), DriverSettings.Size(DriverSettings.ShaderCacheRoomMb));
        }
        catch (NvidiaProblem e)
        {
            LauncherLog.Add("NVIDIA shader cache size not raised: " + e.Message);
            if (e.Status == -137 && short_ is { } mb) NvidiaCacheNeedsAdmin = mb;   // (NVAPI_INVALID_USER_PRIVILEGE)
            return null;
        }
    }

    /// (Windows) This launcher started again as an administrator (Windows asks the player) to raise NVIDIA's shader cache, and
    /// waited for: true when it was raised. False: the player said no, or the driver refused again
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public bool RaiseNvidiaCacheAsAdmin()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(LauncherPath, RaiseNvidiaCacheArgument) { UseShellExecute = true, Verb = "runas" });
            if (p == null) return false;
            p.WaitForExit();
            if (p.ExitCode != 0) return false;
            NvidiaCacheNeedsAdmin = null;
            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // (1223: the player said no)
    }

    /// (Windows) What the launcher started with RaiseNvidiaCacheArgument does, as an administrator: 0 raised (or nothing to
    /// raise), 1 the driver refused or is not there
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static int RaiseNvidiaCacheHere()
    {
        try { return NvApiProfiles.Open() is { } nvidia ? (DriverSettings.RaiseShaderCache(nvidia), 0).Item2 : 1; }
        catch (NvidiaProblem) { return 1; }
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
    /// keeps its settings there made it, Longshot's host/options_path.c; on Linux in the user/ a newer Longshot moves them to,
    /// SaveEnvironment.LongshotUser), else the build's own. Hookshot's settings are imported into it
    public string OptionsFileOf(string folder) =>
        GamePlay.UserFolder(SaveEnv, folder) != null && Path.Combine(SaveEnv.LongshotUser, SettingsCarry.FileName) is var shared && File.Exists(shared)
            ? shared : Path.Combine(folder, SettingsCarry.FileName);

    /// The game folders the builds kept their own saves and settings in: every build's, every Longshot game folder in the builds
    /// folder (a version an update replaced before it took its saves along is no longer listed, its files still there; only
    /// Longshot's, never another game's such as Hookshot's), and the 2.x Builder's (not for a portable launcher)
    IReadOnlyList<string> BuildFolders()
    {
        // (right in the builds folder, and in its tracks' folders: builds/nightly/<version>, InstanceFolders.Folders)
        IEnumerable<string> inBuilds;
        try { inBuilds = InstanceFolders.Folders(BuildsFolder, Platform).Where(d => GameFolder.IsGameFolder(d, Platform)).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { inBuilds = Enumerable.Empty<string>(); }
        var all = Registry.Builds.Select(b => b.Folder).Concat(inBuilds);
        return (Home.Portable ? all : all.Append(LauncherSettings.DefaultBuildsFolder(SaveEnv.Getenv))).ToList();
    }

    /// What the window says of a gather (GatherSaves): which save every version uses now and where the backup is, or why it
    /// could not; null when there was nothing to do
    public static string? GatherSaid((SaveGathered? Done, string? Problem) g) =>
        g.Done != null ? SaveText.Gathered(g.Done) : g.Problem != null ? Tr.F("Your saves were not brought together: {0}", g.Problem) : null;

    /// (Windows) The saves the builds kept in their own folders (BuildFolders) brought into the shared one (Saves.Gather), and each
    /// build's user\save made a link to it (Saves.LinkSaves), so that a build started on its own, without --user, finds the shared
    /// saves and the next start has nothing to move: when the window opens and before a game starts. Done: what the gather did
    /// (null: nothing to bring, or Longshot runs); Problem: why it could not, with nothing changed
    public (SaveGathered? Done, string? Problem) GatherSaves()
    {
        if (Platform != Platform.Windows) return (null, null);
        try
        {
            var folders = BuildFolders();
            var done = Saves.GatherAlone(SaveEnv, Saves.OldUserFolders(SaveEnv, folders, null));
            Saves.LinkSaves(SaveEnv, folders);
            return (done, null);
        }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException)
        {
            LauncherLog.Add($"saves gather: not done: {e.GetType().Name}: {e.Message}");
            return (null, e.Message);
        }
    }

    public void SetDefault(BuildEntry b)
    {
        Registry.SetDefault(b.Id);
        Registry.Save();
    }

    /// The old versions' folders an update left in the builds folder, with nothing of the player's in them (Leftovers.Find)
    public IReadOnlyList<Leftover> FindLeftovers() =>
        Leftovers.Find(BuildsFolder, Registry.Builds.Select(b => b.Folder), Platform, Settings.KeptFolders, new[] { Home.Root }, SaveEnv);

    /// Every game folder in the builds folder no row lists, for the Storage page (Leftovers.All): the ones Home offers, and the
    /// ones it does not (kept, holding mods, a channel's newest whose row went)
    public IReadOnlyList<Leftover> UnlistedGames() =>
        Leftovers.All(BuildsFolder, Registry.Builds.Select(b => b.Folder), Platform, new[] { Home.Root }, SaveEnv)
            .Select(l => Registry.Problem != null ? l with { HoldsYours = true } : l).ToList();

    /// The games in the builds folder a row should have, listed: first a row that lost the game its update built in a new folder
    /// takes it back (Leftovers.LostUpdate), then, once (Leftovers.ShouldRelist), each game whose row went is listed again
    /// (Leftovers.Relist, #141). Nothing when builds.json could not be read: it is never written over then. Saved once, the tools
    /// pruned once (after both: a tool a game listed again uses stays); the rows given a game, returned
    public IReadOnlyList<BuildEntry> Relist()
    {
        if (Registry.Problem != null) return Array.Empty<BuildEntry>();
        var keep = new[] { Home.Root };
        var moved = new List<BuildEntry>();
        foreach (var b in Registry.Builds.ToList())
        {
            if (Leftovers.LostUpdate(BuildsFolder, b, Registry.Builds.Select(x => x.Folder), Platform, keep) is not { } found) continue;
            LauncherLog.Add($"{b.Version} in {b.Folder} has no game; its update's {found.Version} in {found.Folder} is listed in its place");
            moved.Add(Registry.Replace(b.Id, found.Folder, found.Version, b.Channel));
        }
        var added = Array.Empty<BuildEntry>() as IReadOnlyList<BuildEntry>;
        // (settings that could not be read count as relisted before: their flag is lost, and a sweep at each start would undo the
        // player's removals; nor are they written over to remember it)
        if (Leftovers.ShouldRelist(Registry, Settings.RelistedLostBuilds || Settings.Problem != null))
        {
            added = Leftovers.Relist(Registry, BuildsFolder, Platform, keep, Settings.KeptFolders);
            if (!Settings.RelistedLostBuilds && Settings.Problem == null)
            {
                Settings.RelistedLostBuilds = true;
                try { SaveSettings(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        foreach (var b in added) LauncherLog.Add($"build {b.Version} in {b.Folder} was in no row of the list: listed again");
        if (moved.Count + added.Count == 0) return moved;
        remeasure = true;
        try { Registry.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        PruneTools();
        return moved.Concat(added).ToList();
    }

    /// What deleting them frees: their files with no hard link outside them (Disk.Measure: a game file a build was linked to
    /// stays with that build). Seconds for a game folder: off the window's thread
    public static long LeftoverFrees(IReadOnlyList<Leftover> leftovers) =>
        Disk.Measure(leftovers.Select(l => l.Folder).ToList()).Sum(s => s.Frees);

    /// An old version's folder deleted (BuildRegistry.DeleteFolder: a game folder only, never one that holds a build's or the
    /// launcher's home). Returns where the save it still held was copied first (null: it held none)
    public string? DeleteLeftover(Leftover leftover)
    {
        if (Registry.Problem != null) throw new IOException("The builds list could not be read; the folder was not removed.");
        return Leftovers.Delete(leftover.Folder, Platform, Registry.Builds.Select(b => b.Folder).Append(Home.Root).ToList(), SaveEnv);
    }

    /// What deleting a build's folder must leave: the other builds' folders and the launcher's home (BuildRegistry.DeleteFolder)
    public IReadOnlyList<string> KeepOf(BuildEntry b) => Registry.Builds.Where(x => x.Id != b.Id).Select(x => x.Folder).Append(Home.Root).ToList();

    /// A build's game folder deleted as the home's Delete does it (BuildRegistry.DeleteGame): the save it holds itself brought
    /// into the shared one, or backed up, first. running: what runs now (Saves.Running)
    public DeletedGame DeleteGame(BuildEntry b, IReadOnlyList<string> running)
    {
        // (a builder's version: its whole folder goes, Longshot.exe and all, once its game folder has; not one that holds the
        // player's data or another row's game folder, which is left as it is, nothing deleted: BuilderVersions.WhyNotDelete)
        var version = VersionFolderOf(b);
        if (version != null)
        {
            var why = BuilderVersions.WhyNotDelete(version, Home.Root)
                      ?? (KeepOf(b).FirstOrDefault(k => BuildRegistry.Holds(version, k)) is { } other
                          ? $"This version folder holds {other}, which the launcher keeps, so it is not deleted: {version}" : null);
            if (why != null)
            {
                LauncherLog.Add($"version folder not deleted: {why}");
                return new DeletedGame(b.Folder, false, null, null, null, why);
            }
        }
        var done = BuildRegistry.DeleteGame(SaveEnv, b.Folder, Platform, KeepOf(b), () => running);
        if (done.Deleted && version != null)
        {
            try
            {
                BuilderVersions.Delete(version, Home.Root);
                LauncherLog.Add("version folder deleted: " + version);
                if (Settings.RollbackVersion == Path.GetFileName(version)) Settings.RollbackVersion = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // (its game is gone: the row goes; what is left of the folder is pruned later, PruneVersions)
                LauncherLog.Add($"version folder {version}: its game was deleted, the rest not ({e.Message})");
            }
        }
        return done;
    }

    /// The version folder (versions/&lt;v&gt;) of a row whose game folder is in one; null for any other
    public string? VersionFolderOf(BuildEntry b) => BuilderVersions.InVersions(b.Folder, Home.Root)
        ? Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b.Folder)))!) : null;

    /// The version folders nothing uses any more, deleted (BuilderVersions.Prunable): no row plays them, no profile names one as
    /// its version, not the one the last update replaced (Settings.RollbackVersion, kept for going back), not one fetched this
    /// session for a build to come (Prefetch.Tags: its builder is in it before any row is), and none while a game or a builder
    /// runs, or a fetch writes. What is in use is read here, on the window's thread; the folders go off it. The folders deleted, by name
    public Task<IReadOnlyList<string>> PruneVersions(Func<IReadOnlyList<string>>? running = null)
    {
        if (Prefetch.Busy) return Task.FromResult<IReadOnlyList<string>>([]);
        // (a profile whose profile.json cannot be read names no version: nothing is pruned until it is fixed or deleted)
        var profiles = ProfileStore.All(ProfileStore.Root(Home.Root));
        if (profiles.Any(p => p.File == null)) return Task.FromResult<IReadOnlyList<string>>([]);
        var inUse = Registry.Builds.Select(VersionFolderOf).OfType<string>().Select(Path.GetFileName)
            .Concat(profiles.Select(p => p.File?.Version)).Concat(Prefetch.Tags)
            .OfType<string>().ToList();
        var keep = Settings.RollbackVersion;
        var root = Home.Root;
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            try
            {
                if ((running?.Invoke() ?? Saves.Running(Platform)).Contains(Saves.Longshot.Display)) return [];
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return []; }
            var pruned = new List<string>();
            foreach (var v in BuilderVersions.Prunable(root, inUse, keep))
                try
                {
                    BuilderVersions.Delete(v, root);
                    pruned.Add(Path.GetFileName(v));
                    LauncherLog.Add($"versions: {v} pruned: nothing uses it");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LauncherLog.Add($"versions: {v} not pruned ({e.Message})"); }
            return pruned;
        });
    }

    /// The build out of the list; with deleteFiles, its game folder from the disk too (only a game folder: BuildRegistry.DeleteFolder)
    public void Remove(BuildEntry b, bool deleteFiles)
    {
        if (deleteFiles) BuildRegistry.DeleteFolder(b.Folder, Platform, KeepOf(b), null, SaveEnv);
        Registry.Remove(b.Id);
        LauncherLog.Add($"build {b.Version ?? "(no version)"} removed from the list{(deleteFiles ? "" : ", its folder left on the disk")}: {b.Folder}");
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
    /// runs the prune waits (it would take the fetch's staging folder and downloads) and runs when the last one ends (Idle). Its
    /// file work (reading the versions' records, deleting folders) runs off the window's thread, under the installs' own
    /// exclusion (ToolsPrefetch.CleanStaging): a fetch started meanwhile waits for it, or the prune waits for the fetch
    public void PruneTools()
    {
        if (Prefetch.Busy)
        {
            Interlocked.Exchange(ref pruneLater, 1);
            if (Prefetch.Busy) return;
            // (the last fetch ended between the two looks, its Idle maybe before the flag: prune now)
            Interlocked.Exchange(ref pruneLater, 0);
        }
        var builds = Registry.Builds.Select(x => x.Version).OfType<string>().ToList();
        // (make_game.py's build files and SDKs, once no old version is listed: Disk.PruneMakeGame)
        var oldListed = Registry.Builds.Any(IsOld);
        var work = Work;
        var sdks = new[] { Home.NgxSdk, Home.FfxSdk, Home.XessSdk };
        _ = Task.Run(() =>
        {
            // (an older launcher's toolchains/ no row uses: the version folders hold the builders now, and go with their version,
            // BuilderVersions.Prunable. The fetches' versions read under the exclusion: one asked for since is kept)
            var pruned = Prefetch.CleanStaging(() =>
            {
                try { Tools.Prune(builds.Concat(Prefetch.Tags)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            });
            if (!pruned)
            {
                // (an install holds the tools' folder: again once the fetches end, or now when they ended before the flag)
                Interlocked.Exchange(ref pruneLater, 1);
                if (!Prefetch.Busy && Interlocked.Exchange(ref pruneLater, 0) == 1) ViewModels.Ui.Post(PruneTools);
                return;
            }
            try { Disk.PruneMakeGame(work, sdks, oldListed); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LauncherLog.Add("make_game.py's build files not all let go: " + e.Message);
            }
        });
    }

    /// The build files folder changed from `from`, the change saved: the launcher's rows' longshot-builder work folders left in it
    /// (Disk.LeaveLongshotWork) deleted, off the window's thread. No build looks there any more, and each is a gigabyte or more;
    /// a folder a build holds (its lock) or the launcher's own build runs in stays
    public void LeaveWork(string from)
    {
        var to = Work;
        if (BuildRegistry.SameFolder(from, to)) return;
        IReadOnlyCollection<string> building = ViewModels.BuildStepViewModel.Current?.RunningWorkFolder is { } running ? [running] : [];
        _ = Task.Run(() =>
        {
            var gone = Disk.LeaveLongshotWork(from, to, building, (dir, e) => LauncherLog.Add($"build files: {dir} not let go: {e.Message}"));
            if (gone.Count > 0) LauncherLog.Add($"build files moved to {to}: {gone.Count} of its builds' work folders let go in {from}");
        });
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

    /// The shader store's bytes (ShaderCaches.StoreBytes, SaveEnvironment.ShaderStore), counted once in the home's total; null
    /// until measured. The screenshots give their own (ShaderStoreOverride)
    public long? ShaderStoreBytes { get; private set; }
    public Func<long>? ShaderStoreOverride { get; set; }
    bool measuringStore;

    /// The shader store measured again in the background, each time the home is shown (the game adds to it as it plays: a few
    /// files, no walk), then done on the UI thread when its size changed
    public void MeasureShaderStore(Action done)
    {
        if (ShaderStoreOverride is { } own)
        {
            ShaderStoreBytes = own();
            done();
            return;
        }
        if (measuringStore) return;
        measuringStore = true;
        var store = SaveEnv.ShaderStore;
        Task.Run(() =>
        {
            var bytes = ShaderCaches.StoreBytes(store);
            ViewModels.Ui.Post(() =>
            {
                measuringStore = false;
                if (ShaderStoreBytes == bytes) return;
                ShaderStoreBytes = bytes;
                done();
            });
        });
    }

    /// Longshot's save folder, the same for every build (Saves.Shared): the one the Saves page lists and the save editor edits; null
    /// when there is none
    public string? SaveFolder() => Saves.ShownProfile(SaveEnv);

    /// Longshot's save slots, the same for every build
    public IReadOnlyList<SaveSlot> SaveSlots() => SaveFolder() is { } folder ? Saves.Describe(folder) : Array.Empty<SaveSlot>();

    /// The save editor, for the save the Saves page lists (SaveFolder) and the default build's own tools and game files, or why
    /// there is none (SaveEditing.Find). The screenshots give their own
    public Func<(SaveEditing? Editing, SaveEditorWhy Why)>? SaveEditorOverride { get; set; }

    public (SaveEditing? Editing, SaveEditorWhy Why) SaveEditor()
    {
        if (SaveEditorOverride is { } own) return own();
        if (SaveFolder() is not { } save) return (null, SaveEditorWhy.NoSave);
        var (tool, why) = SaveEditing.Find(Registry.Default);
        return tool == null ? (null, why) : (new SaveEditing(tool, SaveEnv, save), why);
    }
}

/// Play refused for a version built by Longshot's old builder (OldVersions): its message says to use the stable launcher
public sealed class OldVersionException(string message) : InvalidOperationException(message);
