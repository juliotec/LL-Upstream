using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LongshotLauncher.Core;

/// A profile's profile.json (v1, Longshot's docs/dev/reference/profiles.md; its launcher-profile crate reads and writes the same):
/// its name, colour, the version it played last, the save Home starts it on, when it last played, the profile it was copied
/// from, the game copy it builds from (a .nup or a dump folder) and its build's upscalers. Kept as the file's own JSON object, so
/// a key this launcher does not know (the builder's, the Mod API's "mods", a newer launcher's) stays as it is, in its place, when
/// the file is written again
public sealed class ProfileFile
{
    public const int Version1 = 1;

    public JsonObject Json { get; }

    public ProfileFile(JsonObject? json = null)
    {
        Json = json ?? new JsonObject();
        if (Json["v"] is null) Json["v"] = Version1;
    }

    string? Str(string key) => Json[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    void Put(string key, string? value)
    {
        if (value == null) Json.Remove(key);
        else Json[key] = value;
    }

    public int V => Json["v"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    /// A file of a newer format than this launcher's (v above 1): read for what it can, never written (the builder refuses it
    /// too, profile_unreadable: update Longshot)
    public bool Newer => V > Version1;
    public string Name { get => Str("name") ?? ""; set => Put("name", value); }
    public string? Color { get => Str("color"); set => Put("color", value); }
    /// The version it played last (a versions/&lt;v&gt; folder's name, or an older version's BuildEntry.Id); null: the launcher's default
    public string? Version { get => Str("version"); set => Put("version", value); }
    /// The save it starts on: a slot's folder name (slot_02). The builder's game reads it ("slot_02", "2" or 2: a number is read
    /// as the slot's folder number); null: the title screen's file list
    public string? StartSlot
    {
        get => Json["start_slot"] is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 0 and <= 99 ? $"slot_{n:00}"
            : Str("start_slot") is { } s && int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m)
              && m is >= 0 and <= 99 ? $"slot_{m:00}" : Str("start_slot");
        set => Put("start_slot", value);
    }
    /// When it last played, ISO 8601
    public DateTime? LastPlayed
    {
        get => Str("last_played") is { } s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                                                    System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
        set => Put("last_played", value?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
    }
    public string? CopyOf { get => Str("copy_of"); set => Put("copy_of", value); }
    /// The game copy this profile's builds are made from: a .nup, or a dump folder (its romfs and exefs). Relative: to the profile's
    /// folder, as the builder reads it
    public string? Game { get => Str("game"); set => Put("game", value); }

    /// Its build's upscalers: "dlss", "fsr4", "xess", as the builder's --upscaler names them (the licences the player accepted
    /// are the launcher's or this PC's, never here)
    public IReadOnlyList<string> Upscalers
    {
        get => Json["upscalers"] is JsonArray a ? a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
        set
        {
            if (value.Count == 0) Json.Remove("upscalers");
            else Json["upscalers"] = new JsonArray(value.Distinct().Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
    }
    public static readonly IReadOnlyList<string> KnownUpscalers = ["dlss", "fsr4", "xess"];

    public static ProfileFile Parse(string text) =>
        new(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new JsonException("profile.json is not a JSON object"));

    public string Write() => Json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
}

/// The profiles' folders, &lt;launcher data&gt;/profiles/&lt;id&gt;/: profile.json, settings.ini (the game's settings) and saves/ (the game's
/// saves: save/&lt;uid&gt;/slot_NN and save-work/), the layout Longshot's builder and game read from LONGSHOT_PROFILE. The folders are
/// the profiles: the launcher lists them, and keeps which one is current in its own settings
public static class ProfileStore
{
    public const string FileName = "profile.json";
    public const string SavesName = "saves";
    public const string SettingsName = "settings.ini";

    public static string Root(string launcherData) => Path.Combine(launcherData, "profiles");
    public static string Dir(string root, string id) => Path.GetFullPath(Path.Combine(root, id));
    public static string FileOf(string dir) => Path.Combine(dir, FileName);
    public static string SavesOf(string dir) => Path.Combine(dir, SavesName);
    public static string SettingsOf(string dir) => Path.Combine(dir, SettingsName);

    /// A profile folder's file, or null when it has none or it cannot be read as one
    public static ProfileFile? Load(string dir) => Read(dir).File;

    /// A profile folder's file and what is wrong with it: no profile.json, one that cannot be read, or one of a newer format (File is
    /// then read, never written); Problem null when it is fine
    public static (ProfileFile? File, string? Problem) Read(string dir)
    {
        try
        {
            if (!File.Exists(FileOf(dir))) return (null, FileName + " is missing");
            var file = ProfileFile.Parse(File.ReadAllText(FileOf(dir)));
            return (file, file.Newer ? FileName + " is of a newer launcher's format" : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, e.Message); }
    }

    /// The file written whole (AtomicFile); a file of a newer format is never written over
    public static void Save(string dir, ProfileFile file)
    {
        if (file.Newer) return;
        Directory.CreateDirectory(dir);
        AtomicFile.Write(FileOf(dir), file.Write());
    }

    /// Every folder under root named as a profile's id, by that name, the default first: its file, or the reason it has none
    /// (a damaged profile is listed, not left out)
    public static IReadOnlyList<(string Id, ProfileFile? File, string? Problem)> All(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Select(d => (Id: Path.GetFileName(d), Read: Read(d)))
                        .Where(x => LauncherProfiles.IsId(x.Id)).Select(x => (x.Id, x.Read.File, x.Read.Problem))
                        .OrderBy(x => x.Id == LauncherProfiles.DefaultId ? 0 : 1).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
    }
}

/// A version of the builder's kind: &lt;launcher data&gt;/versions/&lt;v&gt;/, the folder Longshot's builder calls a version folder
/// (Longshot's docs/dev/reference/build.md, "Play: the version folder and the profile"):
///     versions/&lt;v&gt;/Longshot.exe | Longshot.x86_64   the builder, the release's longshot-builder (Install: from the launcher's tools)
///     versions/&lt;v&gt;/builds/default/                  the game folder it builds (the list's row is this folder)
/// Its cache is the launcher's (--cache, in the build files folder), never cache/ beside it. Play is the builder with --profile:
/// its fast check, an update when it is needed, then the game with LONGSHOT_PROFILE. A version of make_game.py's kind stays where
/// it is, played as before
public static class BuilderVersions
{
    public const string BuildsName = "builds";
    public const string DefaultBuild = "default";

    /// The builder's exit codes at Play (Longshot's build.md): no game copy in the profile (NotBuilt), a build, an update or a
    /// start that failed (Failed), the profile already playing or building in another copy (InUse); the game's own otherwise
    public const int NotBuilt = 120;
    public const int Failed = 121;
    public const int InUse = 123;

    public static string Root(string launcherData) => Path.Combine(launcherData, "versions");
    public static string FolderOf(string launcherData, string version) =>
        Versions.IsTagName(version) ? Path.GetFullPath(Path.Combine(Root(launcherData), version))
            : throw new ArgumentException("Not a version name: " + version, nameof(version));
    public static string ExeName(Platform platform) => platform == Platform.Windows ? "Longshot.exe" : "Longshot.x86_64";
    public static string ExeOf(string folder, Platform platform) => Path.Combine(folder, ExeName(platform));
    public static bool Is(string folder, Platform platform) => File.Exists(ExeOf(folder, platform));

    /// The game folder the builder builds in a version folder when no code mod names another: builds/default
    public static string GameFolderOf(string versionFolder) => Path.Combine(versionFolder, BuildsName, DefaultBuild);

    /// The version folder of a game folder in one (builds/&lt;name&gt;, the builder two folders up); null for any other game folder
    public static string? VersionOfGame(string gameFolder, Platform platform)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
            var builds = Path.GetDirectoryName(full);
            if (builds == null || Path.GetFileName(builds) != BuildsName || Path.GetDirectoryName(builds) is not { } version) return null;
            return Is(version, platform) ? version : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// Whether a game folder is where a version folder's builder builds (builds/&lt;name&gt; in &lt;launcher data&gt;/versions/&lt;v&gt;), with
    /// or without the builder there yet
    public static bool InVersions(string gameFolder, string launcherData)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder));
            var builds = Path.GetDirectoryName(full);
            return builds != null && Path.GetFileName(builds) == BuildsName && Path.GetDirectoryName(builds) is { } version
                   && Path.GetDirectoryName(version) is { } root && BuildRegistry.SameFolder(root, Root(launcherData));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// The release's longshot-builder (the launcher's tools: Toolchain.BuilderExe, checked when it was installed) put in a version
    /// folder as Longshot.exe (Longshot.x86_64): a hard link to it where the two are on one drive, else a copy; one there already
    /// of the same size kept. The version folder's own name is the builder's: it plays and builds the folder it is in
    public static string Install(string versionFolder, string builderExe, Platform platform)
    {
        var to = ExeOf(versionFolder, platform);
        Directory.CreateDirectory(versionFolder);
        var from = new FileInfo(builderExe);
        if (!from.Exists) throw new FileNotFoundException("The version's builder is not installed.", builderExe);
        var there = new FileInfo(to);
        if (there.Exists && (there.Length == from.Length || FileLinks.SameFile(to, builderExe))) return to;
        var part = to + ".part";
        if (File.Exists(part)) File.Delete(part);
        FileLinks.LinkOrCopy(builderExe, part);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(part, File.GetUnixFileMode(part) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        File.Move(part, to, overwrite: true);
        return to;
    }

    /// What in a version folder (or a game folder in it) is the player's own, never deleted with it: saves and settings
    /// (save/, save-work/, user/), a profile (the builder's default profile/ beside Longshot.exe, played without --profile),
    /// profiles, a profile.json or settings.ini. A version folder holding any is left as it is
    public static readonly IReadOnlyList<string> UserData = ["save", "save-work", "user", "saves", "profile", "profiles", ProfileStore.FileName, ProfileStore.SettingsName];

    /// Why the version folder at versionFolder may not be deleted (null: it may): only a folder of versions/ named as a version,
    /// not a link, holding no user data (UserData, at its top or in a game folder of builds/, nor a save a game folder keeps,
    /// Saves.SaveIn). Whether a game runs is the caller's to check
    public static string? WhyNotDelete(string versionFolder, string launcherData)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(versionFolder)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return e.Message; }
        if (!Directory.Exists(full)) return null;
        if (Path.GetDirectoryName(full) is not { } root || !BuildRegistry.SameFolder(root, Root(launcherData)) || !Versions.IsTagName(Path.GetFileName(full)))
            return "This folder is not one of the launcher's version folders, so it is not deleted: " + versionFolder;
        if (new DirectoryInfo(full).LinkTarget != null) return "This version folder is a link to another one, so it is not deleted: " + versionFolder;
        bool Has(string dir, string name) => Directory.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name));
        if (UserData.FirstOrDefault(n => Has(full, n)) is { } top)
            return $"This version folder holds {top}, which may be your saves or settings, so it is not deleted: {versionFolder}";
        var builds = Path.Combine(full, BuildsName);
        if (Directory.Exists(builds))
            foreach (var game in Directory.EnumerateDirectories(builds))
            {
                if (new DirectoryInfo(game).LinkTarget != null) return "A game folder in this version folder is a link, so it is not deleted: " + game;
                if (Has(game, "user") || Has(game, "save") || Saves.SaveIn(game) != null)
                    return $"{game} holds saves, so its version folder is not deleted: {versionFolder}";
            }
        return null;
    }

    /// The version folder deleted whole: its builder (Longshot.exe), its game folders and what else the builder left there, when
    /// WhyNotDelete allows it (IOException otherwise, nothing touched)
    public static void Delete(string versionFolder, string launcherData)
    {
        if (WhyNotDelete(versionFolder, launcherData) is { } why) throw new IOException(why);
        if (Directory.Exists(versionFolder)) Toolchains.DeleteTree(Path.GetFullPath(versionFolder));
        BuilderInfos.Forget(versionFolder);
    }

    /// The version folders an update left behind that nothing uses any more: not a version a row of the launcher's plays
    /// (inUse: the version folders' names, Registry's rows and every profile's profile.json "version"), not keep (the one the
    /// last update replaced, kept for going back to it), and one WhyNotDelete allows
    public static IReadOnlyList<string> Prunable(string launcherData, IEnumerable<string> inUse, string? keep)
    {
        var root = Root(launcherData);
        if (!Directory.Exists(root)) return [];
        var used = new HashSet<string>(inUse, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (keep != null) used.Add(keep);
        return Directory.EnumerateDirectories(root)
            .Where(d => !used.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).EndsWith(".part", StringComparison.Ordinal)
                        // (a game folder being copied in, VersionFolderMigration.Copy's .part: the move's, not to prune)
                        && !(Directory.Exists(Path.Combine(d, BuildsName)) && Directory.EnumerateDirectories(Path.Combine(d, BuildsName), "*.part").Any())
                        && WhyNotDelete(d, launcherData) == null)
            .OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    /// What the launcher names to the games it starts itself and never to the builder: the builder gives its game the profile
    /// (LONGSHOT_PROFILE), whose saves, settings and start slot are the game's to read
    public static readonly IReadOnlyList<string> OldSwitches =
        ["HOS_MODS_LIST", "HOS_OPTIONS_FILE", "HOS_SAVE_DIR", "HOS_START_SLOT", "HOS_PROFILE", "LONGSHOT_PROFILE", "LONGSHOT_STARTED_BY"];

    /// Play's arguments: --profile &lt;the profile's folder&gt; --output json (its events on stdout, for the launcher) --cache &lt;the
    /// launcher's builder cache&gt; --no-self-update (the launcher updates its versions itself: a version folder's builder never
    /// replaces itself with its channel's newest, Longshot's build.md "Self-update"), and --send-build-statistics when the player
    /// said yes to the statistics (Settings' Send performance statistics): the builder then sends every build's end it makes at
    /// play on its own, with no log (BuildReports)
    public static IReadOnlyList<string> PlayArguments(string profileDir, string cache, bool statistics = false) =>
        ["--profile", Path.GetFullPath(profileDir), "--output", "json", "--cache", cache, NoSelfUpdate, .. statistics ? new[] { SendStatistics } : []];

    /// Play's flag for the player's yes to the build statistics
    public const string SendStatistics = "--send-build-statistics";

    /// Play's flag that keeps its daily update check off
    public const string NoSelfUpdate = "--no-self-update";

    /// Play: versions/&lt;v&gt;/Longshot.exe PlayArguments, from the version's folder, with env (the launcher's environment, as a game
    /// it starts gets it: GamePlay.Environment) less OldSwitches. Its stdout is not taken: the builder and its game outlive the
    /// launcher (Play from the command line ends it at once), so the launcher only waits for its exit code (Ended)
    public static ProcessStartInfo StartInfo(string folder, Platform platform, string profileDir, string cache, IReadOnlyDictionary<string, string> env,
                                             bool statistics = false)
    {
        var si = new ProcessStartInfo(ExeOf(folder, platform)) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (var a in PlayArguments(profileDir, cache, statistics)) si.ArgumentList.Add(a);
        si.Environment.Clear();
        foreach (var (k, v) in env) si.Environment[k] = v;
        foreach (var k in OldSwitches) si.Environment.Remove(k);
        return si;
    }

    /// What a Play that ended with code says, from the last error event it wrote (its text) when it gave one: null for the game's
    /// own end (a crash is the crash check's)
    public static string? Ended(int code, string? errorText) => code switch
    {
        NotBuilt => Tr.T("Longshot isn't set up for this profile yet: pick your game file and build this version again."),
        Failed => errorText is { Length: > 0 } t ? Tr.F("Longshot couldn't start: {0}", t) : Tr.T("Longshot couldn't build or start this version."),
        InUse => Tr.T("This profile is already playing or building in another window. Close it first, or pick another profile."),
        _ => null,
    };
}

/// The move to profile folders, once (marked by profiles/.migrated-v1), keeping every original where it is: each profile a
/// launcher's settings listed (the default first, always) becomes &lt;launcher data&gt;/profiles/&lt;id&gt;/profile.json; its saves copied
/// into saves/ (the default's from the --user folder's save/ and save-work/, another's from the folder an older launcher kept its
/// saves in); its settings.ini a copy of the game's settings every version played with (the --user folder's longshot_options.ini);
/// its game copy and upscalers from the version it played last, else the default version (BuildEntry.Nup or its dump folder,
/// Dlss/Fsr/Xess), else the game file the launcher knows
public static class ProfileMigration
{
    public const string Marker = ".migrated-v1";

    public static bool Done(string profilesRoot) => File.Exists(Path.Combine(profilesRoot, Marker));

    public sealed record Result(IReadOnlyList<string> Profiles, IReadOnlyList<string> Notes);

    /// The parts of a save folder a profile's saves/ holds (Longshot's game: save/&lt;uid&gt;/slot_NN, and save-work/, a commit the
    /// game finishes at its next start)
    public static readonly IReadOnlyList<string> SaveParts = ["save", "save-work"];

    public static Result? Run(string profilesRoot, IReadOnlyList<LauncherProfile> profiles, SaveEnvironment env, IReadOnlyList<BuildEntry> builds,
                              string? knownGame = null, string? oldProfilesRoot = null)
    {
        if (Done(profilesRoot)) return null;
        Directory.CreateDirectory(profilesRoot);
        var made = new List<string>();
        var notes = new List<string>();
        var list = profiles.Any(p => p.Id == LauncherProfiles.DefaultId) ? profiles
            : new[] { new LauncherProfile { Id = LauncherProfiles.DefaultId, Name = LauncherProfiles.DefaultName } }.Concat(profiles).ToList();
        var settings = Path.Combine(env.LongshotUser, SettingsCarry.FileName);
        foreach (var p in list.Where(p => LauncherProfiles.IsId(p.Id)))
        {
            var dir = ProfileStore.Dir(profilesRoot, p.Id);
            if (File.Exists(ProfileStore.FileOf(dir))) continue;   // (made already, or damaged: a run cut short keeps what it did)
            var file = new ProfileFile
            {
                Name = p.Name.Length > 0 ? p.Name : LauncherProfiles.DefaultName,
                Color = p.Color,
                StartSlot = p.StartSlot,
                LastPlayed = p.LastPlayedAt,
                CopyOf = p.CopyOf,
            };
            // (the version it played last, else the default, else the first: its game copy and upscalers)
            var from = builds.FirstOrDefault(b => b.Id == p.LastPlayed) ?? builds.FirstOrDefault(b => b.Default) ?? builds.FirstOrDefault();
            file.Game = (from != null ? GameOf(from) : null) ?? knownGame;
            if (from != null)
            {
                file.Version = from.Id;
                file.Upscalers = new[] { (from.Dlss == true, "dlss"), (from.Fsr == true, "fsr4"), (from.Xess == true, "xess") }
                    .Where(x => x.Item1).Select(x => x.Item2).ToList();
            }
            // (the saves: copied, the originals left where they are, so an older launcher and older versions still find them)
            var saves = ProfileStore.SavesOf(dir);
            var source = p.Id == LauncherProfiles.DefaultId ? env.LongshotUser
                : oldProfilesRoot is { } old ? Path.Combine(old, p.Id) : null;
            if (source != null)
                foreach (var part in SaveParts)
                    if (Directory.Exists(Path.Combine(source, part)) && !SamePath(Path.Combine(source, part), Path.Combine(saves, part)))
                    {
                        try { CopyTree(Path.Combine(source, part), Path.Combine(saves, part)); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            notes.Add($"{p.Id}: {part} not wholly copied ({e.Message})");
                        }
                    }
            Directory.CreateDirectory(saves);
            // (the game's settings every version played with: each profile starts with them)
            if (File.Exists(settings) && !File.Exists(ProfileStore.SettingsOf(dir)))
            {
                Directory.CreateDirectory(dir);
                File.Copy(settings, ProfileStore.SettingsOf(dir));
            }
            ProfileStore.Save(dir, file);
            made.Add(p.Id);
        }
        File.WriteAllText(Path.Combine(profilesRoot, Marker), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return new Result(made, notes);
    }

    /// The settings.json's own profiles (LauncherSettings.LegacyProfiles) moved into the folders, for a launcher that kept them
    /// there: settings.json first copied to backup (kept as it was; a backup there already is left), the folders made (Run, once),
    /// every profile's data the file lacks put in its profile.json (what a profile.json holds already stays), then the list gone from
    /// settings.json. Null: settings.json held none
    public static Result? MoveFromSettings(LauncherSettings settings, string settingsPath, string backup, SaveEnvironment env,
                                           IReadOnlyList<BuildEntry> builds, string? knownGame = null, string? oldProfilesRoot = null)
    {
        if (env.ProfilesRoot is not { } root) return null;
        var legacy = settings.LegacyProfiles;
        if (legacy == null && Done(root)) return null;
        if (legacy != null && File.Exists(settingsPath) && !File.Exists(backup)) File.Copy(settingsPath, backup);
        var result = Run(root, legacy ?? [], env, builds, knownGame, oldProfilesRoot);
        if (legacy != null)
        {
            foreach (var p in legacy.Where(p => LauncherProfiles.IsId(p.Id)))
            {
                var dir = ProfileStore.Dir(root, p.Id);
                if (ProfileStore.Read(dir) is not ({ } file, null)) continue;
                var before = file.Write();
                if (file.Name.Length == 0 && p.Name.Length > 0) file.Name = p.Name;
                file.Color ??= p.Color;
                file.StartSlot ??= p.StartSlot;
                file.Version ??= p.LastPlayed;
                file.LastPlayed ??= p.LastPlayedAt;
                file.CopyOf ??= p.CopyOf;
                if (file.Write() != before) ProfileStore.Save(dir, file);
            }
            settings.LegacyProfiles = null;
            settings.Save(settingsPath);
        }
        return result;
    }

    /// A build's game copy as profile.json names it: its .nup, else the dump folder its romfs and exefs folders are in
    public static string? GameOf(BuildEntry b)
    {
        if (b.Nup is { Length: > 0 } nup) return nup;
        if (b.Romfs is not { Length: > 0 } romfs || b.Exefs is not { Length: > 0 } exefs) return null;
        var r = Path.TrimEndingDirectorySeparator(romfs);
        var e = Path.TrimEndingDirectorySeparator(exefs);
        return Path.GetFileName(r) == "romfs" && Path.GetFileName(e) == "exefs" && Path.GetDirectoryName(r) is { } parent
               && BuildRegistry.SameFolder(Path.GetDirectoryName(e) ?? "", parent) ? parent : null;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// from copied into to, a file there already kept, links not followed
    internal static void CopyTree(string from, string to, Func<string, bool>? skip = null)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
            if (new FileInfo(f).LinkTarget == null && !File.Exists(Path.Combine(to, Path.GetFileName(f)))) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
            if (new DirectoryInfo(d).LinkTarget == null && skip?.Invoke(d) != true) CopyTree(d, Path.Combine(to, Path.GetFileName(d)), skip);
    }
}

/// The move of the versions longshot-builder built in a folder of their own (builds/&lt;track&gt;/&lt;version&gt;, before version folders)
/// into &lt;launcher data&gt;/versions/&lt;v&gt;/builds/default, once each, the original kept where it is: its game folder copied whole but
/// for its user/ (its saves are the profiles' now), into a folder of its own first (builds/default.part) and renamed once complete,
/// so a copy cut short is never taken for the game. The list's row then names the copy (same id: its work folder, and so its next
/// update's lift and objects, stay its own). A version of make_game.py's kind is not moved: it is played as before
public static class VersionFolderMigration
{
    /// One version to move: the row, its folder now, and the game folder it gets
    public sealed record Move(BuildEntry Build, string From, string To);

    /// The rows whose game longshot-builder built (LongshotBuilder.CurrentRecord) with a version, not in a version folder yet, and
    /// whose version has no game folder there already
    public static IReadOnlyList<Move> Plan(IReadOnlyList<BuildEntry> builds, string launcherData, Func<string, bool>? builtByBuilder = null)
    {
        builtByBuilder ??= folder => LongshotBuilder.CurrentRecord(folder, builds.FirstOrDefault(b => BuildRegistry.SameFolder(b.Folder, folder))) != null;
        var moves = new List<Move>();
        foreach (var b in builds)
        {
            if (b.Version is not { } v || !Versions.IsTagName(v) || BuilderVersions.InVersions(b.Folder, launcherData)) continue;
            if (!Directory.Exists(b.Folder) || !builtByBuilder(b.Folder)) continue;
            var to = BuilderVersions.GameFolderOf(BuilderVersions.FolderOf(launcherData, v));
            if (Directory.Exists(to) || moves.Any(m => BuildRegistry.SameFolder(m.To, to))) continue;
            moves.Add(new Move(b, b.Folder, to));
        }
        return moves;
    }

    /// The row's work folder (LongshotBuilder.WorkFolder: launcher-&lt;id&gt;, where the launcher's builds of the old folder kept their
    /// lift and objects) moved to the work folder the builder takes for the new game folder by itself
    /// (LongshotBuilder.DefaultWorkFolder), the one Play's update and the launcher's later builds of a version folder use: so the
    /// first Play after the move updates from that work and the shared stores of the same --cache, never from cold. Its note
    /// (game_folder.txt) names the new folder. Nothing done when there is none, or a work folder is there already; false when it
    /// could not be moved (a build holds it)
    public static bool CarryWork(Move move, string work, Platform platform)
    {
        var from = LongshotBuilder.WorkFolder(work, move.Build.Id);
        var to = LongshotBuilder.DefaultWorkFolder(Path.Combine(work, Disk.LongshotBuildCache), move.To, platform);
        if (!Directory.Exists(from) || Directory.Exists(to)) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            File.WriteAllText(Path.Combine(to, LongshotBuilder.WorkGameNote), Path.GetFullPath(move.To));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// A move's copy: the game folder, but for its user/ and the build's lock, into To.part, then renamed To
    public static void Copy(Move move, CancellationToken cancel = default)
    {
        var part = move.To + ".part";
        if (Directory.Exists(part)) Directory.Delete(part, true);
        Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);
        var user = Path.Combine(Path.GetFullPath(move.From), "user");
        ProfileMigration.CopyTree(move.From, part, d =>
        {
            cancel.ThrowIfCancellationRequested();
            return ProfileMigration.SamePath(d, user);
        });
        var lockFile = Path.Combine(part, LongshotBuilder.GameLockFile);
        if (File.Exists(lockFile)) File.Delete(lockFile);
        Directory.Move(part, move.To);
    }
}
