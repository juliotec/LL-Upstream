namespace LongshotLauncher.Core;

/// The launcher's own folder: %LOCALAPPDATA%\LongshotLauncher on Windows, ${XDG_DATA_HOME:-~/.local/share}/longshot/launcher on
/// Linux, LONGSHOT_LAUNCHER_HOME on both; a portable launcher's is beside it (LauncherLayout.PortableHome). Everything in it is
/// the launcher's. What can be made again (the build files, the SDKs a build fetches) is in Cache: on Linux, with the default
/// home, ${XDG_CACHE_HOME:-~/.cache}/longshot/launcher, so that the data folder holds only what cannot; everywhere else the home
/// itself (Windows' %LOCALAPPDATA% is not roamed already, and a portable launcher or a home named by LONGSHOT_LAUNCHER_HOME keeps
/// everything in its one folder)
public sealed class LauncherHome
{
    public const string Variable = "LONGSHOT_LAUNCHER_HOME";

    public string Root { get; }

    /// Where the folders that can be made again go (InCache): Root, or on Linux the cache folder (Resolve)
    public string Cache { get; }

    public LauncherHome(string root, string? cache = null)
    {
        Root = Path.GetFullPath(root);
        Cache = cache == null ? Root : Path.GetFullPath(cache);
    }

    /// Portable: this home is a portable launcher's, beside it (the default builds folder and the save backups are in it too)
    public bool Portable { get; init; }

    public static LauncherHome Resolve(Platform platform, Func<string, string?>? getenv = null, LauncherLayout? layout = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        if (getenv(Variable) is { Length: > 0 } set) return new LauncherHome(set);
        if (layout?.PortableHome is { } portable) return new LauncherHome(portable) { Portable = true };
        if (platform == Platform.Windows) return new LauncherHome(Path.Combine(LauncherLayout.LocalAppData(getenv), "LongshotLauncher"));
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home(getenv), ".local", "share");
        var cache = getenv("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(Home(getenv), ".cache");
        return new LauncherHome(Path.Combine(data, "longshot", "launcher"), Path.Combine(cache, "longshot", "launcher"));
    }

    internal static string Home(Func<string, string?> getenv) =>
        getenv("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string Builds => Path.Combine(Root, "builds.json");
    public string Settings => Path.Combine(Root, "settings.json");
    /// The versions' builders and tools: kept in Root, not Cache. A build's game is played, updated and sent reports for with its
    /// version's tools (PerfReports.ToolsFor, ShaderCaches), which are 0.5-3 GB to download again, and an old version's may no
    /// longer be offered at all
    public string Toolchains => Path.Combine(Root, "toolchains");
    /// The launcher's log files: launcher.log, this run's, and launcher.1.log, the run before (LauncherLog)
    public string Logs => Path.Combine(Root, "logs");
    /// make_game.py's --work unless the player chose another: the build files, kept between builds for a faster rebuild (a build
    /// without them is slower, never wrong: a cache)
    public string Work => InCache(WorkName);
    /// NVIDIA's DLSS SDK, fetched by make_game.py --fetch-ngx-sdk (LONGSHOT_NGX_SDK), AMD's FSR runtime (LONGSHOT_FFX_SDK) and
    /// Intel's XeSS library (--fetch-xess-runtime, LONGSHOT_XESS_SDK): fetched again by the next build that needs them
    public string NgxSdk => InCache("ngx-sdk");
    public string FfxSdk => InCache("ffx-sdk");
    public string XessSdk => InCache("xess-sdk");
    /// A portable launcher's default builds folder and save backups (an installed one's are the system's: LauncherSettings,
    /// SaveEnvironment.BackupRoot)
    public string PortableBuilds => Path.Combine(Root, "builds");
    public string PortableSaveBackups => Path.Combine(Root, "save-backups");
    /// (Windows) A portable launcher's Longshot user folder, its saves, which every build it starts shares (SaveEnvironment.UserFolder)
    public string PortableUser => Path.Combine(Root, "user");

    const string WorkName = "work";

    /// The folders kept in Cache, by name (MoveToCache moves them there from Root)
    public static readonly IReadOnlyList<string> CacheFolders = new[] { WorkName, "ngx-sdk", "ffx-sdk", "xess-sdk" };

    /// A folder of Cache's: in Cache, unless it is still in Root and not in Cache (a launcher from before Cache made it there, and
    /// MoveToCache could not move it: another disk), where it is used as it is
    string InCache(string name)
    {
        var now = Path.Combine(Cache, name);
        if (SameFolder(Cache, Root)) return now;
        var old = Path.Combine(Root, name);
        return Directory.Exists(old) && !Directory.Exists(now) ? old : now;
    }

    static bool SameFolder(string a, string b) =>
        Path.TrimEndingDirectorySeparator(a).Equals(Path.TrimEndingDirectorySeparator(b),
                                                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// The folders of Cache that a launcher from before made in Root, moved to Cache by one rename each: nothing is copied or
    /// deleted, and a folder already in Cache, or one the player chose as the build files' folder (inUse: LauncherSettings.Work),
    /// stays where it is. A rename that fails (Cache on another disk: a rename cannot cross it) leaves the folder in Root, where
    /// InCache keeps using it. Called once at the start, with this home's lock held (InstanceLock), before any build: no build is
    /// writing them. What it did, for the log
    public IReadOnlyList<string> MoveToCache(Func<string, bool>? inUse = null)
    {
        var said = new List<string>();
        if (SameFolder(Cache, Root)) return said;
        foreach (var name in CacheFolders)
        {
            var old = Path.Combine(Root, name);
            var now = Path.Combine(Cache, name);
            try
            {
                if (!Directory.Exists(old) || new DirectoryInfo(old).LinkTarget != null) continue;   // (a link: the player's own arrangement)
                if (inUse?.Invoke(old) == true) continue;
                if (Directory.Exists(now) || File.Exists(now))
                {
                    said.Add($"{old} left where it is: {now} is there already");
                    continue;
                }
                Directory.CreateDirectory(Cache);
                Directory.Move(old, now);
                said.Add($"{old} moved to {now}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                said.Add($"{old} not moved to {now}, used where it is: {e.Message}");
            }
        }
        return said;
    }
}
