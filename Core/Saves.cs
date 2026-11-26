using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// A program that runs the game and keeps its saves (scripts/longshot_saves.py Program): where it keeps its data on each platform,
/// in the order tried ({data}, {home}, {appdata}, {localappdata}), its process names (lower case, no .exe) and other names
public sealed record SaveProgram(string Name, string Display, IReadOnlyDictionary<Platform, string[]> Folders, string[] Processes, string[] Aliases);

/// One profile's save folder (account.sav, option.sav, slot_NN/): its id (the folder above it that names the profile)
public sealed record SaveProfile(string Id, string Path);

/// A slot and when its progress.sav was last written (null: no progress.sav in it)
public sealed record SaveSlot(string Name, DateTime? Saved);

/// Where one program's (or one build's) saves were found: the data folder searched and its profiles; Problem: why none
public sealed record SaveSet(string Owner, string Display, string? Folder, IReadOnlyList<SaveProfile> Profiles, string? Problem)
{
    public bool Found => Profiles.Count > 0;
}

/// What a copy did (or, dry, would do): Backup is the copy of the target taken first (null: the target had nothing yet)
public sealed record SaveCopy(string Source, string Target, string? Backup, IReadOnlyList<string> Slots, IReadOnlyList<string> Files, bool KeptSettings, bool Dry);

/// What Saves.Gather did: the save played last, whose files other than slots every build uses now (From: its user folder;
/// WasShared: it was the shared one already), the old user folders whose save was moved aside, the backup of every one of them,
/// the shared one's too, and every slot of the save they make together (Gather merges them slot by slot)
public sealed record SaveGathered(string From, bool WasShared, IReadOnlyList<string> Moved, string Backup, IReadOnlyList<GatheredSlot> Slots);

/// One slot Saves.Gather took into account: its profile folder (its path below save\), its folder's name in the user folder it
/// comes from (From; Shared: the shared one), and what became of it. The kept save's own slots stay (the shared save's, or with
/// none yet the one played last). Where it has no such slot, the newest copy comes in. Any other copy that differs from every
/// slot kept goes to the first free slot (MovedTo, slot_NN), never over one; with no slot free it stays in the backup only
/// (Left). A copy older than the kept save's own different one stays in the backup only too (Left, Older)
public sealed record GatheredSlot(string Profile, string Slot, string From, bool Shared, string? MovedTo = null, bool Left = false,
                                  bool Older = false)
{
    /// The slot it is in now in the shared save (null: Left)
    public string? Now => Left ? null : MovedTo ?? Slot;
}

/// A copy refused, and why (longshot_saves.Fail)
public sealed class SaveFailure(string message, SaveRefusal reason = SaveRefusal.Other, IReadOnlyList<string>? who = null, string? backup = null,
                                string? code = null)
    : Exception(message)
{
    /// The save editor's reason code for a refusal (`running`, `needs_bag`, `other_version`... : docs/save-editor.md in Longshot),
    /// for a program to switch on; null for any other failure
    public string? Code { get; } = code;

    /// The backup a failed copy made before it stopped (the target is as it was; this is a second copy of it), for the window to
    /// show; null when there was none
    public string? Backup { get; } = backup;

    /// Why the copy was refused, for the window to say it its own way (the message says it the script's way)
    public SaveRefusal Reason { get; } = reason;
    /// Running: what runs; Waiting: the program whose save waits
    public IReadOnlyList<string> Who { get; } = who ?? Array.Empty<string>();
}

/// Running: a game or an emulator runs; Waiting: NX2navi has a commit or menu import waiting; Exists: the copy would replace a
/// save the player has not agreed to replace (HookshotSaves.Import); Other: anything else
public enum SaveRefusal { Other, Running, Waiting, Exists }

/// The environment the save folders are worked out from: the platform and its variables (HOME, XDG_DATA_HOME, APPDATA,
/// LOCALAPPDATA), so the tests point them all into one temporary folder as tests/test_longshot_saves.py does
public sealed record SaveEnvironment(Platform Platform, Func<string, string?> Getenv)
{
    public static SaveEnvironment Current() => new(Platforms.Current, Environment.GetEnvironmentVariable);

    string Home => Getenv("HOME") is { Length: > 0 } h ? h : Getenv("USERPROFILE") is { Length: > 0 } u ? u
                   : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// longshot_saves.data_home
    public string Data => Getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".local", "share");

    string AppData => Getenv("APPDATA") is { Length: > 0 } a ? a : Path.Combine(Home, "AppData", "Roaming");
    string LocalAppData => Getenv("LOCALAPPDATA") is { Length: > 0 } a ? a : Path.Combine(Home, "AppData", "Local");

    /// Where the backups go instead of BackupRoot's default (a portable launcher's own folder); null: the default
    public string? BackupFolder { get; init; }

    /// (Windows) Longshot's user folder instead of LongshotUser's default (a portable launcher's own folder); null: the default
    public string? UserFolder { get; init; }

    /// The folder every build is started with as --user (GamePlay.UserFolder), the same for every build and every version of the
    /// game: %APPDATA%\Longshot on Windows (or a portable launcher's own folder), $XDG_DATA_HOME/longshot on Linux
    public string LongshotRoot => Path.GetFullPath(Platform == Platform.Windows
        ? UserFolder ?? Path.Combine(AppData, "Longshot")
        : Path.Combine(Data, "longshot"));

    /// (Linux) Where a Longshot that keeps its user files apart from the rest of LongshotRoot keeps them: LongshotRoot/user. Such a
    /// game, given --user LongshotRoot, moves them there itself at its first start (save\, temp\, cache\, the settings, mods.json);
    /// one from before keeps them in LongshotRoot itself. The launcher never makes this folder: the game does
    public string LinuxUserFiles => Path.Combine(LongshotRoot, "user");

    /// Longshot's user folder, the same for every build (save\&lt;user id&gt;\, the settings, its cache and its temp), where the
    /// game keeps them now: LongshotRoot on Windows; on Linux LongshotRoot/user once a game has made it (LinuxUserFiles), else
    /// LongshotRoot (no Longshot that keeps them apart has started yet). Worked out again at each use: the game moves them while
    /// the launcher runs
    public string LongshotUser => Platform != Platform.Windows && Directory.Exists(LinuxUserFiles) ? LinuxUserFiles : LongshotRoot;

    /// The shader store every build shares (Longshot's SPIR-V store, ShaderCaches.StoreNames): LONGSHOT_SHADER_STORE when set,
    /// else %LOCALAPPDATA%\Longshot\shaders on Windows and ${XDG_CACHE_HOME:-~/.cache}/longshot/shaders on Linux, where the game
    /// keeps it (gpu/vk/spirv_pack.h's default_dir, read the same way: an XDG_CACHE_HOME that is not a full path is not used).
    /// Null for none: LONGSHOT_SHADER_STORE=0, or no LOCALAPPDATA on Windows. Not in LongshotUser: a portable launcher's builds
    /// share the PC's
    public string? ShaderStore => Getenv("LONGSHOT_SHADER_STORE") is { Length: > 0 } s ? (s == "0" ? null : Path.GetFullPath(s))
        : Platform == Platform.Windows ? (Getenv("LOCALAPPDATA") is { Length: > 0 } l ? Path.GetFullPath(Path.Combine(l, "Longshot", "shaders")) : null)
        : Path.GetFullPath(Path.Combine(Getenv("XDG_CACHE_HOME") is { Length: > 0 } c && Path.IsPathFullyQualified(c) ? c : Path.Combine(Home, ".cache"), "longshot", "shaders"));

    /// Longshot's save folder, the current profile's: save\<user id>\ in SavesUser (Open saves folder, the .zip's top folder,
    /// ImportTarget's parent)
    public string SaveFolder => Path.Combine(SavesUser, "save");

    /// The current profile's folder of saves in place of LongshotUser (LauncherProfiles.SavesHome: its folder's saves/); null:
    /// LongshotUser itself (no profiles' folders)
    public string? SavesHome { get; init; }

    /// The profiles' folders (&lt;launcher data&gt;/profiles: ProfileStore): every profile's saves are its folder's saves/, the
    /// default's too; null: none (the saves are the user folder's)
    public string? ProfilesRoot { get; init; }

    /// The folder that holds save\ for the current profile: SavesHome, else LongshotUser
    public string SavesUser => SavesHome ?? LongshotUser;

    /// Where the launcher's backups go: one folder, save-backups, in %LOCALAPPDATA%\Longshot on Windows and in
    /// $XDG_DATA_HOME/longshot on Linux (beside the user folder, never in it), or a portable launcher's own folder (its
    /// save-backups too). The folders backups went to before are still read (OldBackupRoots), never written, moved or emptied
    public string BackupRoot => BackupFolder ?? (Platform == Platform.Windows
        ? Path.Combine(LocalAppData, "Longshot", "save-backups")
        : Path.Combine(Data, "longshot", "save-backups"));

    /// The backups folders of before, read only: on Windows %LOCALAPPDATA%\Longshot\oot-save-backups (the launcher's and Longshot's
    /// scripts/longshot_saves.py's); on Linux $XDG_DATA_HOME/longshot/save-backup (the launcher's from #80) and
    /// $XDG_DATA_HOME/oot-save-backups (the launcher's before #80, the script's and the game's own, Longshot#1428). None for a
    /// portable launcher, which only ever wrote its own folder
    public IReadOnlyList<string> OldBackupRoots => BackupFolder != null ? Array.Empty<string>()
        : Platform == Platform.Windows ? new[] { Path.Combine(LocalAppData, "Longshot", "oot-save-backups") }
        : new[] { Path.Combine(Data, "longshot", "save-backup"), OldLinuxBackups };

    /// (Linux) $XDG_DATA_HOME/oot-save-backups: the launcher's backups before #80, Longshot's scripts/longshot_saves.py's, and the
    /// game's own (Longshot#1428). Read, never written by the launcher
    public string OldLinuxBackups => Path.Combine(Data, "oot-save-backups");

    /// Every folder backups are read from, BackupRoot first, then OldBackupRoots, so Restore from a backup finds them all (issue
    /// #188). Nothing is moved between them
    public IReadOnlyList<string> BackupRoots => new[] { BackupRoot }.Concat(OldBackupRoots).ToList();

    /// The backups folder to show and open: BackupRoot once it is there, else the first older one that is (a player with no backup
    /// made since the folder's name changed), else BackupRoot
    public string ShownBackupRoot => BackupRoots.FirstOrDefault(Directory.Exists) ?? BackupRoot;

    /// longshot_saves.folders: a program's data folders on this platform, in the order they are tried
    public IReadOnlyList<string> FoldersOf(SaveProgram program) =>
        program.Folders.TryGetValue(Platform, out var list)
            ? list.Select(t => Path.GetFullPath(t.Replace("{data}", Data).Replace("{home}", Home).Replace("{appdata}", AppData)
                                                 .Replace("{localappdata}", LocalAppData).Replace('/', Path.DirectorySeparatorChar))).ToList()
            : Array.Empty<string>();
}

/// The game's saves, the same files in Longshot and in the emulators and ports that run it (Longshot's docs/saves.md): found, summed up and
/// copied one way after a backup. A port of scripts/longshot_saves.py, rule for rule (each piece names the function it mirrors), so
/// that the launcher needs no Python and no installed tools for it. Every build shares one save folder
/// (SaveEnvironment.LongshotUser: save\&lt;user id&gt;\ in %APPDATA%\Longshot on Windows, $XDG_DATA_HOME/longshot/user on Linux, or
/// $XDG_DATA_HOME/longshot itself until a Longshot that keeps its user files apart has started). Windows
/// builds kept their own in their game folder (&lt;folder&gt;\user) before: Gather brings those into the shared one.
public static class Saves
{
    public const string Title = "0400486024DEC000";
    public const string NoAccount = "0000000000000000";
    // Longshot's host/nn_applet.c returns this one user; an emulator's profile id is not read by the game.
    public const string UserId = "5a3c9e1f0b7d24680000000000000001";

    static Dictionary<Platform, string[]> On(string[] linux, string[] windows) => new() { [Platform.Linux] = linux, [Platform.Windows] = windows };

    // (longshot_saves.EDEN, LICHII, EMULATORS, LONGSHOT, FOLDER; their sources are in Longshot's docs/saves.md)
    static readonly string[] EdenLinux = { "{data}/eden" }, EdenWindows = { "{appdata}/eden" };
    public static readonly IReadOnlyList<SaveProgram> Emulators = new[]
    {
        // (Lichii has a data folder of its own since its build 150, 2026-11-10; a build from before keeps its saves in Eden's)
        new SaveProgram("lichii", "Lichii", On(new[] { "{data}/lichii" }.Concat(EdenLinux).ToArray(), new[] { "{appdata}/lichii" }.Concat(EdenWindows).ToArray()),
                        new[] { "lichii" }, Array.Empty<string>()),
        new SaveProgram("frizti", "Frizti (Fritchii)", On(EdenLinux, EdenWindows), new[] { "lichii", "fritchii" }, new[] { "fritchii" }),
        new SaveProgram("orchid", "Orchid", On(EdenLinux, EdenWindows), new[] { "orchid" }, Array.Empty<string>()),
        new SaveProgram("pokedinner", "PokeDinner", On(EdenLinux, EdenWindows), new[] { "eglantine" }, Array.Empty<string>()),
        new SaveProgram("eden", "Eden", On(EdenLinux, EdenWindows), new[] { "eden" }, Array.Empty<string>()),
        new SaveProgram("eglantine", "Eglantine", On(new[] { "{data}/eglantine" }, new[] { "{appdata}/eglantine" }), new[] { "eglantine" }, Array.Empty<string>()),
        new SaveProgram("nx2navi", "NX2navi", On(new[] { "{home}/Games/Ocarina" }, new[] { "{localappdata}/Programs/NX2navi/game", "C:/Games/Ocarina" }),
                        new[] { "nx2navi", "ocarina" }, Array.Empty<string>()),
    };
    // (Linux: the user folder a Longshot that keeps its user files apart moved them to, SaveEnvironment.LinuxUserFiles, first;
    // then the folder an older one keeps them in itself)
    public static readonly SaveProgram Longshot = new("longshot", "Longshot", On(new[] { "{data}/longshot/user", "{data}/longshot" }, new[] { "{appdata}/Longshot" }),
                                                      new[] { "longshot" }, Array.Empty<string>());
    public static readonly SaveProgram Folder = new("folder", "the folder", On(Array.Empty<string>(), Array.Empty<string>()), Array.Empty<string>(), Array.Empty<string>());
    /// A .zip of saves (SaveArchive), unpacked into a folder of its own first: its import's backup is named import-zip-&lt;stamp&gt;
    public static readonly SaveProgram Zip = new("zip", "the .zip file", On(Array.Empty<string>(), Array.Empty<string>()), Array.Empty<string>(), Array.Empty<string>());

    // (longshot_saves.LAYOUTS: where the save folders sit under a data folder; "<id>" is one folder per profile. A folder deeper down
    // the same path is accepted too; the first layout that finds a profile wins)
    static readonly string[][] Layouts =
    {
        new[] { "nand", "user", "save", "account", "<id>", Title, "0" },   // (Eden's newer layout: used first when it exists)
        new[] { "nand", "user", "save", NoAccount, "<id>", Title },        // (Eden and its forks: Lichii, Eglantine, ...)
        new[] { "profile", "save", "<id>" },                                // (NX2navi's game folder)
        new[] { "user", "save", "<id>" },                                   // (a Windows Longshot game folder of before, Hookshot's)
        new[] { "save", "<id>" },                                           // (Longshot's data folder)
    };

    // ---- finding them ----

    /// longshot_saves.subdirs: the folders in path, sorted; none when it cannot be read
    static List<string> Subdirs(string path)
    {
        try { return Directory.EnumerateDirectories(path).Select(System.IO.Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new List<string>(); }
    }

    /// longshot_saves.slots
    public static IReadOnlyList<string> SlotNames(string path) => Subdirs(path).Where(n => n.StartsWith("slot_", StringComparison.Ordinal)).ToList();

    /// longshot_saves.is_save_folder
    public static bool IsSaveFolder(string path) => File.Exists(System.IO.Path.Combine(path, "account.sav")) || SlotNames(path).Count > 0;

    static string Base(string path) => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));

    static string Up(string path) => System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(path)) ?? path;

    /// longshot_saves.walk
    static IEnumerable<(string? Key, string Path)> Walk(string root, ArraySegment<string> parts, string? key)
    {
        if (parts.Count == 0)
        {
            yield return (key, root);
            yield break;
        }
        foreach (var n in Subdirs(root))
        {
            if (parts[0] == "<id>")
                foreach (var x in Walk(System.IO.Path.Combine(root, n), parts[1..], n)) yield return x;
            else if (string.Equals(n, parts[0], StringComparison.OrdinalIgnoreCase))
                foreach (var x in Walk(System.IO.Path.Combine(root, n), parts[1..], key)) yield return x;
        }
    }

    /// longshot_saves.find_saves: the game's save folders under root, one per profile id, in the order found; root may be the save
    /// folder itself
    public static IReadOnlyList<SaveProfile> Find(string root)
    {
        var norm = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        var titleFolder = Base(norm).ToUpperInvariant() == Title;
        if (IsSaveFolder(norm) || (titleFolder && !Subdirs(norm).Contains("0")))
        {
            var key = norm;
            while (Base(key).ToUpperInvariant() is Title or "0" && Up(key) != key) key = Up(key);
            return new[] { new SaveProfile(Base(key) is { Length: > 0 } b ? b : norm, norm) };
        }
        var found = new List<SaveProfile>();
        foreach (var layout in Layouts)
        {
            var idpos = Array.IndexOf(layout, "<id>");
            for (var start = 0; start < layout.Length; start++)
            {
                // (below the title id folder, only the title id folder itself is a place to start)
                if (layout.Contains(Title) && !layout[start..].Contains(Title) && !titleFolder) continue;
                string? key = null;
                if (start > idpos)   // (root is at or below the <id> folder: the id is a folder above it)
                {
                    var up = norm;
                    for (var i = 0; i < start - idpos - 1; i++) up = Up(up);
                    key = Base(up);
                }
                foreach (var (k, path) in Walk(norm, new ArraySegment<string>(layout, start, layout.Length - start), key))
                {
                    // (a folder the layout names, the title id folder, counts as soon as it is there: the game may not have saved yet.
                    // A profile folder counts only when it holds a save: Eden's profile folders do not)
                    if ((layout[^1] != "<id>" || IsSaveFolder(path)) && k != null && found.All(f => f.Id != k && f.Path != path))
                        found.Add(new SaveProfile(k, path));
                }
            }
        }
        return found;
    }

    /// longshot_saves.locate: (folder, profiles) for program: the override when given, else the first of its folders with saves
    public static (string Folder, IReadOnlyList<SaveProfile> Profiles) Locate(SaveProgram program, string? folder, SaveEnvironment env)
    {
        if (folder != null)
        {
            if (!Directory.Exists(folder)) throw new SaveFailure($"No folder at {folder}.");
            var found = Find(folder);
            if (found.Count == 0)
                throw new SaveFailure($"No save of the game under {folder} (a folder with account.sav and slot_*, or {Title} under an " +
                                      "emulator's nand/user/save): start the game there once and save.");
            return (folder, found);
        }
        var tried = env.FoldersOf(program);
        foreach (var d in tried)
            if (Directory.Exists(d) && Find(d) is { Count: > 0 } found) return (d, found);
        throw new SaveFailure($"No {program.Display} save found (looked in {(tried.Count > 0 ? string.Join(", ", tried) : "no default folder")}): " +
                              $"start the game in {program.Display} once and save, or choose its folder.");
    }

    /// The folder an export writes to: the folder the player chose needs no save yet (the export makes it the save folder:
    /// account.sav, option.sav and slot_* in it), where an emulator's folder is found by its layout (Locate)
    public static (string Folder, IReadOnlyList<SaveProfile> Profiles) ExportTarget(SaveProgram program, string? folder, SaveEnvironment env)
    {
        if (folder == null || program.Name != Folder.Name) return Locate(program, folder, env);
        if (!Directory.Exists(folder)) throw new SaveFailure($"No folder at {folder}.");
        // (a copy puts the new save in place by moving a folder in beside it: the top of a drive has no place beside it)
        if (Base(folder).Length == 0) throw new SaveFailure($"{folder} is the top of a drive: make a folder in it and export to that.");
        var found = Find(folder);
        if (found.Count > 0) return (folder, found);
        return (folder, new[] { new SaveProfile(Base(folder), folder) });
    }

    /// longshot_saves.pick: the one profile, or the one asked for; several and none asked for is an error that lists them
    public static SaveProfile Pick(IReadOnlyList<SaveProfile> found, string name, string? wanted = null)
    {
        if (wanted != null)
            return found.FirstOrDefault(p => string.Equals(p.Id, wanted, StringComparison.OrdinalIgnoreCase))
                   ?? throw new SaveFailure($"{name} has no profile {wanted}; it has: {string.Join(", ", found.Select(p => p.Id).Order(StringComparer.Ordinal))}");
        if (found.Count > 1)
            throw new SaveFailure($"{name} has saves of the game in {found.Count} profiles, so pick one:" +
                                  string.Concat(found.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => $"\n  {p.Id}  {p.Path}")));
        return found.Count == 1 ? found[0] : throw new SaveFailure($"{name} has no save.");
    }

    /// longshot_saves.program_named: an emulator by its name or another name
    public static SaveProgram Named(string name) =>
        Emulators.FirstOrDefault(p => p.Name == name.ToLowerInvariant() || p.Aliases.Contains(name.ToLowerInvariant()))
        ?? throw new SaveFailure($"Unknown emulator {name}: use one of {string.Join(", ", Emulators.Select(p => p.Name))}, or choose a folder.");

    /// longshot_saves.pending_commit: NX2navi writes a save to profile/save-tx first and moves it into profile/save/&lt;account&gt; after;
    /// while a finished commit waits there (save-tx/ready-&lt;n&gt;, n from 1), that is the save the game loads next. The rest of
    /// save-tx is not a save in waiting (NX2navi's runtime/fs_host.c, "save transactions"): work/ is the working copy, which a
    /// quit leaves behind (the exit waits for a commit but does not unmount the save) and the next start deletes; new/ is an unfinished
    /// commit, rolled back; old/ is the save a commit replaced, on its way to save-backup. The menu's completed import in
    /// profile/save-import also replaces the current save at the next start (fs_host.c import_pending); save-import.part does not
    public static string? PendingCommit(string save)
    {
        var parent = Up(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(save)));
        if (Base(parent).ToLowerInvariant() != "save") return null;
        var import = System.IO.Path.Combine(Up(parent), "save-import");
        if (Directory.Exists(import)) return import;
        var tx = System.IO.Path.Combine(Up(parent), "save-tx");
        return Subdirs(tx).Any(IsFinishedCommit) ? tx : null;
    }

    /// fs_host.c ready_number: "ready-" and a number above 0, nothing else
    static bool IsFinishedCommit(string name) =>
        name.StartsWith("ready-", StringComparison.Ordinal) && name.Length > 6 && name[6..].All(char.IsAsciiDigit)
        && ulong.TryParse(name[6..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
        && n is > 0 and <= uint.MaxValue;

    /// longshot_saves.describe: each slot and when its progress.sav was written (local time)
    public static IReadOnlyList<SaveSlot> Describe(string path) =>
        SlotNames(path).Select(s =>
        {
            var p = System.IO.Path.Combine(path, s, "progress.sav");
            return new SaveSlot(s, File.Exists(p) ? File.GetLastWriteTime(p) : null);
        }).ToList();

    // ---- by build, and everything at once (longshot_saves.list_all) ----

    /// Longshot's saves, the same for every build: in SaveEnvironment.LongshotUser (save\&lt;user id&gt;)
    public static SaveSet Shared(SaveEnvironment env)
    {
        var user = env.SavesUser;
        var found = Directory.Exists(user) ? Find(user) : Array.Empty<SaveProfile>();
        return new SaveSet("longshot", "Longshot", user, found, found.Count == 0 ? $"No Longshot save in {user} yet." : null);
    }

    /// Where Longshot's save of a profile goes when there is none yet: &lt;LongshotUser&gt;\save\&lt;id&gt;
    public static string SharedSavePath(SaveEnvironment env, string profile) => System.IO.Path.Combine(env.SaveFolder, profile);

    /// Where an import goes: the save of the one user the game opens (UserId: host/nn_applet.c's user_uid, which host/nn_fs.c
    /// names save\&lt;high word&gt;&lt;low word&gt;), whether it is there yet or not. Another profile folder in save\ (a stray one, a copy
    /// made by hand) is never read by the game, so an import never goes there and never has to choose between them
    public static string ImportTarget(SaveEnvironment env) => SharedSavePath(env, UserId);

    /// The profile of Longshot's save the Saves page lists and the save editor edits: the game's (UserId) when it is there, else the
    /// first one found (Shared); null when there is none. A stray profile beside the game's sorts first and is never read by the game
    public static string? ShownProfile(SaveEnvironment env) =>
        Shared(env) is { Found: true } set
            ? (set.Profiles.FirstOrDefault(p => string.Equals(p.Id, UserId, StringComparison.OrdinalIgnoreCase)) ?? set.Profiles[0]).Path
            : null;

    /// Every emulator's saves, grouped by the folder they come from (the emulators that share Eden's folder in one group), as
    /// list_all shows them: a group with nothing names the folders it looked in
    public static IReadOnlyList<SaveSet> Emulator(SaveEnvironment env)
    {
        var groups = new List<(string Key, string? Folder, List<SaveProgram> Programs, IReadOnlyList<string> Tried)>();
        foreach (var program in Emulators)
        {
            var tried = env.FoldersOf(program);
            var used = tried.FirstOrDefault(d => Directory.Exists(d) && Find(d).Count > 0);
            var key = used ?? "\0" + string.Join("\0", tried);
            var at = groups.FindIndex(g => g.Key == key);
            if (at < 0) groups.Add((key, used, new List<SaveProgram> { program }, tried));
            else groups[at].Programs.Add(program);
        }
        return groups.Select(g =>
        {
            var display = string.Join(" / ", g.Programs.Select(p => p.Display));
            return g.Folder != null
                ? new SaveSet(g.Programs[0].Name, display, g.Folder, Find(g.Folder), null)
                : new SaveSet(g.Programs[0].Name, display, null, Array.Empty<SaveProfile>(),
                              "Nothing in " + (g.Tried.Count > 0 ? string.Join(", ", g.Tried) : "(no default folder)"));
        }).ToList();
    }

    // ---- is a game running (longshot_saves.process_names, running_games) ----

    /// {process name: what it belongs to}
    public static IReadOnlyDictionary<string, string> ProcessNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var program in Emulators.Prepend(Longshot).Append(HookshotSaves.Program))
            foreach (var p in program.Processes)
                if (names.TryGetValue(p, out var was)) names[p] = was + " or " + program.Display;
                else
                {
                    names[p] = program.Display;
                    order.Add(p);
                }
        return names;
    }

    /// Whether a process is the launcher's own AppImage runtime: the launcher runs unpacked from a FUSE mount, and a
    /// separate runtime process (comm like the file's, argv[0] the AppImage's path) stays alive beside it. Gear Lever
    /// (#428) renames the AppImage after its .desktop name, and a deduped rename (longshot-2.appimage) walks into the
    /// game-AppImage regex below: the launcher read its own runtime as a running game and refused its own update (#118).
    /// A file name can never settle this — a renamed launcher and a game's longshot-2.appimage share names — but the
    /// paths never collide: the runtime's argv[0] is the exact file the AppImage runtime runs, and the same runtime
    /// exports it as $APPIMAGE for the launcher process to answer with.
    public static bool IsOwnRuntime(string? argv0Path, string? ownAppImage)
    {
        if (string.IsNullOrEmpty(argv0Path) || string.IsNullOrEmpty(ownAppImage)) return false;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(argv0Path!), System.IO.Path.GetFullPath(ownAppImage!),
                                 StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or System.Security.SecurityException) { return false; }
    }

    /// The launcher's own AppImage path for the running check: $APPIMAGE the AppImage runtime exports to the launcher
    /// process it started; null when the launcher runs unpacked (a plain folder, a dev run: no runtime process exists)
    public static string? OwnAppImage(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var path = getenv("APPIMAGE");
        return string.IsNullOrEmpty(path) ? null : path;
    }

    /// What is running, from the processes' names: on Windows the image names (with or without .exe), on Linux each process's
    /// comm and the file name of its argv[0] (an AppImage named after the program counts)
    public static IReadOnlyList<string> RunningFrom(Platform platform, IEnumerable<string> candidates)
    {
        var known = ProcessNames();
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var appImage = new Regex("^(" + string.Join("|", known.Keys.Select(Regex.Escape)) + @")[-_.].*\.appimage$", RegexOptions.CultureInvariant);
        foreach (var candidate in candidates)
        {
            var low = candidate.ToLowerInvariant();
            if (platform == Platform.Windows)
            {
                var image = low.EndsWith(".exe", StringComparison.Ordinal) ? low[..^4] : low;
                if (known.TryGetValue(image, out var w)) names.Add(w);
                continue;
            }
            if (known.TryGetValue(low, out var name)) names.Add(name);
            else if (appImage.Match(low) is { Success: true } m) names.Add(known[m.Groups[1].Value]);
        }
        return names.ToList();
    }

    /// The games running on this PC now, this launcher left out (its AppImage may be named Longshot-...AppImage)
    public static IReadOnlyList<string> Running(Platform platform, Func<string, string?>? getenv = null) =>
        RunningFrom(platform, Candidates(platform, getenv));

    /// Whether a longshot-builder program runs, from the processes' names: longshot-builder, longshot-builder.exe or a release's
    /// file name, longshot-builder-&lt;version&gt;-&lt;system&gt;, in any case. Any of its commands: a build, or a check or perf-report,
    /// which write nothing of the game's (BuilderRunning tells them apart). Not a game: Running leaves it out, so the saves never
    /// wait for a build
    public static bool BuilderAmong(IEnumerable<string> candidates) => candidates.Any(c =>
    {
        var low = c.ToLowerInvariant();
        if (low.EndsWith(".exe", StringComparison.Ordinal)) low = low[..^4];
        return low == LongshotBuilder.Name || low.StartsWith(LongshotBuilder.Name + "-", StringComparison.Ordinal);
    });

    /// Whether longshot-builder is building one of these game folders now (build, update or prepare, which write the folder, its
    /// shader caches and the shader store), not merely running (check, perf-report): it holds the folder's lock
    /// (LongshotBuilder.Building), looked at only while a longshot-builder program runs (BuilderAmong). candidates, building: the
    /// tests' stand-ins
    public static bool BuilderRunning(Platform platform, IEnumerable<string> folders, Func<string, string?>? getenv = null,
                                      IReadOnlyList<string>? candidates = null, Func<string, bool>? building = null) =>
        BuilderAmong(candidates ?? Candidates(platform, getenv)) && folders.Any(building ?? LongshotBuilder.Building);

    /// The names of the processes running on this PC now, this launcher and its own AppImage runtime left out: Windows' image
    /// names; Linux's comm and argv[0]'s file name of each
    public static IReadOnlyList<string> Candidates(Platform platform, Func<string, string?>? getenv = null)
    {
        var own = Environment.ProcessId;
        // (#118: the launcher's pid is never a game, but its AppImage runtime is a DIFFERENT process, and Gear Lever's
        //  rename made its file name read as one. The runtime's argv[0] is the AppImage file; $APPIMAGE says which file
        //  is the launcher's own. Exclude the runtime whose argv[0] is that file — and the launcher's own .desktop name,
        //  which package.sh now spells "Longshot Launcher", keeps Gear Lever from naming the file after the game at all.)
        var ownImage = platform == Platform.Windows ? null : OwnAppImage(getenv);
        var candidates = new List<string>();
        if (platform == Platform.Windows)
        {
            foreach (var p in Process.GetProcesses())
                using (p)
                    if (p.Id != own) candidates.Add(p.ProcessName);
        }
        else
        {
            IEnumerable<string> pids;
            try { pids = Directory.EnumerateDirectories("/proc").Select(System.IO.Path.GetFileName).OfType<string>().Where(n => n.All(char.IsAsciiDigit)).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pids = Array.Empty<string>(); }
            foreach (var pid in pids)
            {
                if (pid == own.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
                try
                {
                    var argv0 = File.ReadAllText($"/proc/{pid}/cmdline", Encoding.UTF8).Split('\0')[0];
                    if (ownImage != null && IsOwnRuntime(argv0, ownImage)) continue;   // (the launcher's own runtime)
                    candidates.Add(File.ReadAllText($"/proc/{pid}/comm", Encoding.UTF8).Trim());
                    candidates.Add(System.IO.Path.GetFileName(argv0));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return candidates;
    }

    // ---- the copy (longshot_saves.run, copy_saves, refuse_links) ----

    static bool IsLink(FileSystemInfo info) => info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// longshot_saves.refuse_links: a save from someone else is only files and folders: a link in it would copy what it points to
    internal static void RefuseLinks(string path)
    {
        FileSystemInfo root = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!root.Exists && root.LinkTarget == null) return;
        if (IsLink(root)) throw new SaveFailure($"{path} is a link: a save holds only files and folders, so nothing was copied.");
        if (root is not DirectoryInfo dir) return;
        foreach (var child in dir.EnumerateFileSystemInfos())
        {
            if (IsLink(child)) throw new SaveFailure($"{child.FullName} is a link: a save holds only files and folders, so nothing was copied.");
            if (child is DirectoryInfo) RefuseLinks(child.FullName);
        }
    }

    /// The tests' way to make a copy fail where a full disk or a locked file would: called with "file" before each file is copied
    /// and "swap" between the two renames that put the copy in place; whatever it throws is what the copy meets. Also "rename"
    /// before Gather renames an old save aside (a file in use), and "backup" before BackUpForDelete copies a save
    internal static Action<string>? Fault { get; set; }

    internal static void CopyTree(string from, string to, Func<string, bool>? skip = null, bool faults = true)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            if (skip?.Invoke(System.IO.Path.GetFileName(f)) == true) continue;
            if (faults) Fault?.Invoke("file");
            File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(from))
            if (skip?.Invoke(System.IO.Path.GetFileName(d)) != true) CopyTree(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)), null, faults);
    }

    internal static void DeleteTree(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Where a copy into target is made before it takes target's place, and where target waits during the swap: beside it (one
    /// disk, so each move is a rename), hidden, one name per target, so that what a copy cut short left is found by the next one
    internal static (string Staging, string Aside) Beside(string target)
    {
        var parent = System.IO.Path.GetDirectoryName(target)!;
        var name = Base(target);
        return (System.IO.Path.Combine(parent, $".{name}.longshot-staging"), System.IO.Path.Combine(parent, $".{name}.longshot-old"));
    }

    /// path with every link on the way resolved (a symlinked ~/Games, a junction, the folder itself), the parts not there yet
    /// kept as they are: two paths to one folder compare equal, whichever links they pass through
    internal static string Real(string path) => Real(path, 0);

    static string Real(string path, int depth)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var root = System.IO.Path.GetPathRoot(full) ?? "";
        var real = root;
        foreach (var part in full[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            real = System.IO.Path.Combine(real, part);
            if (depth >= 40) continue;   // (a loop of links: left as it is from there)
            try { if (new DirectoryInfo(real).ResolveLinkTarget(true) is { } t) real = Real(t.FullName, depth + 1); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return real.Length == 0 ? full : System.IO.Path.TrimEndingDirectorySeparator(real);
    }

    internal static bool Inside(string root, string path)
    {
        var c = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, c) || path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, c);
    }

    /// longshot_saves.run (after the two sides are picked) and copy_saves: src's slots, account.sav and option.sav (not with
    /// keepSettings) into dst, which loses its own slots; dst is copied whole to &lt;backupRoot&gt;/&lt;command&gt;-&lt;name&gt;-&lt;yyyyMMdd-HHmmss&gt;
    /// first. Refused (SaveFailure) when the two are one folder, NX2navi still has a save waiting on either side, a game runs
    /// (running: the names; not asked on a dry run), the backup would go inside dst, src has no slot, or src holds a link.
    /// force skips the waiting save and the running game. dst is made when it is not there yet (a build that has no save).
    public static SaveCopy Copy(string src, string dst, string backupRoot, string command, string name, bool keepSettings = false,
                                bool dryRun = false, bool force = false, Func<IReadOnlyList<string>>? running = null, Func<DateTime>? now = null)
    {
        if (Real(src) == Real(dst)) throw new SaveFailure($"{src} is both the source and the target.");
        // (a copy cut short between its two renames, the PC off: dst waits beside itself, and goes back first)
        if (!dryRun && !Directory.Exists(dst) && Beside(System.IO.Path.GetFullPath(dst)).Aside is var left && Directory.Exists(left))
            Directory.Move(left, System.IO.Path.GetFullPath(dst));
        if (!force)
            foreach (var side in new[] { src, dst })
                if (PendingCommit(side) is { } tx)
                    throw new SaveFailure($"NX2navi still has a save waiting in {tx}, which it loads instead of {side}: start NX2navi and quit it from " +
                                          "the game, then copy.", SaveRefusal.Waiting, new[] { "NX2navi" });
        if (!dryRun && !force && (running ?? (() => Running(Platforms.Current)))() is { Count: > 0 } busy)
            throw new SaveFailure($"Close {string.Join(" and ", busy)} first: a running game can write its save again and undo the copy.",
                                  SaveRefusal.Running, busy);
        var stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backup = System.IO.Path.Combine(backupRoot, $"{command}-{name}-{stamp}");
        var at = backup;
        for (var n = 2; Directory.Exists(at) || File.Exists(at); n++) at = $"{backup}-{n}";   // (two copies in the same second)
        backup = at;

        if (Inside(Real(dst), Real(backup))) throw new SaveFailure($"Choose a backup folder outside the target save folder ({dst}).");
        var names = SlotNames(src);
        if (names.Count == 0) throw new SaveFailure($"{src} has no slot_* folders: nothing to copy.");
        foreach (var s in names) RefuseLinks(System.IO.Path.Combine(src, s));
        foreach (var f in new[] { "account.sav", "option.sav" }) RefuseLinks(System.IO.Path.Combine(src, f));
        var files = new[] { "account.sav", "option.sav" }.Where(f => File.Exists(System.IO.Path.Combine(src, f))).ToList();
        if (keepSettings) files.Remove("option.sav");
        var had = Directory.Exists(dst);
        var target = had ? Real(dst) : System.IO.Path.GetFullPath(dst);
        // (a target with no save file in it has nothing to back up: a folder made for the copy, or one that holds only other
        // files. Those stay: the staging below copies everything but slot_* and account.sav/option.sav)
        var hadSave = had && (SlotNames(target).Count > 0
                              || new[] { "account.sav", "option.sav" }.Any(f => File.Exists(System.IO.Path.Combine(target, f))));
        var result = new SaveCopy(src, dst, hadSave ? backup : null, names, files, keepSettings, dryRun);
        if (dryRun) return result;

        // (never less than before: dst is backed up, the new save is made whole in a folder beside it, and only then does it take
        // dst's place, by two renames. Any failure on the way leaves dst as it was, and says where the backup is)
        var (staging, aside) = Beside(target);
        DeleteTree(staging);
        DeleteTree(aside);
        if (hadSave)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                CopyTree(target, backup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteTree(backup);
                throw new SaveFailure($"{dst} could not be backed up ({e.Message}), so nothing was copied: it is as it was.");
            }
        }
        var slotsAndFiles = new HashSet<string>(files, StringComparer.Ordinal);
        try
        {
            if (had) CopyTree(target, staging, n => n.StartsWith("slot_", StringComparison.Ordinal) || slotsAndFiles.Contains(n));
            else Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(staging);
            foreach (var s in names) CopyTree(System.IO.Path.Combine(src, s), System.IO.Path.Combine(staging, s));
            foreach (var f in files)
            {
                Fault?.Invoke("file");
                File.Copy(System.IO.Path.Combine(src, f), System.IO.Path.Combine(staging, f), overwrite: true);
            }
            if (had)
            {
                Directory.Move(target, aside);
                try
                {
                    Fault?.Invoke("swap");
                    Directory.Move(staging, target);
                }
                catch (Exception) when (!Directory.Exists(target))
                {
                    Directory.Move(aside, target);
                    throw;
                }
                DeleteTree(aside);
            }
            else Directory.Move(staging, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTree(staging);
            var kept = !had || Directory.Exists(target) || (hadSave && Restore(backup, target));
            var where = hadSave ? $" A backup of it is in {backup}." : "";
            throw new SaveFailure(kept
                ? $"The copy failed ({e.Message}). Nothing was changed: {dst} is as it was.{where}"
                : $"The copy failed ({e.Message}) and {dst} could not be put back by itself.{where}" +
                  (hadSave ? " Copy it back from there." : $" What waited beside it is in {aside}."),
                backup: hadSave ? backup : null);
        }
        return result;
    }

    /// The backup copied back to target, when the swap lost it; false when that failed too
    static bool Restore(string backup, string target)
    {
        try
        {
            CopyTree(backup, target, faults: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// Import (an emulator's save into Longshot) and export (Longshot's save into an emulator), as longshot_saves.run picks the two sides:
    /// Longshot's one profile, the emulator's (profile: when it has several); folder: the emulator's folder instead of where it is
    /// looked for (a portable user folder, NX2navi's game folder, any folder holding a save). An export's folder may hold
    /// no save yet: it becomes the save folder (ExportTarget)
    public static SaveCopy ImportExport(bool import, SaveProgram other, string? folder, SaveEnvironment env, string? profile = null,
                                        bool keepSettings = false, bool dryRun = false, bool force = false, string? backupRoot = null,
                                        Func<IReadOnlyList<string>>? running = null, Func<DateTime>? now = null)
    {
        var build = Shared(env);
        var em = (import ? Locate(other, folder, env) : ExportTarget(other, folder, env)).Profiles;
        var emulator = Pick(em, other.Display, profile);
        if (!import && !build.Found) throw new SaveFailure((build.Problem ?? "No Longshot save found.") + " Start Longshot once and save first.");
        var hs = import ? ImportTarget(env) : Pick(build.Profiles, "Longshot").Path;
        var (src, dst) = import ? (emulator.Path, hs) : (hs, emulator.Path);
        return Copy(src, dst, backupRoot ?? env.BackupRoot, import ? "import" : "export", other.Name, keepSettings, dryRun, force, running, now);
    }

    // ---- Windows: the saves each build kept in its own folder, into the shared one ----

    /// The user folders where Windows builds kept their saves before they shared one (Gather takes them in): each build's own
    /// (&lt;folder&gt;\user, until the launcher started every build with --user) and the 2.x Builder's game folder's, oldBuilder
    /// (%USERPROFILE%\Games\Longshot; null for a portable launcher, which writes nothing outside its own folder). None on Linux,
    /// where the game has always shared them
    public static IReadOnlyList<string> OldUserFolders(SaveEnvironment env, IEnumerable<string> buildFolders, string? oldBuilder)
    {
        if (env.Platform != Platform.Windows) return Array.Empty<string>();
        var shared = System.IO.Path.TrimEndingDirectorySeparator(env.LongshotUser);
        var list = new List<string>();
        foreach (var f in oldBuilder == null ? buildFolders : buildFolders.Append(oldBuilder))
        {
            var user = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(f, "user")));
            if (!user.Equals(shared, StringComparison.OrdinalIgnoreCase) && !list.Contains(user, StringComparer.OrdinalIgnoreCase)) list.Add(user);
        }
        return list;
    }

    /// When a save folder's adventure was last saved: its newest progress.sav, in any profile and slot (DateTime.MinValue when it
    /// has none; an empty one is none). Only progress counts: a game that just started writes account.sav and option.sav before anything is played
    static DateTime LastProgress(string save)
    {
        try
        {
            return Directory.EnumerateFiles(save, "progress.sav", SearchOption.AllDirectories).Select(f => new FileInfo(f))
                            .Where(f => f.Length > 0).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    static bool HasSave(string user) => Directory.Exists(System.IO.Path.Combine(user, "save")) && Find(user).Count > 0;

    static bool IsLinked(string path) => (Directory.Exists(path) || File.Exists(path)) && IsLink(new DirectoryInfo(path));

    /// The per-player files the game keeps beside save\ in its user folder (host/nn_hid.c, gpu/render/rx_present_fx.cpp): brought
    /// along with the save that is kept, when the shared folder has none of its own yet
    internal static readonly string[] PlayerFiles = { "mouse_sens.txt", "present.txt" };

    /// The saves of the old user folders (OldUserFolders) into the shared one, so that every build plays the same adventure,
    /// merged slot by slot and never over a slot of the shared save (issue #146: keeping one whole folder cost every other slot).
    /// The kept save is the shared one, or with none yet the one played last (the newest progress.sav of all; a tie: the first):
    /// its slots and its other files (account.sav, option.sav, and then the player files beside save\) stay as they are. A slot
    /// it lacks comes from the copy saved last (its progress.sav's time; an empty or missing one is the oldest; a tie: the first
    /// folder's). Any other copy that differs from every slot kept, byte for byte, may be another adventure (an old build
    /// started on its own, a new game begun in the same slot) or the same one played on: it goes to the first free slot_NN of
    /// that profile (slot_00..slot_05, the ones the game shows), the newest first, and with none free stays in the backup only.
    /// Only a copy older than the kept save's own different slot stays in the backup without a free slot being tried. The home
    /// is told which of each (SaveGathered.Slots). Every one of them is backed up whole
    /// first, in one folder (&lt;backups&gt;/gather-&lt;yyyyMMdd-HHmmss&gt;/&lt;name&gt;); the merge is made beside the shared save\ and
    /// takes its place by two renames, so a failure leaves it as it was; and each old folder's save\ is then renamed
    /// save.moved-&lt;stamp&gt;, so that nothing is deleted, and made a link to the shared save in its place (Longshot#639): a game
    /// started on its own, without --user, keeps its save in that folder, and finds the shared one there instead of none, so that
    /// no start of the launcher takes that folder in again and moves it. An old folder
    /// that is a link, or holds one as its user\ or save\ (a player's own synced folder, or the shared one itself), is the
    /// player's own arrangement and is left alone. Null when no old folder has a save, or while Longshot runs (the next start
    /// tries again). A failure changes nothing (SaveFailure). An old save that cannot be renamed (a file in use) is taken in
    /// again at the next start, where its slots are the same as the shared one's and change nothing; a link that cannot be made
    /// (a disk with no junctions) leaves the folder renamed, as it was before links, and LinkSaves tries again
    public static SaveGathered? Gather(SaveEnvironment env, IReadOnlyList<string> oldUsers, Func<IReadOnlyList<string>>? running = null,
                                       Func<DateTime>? now = null)
    {
        if (env.Platform != Platform.Windows) return null;
        var shared = env.LongshotUser;
        var sharedSave = System.IO.Path.Combine(shared, "save");
        var sharedReal = Real(sharedSave);
        var old = oldUsers.Where(u => HasSave(u) && !IsLinked(u) && !IsLinked(System.IO.Path.Combine(u, "save"))
                                      && !Inside(sharedReal, Real(System.IO.Path.Combine(u, "save"))) && !Inside(Real(System.IO.Path.Combine(u, "save")), sharedReal))
                          .ToList();
        if (old.Count == 0) return null;
        if ((running ?? (() => Running(Platforms.Current)))().Any(n => n.Contains(Longshot.Display, StringComparison.Ordinal))) return null;

        // (a gather cut short between its two renames, the PC off: the shared save waits beside itself, and goes back first)
        var (staging, aside) = Beside(sharedSave);
        if (!Directory.Exists(sharedSave) && Directory.Exists(aside)) Directory.Move(aside, sharedSave);

        // (each one named in the backup for its game folder: "v3.0.0-rc.1", or "Longshot" for the 2.x Builder's)
        var sides = new List<(string User, string Name)>();
        if (HasSave(shared)) sides.Add((shared, "shared"));
        foreach (var u in old)
        {
            var name = Base(Up(u)) is { Length: > 0 } b ? b : "build";
            var unique = name;
            for (var n = 2; sides.Any(s => s.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); n++) unique = $"{name} ({n})";
            sides.Add((u, unique));
        }
        // (the shared save, when there is one; else the one played last. Stable: the first on a tie)
        var kept = sides[0].User == shared ? sides[0] : sides.OrderByDescending(s => LastProgress(System.IO.Path.Combine(s.User, "save"))).First();
        var (slots, fromOther) = MergePlan(sides.Select(s => s.User).ToList(), sides.IndexOf(kept), shared);
        var changed = kept.User != shared || slots.Any(s => !s.Left && (s.From != kept.User || s.MovedTo != null));

        var stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backup = System.IO.Path.Combine(env.BackupRoot, "gather-" + stamp);
        for (var n = 2; Directory.Exists(backup) || File.Exists(backup); n++) backup = System.IO.Path.Combine(env.BackupRoot, $"gather-{stamp}-{n}");
        try
        {
            foreach (var (user, name) in sides) CopyTree(System.IO.Path.Combine(user, "save"), System.IO.Path.Combine(backup, name));
            // (what a gather cut short left aside while a new shared save was made since: backed up too, before it goes)
            if (Directory.Exists(aside)) CopyTree(aside, System.IO.Path.Combine(backup, "shared (set aside)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTree(backup);
            LauncherLog.Add($"saves gather: the backup in {backup} failed ({e.GetType().Name}: {e.Message}); nothing moved");
            throw new SaveFailure($"The saves could not be backed up ({e.Message}), so none was moved: they are as they were.");
        }

        if (!changed) DeleteTree(aside);   // (backed up above)
        else
        {
            DeleteTree(staging);
            DeleteTree(aside);
            try
            {
                Directory.CreateDirectory(shared);
                // (the shared save whole, or the one played last; each profile it lacks, without slots; then each slot from
                // elsewhere, where the plan put it: never over a slot of the shared save)
                CopyTree(System.IO.Path.Combine(kept.User, "save"), staging);
                foreach (var (profile, from) in fromOther)
                    CopyTree(from, System.IO.Path.Combine(staging, profile), n => n.StartsWith("slot_", StringComparison.Ordinal));
                foreach (var s in slots)
                {
                    if (s.Now is not { } slotNow || (s.From == kept.User && s.MovedTo == null)) continue;
                    var into = System.IO.Path.Combine(staging, s.Profile, slotNow);
                    if (Directory.Exists(into)) throw new IOException($"{s.Profile}/{slotNow} would be written over");   // (never: the plan put it in a free slot)
                    CopyTree(System.IO.Path.Combine(s.From, "save", s.Profile, s.Slot), into);
                }
                if (Directory.Exists(sharedSave))
                {
                    Directory.Move(sharedSave, aside);
                    try
                    {
                        Fault?.Invoke("swap");
                        Directory.Move(staging, sharedSave);
                    }
                    catch (Exception) when (!Directory.Exists(sharedSave))
                    {
                        Directory.Move(aside, sharedSave);
                        throw;
                    }
                    DeleteTree(aside);
                }
                else Directory.Move(staging, sharedSave);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteTree(staging);
                LauncherLog.Add($"saves gather: the save of {kept.User} could not be moved into {shared} ({e.GetType().Name}: {e.Message}); nothing changed, backup in {backup}");
                throw new SaveFailure($"The saves could not be moved ({e.Message}). Nothing was changed; a backup of them all is in {backup}.", backup: backup);
            }
            if (kept.User != shared)
                foreach (var f in PlayerFiles)
                {
                    var from = System.IO.Path.Combine(kept.User, f);
                    var to = System.IO.Path.Combine(shared, f);
                    try { if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
        }

        var moved = new List<string>();
        foreach (var u in old)
        {
            var to = System.IO.Path.Combine(u, "save.moved-" + stamp);
            for (var n = 2; Directory.Exists(to) || File.Exists(to); n++) to = System.IO.Path.Combine(u, $"save.moved-{stamp}-{n}");
            try
            {
                Fault?.Invoke("rename");
                Directory.Move(System.IO.Path.Combine(u, "save"), to);
                moved.Add(u);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var link = System.IO.Path.Combine(u, "save");
            LauncherLog.Add(MakeLink(link, sharedSave) ? $"saves link: {link} is now a link to {sharedSave}, the shared save"
                                                       : $"saves link: no link could be made at {link}; the folder stays renamed aside");
        }
        var movedSlots = slots.Where(s => s.MovedTo != null).Select(s => $"{s.Profile}/{s.Slot} of {s.From} to {s.MovedTo}").ToList();
        var leftSlots = slots.Where(s => s.Left).Select(s => $"{s.Profile}/{s.Slot} of {s.From}{(s.Older ? " (older)" : "")}").ToList();
        var tookSlots = slots.Where(s => s.From != kept.User && s.MovedTo == null && !s.Left).Select(s => $"{s.Profile}/{s.Slot} of {s.From}").ToList();
        LauncherLog.Add($"saves gather: backed up {string.Join(", ", sides.Select(s => s.User))} to {backup}; "
                        + (kept.User == shared ? $"the shared save in {shared} kept" : $"the save of {kept.User} copied into {shared}")
                        + (tookSlots.Count > 0 ? $"; slots it lacked, taken in: {string.Join(", ", tookSlots)}" : "")
                        + (movedSlots.Count > 0 ? $"; slots put in a free slot: {string.Join(", ", movedSlots)}" : "")
                        + (leftSlots.Count > 0 ? $"; slots kept in the backup only: {string.Join(", ", leftSlots)}" : "")
                        + (moved.Count > 0 ? $"; old saves renamed save.moved-{stamp}(-n) in {string.Join(", ", moved)}" : "")
                        + (moved.Count < old.Count ? $"; not renamed (a file in use, or refused): {string.Join(", ", old.Except(moved))}" : ""));
        return new SaveGathered(kept.User, kept.User == shared, moved, backup, slots);
    }

    // ---- Windows: a build started on its own finds the shared save ----

    /// What a link at path points at as written, whether that is there or not; null when path is no link
    static string? LinkText(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The folder a link at path names, in full (a relative link is read from the folder that holds it; a junction's text may keep
    /// the system's \??\ in front on Windows); null: path is no link
    static string? LinkedTo(string path)
    {
        if (LinkText(path) is not { Length: > 0 } text) return null;
        if (OperatingSystem.IsWindows() && text.StartsWith(@"\??\", StringComparison.Ordinal)) text = text[4..];
        return System.IO.Path.GetFullPath(text, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "");
    }

    static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                      System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// path is a link to the shared save of env (the launcher's own, not one the player made to a place of their own)
    public static bool LinksToShared(SaveEnvironment env, string path) =>
        LinkedTo(path) is { } target && SamePath(target, System.IO.Path.Combine(env.LongshotUser, "save"));

    /// The link at path taken away, whether what it names is there or not (Unix says no such folder to Directory.Delete for a link
    /// to nothing); what it names stays
    static void RemoveLink(string path)
    {
        try { Directory.Delete(path); }
        catch (DirectoryNotFoundException) when (LinkText(path) != null) { File.Delete(path); }
    }

    /// The tests' way to make a link that names something else, as a junction made wrong would; null: FileLinks.TryDirectoryLink
    internal static Func<string, string, bool>? LinkMaker { get; set; }

    /// A link at link to the shared save, made best effort and checked: it names the shared save and what it names can be read, else
    /// it is taken away again (a link that names anything else would send a game's saves there). False when none was made (the
    /// caller leaves the folder as it was)
    static bool MakeLink(string link, string sharedSave)
    {
        try
        {
            Directory.CreateDirectory(sharedSave);
            Fault?.Invoke("link");
            if (!(LinkMaker ?? FileLinks.TryDirectoryLink)(link, sharedSave)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        if (Directory.Exists(link) && (LinkedTo(link) is { } named && SamePath(named, sharedSave) || SamePath(Real(link), Real(sharedSave)))) return true;
        try { RemoveLink(link); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    enum LinkPlan { Nothing, Heal, Make, Aside, Empty, Repoint }

    /// What LinkSaves does with a game folder's user\save: Nothing; Heal, the shared save the link names is gone; Make the link
    /// where there is no folder; Aside, then Make, where a folder with no save in it is (the game's own options, say); Empty
    /// where an empty one is; Repoint a portable launcher's link whose folder has moved, so that it names nothing now
    static LinkPlan PlanLink(string gameFolder, string sharedSave)
    {
        var user = System.IO.Path.Combine(gameFolder, "user");
        var save = System.IO.Path.Combine(user, "save");
        if (LinkText(user) != null) return LinkPlan.Nothing;   // (user\ itself a link: the player's own arrangement)
        // (the shared save itself, or a folder in it or holding it, is never made a link: a game folder whose user\save is the shared one)
        if (LinkText(save) == null && (Inside(Real(sharedSave), Real(save)) || Inside(Real(save), Real(sharedSave)))) return LinkPlan.Nothing;
        if (LinkedTo(save) is { } target)
        {
            if (SamePath(target, sharedSave)) return Directory.Exists(sharedSave) ? LinkPlan.Nothing : LinkPlan.Heal;
            // (a portable launcher's folder moved: its link names the shared save where it was, which is not there now; any other
            // link is the player's own)
            var tail = System.IO.Path.DirectorySeparatorChar + LauncherLayout.PortableData + System.IO.Path.DirectorySeparatorChar + "user"
                       + System.IO.Path.DirectorySeparatorChar + "save";
            return !Directory.Exists(save) && target.Replace('/', System.IO.Path.DirectorySeparatorChar).EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                ? LinkPlan.Repoint : LinkPlan.Nothing;
        }
        if (File.Exists(save)) return LinkPlan.Nothing;
        if (!Directory.Exists(save)) return LinkPlan.Make;
        if (HasSave(user)) return LinkPlan.Nothing;   // (a save no gather has taken in yet: it comes first, and the folder is not ours to take)
        return Directory.EnumerateFileSystemEntries(save).Any() ? LinkPlan.Aside : LinkPlan.Empty;
    }

    /// (Windows) Each game folder's user\save made a link to the shared save, so that a build started on its own, without --user,
    /// reads and writes the shared saves instead of a folder of its own that the launcher would take in and move at its next start
    /// (Longshot#639; Gather makes the link where it renames a save aside). Where there is no user\save the link is made; where
    /// there is a folder with no save in it (the game's own options, say) it is renamed save.moved-&lt;stamp&gt; first and nothing is
    /// deleted; a link of a portable launcher whose folder moved is made again; a link the player made, a user\ that is a link, a
    /// folder with a save in it (Gather's to take in) and anything that is not a game folder are left alone. Nothing is done while
    /// Longshot runs, or when another launcher holds the gather lock: the next start tries again. Best effort: what cannot be done
    /// is left as it was, with no error. The folders linked
    public static IReadOnlyList<string> LinkSaves(SaveEnvironment env, IEnumerable<string> gameFolders, Func<IReadOnlyList<string>>? running = null,
                                                  Func<DateTime>? now = null)
    {
        var linked = new List<string>();
        if (env.Platform != Platform.Windows) return linked;
        var sharedSave = System.IO.Path.Combine(env.LongshotUser, "save");
        var folders = new List<string>();
        foreach (var folder in gameFolders)
        {
            try
            {
                var full = System.IO.Path.GetFullPath(folder);
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase) && GameFolder.IsGameFolder(full, env.Platform)
                    && PlanLink(full, sharedSave) != LinkPlan.Nothing) folders.Add(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        if (folders.Count == 0) return linked;
        if ((running ?? (() => Running(Platforms.Current)))().Any(n => n.Contains(Longshot.Display, StringComparison.Ordinal))) return linked;
        FileStream held;
        try { held = HoldGatherLock(env); }
        catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException) { return linked; }
        using var _ = held;
        var stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var folder in folders)
        {
            try
            {
                var save = System.IO.Path.Combine(folder, "user", "save");
                var plan = PlanLink(folder, sharedSave);   // (again, now that no other launcher is changing it)
                string? setAside = null;
                switch (plan)
                {
                    case LinkPlan.Nothing: continue;
                    case LinkPlan.Heal:
                        Directory.CreateDirectory(sharedSave);
                        LauncherLog.Add($"saves link: the shared save {sharedSave}, which {save} names, was gone and is made again");
                        continue;
                    case LinkPlan.Repoint: RemoveLink(save); break;   // (the link that names nothing goes: what it named is not there)
                    case LinkPlan.Empty: DeleteTree(save); break;
                    case LinkPlan.Aside:
                        var to = save + ".moved-" + stamp;
                        for (var n = 2; Directory.Exists(to) || File.Exists(to); n++) to = $"{save}.moved-{stamp}-{n}";
                        Fault?.Invoke("rename");
                        Directory.Move(save, to);
                        setAside = to;
                        break;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(save)!);
                if (MakeLink(save, sharedSave))
                {
                    linked.Add(folder);
                    LauncherLog.Add($"saves link: {save} is now a link to {sharedSave}, the shared save"
                                    + (plan == LinkPlan.Repoint ? "; the link before named a folder that is gone" : setAside != null ? $"; what was there is renamed {setAside}" : ""));
                }
                else LauncherLog.Add($"saves link: no link could be made at {save}" + (setAside != null ? $"; what was there stays renamed {setAside}" : ""));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return linked;
    }

    /// When a slot was last saved: its progress.sav's time (DateTime.MinValue when it has none, or an empty one: never the newest)
    static DateTime SlotProgress(string slot)
    {
        try
        {
            var p = new FileInfo(System.IO.Path.Combine(slot, "progress.sav"));
            return p.Exists && p.Length > 0 ? p.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// The slots a merge may fill, the ones the game shows
    const int GameSlots = 6;

    /// Two copies of a slot hold the same files, byte for byte (the same save: no conflict); false when either cannot be read
    static bool SameSlot(string a, string b)
    {
        try
        {
            List<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal).ToList();
            var names = Files(a);
            if (!names.SequenceEqual(Files(b), StringComparer.Ordinal)) return false;
            return names.All(n => File.ReadAllBytes(System.IO.Path.Combine(a, n)).AsSpan().SequenceEqual(File.ReadAllBytes(System.IO.Path.Combine(b, n))));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// Gather's merge, worked out from the user folders (the shared one first when it has a save; users[keptAt], the shared one or
    /// else the one played last, is kept: its slots and its files that are not slots): every copy of every slot of every profile
    /// with what becomes of it (GatheredSlot), and the profiles users[keptAt] lacks, each with the folder its files other than
    /// slots come from (of those that have it, the one whose own newest progress is the newest; a tie: the first). A slot the kept
    /// save has stays; another copy of it saved later and different goes to a free slot, one saved before or at the same time
    /// stays in the backup (Older). A slot it lacks comes from the newest copy (a tie: the first folder's); every other different
    /// copy goes to a free slot too. A copy the same as one already in, byte for byte, is the same save: nothing to do. Profiles
    /// are the save\ folder's (Find), by their path below it
    static (IReadOnlyList<GatheredSlot> Slots, IReadOnlyList<(string Profile, string From)> FromOther) MergePlan(IReadOnlyList<string> users,
                                                                                                                  int keptAt, string shared)
    {
        var profiles = users.Select(u =>
        {
            var save = System.IO.Path.Combine(u, "save");
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Find(save)) found.TryAdd(System.IO.Path.GetRelativePath(save, p.Path), p.Path);
            return found;
        }).ToList();
        var slots = new List<GatheredSlot>();
        var fromOther = new List<(string, string)>();
        foreach (var rel in profiles.SelectMany(d => d.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var holders = Enumerable.Range(0, users.Count).Where(i => profiles[i].ContainsKey(rel)).ToList();
            if (!profiles[keptAt].ContainsKey(rel))
                fromOther.Add((rel, profiles[holders.OrderByDescending(i => LastProgress(profiles[i][rel])).First()][rel]));
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (the copies in the result, whatever their slot: the kept save's, then each one placed. A copy the same as one of them
            // is already in, such as one a gather before put in a free slot from a folder it could not rename)
            var present = profiles[keptAt].TryGetValue(rel, out var keptProfile)
                ? SlotNames(keptProfile).Select(n => System.IO.Path.Combine(keptProfile, n)).ToList() : new List<string>();
            var incoming = new List<(int Side, string Slot, string Dir, DateTime Time)>();
            var older = new List<(int Side, string Slot, string Dir)>();
            // (first what each slot keeps, then every other copy: one the same as anything kept is dropped, whatever its slot)
            var kept = new List<(string Slot, (int Side, string Dir, DateTime Time) First, List<(int Side, string Dir, DateTime Time)> Copies)>();
            foreach (var slot in holders.SelectMany(i => SlotNames(profiles[i][rel])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var copies = holders.Select(i => (Side: i, Dir: System.IO.Path.Combine(profiles[i][rel], slot)))
                                    .Where(c => Directory.Exists(c.Dir)).Select(c => (c.Side, c.Dir, Time: SlotProgress(c.Dir))).ToList();
                var first = copies.FirstOrDefault(c => c.Side == keptAt);
                if (first.Dir == null)
                {
                    first = copies[0];
                    foreach (var c in copies.Skip(1))
                        if (c.Time > first.Time) first = c;
                    present.Add(first.Dir);
                }
                slots.Add(new GatheredSlot(rel, slot, users[first.Side], users[first.Side] == shared));
                placed.Add(slot);
                kept.Add((slot, first, copies));
            }
            foreach (var (slot, first, copies) in kept)
                foreach (var c in copies.Where(c => c.Side != first.Side))
                {
                    if (present.Any(o => SameSlot(o, c.Dir)) || incoming.Any(o => SameSlot(o.Dir, c.Dir)) || older.Any(o => SameSlot(o.Dir, c.Dir))) continue;
                    // (the kept save's own slot is never replaced: a later copy is another adventure, or this one played on
                    // elsewhere, and goes to a free slot; an earlier one stays in the backup. Where the kept save had none, every
                    // other copy goes to a free slot: the newest took the slot's place, but each may be an adventure of its own)
                    if (first.Side == keptAt && c.Time <= first.Time) older.Add((c.Side, slot, c.Dir));
                    else incoming.Add((c.Side, slot, c.Dir, c.Time));
                }
            foreach (var c in incoming.OrderByDescending(c => c.Time).ThenBy(c => c.Side))
            {
                var free = Enumerable.Range(0, GameSlots).Select(n => $"slot_{n:00}").FirstOrDefault(n => !placed.Contains(n));
                if (free != null) placed.Add(free);
                slots.Add(new GatheredSlot(rel, c.Slot, users[c.Side], users[c.Side] == shared, free, free == null));
            }
            foreach (var c in older) slots.Add(new GatheredSlot(rel, c.Slot, users[c.Side], users[c.Side] == shared, null, true, true));
        }
        return (slots, fromOther);
    }

    // ---- before a version's folder is deleted ----

    /// How long a gather waits for another one to finish (GatherAlone)
    internal static TimeSpan GatherWait { get; set; } = TimeSpan.FromSeconds(10);

    /// Gather, one at a time on this PC: the launcher's start, Play, Delete and a Windows shortcut's --play (a launcher process of
    /// its own) may each run one, and they share the folders beside the shared save (Beside). Each holds the gather lock
    /// (HoldGatherLock) while it runs; one that cannot get it within GatherWait is refused (SaveFailure), changing nothing
    public static SaveGathered? GatherAlone(SaveEnvironment env, IReadOnlyList<string> oldUsers, Func<IReadOnlyList<string>>? running = null,
                                           Func<DateTime>? now = null)
    {
        if (env.Platform != Platform.Windows) return null;   // (Gather does nothing there)
        using var held = HoldGatherLock(env);
        return Gather(env, oldUsers, running, now);
    }

    /// The gather lock: Longshot's user folder's .longshot-gather.lock, opened by one process at a time (FileShare.None: a share
    /// lock on Windows, an exclusive flock on Linux), whatever thread or process asks; released when the stream is closed, or
    /// by the system when the process ends. SaveFailure when another holds it still after GatherWait
    internal static FileStream HoldGatherLock(SaveEnvironment env)
    {
        Directory.CreateDirectory(env.LongshotUser);
        var path = System.IO.Path.Combine(env.LongshotUser, ".longshot-gather.lock");
        var until = DateTime.UtcNow + GatherWait;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(50); }
            catch (IOException)
            {
                throw new SaveFailure("Another Longshot Launcher is bringing the saves together right now, so this one did not: the next start tries again.");
            }
        }
    }

    /// The save a game folder still holds itself: its user\save when any file is in it, and neither user\ nor user\save is a
    /// link (deleting the folder removes a link and never follows it: Toolchains.DeleteTree). A folder that cannot be read counts
    /// as holding one. Null: deleting the folder takes no save with it. A save Gather took in is renamed save.moved-*, its backup
    /// kept: not one of these
    public static string? SaveIn(string gameFolder)
    {
        var user = System.IO.Path.Combine(System.IO.Path.GetFullPath(gameFolder), "user");
        var save = System.IO.Path.Combine(user, "save");
        try
        {
            if (IsLinked(user) || IsLinked(save)) return null;
            if (!Directory.Exists(save))
            {
                // (a user\ that cannot be listed may hide its save\: this throws, and it counts as holding one)
                if (Directory.Exists(user)) _ = Directory.EnumerateFileSystemEntries(user).Any();
                return null;
            }
            return Directory.EnumerateFiles(save, "*", SearchOption.AllDirectories).Any() ? save : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return save; }   // (cannot tell: kept)
    }

    /// The save a game folder still holds (SaveIn) copied whole before the folder is deleted, to
    /// &lt;backups&gt;/delete-&lt;yyyyMMdd-HHmmss&gt;/&lt;the folder's name&gt;, and the copy checked file by file against it: where the copy
    /// is. A copy made before with the same files (a retry, or the folder deleted after the home's Delete took one) is used
    /// again rather than a new one. SaveFailure, which says why, when no checked copy could be made: the folder must stay
    public static string BackUpForDelete(SaveEnvironment env, string gameFolder, string save, Func<DateTime>? now = null)
    {
        var name = Base(System.IO.Path.GetFullPath(gameFolder)) is { Length: > 0 } b ? b : "build";
        try
        {
            if (Directory.Exists(env.BackupRoot))
                foreach (var d in Directory.EnumerateDirectories(env.BackupRoot, "delete-*").Order(StringComparer.Ordinal).Reverse())
                    if (CopiedWhole(save, System.IO.Path.Combine(d, name))) return System.IO.Path.Combine(d, name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var stamp = (now ?? (() => DateTime.Now))().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var root = System.IO.Path.Combine(env.BackupRoot, "delete-" + stamp);
        for (var n = 2; Directory.Exists(root) || File.Exists(root); n++) root = System.IO.Path.Combine(env.BackupRoot, $"delete-{stamp}-{n}");
        var copy = System.IO.Path.Combine(root, name);
        try
        {
            Fault?.Invoke("backup");
            if (Inside(Real(System.IO.Path.GetFullPath(gameFolder)), Real(root)))
                throw new IOException("the backup folder is inside that folder");
            CopyTree(save, copy, faults: false);
            if (!CopiedWhole(save, copy)) throw new IOException("the copy differs from the save");
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteTree(root);
            throw new SaveFailure(Tr.F("the save in {0} could not be backed up ({1}), so the folder was left as it was.", save, e.Message));
        }
    }

    /// copy holds the same files as save, byte for byte, and nothing else; false when either cannot be read
    internal static bool CopiedWhole(string save, string copy)
    {
        try
        {
            if (!Directory.Exists(save) || !Directory.Exists(copy)) return false;
            List<string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(root, f)).Order(StringComparer.Ordinal).ToList();
            var names = Files(save);
            if (!names.SequenceEqual(Files(copy), StringComparer.Ordinal)) return false;
            foreach (var n in names)
            {
                var (a, c) = (System.IO.Path.Combine(save, n), System.IO.Path.Combine(copy, n));
                if (new FileInfo(a).Length != new FileInfo(c).Length || !File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(c))) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
