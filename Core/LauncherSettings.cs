using System.Text.Json;

namespace LongshotLauncher.Core;

/// What the build wizard remembers (&lt;home&gt;/settings.json): the game files last picked, the build's options and the channel new
/// builds start on. A license flag is true only once its license was accepted on its tick.
public sealed class LauncherSettings
{
    /// 2: defaultChannel is null until the player picks one (format 1 wrote "stable" for everyone; Load reads it as not picked)
    public const int CurrentFormat = 2;
    public int Format { get; set; } = CurrentFormat;
    public string? Nup { get; set; }
    public string? Romfs { get; set; }
    public string? Exefs { get; set; }
    public string? Language { get; set; }
    /// The launcher's own language (Tr.All's code), apart from the game's (Language); null: the system's, else English
    public string? UiLanguage { get; set; }
    public bool Dlss { get; set; }
    public bool DlssLicense { get; set; }
    public bool Fsr { get; set; }
    public bool FsrLicense { get; set; }
    /// (Linux) The AMD FSR 4 notice read: nothing of AMD's is downloaded there, so no licence is accepted, and FsrLicense is left
    /// as it was (a launcher home shared with Windows must not take this for AMD's licence)
    public bool FsrLinuxNotice { get; set; }
    /// (Linux) The player's Vulkan FSR 4 library, copied into each game folder built with FSR 4 (FsrLibrary); null: none chosen
    public string? FsrLibraryPath { get; set; }
    /// (Windows) Intel XeSS: the build fetches Intel's libxess.dll into the game folder, under Intel's licence (XessLicense)
    public bool Xess { get; set; }
    public bool XessLicense { get; set; }
    /// (Windows, Intel graphics) Intel shader compiles: Side by side hands Intel's compiler several shaders at once, in the build's
    /// shader preparation and in play (RX_VK_NO_INTEL_SERIAL=1, SystemInfo.IntelCompilesApply); false: One at a time, the game's
    /// default, whose own Intel shader compiles row then decides
    public bool IntelSideBySide { get; set; }
    /// (Windows, Intel graphics) Shader time limit, Longer: the waits on a driver that seems stuck made longer, for the build's
    /// shader preparation and in play (GamePlay.IntelLongerLimits); false: Normal, Longshot's own
    public bool IntelLongerLimits { get; set; }
    /// Build memory in GB (LONGSHOT_MEM_LIMIT_GB); null: Automatic
    public int? MemoryGb { get; set; }
    /// Build workers (LONGSHOT_JOBS / --jobs); null: Automatic
    public int? BuildWorkers { get; set; }
    /// make_game.py's --work; null: the launcher's own (LauncherHome.Work)
    public string? Work { get; set; }
    /// The channel new builds start on, as the player picked it in Settings; null: not picked, so it follows the launcher (BuildChannel)
    public string? DefaultChannel { get; set; }
    /// The channel the launcher updates itself from (its own releases, apart from Longshot's); null: its own version's track
    public string? LauncherChannel { get; set; }
    /// The player answered the offer to import Hookshot's saves (imported, or Not now): it is not offered again at start
    public bool HookshotSavesAsked { get; set; }
    /// The player answered the offer to import NX2navi's saves (imported, or Not now)
    public bool Nx2naviSavesAsked { get; set; }
    /// The old versions' folders the player chose to keep when the home offered to delete them (Leftovers): not offered again
    public List<string> KeptFolders { get; set; } = new();
    /// Where new builds go, each in its own folder (FolderFor); null: DefaultBuildsFolder
    public string? BuildsFolder { get; set; }
    /// Ask git.magicbean.dev for each channel's newest version when the launcher starts
    public bool CheckUpdates { get; set; } = true;
    /// Download an update's build tools as soon as the update check finds it, before the player starts the update (ToolsPrefetch):
    /// ~500 MB a version, so off unless chosen. A first build's tools always start early (LONGSHOT_LAUNCHER_NO_PREFETCH=1 stops both)
    public bool DownloadUpdatesEarly { get; set; }
    /// Send performance statistics: every build's game measures how its frames went and the launcher sends the summaries to the
    /// team (PerfReports); off by default
    public bool SendPerformanceStatistics { get; set; }
    /// Keep a copy of what was sent: each summary sent is kept as sent in the game folder's perf_sent/, for the player to read
    /// (keep_sent=0 in longshot_perf.ini when off, which also deletes the copies); on by default
    public bool KeepSentPerformanceStatistics { get; set; } = true;
    /// Send crash reports automatically: after a crash the game sends its report without showing its crash window
    /// (CrashReports: longshot_crash.ini send=1 in each game folder); off by default, when the window asks
    public bool SendCrashReportsAutomatically { get; set; }
    /// Each build folder's log already shown on the problem page, by its write time: a crash is shown once
    public Dictionary<string, long> SeenCrashLogs { get; set; } = new();
    /// When the launcher first looked for crashes (UTC ticks; null: not yet): a log from before then is not offered, so the first
    /// start with the check doesn't bring up a crash from weeks ago
    public long? CrashChecksSince { get; set; }

    /// The channel new builds start on: the one the player picked, else the launcher's own (a nightly launcher builds nightlies, an rc
    /// launcher release candidates; LauncherChannel when set, else its own version's track)
    public string BuildChannel(string ownTag) => DefaultChannel ?? SelfUpdate.ChannelOf(ownTag, LauncherChannel);

    /// The folder builds go in when none was chosen: make_game.py's own default game folder's parent, ~/Games/Longshot
    /// (%USERPROFILE%\Games\Longshot on Windows)
    public static string DefaultBuildsFolder(Func<string, string?>? getenv = null) =>
        Path.Combine(LauncherHome.Home(getenv ?? Environment.GetEnvironmentVariable), "Games", "Longshot");

    public string BuildsFolderOrDefault(Func<string, string?>? getenv = null) =>
        string.IsNullOrWhiteSpace(BuildsFolder) ? DefaultBuildsFolder(getenv) : BuildsFolder;

    /// A new build's folder: its channel's name in the builds folder (stable, rc, nightly), with " (2)" and so on when that is
    /// taken by something that is not the build being replaced. Not the version's name: an update stays in its build's folder
    /// (BuildRegistry.FolderOf), so a version in the name would be the wrong one after the first update
    public static string FolderFor(string buildsFolder, string channel, Func<string, bool>? taken = null)
    {
        taken ??= p => Directory.Exists(p) || File.Exists(p);
        var name = Versions.IsChannel(channel) ? channel : "Longshot";
        var path = Path.Combine(buildsFolder, name);
        for (var n = 2; taken(path); n++) path = Path.Combine(buildsFolder, $"{name} ({n})");
        return path;
    }

    /// The launcher's language: the one picked, else the system's (its name for it: de-DE, pt_BR.UTF-8), else English
    public string UiLanguageOr(string? system) => UiLanguage ?? Tr.Pick(system) ?? Tr.English;

    /// The saved settings, or the defaults when there are none or they cannot be read
    public static LauncherSettings Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.LauncherSettings) ?? new LauncherSettings();
            if (s.Format < CurrentFormat)
            {
                // (format 1 saved its own default, "stable", for every player who never picked: that one is not a pick)
                if (s.DefaultChannel == Versions.Stable) s.DefaultChannel = null;
                s.Format = CurrentFormat;
            }
            return s.Checked();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new LauncherSettings();
        }
    }

    LauncherSettings Checked()
    {
        if (DefaultChannel != null && !Versions.IsChannel(DefaultChannel)) DefaultChannel = null;
        if (LauncherChannel != null && !Versions.IsChannel(LauncherChannel)) LauncherChannel = null;
        UiLanguage = Tr.Pick(UiLanguage);
        if (MemoryGb is <= 0) MemoryGb = null;
        if (BuildWorkers is <= 0) BuildWorkers = null;
        if (!DlssLicense) Dlss = false;   // (a tick saved before its license was accepted is not one)
        if (!FsrLicense && !FsrLinuxNotice) Fsr = false;
        if (!XessLicense) Xess = false;
        SeenCrashLogs ??= new();
        return this;
    }

    public void Save(string path) => AtomicFile.Write(path, JsonSerializer.Serialize(Checked(), CoreJson.Default.LauncherSettings));
}
