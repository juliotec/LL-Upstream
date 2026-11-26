using System.Globalization;
using System.Text.Json;

namespace LongshotLauncher.Core;

/// Longshot's new builder, one self-contained program a release ships per system (LongshotBuildRelease: it carries its sources,
/// compilers and libraries, so nothing else is installed for it), a clean break from make_game.py: none of its options, @@ lines
/// or folder layouts. The launcher runs
///     longshot-builder build --game &lt;file.nup&gt; --out &lt;game folder&gt; --output json [--jobs N] [--language ..]
///                          [--upscaler dlss,fsr4,xess|none] [--accept-licenses nvidia,amd,intel] [--no-prepare-shaders]
///     Longshot.exe build --profile &lt;profile&gt; --game &lt;file.nup&gt; --output json ...   (a version folder's builder, BuilderVersions:
///                          its game folder the version's builds/default, the profile's profile.json written)
///     longshot-builder update &lt;game folder&gt; --output json ...  (a BuildRequest whose source is BuildSource.Update)
///     Longshot.exe update --profile &lt;profile&gt; --output json ...
///     longshot-builder check --game &lt;file.nup&gt; --output json
///     longshot-builder index --game &lt;file.nup&gt; --out &lt;game folder&gt; --output json     (a .nup moved: Index)
/// and reads its events (BuildEvents): --output json, the events alone on stdout (Longshot #2286 took --json away: a usage error
/// now). Exit status 0 done, 1 stopped (said by its error event), 2 a command line it does not take (said on stderr). No
/// command names the launcher to the builder. Every command is given the launcher's settings as flags (Settings: --cache and --builder-exe; Longshot #2286
/// reads them in place of LONGSHOT_CACHE and LONGSHOT_BUILDER_EXE; never --ngx-sdk, Build says why). Its
/// build files are in its --cache, which the launcher gives inside its own build files folder
/// (Disk.LongshotBuildCache), so Storage and Remove everything find them. The game it builds reads its files from the player's
/// .nup where it is (an index in the folder, longshot_romfs.idx): the .nup is never copied, and must stay where it is
/// (NupInPlace, MissingNup)
public sealed class LongshotBuilder(Toolchain tools, string exe, Platform platform, LauncherHome home, string launcher,
                                    Func<string, string?>? getenv = null)
{
    /// The record the build leaves in the game folder (build-driver's folder.rs RECORD_FILE): what it was built from
    public const string RecordFile = "longshot_build.json";

    /// Its names, the newest first: RecordFile, then the ones a builder before wrote (folder.rs OLD_RECORD_FILES), read when the
    /// newer is not there, and replaced by it at the folder's next build
    public static readonly IReadOnlyList<string> RecordFiles = [RecordFile, "longshot_builder.json", "longshot.json"];

    /// How every command is asked to speak: its events as JSON Lines on stdout and nothing else there
    public static readonly IReadOnlyList<string> OutputJson = ["--output", "json"];

    /// The same builder run as a version folder's (BuilderVersions.ExeOf): what plays and builds that folder
    public LongshotBuilder In(string versionFolder) =>
        new(tools, BuilderVersions.ExeOf(versionFolder, platform), platform, home, launcher, variable);

    readonly Func<string, string?> variable = getenv ?? System.Environment.GetEnvironmentVariable;

    public Toolchain Tools => tools;
    public string Exe => exe;

    /// The program's name, the one place it is said (Longshot #2286): its release files are &lt;Name&gt;-&lt;version&gt;-&lt;system&gt; (.exe on
    /// windows-x64) with a manifest &lt;Name&gt;-&lt;version&gt;-&lt;system&gt;.json and deltas &lt;Name&gt;-&lt;from&gt;-to-&lt;version&gt;-&lt;system&gt;.zst-patch
    /// (LongshotBuildRelease: the manifest names them), and the file installed in a version's folder is named for it
    public const string Name = "longshot-builder";

    public static string ExeName(Platform platform) => platform == Platform.Windows ? Name + ".exe" : Name;

    /// The vendors whose licences longshot-builder may download files under (--accept-licenses), by the request: NVIDIA for DLSS,
    /// AMD for FSR 4's runtime and Intel for XeSS's library (both on Windows only: Linux has neither to download). Only a licence
    /// the player accepted (BuildRequest's ...Licence, the settings step's prompts) is ever named
    public static IReadOnlyList<string> Licences(BuildRequest r, Platform platform)
    {
        var v = new List<string>();
        if (r.Dlss && r.DlssLicense) v.Add("nvidia");
        if (platform == Platform.Windows && r.Fsr && r.FsrLicense) v.Add("amd");
        if (platform == Platform.Windows && r.Xess && r.XessLicense) v.Add("intel");
        return v;
    }

    /// The vendor a license_not_accepted error names ("... (--accept-licenses amd), or build without it."): nvidia, amd or intel;
    /// null when it names none
    public static string? LicenceOf(string? text) =>
        text != null && System.Text.RegularExpressions.Regex.Match(text, @"--accept-licenses (nvidia|amd|intel)\b") is { Success: true } m ? m.Groups[1].Value : null;

    public BuilderRun Build(BuildRequest r)
    {
        if (r.Jobs is <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Build workers must be positive.");
        if (r.MemoryGb is <= 0) throw new ArgumentOutOfRangeException(nameof(r), "Build memory must be positive.");
        var args = new List<string>();
        if (r.Source is BuildSource.Update u)
        {
            if (r.Profile is { } profile) args.AddRange(["update", "--profile", Path.GetFullPath(profile)]);
            else args.AddRange(["update", u.Folder]);
        }
        else
        {
            args.Add("build");
            if (r.Profile is { } profile) args.AddRange(["--profile", Path.GetFullPath(profile), "--game", Game(r.Source)]);
            else args.AddRange(["--game", Game(r.Source), "--out", r.Out ?? throw new ArgumentException("A build needs its game folder (Out).", nameof(r))]);
            if (Languages.Normalize(r.Language) is { } language) args.AddRange(["--language", language]);
        }
        args.AddRange(OutputJson);
        // (the row's own work folder, so an update into its renamed folder finds its last build's lift and objects: the builder's
        // own is named for the game folder's path)
        // (the row's work folder, which an update's rename of its folder keeps; not for a version folder's build, never renamed:
        // the builder's own for it, DefaultWorkFolder, which Play's update takes too, so the two share one work folder)
        if (r.WorkId is { } id && r.Profile == null) args.AddRange(["--work", WorkFolder(r.Work ?? home.Work, id)]);
        if (r.Jobs is int jobs) args.AddRange(["--jobs", jobs.ToString(CultureInfo.InvariantCulture)]);
        var notes = new List<string>();
        // (an upscaler only with its licence accepted: the settings step shows the licence before one turns on. DLSS's SDK is
        // longshot-builder's to download under NVIDIA's licence, at the commit it pins; FSR 4's and XeSS's runtimes are downloaded on
        // Windows under AMD's and Intel's)
        var dlss = r.Dlss && r.DlssLicense;
        var fsr = r.Fsr && r.FsrLicense;
        var xess = r.Xess && r.XessLicense && platform == Platform.Windows;
        var upscalers = new List<string>();
        if (dlss) upscalers.Add("dlss");
        if (fsr) upscalers.Add("fsr4");
        if (xess) upscalers.Add("xess");
        // (always said: an update given none keeps the folder's, and the player's choice is this build's)
        args.AddRange(["--upscaler", upscalers.Count > 0 ? string.Join(",", upscalers) : "none"]);
        // (build and update alike: the record keeps them for a later update, but the player's answers now are what count)
        if (Licences(r, platform) is { Count: > 0 } licences) args.AddRange(["--accept-licenses", string.Join(",", licences)]);
        if (!r.PrepareShaders) args.Add("--no-prepare-shaders");
        // (the player's yes to statistics: the builder sends the build's end itself, as at play)
        if (r.SendStatistics) args.Add(BuilderVersions.SendStatistics);
        // (never --ngx-sdk: the SDK an earlier make_game.py build fetched into the launcher's ngx-sdk folder is of that release's
        // pin, and a folder given is taken as it is, none of NVIDIA's files downloaded, so DLSS would be built from an older SDK than
        // the one the builder pins and the settings step shows the licence of. The builder says no pin to compare a folder with)
        args.AddRange(Settings(r.Work));
        var env = Environment();
        // (Build memory: the compilers' memory gate, set after the PC's variables are dropped, in whole GB as the builder reads it)
        if (r.MemoryGb is int memory) env["LONGSHOT_MEM_LIMIT_GB"] = memory.ToString(CultureInfo.InvariantCulture);
        if (platform == Platform.Windows && GamePlay.IntelCompilesValue(r.IntelCompiles) is string compiles) env[GamePlay.IntelCompilesVar] = compiles;
        if (platform == Platform.Windows && r.IntelLongerLimits)
            foreach (var (name, value) in GamePlay.IntelLongerLimits) env[name] = value;
        return new BuilderRun(Command(args, env, r.LowPriority), dlss, fsr, xess, notes);
    }

    /// longshot-builder check of a game file
    public MakeGameCommand Check(BuildSource source, string? work = null) =>
        source is BuildSource.Update ? throw new ArgumentException("Only game files are checked, not a game folder.", nameof(source))
            : Command(["check", "--game", Game(source), .. OutputJson, .. Settings(work)], Environment(), true);

    public async Task<GameCheck> CheckAsync(BuildSource source, string? work = null, CancellationToken cancel = default)
    {
        MakeGameCommand command;
        try { command = Check(source, work); }
        catch (ArgumentException e) { return new GameCheck(GameCheckStatus.CheckFailed, e.Message); }
        if (MakeGameRun.Blocked(command, platform == Platform.Windows) is { } blocked) return new GameCheck(GameCheckStatus.CheckFailed, blocked, ThisPc: true);
        var lines = new List<string>();
        string? lastError = null;
        try
        {
            var code = await MakeGameRun.RunAsync(command.StartInfo(), l => { lock (lines) lines.Add(l); },
                                                  l => { lock (lines) lastError = WhyFrom(lastError, l); }, cancel, command.LowPriority).ConfigureAwait(false);
            lock (lines) return ReadCheck(lines.ToList(), lastError, code);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new GameCheck(GameCheckStatus.CheckFailed, Tr.F("The build tools could not start: {0}", e.Message), ThisPc: true);
        }
    }

    /// longshot-builder index of a game folder whose .nup moved (MissingNup): the same .nup, picked where it is now, pointed at in
    /// seconds rather than the game built again. It checks the .nup is the one the folder was built from (its size and table of
    /// contents: not_the_same_nup otherwise), makes the romfs index again for its new place and has the folder's record name it.
    /// It takes the game folder's lock itself (GameLockFile)
    public MakeGameCommand Index(string nup, string folder, string? work = null) =>
        Command(["index", "--game", nup, "--out", folder, .. OutputJson, .. Settings(work)], Environment(), false);

    public async Task<IndexResult> IndexAsync(string nup, string folder, string? work = null, CancellationToken cancel = default)
    {
        var command = Index(nup, folder, work);
        if (MakeGameRun.Blocked(command, platform == Platform.Windows) is { } blocked) return new IndexResult(IndexOutcome.Failed, blocked);
        var lines = new List<string>();
        string? lastError = null;
        try
        {
            var code = await MakeGameRun.RunAsync(command.StartInfo(), l => { lock (lines) lines.Add(l); },
                                                  l => { lock (lines) lastError = WhyFrom(lastError, l); }, cancel, command.LowPriority).ConfigureAwait(false);
            lock (lines) return ReadIndex(lines.ToList(), lastError, code);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new IndexResult(IndexOutcome.Failed, Tr.F("The build tools could not start: {0}", e.Message));
        }
    }

    /// index's answer from its stdout, the line of its stderr that says why (WhyFrom) and its exit status: done (exit status 0, no
    /// error event), another .nup than the folder's (not_the_same_nup: the game is built again from it), or why it stopped
    public static IndexResult ReadIndex(IEnumerable<string> stdout, string? lastError, int exitCode)
    {
        BuildEvent.Error? error = null;
        foreach (var line in stdout)
            if (BuildEvents.Parse(line) is BuildEvent.Error e) error = e;
        if (error != null)
            return new IndexResult(error.Code == "not_the_same_nup" ? IndexOutcome.NotTheSameNup : IndexOutcome.Failed, error.Text, error.Code);
        if (exitCode == 0) return new IndexResult(IndexOutcome.Done);
        return new IndexResult(IndexOutcome.Failed,
            string.IsNullOrWhiteSpace(lastError) ? Tr.F("The build tools stopped without saying why (exit code {0}).", exitCode) : lastError.Trim());
    }

    /// The moved .nup of entry's game folder pointed at where it is now (IndexAsync), and, once it is, the row and the launcher's
    /// game-file setting naming it (saved: the next update builds from it, and step 1 picks it). Nothing changes otherwise:
    /// not_the_same_nup is for the caller to build again from it (the build then names it). The row and the setting change on the
    /// caller's thread (the window's: no ConfigureAwait), as a build's end changes them
    public async Task<IndexResult> RepointAsync(BuildEntry entry, string nup, BuildRegistry registry, LauncherSettings settings, string settingsPath,
                                                string? work = null, CancellationToken cancel = default)
    {
        var result = await IndexAsync(nup, entry.Folder, work, cancel);
        if (result.Outcome != IndexOutcome.Done) return result;
        entry.Nup = nup;
        entry.Romfs = entry.Exefs = null;
        entry.NupInPlace = true;
        settings.Nup = nup;
        settings.Romfs = settings.Exefs = null;
        try
        {
            registry.Save();
            settings.Save(settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // (the game reads it already: its folder's record names it. Only the launcher's list did not keep it)
            return result with { Text = Tr.F("The list of builds could not be saved: {0}", e.Message) };
        }
        return result;
    }

    /// What a builder command said: its exit status (-1: it could not start), its events in order, its error event (null: none)
    /// and the line of its stderr that says why (WhyFrom), or why it could not start
    public sealed record Answer(int Exit, IReadOnlyList<BuildEvent> Events, BuildEvent.Error? Error, string? Why);

    /// The variables of the launcher's that a builder's command keeps, though Dropped takes every LONGSHOT_ one: the release
    /// list's way (LONGSHOT_LAUNCHER_NO_CDN: git.magicbean.dev alone) and one more key to trust (LONGSHOT_LAUNCHER_TRUST, a test
    /// release's), which fetch reads under these names too
    public static readonly IReadOnlyList<string> Kept = ["LONGSHOT_LAUNCHER_NO_CDN", "LONGSHOT_LAUNCHER_TRUST"];

    /// exe run with args (--output json among them), its events handed to each as they come (from a pool thread), with the
    /// environment every builder run has (Dropped) and env's changes on top. Cancelling ends it (OperationCanceledException)
    public static async Task<Answer> AskAsync(string exe, IReadOnlyList<string> args, Action<BuildEvent>? each = null, CancellationToken cancel = default,
                                              Func<string, string?>? getenv = null, IReadOnlyDictionary<string, string?>? env = null, bool lowPriority = false)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var k in Kept)
            if ((getenv ?? System.Environment.GetEnvironmentVariable)(k) is { Length: > 0 } v) variables[k] = v;
        if (env != null)
            foreach (var (k, v) in env) variables[k] = v;
        var command = new MakeGameCommand(exe, args, Path.GetDirectoryName(exe) ?? ".", variables, lowPriority, Dropped);
        var events = new List<BuildEvent>();
        string? why = null;
        try
        {
            var code = await MakeGameRun.RunAsync(command.StartInfo(), l =>
            {
                if (BuildEvents.Parse(l) is not { } e) return;
                lock (events) events.Add(e);
                each?.Invoke(e);
            }, l => { lock (events) why = WhyFrom(why, l); }, cancel, lowPriority).ConfigureAwait(false);
            lock (events) return new Answer(code, events.ToList(), events.OfType<BuildEvent.Error>().LastOrDefault(), why?.Trim());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new Answer(-1, [], null, Tr.F("The build tools could not start: {0}", e.Message));
        }
    }

    /// A command of this builder's (AskAsync) with the environment its builds have (Environment: the launcher's data folder)
    public Task<Answer> AskAsync(IReadOnlyList<string> args, Action<BuildEvent>? each = null, CancellationToken cancel = default, bool lowPriority = false) =>
        AskAsync(exe, args, each, cancel, variable, Environment(), lowPriority);

    /// What this builder says of itself and of profile's game folder (BuilderInfo, `info --profile &lt;profile&gt; --output json`);
    /// null when it gave no info event (an older builder, or one that did not start)
    public async Task<BuilderInfo?> InfoAsync(string? profile, CancellationToken cancel = default) =>
        (await AskAsync(BuilderInfo.Arguments(profile), cancel: cancel).ConfigureAwait(false)).Events.OfType<BuildEvent.Info>().LastOrDefault()?.Value;

    public static MakeGameLine? Read(string line) =>
        BuildEvents.Parse(line) is { } e ? BuildEvents.ToLine(e)
        // (stdout carries events only: anything else is shown in the details, an event of another version passed over)
        : line.TrimStart().StartsWith('{') ? null : new MakeGameLine.Plain(line.TrimEnd('\r'));

    /// The error codes that are the game files' (build-events.md's "Codes": The game files): a check's Unreadable, but
    /// unsupported_build, another version (OtherVersion). Every other code is this PC's or the builder's own (OwnCodes). The
    /// tests compare both with a copy of the doc's lists (tests/fixtures/longshot-builder/codes.md)
    public static readonly IReadOnlySet<string> FileCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "game_not_found", "game_folder_incomplete", "no_game_files", "no_romfs", "nup_missing", "not_a_nup", "nup_incomplete",
        "nup_unreadable", "nup_locked", "nup_drive_error", "nup_unsafe_path", "nup_path_not_unicode", "nup_path_too_long", "nup_damaged",
        "no_such_title", "nup_moved", "not_the_same_nup", "not_a_module", "unsupported_build", "romfs_incomplete", "dump_damaged", "dump_has_links", "dump_unreadable", "extraction_link",
    };

    /// The builder's own failures (build-events.md's "Codes": The builder's own failures), not the player's nor this PC's to fix
    /// (their text asks for the build log)
    public static readonly IReadOnlySet<string> OwnCodes = new HashSet<string>(StringComparer.Ordinal) { "io", "tool_failed", "bad_data", "internal_error" };

    /// The check's answer from its stdout, the line of its stderr that says why (WhyFrom) and its exit status: its check event
    /// (the supported build, or another), its error event (the file's, or not: FileCodes), or nothing said (the check could not
    /// run: exit status 2 is a command line it did not take, said on stderr)
    public static GameCheck ReadCheck(IEnumerable<string> stdout, string? lastError, int exitCode)
    {
        GameCheck? answer = null;
        foreach (var line in stdout)
            switch (BuildEvents.Parse(line))
            {
                case BuildEvent.Check { Ok: true } c:
                    answer = new GameCheck(GameCheckStatus.Supported, c.Text);
                    break;
                case BuildEvent.Check c:
                    answer = new GameCheck(GameCheckStatus.OtherVersion, c.Text, Code: "unsupported_build");
                    break;
                case BuildEvent.Error e:
                    var status = e.Code == "unsupported_build" ? GameCheckStatus.OtherVersion
                        : FileCodes.Contains(e.Code) ? GameCheckStatus.Unreadable : GameCheckStatus.CheckFailed;
                    answer = new GameCheck(status, e.Text, ThisPc: status == GameCheckStatus.CheckFailed && !OwnCodes.Contains(e.Code), Code: e.Code);
                    break;
            }
        return answer ?? new GameCheck(GameCheckStatus.CheckFailed,
            string.IsNullOrWhiteSpace(lastError) ? Tr.F("The build tools stopped without saying why (exit code {0}).", exitCode) : lastError.Trim());
    }

    /// What --game names: the .nup, or a folder holding romfs/ and exefs/ (a developer's unpacked dump)
    static string Game(BuildSource source) => source switch
    {
        BuildSource.Nup n => n.Path,
        BuildSource.Folders f when Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(f.Romfs)) is { } parent
                                   && Path.GetFileName(Path.TrimEndingDirectorySeparator(f.Romfs)) == "romfs"
                                   && Path.GetFileName(Path.TrimEndingDirectorySeparator(f.Exefs)) == "exefs"
                                   && BuildRegistry.SameFolder(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(f.Exefs)) ?? "", parent) => parent,
        // (said to the player: CheckAsync's answer)
        BuildSource.Folders => throw new ArgumentException(
            Tr.T("This version's builder takes a .nup, or one folder holding the romfs and exefs folders side by side.")),
        _ => throw new ArgumentException(Tr.T("A build from game files needs a .nup or a dump folder.")),
    };

    /// The work folder of a launcher's row (BuildRequest.WorkId) in longshot-builder's cache (work: the build files folder): beside
    /// the builder's own, under work/, so its prune of a work folder whose game folder is gone takes it too once the row is
    /// deleted. Only the id's letters and digits
    public static string WorkFolder(string work, string id) =>
        Path.Combine(work, Disk.LongshotWork, "launcher-" + new string(id.Where(char.IsAsciiLetterOrDigit).ToArray()));

    /// The note in a work folder of the game folder it builds (Longshot #2286's layout.rs GAME_NOTE)
    public const string WorkGameNote = "game_folder.txt";

    /// The work folder longshot-builder takes by itself for gameFolder (no --work: Play's update, and the launcher's builds of a
    /// version folder) in cache, as Longshot #2286's layout.rs work_of names it: work/&lt;its name's letters, digits and dashes,
    /// 24 at most&gt;-&lt;the first 10 hex of the SHA-1 of its absolute path, '/' between parts, no '/' at its end&gt;; on Windows both
    /// in lower case, on Linux the path with its links resolved (layout.rs game_folder)
    public static string DefaultWorkFolder(string cache, string gameFolder, Platform platform)
    {
        var abs = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
        if (platform != Platform.Windows) abs = Saves.Real(abs);
        var text = abs.Replace('\\', '/').TrimEnd('/');
        if (platform == Platform.Windows) text = text.ToLowerInvariant();
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..10];
        var name = new string(abs.Replace('\\', '/').TrimEnd('/').Split('/')[^1].Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(24).ToArray());
        if (platform == Platform.Windows) name = name.ToLowerInvariant();
        return Path.Combine(cache, "work", name.Length == 0 ? id : name + "-" + id);
    }

    /// The launcher's settings every command is given, as flags after it: the build files in the launcher's build files folder
    /// (work, else the launcher's), and the launcher named for the game's update offer
    IEnumerable<string> Settings(string? work) =>
        ["--cache", Path.Combine(work ?? home.Work, Disk.LongshotBuildCache), "--builder-exe", launcher];

    /// The environment of every run: the variables the PC may have set that the launcher's choices replace left out (Dropped
    /// takes every LONGSHOT_, RX_ and A64_ one too). No tools' folders: longshot-builder carries its compilers and libraries.
    /// The PC's LONGSHOT_SHADER_STORE is set again: the game the build starts to prepare its shaders prepares them in the store it
    /// plays with (SaveEnvironment.ShaderStore reads the same variable, and Play keeps it: GamePlay.Environment), as
    /// longshot-builder's own game_env keeps it for that game; unset, the game's default store is both's. LONGSHOT_DATA (the
    /// builder takes no flag for it) is the launcher's own data folder (LauncherHome.BuilderData), so the notes of the licences
    /// accepted for its game folders never mix with a developer's, and go with the launcher's home (RemoveEverything)
    Dictionary<string, string?> Environment() => new(StringComparer.Ordinal)
    {
        [DataVar] = home.BuilderData,
        ["LONGSHOT_ROOT"] = null,
        ["LONGSHOT_STORE"] = null,
        ["LONGSHOT_JOBS"] = null,
        ["LONGSHOT_NO_PREPARE"] = null,
        ["LONGSHOT_FSR"] = null,
        ["LONGSHOT_OBJ_STORE"] = null,
        ["LONGSHOT_TOOLS"] = null,
        [ShaderStoreVar] = variable(ShaderStoreVar) is { Length: > 0 } store ? store : null,
    };

    /// The shader store's switch (Longshot's switches.md): the one LONGSHOT_ variable of the PC's a build keeps
    public const string ShaderStoreVar = "LONGSHOT_SHADER_STORE";

    /// longshot-builder's data folder (build-driver's layout.rs data_root): what must outlive its cache, the licences accepted
    public const string DataVar = "LONGSHOT_DATA";

    MakeGameCommand Command(IReadOnlyList<string> args, Dictionary<string, string?> env, bool lowPriority) =>
        new(exe, args, Path.GetDirectoryName(exe) ?? tools.Folder, env, lowPriority, Dropped);

    /// The environment variables a build is started without, by prefix: any of them set on the PC (LONGSHOT_ROOT, LONGSHOT_STORE,
    /// LONGSHOT_SYSROOT, LONGSHOT_CACHE, LONGSHOT_NGX_SDK, RX_CPM, any RX_ or A64_ switch...) would redirect the build away from what
    /// the release carries and the player chose (the launcher's own cache, builder and NGX SDK are flags, Settings, which win over a
    /// variable anyway). The launcher's own variables (Build: Intel's RX_VK_ switches, LONGSHOT_MEM_LIMIT_GB; Environment: the shader
    /// store's, LONGSHOT_DATA) are set again after (MakeGameCommand.StartInfo). PerfReports drops the same
    public static readonly IReadOnlyList<string> Dropped = ["LONGSHOT_", "RX_", "A64_"];

    /// What a game folder's RecordFile says (build-driver's folder.rs Record): the release that built it ("builder"), the .nup its
    /// game reads its files from ("game": {"nup": ...}; null when built from a dump folder) and the upscalers built in
    /// ("upscalers": dlss, fsr4, xess, the names --upscaler takes)
    public sealed record BuildRecord(string? Builder, string? Nup, IReadOnlyList<string> Upscalers);

    /// The folder's RecordFile; null for a folder it did not build, or a record that can't be read
    public static BuildRecord? ReadRecord(string folder)
    {
        try
        {
            var path = RecordFiles.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
            if (path == null) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var nup = r.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Object ? Str(game, "nup") : null;
            var upscalers = r.TryGetProperty("upscalers", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList() : [];
            return new BuildRecord(Str(r, "builder"), nup, upscalers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    /// The .nup a game folder built by longshot-builder reads its files from; null for a folder it did not build, or one built
    /// from a dump folder
    public static string? RecordedNup(string folder) => ReadRecord(folder)?.Nup;

    /// The record of a game folder whose game longshot-builder built last: entry (the launcher's list's) says so
    /// (BuildEntry.NupInPlace, set by the build that made it); with none (--play of a folder no list holds), the folder's stamp
    /// (GameFolder.Version, longshot_version.txt) must name the release the record does, as a build by longshot-builder writes
    /// both. A make_game.py build over the folder since (an older launcher's) leaves a record that is not its game's (one from
    /// before that has another stamp)
    public static BuildRecord? CurrentRecord(string folder, BuildEntry? entry)
    {
        if (entry != null && entry.NupInPlace != true) return null;
        var record = ReadRecord(folder);
        if (record == null || entry != null) return record;
        return record.Builder is { Length: > 0 } builder && GameFolder.Version(folder) == builder ? record : null;
    }

    /// A game folder's game can't start: the .nup it reads its files from (CurrentRecord) is not where it was built from, and
    /// whether its drive is not there at all (a drive not connected: DriveRoot), which connecting it mends
    public sealed record MissingGame(string Nup, bool DriveMissing);

    /// The .nup a game folder reads its files from, when it is not there any more (moved, renamed or deleted, or its drive not
    /// connected): the game can't start until it is put back, or the game is built again from where it is now. Null when it is
    /// there, or the folder's game reads none (CurrentRecord). fileExists, driveThere: the tests' stand-ins
    public static MissingGame? MissingNup(string folder, BuildEntry? entry, Func<string, bool>? fileExists = null,
                                          Func<string, bool>? driveThere = null)
    {
        if (CurrentRecord(folder, entry)?.Nup is not { Length: > 0 } nup || (fileExists ?? File.Exists)(nup)) return null;
        return new MissingGame(nup, DriveRoot(nup) is { } root && !(driveThere ?? DriveThere)(root));
    }

    /// The drive a path is on, as the player plugs it in: Windows' root (E:\, \\server\share\); on Linux the folder a removable
    /// drive is mounted at, under /run/media/&lt;user&gt;/, /media/&lt;user&gt;/ or /mnt/ (the system's own drive, /, is always there:
    /// null). os: the tests' stand-in
    public static string? DriveRoot(string path, Platform? os = null)
    {
        if ((os ?? Platforms.Current) == Platform.Windows)
        {
            if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) return char.ToUpperInvariant(path[0]) + @":\";
            var unc = path.StartsWith(@"\\", StringComparison.Ordinal) ? path[2..].Split('\\', '/') : [];
            return unc.Length >= 2 && unc[0].Length > 0 && unc[1].Length > 0 ? $@"\\{unc[0]}\{unc[1]}\" : null;
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var depth = parts is ["run", "media", ..] ? 4 : parts is ["media", ..] ? 3 : parts is ["mnt", ..] ? 2 : 0;
        return depth > 0 && parts.Length > depth ? "/" + string.Join('/', parts.Take(depth)) : null;
    }

    /// A drive's root there: a folder with something in it (an unmounted mount point is an empty folder)
    static bool DriveThere(string root)
    {
        try { return Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    /// The first line of stderr that says why a run could not start: longshot-builder writes "longshot-builder: why" before its
    /// usage (exit status 2), so the first line of that form wins over the usage's lines after it; with none, the last line said.
    /// kept: the line chosen so far (null: none yet); line: the next one
    public static string? WhyFrom(string? kept, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return kept;
        var said = line.Trim();
        if (kept != null && kept.StartsWith(Name + ":", StringComparison.Ordinal)) return kept;
        return said;
    }

    /// longshot-builder's own cache when it is given no --cache (build-driver's layout.rs cache_root): %LOCALAPPDATA%\Longshot\Builder
    /// on Windows, ${XDG_CACHE_HOME:-~/.cache}/longshot-builder elsewhere. Null when LONGSHOT_CACHE names another (a developer's)
    public static string? DefaultCache(Platform platform, Func<string, string?> getenv)
    {
        string? Set(string k) => getenv(k) is { } v && v.Trim().Length > 0 ? v.Trim() : null;
        if (Set("LONGSHOT_CACHE") != null) return null;
        if (platform == Platform.Windows)
            return (Set("LOCALAPPDATA") ?? (Set("USERPROFILE") is { } u ? Path.Combine(u, "AppData", "Local") : null)) is { } local
                ? Path.Combine(local, "Longshot", "Builder") : null;
        return (Set("XDG_CACHE_HOME") ?? (Set("HOME") is { } h ? Path.Combine(h, ".cache") : null)) is { } cache ? Path.Combine(cache, Name) : null;
    }

    /// Whether the default cache holds only what a launcher's runs left there (a perf-report given no --cache unpacked its release's
    /// tree and tools): no work folder and no store, which only a build makes. A developer's builds there are never the launcher's
    public static bool LauncherLeftDefaultCache(string cache)
    {
        static bool Holds(string dir)
        {
            try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
        try
        {
            var info = new DirectoryInfo(cache);
            return info.Exists && info.LinkTarget == null && !Holds(Path.Combine(cache, "work")) && !Holds(Path.Combine(cache, "store"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// The lock a build holds in its game folder from its start to its end (build-core's lock.rs GAME_FILE): build (and update,
    /// a build), image and index take it (build-driver's build.rs: build, read_only, index_only), which write the folder's files
    /// or its romfs index; prepare, check, perf-report and share take none
    public const string GameLockFile = ".longshot-builder.lock";

    /// Whether a build holds gameFolder's lock now. The builder locks the file (Rust's File::try_lock: flock on Linux, LockFileEx
    /// on Windows), and the lock goes with its process however it ends, so a lock file left behind is no build. Looked at by
    /// opening it as the builder's lock would refuse: Linux, with FileShare.None, which .NET takes as flock(LOCK_EX | LOCK_NB);
    /// Windows, sharing it as the builder does, then a lock of its first byte. Only for that moment, and a build starting just
    /// then says another build holds the folder (game_folder_busy), so callers look only while a longshot-builder program runs
    /// (Saves.BuilderRunning)
    public static bool Building(string gameFolder) => Held(Path.Combine(gameFolder, GameLockFile));

    /// The lock a build holds in its work folder from its start to its end (build-core's lock.rs FILE): build, update and image
    public const string WorkLockFile = "longshot-builder.lock";

    /// Whether a longshot-builder lock file (GameLockFile, WorkLockFile) is held by a build now, looked at as Building says
    public static bool Held(string path)
    {
        try
        {
            var windows = OperatingSystem.IsWindows();
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, windows ? FileShare.ReadWrite | FileShare.Delete : FileShare.None);
            if (windows)
            {
                f.Lock(0, 1);
                f.Unlock(0, 1);
            }
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException
                                  or NotSupportedException) { return false; }
        catch (IOException) { return true; }
    }
}

/// What longshot-builder index did (LongshotBuilder.IndexAsync): the game folder points at the .nup's new place (Done), the .nup
/// is not the one it was built from (NotTheSameNup: build again from it), or it stopped (Failed, Text says why)
public enum IndexOutcome { Done, NotTheSameNup, Failed }

public sealed record IndexResult(IndexOutcome Outcome, string? Text = null, string? Code = null);

/// A game folder's game can't start: the .nup it reads its files from is not where it was built from (LongshotBuilder.MissingNup),
/// or the drive it is on is not connected
public sealed class NupMovedException(string nup, bool driveMissing = false) : IOException(driveMissing
    ? Tr.F("Longshot reads the game's files from your .nup at {0}, and that drive is not connected. Connect it, or pick the .nup where it is now to build the game again.", nup)
    : Tr.F("Longshot reads the game's files from your .nup, which is no longer at {0}. Put it back, or pick it where it is now to build the game again.", nup))
{
    public string Nup => nup;
    public bool DriveMissing => driveMissing;
}
