using System.Diagnostics;
using System.Globalization;

namespace LongshotLauncher.Core;

/// What a build is made from: a .nup, the romfs and exefs folders, or (Update) a folder built before, from the game files it keeps
public abstract record BuildSource
{
    public sealed record Nup(string Path) : BuildSource;
    public sealed record Folders(string Romfs, string Exefs) : BuildSource;
    public sealed record Update(string Folder) : BuildSource;
}

/// A build's choices (the wizard's settings step, LauncherSettings). Out is not used by an update (the folder is the update's).
/// IntelCompiles: Intel shader compiles (GamePlay.IntelCompilesAuto,
/// SideBySide, OneAtATime); IntelLongerLimits: Shader time limit on Longer (LauncherSettings, where they apply). Xess: Intel's XeSS library fetched
/// under Intel's licence (XessLicense), Windows only. LowPriority: the builder and everything it starts run below normal priority
/// (MakeGameRun.RunAsync), so the PC stays usable while it builds; off for Build workers' Every thread (SystemInfo.LowPriority).
/// WorkId: the launcher's row the build is of (BuildEntry.Id, kept by its updates however its folder is renamed), which names
/// longshot-builder's work folder (LongshotBuilder.WorkFolder); null: the builder's own, by the game folder's path. Profile: (a
/// version folder's builder, BuilderVersions) the profile folder the build is for: build --profile, its game folder the version
/// folder's (Out is then that folder, the builder's to place), profile.json's game and upscalers written by the builder
public sealed record BuildRequest(
    BuildSource Source, string? Out, string? Language = null, string? Work = null, int? Jobs = null, int? MemoryGb = null,
    bool PrepareShaders = true, bool Dlss = false, bool DlssLicense = false, bool Fsr = false, bool FsrLicense = false,
    int IntelCompiles = GamePlay.IntelCompilesAuto, bool IntelLongerLimits = false, bool Xess = false,
    bool XessLicense = false, bool LowPriority = true, string? WorkId = null, string? Profile = null, bool SendStatistics = false);

/// A run of the builder (LongshotBuilder): the program, its arguments, its folder and the environment changes (null: removed).
/// LowPriority: run below normal priority, the compilers it starts with it (BuildRequest.LowPriority, MakeGameRun.RunAsync).
/// DroppedPrefixes: every inherited variable starting with one of them removed before Environment is applied
/// (LongshotBuilder.Dropped)
public sealed record MakeGameCommand(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment,
    bool LowPriority = false, IReadOnlyList<string>? DroppedPrefixes = null)
{
    public ProcessStartInfo StartInfo()
    {
        var si = new ProcessStartInfo(FileName)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in Arguments) si.ArgumentList.Add(a);
        if (DroppedPrefixes is { Count: > 0 } dropped)
            foreach (var k in si.Environment.Keys.Where(k => dropped.Any(d => k.StartsWith(d, OperatingSystem.IsWindows()
                         ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))).ToList())
                si.Environment.Remove(k);
        foreach (var (k, v) in Environment)
            if (v == null) si.Environment.Remove(k);
            else si.Environment[k] = v;
        return si;
    }
}

/// Play: the build's game from its folder, as the 2.x Builders start it (MainWindow.StartGame; on Linux longshot_builder.play with
/// make_game.game_env: without what the AppImage runtime and the tools put in the environment)
public static class GamePlay
{
    static readonly string[] Dropped = { "LONGSHOT_", "PYTHON", "APPDIR", "APPIMAGE", "ARGV0", "OWD" };   // (make_game.GAME_ENV_DROP)

    /// Longshot's switch for Intel shader compiles, over its in-game row for the run (README, Intel laptops): 1 Side by side,
    /// 0 One at a time. While it is set the game shows that row fixed, so Auto leaves it unset and the row free
    public const string IntelCompilesVar = "RX_VK_NO_INTEL_SERIAL";

    /// The launcher's Intel shader compiles choice, numbered as the game's row (gpu/vk/vk_menu.h intel_compiles): Auto, the
    /// game's own (side by side on the Arc B-series from driver 101.9154, one at a time on other Intel graphics: intel_compiles.h)
    public const int IntelCompilesAuto = 0, IntelCompilesSideBySide = 1, IntelCompilesOneAtATime = 2;

    /// RX_VK_NO_INTEL_SERIAL for a choice; null for Auto, which sets nothing
    public static string? IntelCompilesValue(int choice) =>
        choice == IntelCompilesSideBySide ? "1" : choice == IntelCompilesOneAtATime ? "0" : null;

    /// Shader time limit, Longer: Longshot's waits on a driver that seems stuck, made longer for Intel's compiler one at a time,
    /// which can sit on one compile for most of a minute (Longshot issue 407: 57 s; its preparation was stopped at 300 s
    /// without a fault). The preparation's watchdog (gpu/vk/vk_main.cpp prepare_watchdog, 300 s) and the noted and predicted
    /// pipelines' last stragglers (vk_draw.cpp precompile_run and predict_run, 30 s), about three times that pause
    public static readonly (string Name, string Value)[] IntelLongerLimits =
    {
        ("RX_VK_PREPARE_STALL_S", "1800"), ("RX_VK_PRECOMPILE_STALL_S", "180"), ("RX_VK_PREDICT_STALL_S", "180"),
    };

    /// The environment the game starts with, from the launcher's. On Linux LONGSHOT_NFC_DIR and LONGSHOT_SHADER_STORE are kept,
    /// though the other LONGSHOT_ variables are dropped: the NFC page (NfcTags.Folder) and the game must read the same folder, and
    /// the game must play with the shader store the Storage page counts (SaveEnvironment.ShaderStore) and longshot-builder prepared
    /// its shaders in (LongshotBuilder.ShaderStoreVar, kept for that preparation too). intelCompiles: Intel shader
    /// compiles (IntelCompilesAuto...); intelLongerLimits: Shader time limit on Longer (both Windows only)
    public static Dictionary<string, string> Environment(Platform platform, IReadOnlyDictionary<string, string> current,
                                                         int intelCompiles = IntelCompilesAuto, bool intelLongerLimits = false)
    {
        var env = platform == Platform.Windows
            ? new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase)
            : current.Where(kv => (kv.Key is NfcTags.Variable or LongshotBuilder.ShaderStoreVar || !Dropped.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal)))
                                  && kv.Key != "LD_LIBRARY_PATH")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        env["LONGSHOT_NO_UPDATE_CHECK"] = "1";   // (the launcher already checked for updates: the game does not ask again)
        if (platform == Platform.Windows && IntelCompilesValue(intelCompiles) is string compiles) env[IntelCompilesVar] = compiles;
        if (platform == Platform.Windows && intelLongerLimits)
            foreach (var (name, value) in IntelLongerLimits) env[name] = value;
        return env;
    }

    /// The user folder a build is started with (StartInfo's user): Longshot's shared one, where its saves are and, from the
    /// Longshot that takes --user for them, its settings (host/options_path.c). SaveEnvironment.LongshotRoot for every version:
    /// on Linux a Longshot that keeps its user files apart keeps them in its user/ (SaveEnvironment.LinuxUserFiles, moved there by
    /// the game itself), one from before in the folder itself, so no version needs another. Every Windows build. On Linux the
    /// game finds the shared folder by itself for the saves, and keeps its settings beside itself without --user: given it too,
    /// unless the build's folder has a user/ of its own, which the game takes in itself (an old build's saves, moved into the
    /// shared folder at its next start) or keeps (a link of the player's: their own arrangement). Null: none
    public static string? UserFolder(SaveEnvironment env, string folder)
    {
        if (env.Platform == Platform.Windows) return env.LongshotRoot;
        var own = Path.Combine(folder, "user");
        try
        {
            if (Directory.Exists(own) || File.Exists(own) || new FileInfo(own).LinkTarget != null) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return env.LongshotRoot;
    }

    /// The options the player typed for gamescope (LauncherSettings.GamescopeOptions), split as a shell would: spaces separate
    /// them, '...' and "..." group them (quotes of the other kind stay in the word), and a backslash escapes the next character
    /// outside quotes. In double quotes it escapes only $, `, ", backslash and newline. Nothing for null or blank
    public static IReadOnlyList<string> SplitOptions(string? options)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(options)) return words;
        var word = new System.Text.StringBuilder();
        var quoted = '\0';
        var started = false;
        for (var i = 0; i < options.Length; i++)
        {
            var c = options[i];
            if (quoted != '\0')
            {
                if (c == quoted) quoted = '\0';
                else if (c == '\\' && quoted == '"' && i + 1 < options.Length && options[i + 1] is '$' or '`' or '"' or '\\' or '\n')
                {
                    if (options[++i] != '\n') word.Append(options[i]);
                }
                else word.Append(c);
            }
            else if (c is '\'' or '"') { quoted = c; started = true; }
            else if (c == '\\' && i + 1 < options.Length) { word.Append(options[++i]); started = true; }
            else if (char.IsWhiteSpace(c)) { if (started) { words.Add(word.ToString()); word.Clear(); started = false; } }
            else { word.Append(c); started = true; }
        }
        if (started) words.Add(word.ToString());
        return words;
    }

    /// user: Longshot's user folder, which every build shares (UserFolder), given to the game as --user. Every build takes --user,
    /// the ones from before the launcher too; without it the game keeps its saves in its own folder's user\ on Windows.
    /// gamescope: (Linux) start the game through gamescope, with gamescopeOptions before the game (SplitOptions)
    public static ProcessStartInfo StartInfo(string folder, Platform platform, IReadOnlyDictionary<string, string>? current = null, string? user = null,
                                             int intelCompiles = IntelCompilesAuto, bool intelLongerLimits = false,
                                             bool gamescope = false, string? gamescopeOptions = null)
    {
        current ??= CurrentEnvironment();
        var exe = GameFolder.ExePath(folder, platform);
        var game = new List<string>();
        if (user != null)
        {
            game.Add("--user");
            game.Add(user);
        }
        ProcessStartInfo si;
        if (gamescope && platform == Platform.Linux)
        {
            si = new ProcessStartInfo("gamescope") { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var option in SplitOptions(gamescopeOptions)) si.ArgumentList.Add(option);
            si.ArgumentList.Add("--");
            si.ArgumentList.Add(exe);
            foreach (var argument in game) si.ArgumentList.Add(argument);
        }
        else
        {
            si = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var argument in game) si.ArgumentList.Add(argument);
        }
        si.Environment.Clear();
        foreach (var (k, v) in Environment(platform, current, intelCompiles, intelLongerLimits)) si.Environment[k] = v;
        return si;
    }

    /// The launcher's own environment, as StartInfo takes it when given none
    public static Dictionary<string, string> CurrentEnvironment() =>
        System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
}
