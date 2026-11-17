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
/// RomfsFrom: another game folder built from the same .nup (BuildRegistry.SameGameFiles), whose game files make_game links in
/// instead of reading the .nup again (LONGSHOT_ROMFS_FROM). IntelCompiles: Intel shader compiles (GamePlay.IntelCompilesAuto,
/// SideBySide, OneAtATime); IntelLongerLimits: Shader time limit on Longer (LauncherSettings, where they apply). Xess: Intel's XeSS library fetched
/// under Intel's licence (XessLicense), Windows only
public sealed record BuildRequest(
    BuildSource Source, string? Out, string? Language = null, string? Work = null, int? Jobs = null, int? MemoryGb = null,
    bool PrepareShaders = true, bool Dlss = false, bool DlssLicense = false, bool Fsr = false, bool FsrLicense = false,
    string? RomfsFrom = null, int IntelCompiles = GamePlay.IntelCompilesAuto, bool IntelLongerLimits = false, bool Xess = false,
    bool XessLicense = false);

/// make_game.py as the 2.x Builders start it (Runner.Info and MainWindow.Run on Windows; builder/linux/AppRun and
/// longshot_builder.py on Linux): the program, its arguments, its folder and the environment changes (null: removed)
public sealed record MakeGameCommand(
    string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment)
{
    /// launcher: this launcher's own path, written into the game folder's stamp (builder=) so the game's update offer starts it
    /// (host/hos_update.c). directoryExists, fileExists: the tests' stand-ins for the disk
    public static MakeGameCommand For(Toolchain tools, BuildRequest r, Platform platform, LauncherHome home, string launcher,
                                      Func<string, bool>? directoryExists = null, Func<string, bool>? fileExists = null)
    {
        directoryExists ??= Directory.Exists;
        fileExists ??= File.Exists;
        if (r.Jobs is <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Build workers must be positive.");
        if (r.MemoryGb is <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Build memory must be positive.");
        var windows = platform == Platform.Windows;
        var args = new List<string> { "-I", tools.MakeGame };
        switch (r.Source)
        {
            case BuildSource.Nup n:
                args.AddRange(new[] { "--nup", n.Path });
                break;
            case BuildSource.Folders f:
                args.AddRange(new[] { "--romfs", f.Romfs, "--exefs", f.Exefs });
                break;
            case BuildSource.Update u:
                args.AddRange(new[] { "--update", u.Folder });
                break;
        }
        if (r.Source is not BuildSource.Update)
        {
            args.AddRange(new[] { "--out", r.Out ?? throw new ArgumentException("A build needs its game folder (Out).", nameof(r)) });
            // (a code make_game knows: "de-DE" from a 2.x settings file is "de"; none known, make_game keeps the folder's or English)
            if (Languages.Normalize(r.Language) is { } language) args.AddRange(new[] { "--language", language });
        }
        var work = r.Work ?? home.Work;
        args.AddRange(new[] { "--work", work });
        // (the build workers: LONGSHOT_JOBS on Windows, BuildWorkers.Apply; --jobs on Linux, as longshot_builder.py passes them)
        if (!windows && r.Jobs is int jobs) args.AddRange(new[] { "--jobs", jobs.ToString(CultureInfo.InvariantCulture) });
        if (!r.PrepareShaders) args.Add("--no-prepare-shaders");
        var dlss = r.Dlss && r.DlssLicense;
        if (dlss) args.AddRange(new[] { "--fetch-ngx-sdk", "--accept-nvidia-license" });
        // (AMD FSR 4: on Windows AMD's runtime is fetched under AMD's licence; AMD publishes none for Linux, where the player puts a
        // Vulkan FSR 4 library beside the game, and the build compiles its own Vulkan back end, as with DLSS)
        var fsr = r.Fsr && r.FsrLicense && FsrBuilds(tools, platform, directoryExists);
        if (fsr) args.AddRange(windows ? new[] { "--fsr", "--fetch-fsr-runtime", "--accept-amd-license" } : new[] { "--fsr" });
        // (Intel XeSS: every Windows build of the game has it, and it runs once Intel's libxess.dll is beside the game, which the
        // build fetches under Intel's licence. Intel publishes no XeSS library for Linux. Tools from before the flag: XessBuilds)
        var xess = windows && r.Xess && r.XessLicense && XessBuilds(tools, fileExists);
        if (xess) args.AddRange(new[] { "--fetch-xess-runtime", "--accept-intel-license" });

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["LONGSHOT_BUILDER_EXE"] = launcher,
            // (the 2.x setup's home and portable mark: make_game would take them for its work folder and stamp; the launcher passes --work)
            ["LONGSHOT_SETUP_BASE"] = null,
            ["LONGSHOT_BUILDER_PORTABLE"] = null,
            ["LONGSHOT_BUILDER_ARGS"] = null,
            // (DLSS, FSR and XeSS only when ticked: one set on the PC is not passed on either)
            ["LONGSHOT_NGX_SDK"] = dlss ? home.NgxSdk : null,
            ["LONGSHOT_FFX_SDK"] = fsr && windows ? home.FfxSdk : null,
            ["LONGSHOT_XESS_SDK"] = xess ? home.XessSdk : null,
            ["LONGSHOT_MEM_LIMIT_GB"] = r.MemoryGb?.ToString(CultureInfo.InvariantCulture),
            // (Longshot's object and lift stores, nx2/object_store.py and lift_store.py: a build of another version, or of one built
            // before, takes the objects and the translation an earlier build made instead of compiling and translating them again,
            // where the work folder alone keeps only the last build's. Inside the work folder, so on its drive whichever one the
            // player chose (the objects are hard links into objcache there; copies only on a file system without hard links, exFAT,
            // FAT, some network shares), and Free up space lets them go with the rest (Disk.WorkParts). Tools from before the stores,
            // Longshot before v3.0.0-rc.4 and nightly-164, read none of these)
            ["LONGSHOT_OBJ_STORE"] = Path.Combine(work, Disk.ObjectStore),
            ["LONGSHOT_LIFT_STORE"] = Path.Combine(work, Disk.LiftStore),
            // (the objects no build holds any more: gone after 14 days, and the oldest first above 4 GB rather than Longshot's 8,
            // about four versions' objects of their own, a build's being ~1 GB. A kept translation is ~1.6 GB, its lifted C whole:
            // two, the version built and the one before (stable and nightly, or going back), not Longshot's three)
            ["LONGSHOT_OBJ_STORE_GB"] = "4",
            ["LONGSHOT_LIFT_STORE_KEEP"] = "2",
            // (a version's game files from another game folder of the same .nup: hard links, not ~25 GB read and written again. A
            // builder that does not know the variable reads the .nup as before; one set on the PC is not passed on)
            ["LONGSHOT_ROMFS_FROM"] = r.Source is BuildSource.Nup ? r.RomfsFrom : null,
        };
        if (windows && r.Jobs is int workers) env["LONGSHOT_JOBS"] = workers.ToString(CultureInfo.InvariantCulture);
        // (Intel shader compiles, Side by side or One at a time: the shader preparation make_game starts takes it from its
        // environment, as the game does. Auto sets nothing, so the game's own Auto, or one set on the PC, applies)
        if (windows && GamePlay.IntelCompilesValue(r.IntelCompiles) is string compiles) env[GamePlay.IntelCompilesVar] = compiles;
        if (windows && r.IntelLongerLimits)
            foreach (var (name, value) in GamePlay.IntelLongerLimits) env[name] = value;
        if (windows)
        {
            // (the tools' own compiler, headers and libraries when they have them: nothing of the PC's but Windows' own)
            foreach (var (name, folder) in new[] { ("EMSDK", "emsdk"), ("RX_CPM", "cpm") })
            {
                var path = Path.Combine(tools.ToolsRoot, folder);
                if (directoryExists(path)) env[name] = path;
            }
        }
        else
        {
            // (builder/linux/AppRun: everything the AppImage builds with)
            var lib = tools.LibRoot;
            env["LONGSHOT_TOOLS"] = tools.ToolsRoot;
            env["EMSDK"] = Path.Combine(lib, "emsdk");
            env["LONGSHOT_SYSROOT"] = Path.Combine(lib, "sysroot");
            env["LONGSHOT_FFMPEG"] = Path.Combine(lib, "ffmpeg");
            // (tools.lock's sdl2, unpacked beside the builder; a builder that carried its tools has it in lib itself)
            env["LONGSHOT_SDL2"] = directoryExists(Path.Combine(lib, "sdl2"))
                ? Path.Combine(lib, "sdl2", "libSDL2-2.0.so.0") : Path.Combine(lib, "libSDL2-2.0.so.0");
            // (the Vulkan back end: every build of current tools compiles it, and their AppImage carries none. One made before
            // 2026-11-14 carried a prebuilt lib/longshot_vk.so, which its build took in place of compiling one unless DLSS or FSR 4
            // was on (NGX or AMD's FSR API headers, which the prebuilt one has neither of): given only where it is there)
            var prebuilt = Path.Combine(lib, "longshot_vk.so");
            env["LONGSHOT_VK_PREBUILT"] = dlss || fsr || !fileExists(prebuilt) ? null : prebuilt;
        }
        return new MakeGameCommand(tools.Python, args, tools.ToolsRoot, env);
    }

    /// Whether these tools build AMD FSR 4: on Windows always; on Linux when they carry AMD's FSR API headers, which the Vulkan
    /// back end compiled in the build reads (an AppImage made before it carried them has none, and its build would stop at them)
    public static bool FsrBuilds(Toolchain tools, Platform platform, Func<string, bool>? directoryExists = null) =>
        platform == Platform.Windows || (directoryExists ?? Directory.Exists)(Path.Combine(tools.ToolsRoot, "third_party", "fidelityfx"));

    /// Whether these tools fetch Intel's XeSS library: they carry scripts/xess_sdk.py, which make_game.py's --fetch-xess-runtime
    /// runs (Longshot's tools from before it would stop at the flag they do not know)
    public static bool XessBuilds(Toolchain tools, Func<string, bool>? fileExists = null) =>
        (fileExists ?? File.Exists)(Path.Combine(tools.ToolsRoot, "scripts", "xess_sdk.py"));

    /// make_game.py --check: is this the game, and the version these tools build? (the 2.x Builders' check: --nup, or the exefs
    /// folder alone). The same program and environment as a build with no DLSS; a .nup is read into &lt;work&gt;/probe
    public static MakeGameCommand ForCheck(Toolchain tools, BuildSource source, Platform platform, LauncherHome home, string launcher,
                                           string? work = null, Func<string, bool>? directoryExists = null)
    {
        var build = For(tools, new BuildRequest(new BuildSource.Update("."), null, Work: work), platform, home, launcher, directoryExists);
        var args = new List<string> { "-I", tools.MakeGame, "--check" };
        switch (source)
        {
            case BuildSource.Nup n:
                args.AddRange(new[] { "--nup", n.Path });
                break;
            case BuildSource.Folders f:
                args.AddRange(new[] { "--exefs", f.Exefs });
                break;
            default:
                throw new ArgumentException("Only game files are checked, not a game folder.", nameof(source));
        }
        args.AddRange(new[] { "--work", work ?? home.Work });
        return build with { Arguments = args };
    }

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

    /// The environment the game starts with, from the launcher's. intelCompiles: Intel shader compiles (IntelCompilesAuto...);
    /// intelLongerLimits: Shader time limit on Longer (both Windows only)
    public static Dictionary<string, string> Environment(Platform platform, IReadOnlyDictionary<string, string> current,
                                                         int intelCompiles = IntelCompilesAuto, bool intelLongerLimits = false)
    {
        var env = platform == Platform.Windows
            ? new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase)
            : current.Where(kv => !Dropped.Any(p => kv.Key.StartsWith(p, StringComparison.Ordinal)) && kv.Key != "LD_LIBRARY_PATH")
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        env["LONGSHOT_NO_UPDATE_CHECK"] = "1";   // (the launcher already checked for updates: the game does not ask again)
        if (platform == Platform.Windows && IntelCompilesValue(intelCompiles) is string compiles) env[IntelCompilesVar] = compiles;
        if (platform == Platform.Windows && intelLongerLimits)
            foreach (var (name, value) in IntelLongerLimits) env[name] = value;
        return env;
    }

    /// The user folder a build is started with (StartInfo's user): Longshot's shared one (SaveEnvironment.LongshotUser), where
    /// its saves are and, from the Longshot that takes --user for them, its settings (host/options_path.c). Every Windows build.
    /// On Linux the game finds the shared folder by itself for the saves, and keeps its settings beside itself without --user:
    /// given it too, unless the build's folder has a user/ of its own, which the game takes in itself (an old build's saves, moved
    /// into the shared folder at its next start) or keeps (a link of the player's: their own arrangement). Null: none
    public static string? UserFolder(SaveEnvironment env, string folder)
    {
        if (env.Platform == Platform.Windows) return env.LongshotUser;
        var own = Path.Combine(folder, "user");
        try
        {
            if (Directory.Exists(own) || File.Exists(own) || new FileInfo(own).LinkTarget != null) return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return env.LongshotUser;
    }

    /// user: Longshot's user folder, which every build shares (UserFolder), given to the game as --user. Every build takes --user,
    /// the ones from before the launcher too; without it the game keeps its saves in its own folder's user\ on Windows
    public static ProcessStartInfo StartInfo(string folder, Platform platform, IReadOnlyDictionary<string, string>? current = null, string? user = null,
                                             int intelCompiles = IntelCompilesAuto, bool intelLongerLimits = false)
    {
        current ??= System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "");
        var si = new ProcessStartInfo(GameFolder.ExePath(folder, platform)) { UseShellExecute = false, WorkingDirectory = folder };
        if (user != null)
        {
            si.ArgumentList.Add("--user");
            si.ArgumentList.Add(user);
        }
        si.Environment.Clear();
        foreach (var (k, v) in Environment(platform, current, intelCompiles, intelLongerLimits)) si.Environment[k] = v;
        return si;
    }
}
