namespace LongshotLauncher.Core;

/// What Longshot keeps on this PC, by what it is for, never by build: the Storage page. Each place it keeps something is one
/// part, measured once with the others (Disk.Measure: a file hard-linked into several parts, a version's game files linked from
/// another's, counts once), and each part is one kind of item in one of the groups. What may go is let go one item at a time,
/// with what that costs said first; the player's saves, mods and settings and the game files never go here. A version goes
/// whole, through the launcher's delete (Home's: LauncherService.DeleteGame), from its row in Installed games
public static class Storage
{
    public enum Group { GameFiles, Games, ShaderCaches, FasterUpdates, BuildTools, YourData }

    /// The groups in the page's order: a name, and what they are for (English: Tr.T where shown)
    public sealed record GroupInfo(Group Group, string Name, string About, string? Note);

    public static readonly IReadOnlyList<GroupInfo> Groups = new[]
    {
        new GroupInfo(Group.GameFiles, Tr.N("Game files"),
            Tr.N("The game itself: its models, textures, sound and text, unpacked from your dump. Every version needs them to play. Versions made from the same dump share one copy, counted once however many you have: each row is one copy on the disk."),
            Tr.N("Freed only when no version uses them any more: remove versions in Installed games.")),
        new GroupInfo(Group.Games, Tr.N("Installed games"),
            Tr.N("What runs when you press Play: each version's program and its list of shaders. Folders of versions that are no longer in your list are here too: an update left them, or a version was taken off the list with its files kept."),
            Tr.N("Removing a version deletes its folder: its program, shader caches, mods and settings. Your saves are kept.")),
        new GroupInfo(Group.ShaderCaches, Tr.N("Shader caches"),
            Tr.N("Shaders and pipelines prepared for your graphics card, one set for each installed version, so any of them starts at once. They go with their version. Only clear them if you won't be playing again soon: a version can't start until it has prepared them again."),
            null),
        new GroupInfo(Group.FasterUpdates, Tr.N("Faster updates"),
            Tr.N("Your most recent build's work, kept so the next update redoes only what changed. A nightly grows from the last stable and a stable is cut from a tested nightly, so the newest build is the best start for either. None of it is needed to play."),
            null),
        new GroupInfo(Group.BuildTools, Tr.N("Build tools"),
            Tr.N("The builder each version was made with, and the graphics libraries some builds download."),
            Tr.N("A version's tools go by themselves once no version uses them.")),
        new GroupInfo(Group.YourData, Tr.N("Your data"),
            Tr.N("Saves, save backups, mods, settings and logs."),
            Tr.N("Saves, backups, mods and settings are never removed here. Saves and their backups are managed in Saves.")),
    };

    /// What an item is: its group, its name, the line under it, and what removing it costs (null: it never goes from here)
    public enum Kind
    {
        GameFiles, Programs, Unlisted, ShaderCache, ShaderStore,
        RecentCode, RecentTranslation, OlderCode, OlderTranslations, Unfinished,
        Builders, GraphicsLibraries, UnfinishedDownloads,
        Saves, ModsAndSettings, Logs,
    }

    public sealed record KindInfo(Kind Kind, Group Group, string Name, string About, string? Cost);

    public static readonly IReadOnlyList<KindInfo> Kinds = new[]
    {
        new KindInfo(Kind.GameFiles, Group.GameFiles, Tr.N("Unpacked game files"), Tr.N("Shared by every version made from the same dump"), null),
        new KindInfo(Kind.Programs, Group.Games, "", Tr.N("Its program and shader list"),
            Tr.N("Deletes this version and its folder. Your saves are kept.")),
        new KindInfo(Kind.Unlisted, Group.Games, "", Tr.N("Not in your versions"),
            Tr.N("Deletes this folder. It isn't in your versions, so nothing you play uses it. A save still in it is backed up first.")),
        new KindInfo(Kind.ShaderCache, Group.ShaderCaches, "", Tr.N("Needed to play. It prepares them again before it can start, several minutes"),
            Tr.N("Only recommended if you won't be playing it again soon: it has to prepare its shaders again before you can play it, several minutes.")),
        new KindInfo(Kind.ShaderStore, Group.ShaderCaches, Tr.N("Shared shader store"), Tr.N("The translated shaders every version's caches refer to"),
            Tr.N("Only recommended if you won't be playing again soon: every version's shader caches start over, and each prepares its shaders again before you can play it, several minutes.")),
        new KindInfo(Kind.RecentCode, Group.FasterUpdates, Tr.N("Compiled code"), Tr.N("From your most recent build. Reused for what an update leaves as it was"),
            Tr.N("The next update compiles all of the game's code again: up to as long as a first build.")),
        new KindInfo(Kind.RecentTranslation, Group.FasterUpdates, Tr.N("Translated game code"), Tr.N("From your most recent build. Skips translating when the next update keeps the same tools"),
            Tr.N("The next update translates the game again.")),
        new KindInfo(Kind.OlderCode, Group.FasterUpdates, Tr.N("Compiled code of older builds"), Tr.N("Kept for switching versions"),
            Tr.N("Nothing for the next update: going back to an older version compiles it again.")),
        new KindInfo(Kind.OlderTranslations, Group.FasterUpdates, Tr.N("Translations of older builds"), Tr.N("Kept for switching versions"),
            Tr.N("Nothing for the next update: going back to an older version translates it again.")),
        new KindInfo(Kind.Unfinished, Group.FasterUpdates, Tr.N("Left by a build that stopped"), Tr.N("The game's program and a dump check, unpacked"),
            Tr.N("Nothing: the next build unpacks them again.")),
        new KindInfo(Kind.Builders, Group.BuildTools, Tr.N("Builders of installed versions"), Tr.N("Files they share are counted once"), null),
        new KindInfo(Kind.GraphicsLibraries, Group.BuildTools, Tr.N("Graphics libraries (DLSS, FSR, XeSS)"), Tr.N("Downloaded again by the next build that needs them"),
            Tr.N("The next build that uses them downloads them again.")),
        new KindInfo(Kind.UnfinishedDownloads, Group.BuildTools, Tr.N("Unfinished downloads"), Tr.N("Left by a download that stopped"),
            Tr.N("Nothing: a new download starts over anyway.")),
        new KindInfo(Kind.Saves, Group.YourData, Tr.N("Saves and backups"), Tr.N("Kept whatever else you remove"), null),
        new KindInfo(Kind.ModsAndSettings, Group.YourData, Tr.N("Mods and settings"), Tr.N("Kept with each version"), null),
        new KindInfo(Kind.Logs, Group.YourData, Tr.N("Logs"), Tr.N("The launcher's and the games', used for bug reports"),
            Tr.N("Logs from earlier runs go, so a bug report can't include them; new ones start at the next run.")),
    };

    public static KindInfo Info(Kind kind) => Kinds.First(k => k.Kind == kind);

    /// One installed version: its row's id, how the page names it, and its game folder (one make_game made). Unlisted: a game
    /// folder in the builds folder no row lists (Leftovers.All), deleted whole: its game files counted with the game files, the
    /// rest its row, what deleting it frees both (Item.Frees past Item.Bytes: the game files only it has); HoldsYours: one with something
    /// of the player's a delete would lose (its mods), never deleted from here
    public sealed record Game(string Id, string Name, string Folder, bool Unlisted = false, bool HoldsYours = false);

    /// Every place Longshot keeps something, from the launcher's state (Of) or the tests'
    /// ShaderStore: the shader store every version shares (SaveEnvironment.ShaderStore; null: none)
    public sealed record Places(string Work, string Toolchains, string LauncherLogs, IReadOnlyList<string> Sdks, IReadOnlyList<Game> Games,
                                IReadOnlyList<string> SaveFolders, string? ShaderStore = null, IReadOnlyList<string>? SettingsFolders = null);

    /// What the page lists: one kind of thing (per version for a program or a shader cache: Game) on one drive (Drive:
    /// Disk.DriveName; null: wherever it is), its bytes (each file once), and what removing it frees (its files with no hard link
    /// outside it: Disk.FolderSize.Frees). A hard link never crosses drives, so a drive's items add up to what it holds
    /// Game files: one item a copy on the disk (Disk.MeasureCopies: the versions' romfs/ that share files), Game the first version
    /// of it, Users every version using it (listed or not), Frees what removing all of them frees (less: other folders, outside
    /// what the page measures, link to its files too: Elsewhere, where they can be told (Windows), CopyHolders)
    public sealed record Item(Kind Kind, long Bytes, long Frees, Game? Game = null, string? Drive = null, IReadOnlyList<Game>? Users = null,
                              IReadOnlyList<string>? Elsewhere = null, bool HasClearableFiles = false, IReadOnlyList<string>? Targets = null)
    {
        public Group Group => Info(Kind).Group;
        public bool Removable => Info(Kind).Cost != null;
    }

    /// The work folder's parts, by the kind they are (Disk.WorkParts, each in one); system_headers.txt goes with the compiled
    /// code: without it the next build deletes objcache anyway (make_game.py drop_stale_objects)
    static readonly (Kind Kind, string[] Parts)[] WorkKinds =
    {
        (Kind.RecentCode, new[] { "objcache", "system_headers.txt" }),
        (Kind.RecentTranslation, new[] { "sa" }),
        (Kind.OlderCode, new[] { Disk.ObjectStore }),
        (Kind.OlderTranslations, new[] { Disk.LiftStore }),
        (Kind.Unfinished, new[] { "exefs", "probe" }),
    };

    public static IReadOnlyList<string> WorkPartsOf(Kind kind) => WorkKinds.FirstOrDefault(w => w.Kind == kind).Parts ?? Array.Empty<string>();

    /// A game folder's own parts beside the game files: the player's (mods/, user/: the saves' link and the user files, logs/),
    /// its shader caches (cache/), and the rest is its program (bin/, the exe, the shader list, ngx/ ...)
    const string Romfs = "romfs", Exefs = ".longshot_exefs", Mods = "mods", User = "user", GameLogs = "logs";
    static readonly HashSet<string> SettingsNames = new(new[] { "longshot_options.ini", "mods.json", "language.txt" }.Concat(Saves.PlayerFiles), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> NotProgram = new(new[] { Romfs, Exefs, ShaderCaches.CacheDir, Mods, User, GameLogs }.Concat(SettingsNames), StringComparer.OrdinalIgnoreCase);
    static bool GameLog(string name) => name == CrashCheck.LogName || name.StartsWith("longshot_log.", StringComparison.Ordinal)
        && name.EndsWith(".txt", StringComparison.Ordinal) && name[13..^4].Length > 0 && name[13..^4].All(char.IsAsciiDigit);
    static bool LauncherLogName(string name) => name is LauncherLog.FileName or LauncherLog.PreviousName or LauncherLog.PlayName or LauncherLog.PlayPreviousName;

    /// Every item, measured: each part once, a file hard-linked between parts in the first that has it (the work folder's
    /// objects before the object store's links to them, an earlier version's game files before a later one's). Off the
    /// window's thread: a game folder is ~187,000 files (Disk.Measure)
    public static IReadOnlyList<Item> Measure(Places p, Action? partial = null)
    {
        var parts = new List<(string Path, Kind Kind, Game? Game, bool Program)>();
        foreach (var g in p.Games.Where(g => !g.Unlisted))
        {
            parts.Add((Path.Combine(g.Folder, Romfs), Kind.GameFiles, g, false));
            parts.Add((Path.Combine(g.Folder, Exefs), Kind.GameFiles, g, false));
            parts.Add((Path.Combine(g.Folder, Mods), Kind.ModsAndSettings, g, false));
            parts.Add((g.Folder, Kind.ModsAndSettings, g, false));
            parts.Add((Path.Combine(g.Folder, User), Kind.Saves, g, false));
            parts.Add((Path.Combine(g.Folder, User), Kind.ModsAndSettings, g, false));
            var user = new DirectoryInfo(Path.Combine(g.Folder, User));
            if (user.LinkTarget == null && (!user.Exists || !user.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                parts.Add((Path.Combine(user.FullName, Mods), Kind.ModsAndSettings, g, false));
            parts.Add((Path.Combine(g.Folder, GameLogs), Kind.Logs, g, false));
            parts.Add((g.Folder, Kind.Logs, g, false));
            parts.Add((Path.Combine(g.Folder, ShaderCaches.CacheDir), Kind.ShaderCache, g, false));
            parts.Add((Path.Combine(g.Folder, ShaderCaches.CacheDir), Kind.Programs, g, false));
            parts.Add((g.Folder, Kind.Programs, g, true));
        }
        foreach (var (kind, names) in WorkKinds)
            foreach (var n in names)
                parts.Add((Path.Combine(p.Work, n), kind, null, false));
        parts.Add((p.Toolchains, Kind.Builders, null, false));
        foreach (var d in Unfinished(p.Toolchains)) parts.Add((d, Kind.UnfinishedDownloads, null, false));
        var sdkTargets = new List<string>();
        foreach (var s in p.Sdks)
        {
            parts.Add((s, Kind.GraphicsLibraries, null, false));
            try { File.GetAttributes(s); sdkTargets.Add(s); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial?.Invoke(); }
        }
        foreach (var s in p.SaveFolders) parts.Add((s, Kind.Saves, null, false));
        foreach (var s in p.SettingsFolders ?? Array.Empty<string>())
        {
            parts.Add((s, Kind.ModsAndSettings, null, false));
            parts.Add((Path.Combine(s, Mods), Kind.ModsAndSettings, null, false));
        }
        parts.Add((p.LauncherLogs, Kind.Logs, null, false));
        // (the store's own files only: LONGSHOT_SHADER_STORE may name a folder that holds other things)
        if (p.ShaderStore != null) parts.Add((p.ShaderStore, Kind.ShaderStore, null, false));
        // (a folder no row lists, last: its game files with the game files (one it shares with a listed version, or that are
        // hard links to another copy, counted there or in Game files once), the rest its own row; what deleting it frees is
        // both parts' files with no link outside them)
        foreach (var g in p.Games.Where(g => g.Unlisted))
        {
            parts.Add((Path.Combine(g.Folder, Romfs), Kind.GameFiles, g, false));
            parts.Add((Path.Combine(g.Folder, Exefs), Kind.GameFiles, g, false));
            parts.Add((g.Folder, Kind.Unlisted, g, false));
        }

        // (files: Disk.Measure walks folders, and a file named as one counts nothing; each version's romfs/ told apart by copy)
        bool IsRomfs(int i) => parts[i].Kind == Kind.GameFiles && Path.GetFileName(parts[i].Path) == Romfs;
        var cacheNames = p.Games.Where(g => !g.Unlisted).ToDictionary(g => g.Id, g =>
        {
            try { return ShaderCaches.Clearable(g.Folder, strict: true).Select(f => f.Name).ToHashSet(StringComparer.Ordinal); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { partial?.Invoke(); return new HashSet<string>(StringComparer.Ordinal); }
        });
        var measured = Disk.MeasureCopies(parts.Select(x => x.Path).ToList(), (i, name) =>
            parts[i].Program ? NotProgram.Contains(name) || GameLog(name)
            : parts[i].Kind == Kind.Unlisted ? name is Romfs or Exefs
            : parts[i].Game is { } cacheGame && Path.GetFileName(parts[i].Path) == ShaderCaches.CacheDir
                && parts[i].Kind is Kind.ShaderCache or Kind.Programs
                ? parts[i].Kind == Kind.ShaderCache
                    ? !cacheNames[cacheGame.Id].Contains(name) || Directory.Exists(Path.Combine(parts[i].Path, name))
                    : cacheNames[cacheGame.Id].Contains(name)
            : parts[i].Kind == Kind.ModsAndSettings && Path.GetFileName(parts[i].Path) != Mods
                ? !SettingsNames.Contains(name) || Directory.Exists(Path.Combine(parts[i].Path, name))
            : parts[i].Kind == Kind.Saves && parts[i].Game != null ? SettingsNames.Contains(name) || name == Mods
            : parts[i].Kind == Kind.Logs ? !(parts[i].Game == null ? LauncherLogName(name) : GameLog(name)) || Directory.Exists(Path.Combine(parts[i].Path, name))
            : parts[i].Kind == Kind.ShaderStore ? Directory.Exists(Path.Combine(parts[i].Path, name)) || !ShaderCaches.StoreNames.Any(n => name == n || name.StartsWith(n + ".", StringComparison.Ordinal))
            : parts[i].Kind == Kind.Builders && IsUnfinished(name), IsRomfs);
        if (measured.Partial) partial?.Invoke();
        var sizes = measured.Sizes;
        // (a version's copy: the one its romfs/ is in; the copy named by its first part's version)
        var copyOf = Enumerable.Range(0, parts.Count).Where(IsRomfs).ToDictionary(i => parts[i].Game!, i => measured.Copy[i]);
        var bytes = new Dictionary<(Kind, string?, string), (long Bytes, long Frees, Game? Game, List<Game>? Users)>();
        void Add(Kind kind, Game? game, string drive, long b, long f)
        {
            var own = kind is Kind.ShaderCache or Kind.Programs or Kind.Unlisted;
            var copy = kind == Kind.GameFiles && game != null && copyOf.TryGetValue(game, out var c) ? parts[c].Game : null;
            var key = (kind, own ? game?.Id : copy?.Id, drive);
            var was = bytes.GetValueOrDefault(key);
            var users = copy == null ? null : was.Users ?? new List<Game>();
            if (users != null && !users.Contains(game!)) users.Add(game!);
            bytes[key] = (was.Bytes + b, was.Frees + f, own ? game : copy, users);
        }
        for (var i = 0; i < parts.Count; i++)
        {
            var (path, kind, game, program) = parts[i];
            var drive = Disk.DriveName(path);
            var s = sizes[i];
            if (File.Exists(path) && new FileInfo(path).LinkTarget == null) s = new Disk.FolderSize(new FileInfo(path).Length, new FileInfo(path).Length, new FileInfo(path).Length);
            if (kind == Kind.GameFiles)
            {
                // (counted with the game files, a copy freed once every version of it goes (romfs/: Disk's copy frees; the exefs is
                // each version's own); one no row lists frees its part with its folder)
                Add(Kind.GameFiles, game, drive, s.Counted, IsRomfs(i) ? (measured.Copy[i] == i ? measured.CopyFrees[i] : 0) : s.Frees);
                if (game is { Unlisted: true }) Add(Kind.Unlisted, game, drive, 0, s.Frees);
                continue;
            }
            Add(kind, game, drive, s.Counted, s.Frees);
        }
        // (a copy other folders link to as well: which, by its largest such file's names)
        var elsewhere = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (c, file) in measured.CopyHeld ?? new Dictionary<int, string>())
            if (parts[c].Game is { } first) elsewhere[first.Id] = CopyHolders(file, p.Games.Select(g => g.Folder).ToList());
        return Kinds.Select(k => k.Kind)
                    .SelectMany(k => bytes.Where(b => b.Key.Item1 == k)
                                          .Select(b => new Item(k, b.Value.Bytes, b.Value.Frees, b.Value.Game, b.Key.Item3, b.Value.Users,
                                                                k == Kind.GameFiles && b.Value.Game is { } g ? elsewhere.GetValueOrDefault(g.Id) : null,
                                                                k == Kind.ShaderCache && b.Value.Game is { } cg && cacheNames[cg.Id].Count > 0,
                                                                k == Kind.GraphicsLibraries ? sdkTargets.Where(s => SameDrive(Disk.DriveName(s), b.Key.Item3)).ToList() : null)))
                    .Where(i => i.Bytes > 0 || i.Kind == Kind.ShaderCache).ToList();
    }

    /// The folders a copy's file is linked from, but the page's own game folders: each of its names (FileLinks.Names) less the
    /// path they all end with (the file's place in its romfs/), so that one is the folder holding that romfs/ (a developer's base
    /// holds it in game/). None where a file's names cannot be told (Linux)
    public static IReadOnlyList<string> CopyHolders(string file, IReadOnlyList<string> games, Func<string, IReadOnlyList<string>>? names = null)
    {
        var all = (names ?? FileLinks.Names)(file).Select(Path.GetFullPath).ToList();
        if (all.Count < 2) return Array.Empty<string>();
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Inside(string root, string path) => path.Equals(root, comparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison);
        var sample = Path.GetFullPath(file);
        var game = games.Select(Path.GetFullPath).FirstOrDefault(g => Inside(Path.Combine(g, Romfs), sample));
        var suffix = game == null ? null : Path.DirectorySeparatorChar + Path.GetRelativePath(game, sample);
        return all.Where(n => !games.Any(g => Inside(Path.GetFullPath(g), n)))
                  .Select(n => suffix != null && n.EndsWith(suffix, comparison) ? Path.TrimEndingDirectorySeparator(n[..^suffix.Length] + Path.DirectorySeparatorChar) : Path.GetDirectoryName(n)!)
                  .Distinct(cmp).Order(cmp).ToList();
    }

    /// Two drive names one drive (Disk.DriveName: Windows' letters in either case)
    public static bool SameDrive(string? a, string? b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// The tools' staging that a fetch that stopped left: Toolchains' .downloads and its &lt;tag&gt;.part folders
    static bool IsUnfinished(string name) => name == ".downloads" || name.EndsWith(".part", StringComparison.Ordinal);

    static IEnumerable<string> Unfinished(string toolchains)
    {
        try { return new DirectoryInfo(toolchains).EnumerateDirectories().Where(d => IsUnfinished(d.Name) && d.LinkTarget == null).Select(d => d.FullName).ToList(); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    /// Why an item cannot go now: Longshot runs (it holds its shader caches and logs, and no version is deleted while a game
    /// runs, as Home's delete), or a download runs (it holds the tools' staging, as LauncherService.PruneTools waits for), or
    /// a folder no row lists holds something of the player's (HoldsYours)
    public enum Refusal { None, Running, Downloading, NotRemovable, HoldsYours }

    /// Whether item may go now. running: Longshot runs; downloading: a tools fetch runs
    public static Refusal CanRemove(Item item, bool running, bool downloading) =>
        !item.Removable ? Refusal.NotRemovable
        : item.Kind == Kind.Unlisted && item.Game is { HoldsYours: true } ? Refusal.HoldsYours
        : running && item.Kind is Kind.ShaderCache or Kind.ShaderStore or Kind.Logs or Kind.Programs or Kind.Unlisted ? Refusal.Running
        : downloading && item.Kind == Kind.UnfinishedDownloads ? Refusal.Downloading
        : Refusal.None;

    /// item let go: only what it is (a work part by name, a version's shader caches, the SDK folders, the tools' staging, the
    /// logs but the one this run writes), never a link followed. Refused (nothing deleted) as CanRemove says; a file that
    /// cannot be deleted is the caller's exception, the ones before it gone. Off the window's thread
    /// cachesHeld: the tests' stand-in for the shader caches' own look at whether Longshot runs (ShaderCaches.Clear)
    /// deleteFolder: how a folder no row lists is deleted (the launcher's: LauncherService.DeleteLeftover, its save backed up
    /// first); none, it is not
    public static Refusal Remove(Item item, Places p, bool running, bool downloading, string? ownLog = null, Func<bool>? cachesHeld = null,
                                 Action<Game>? deleteFolder = null, Action<int, int, int>? logOutcome = null,
                                 Action<int, int, int>? folderOutcome = null)
    {
        var refusal = CanRemove(item, running, downloading);
        if (refusal != Refusal.None) return refusal;
        // (an item of one drive lets go only what is on it: the page's drive picked, never another's)
        bool On(string path) => item.Drive == null || SameDrive(Disk.DriveName(path), item.Drive);
        switch (item.Kind)
        {
            // (a version goes whole, its saves brought out first, through the launcher's delete: never one folder deleted here)
            case Kind.Programs:
                return Refusal.NotRemovable;
            case Kind.Unlisted when item.Game is { } g:
                if (deleteFolder == null || !On(g.Folder)) return Refusal.NotRemovable;
                deleteFolder(g);
                break;
            case Kind.ShaderCache when item.Game is { } g:
                // (its own look at whether Longshot runs as well: running only ever refuses, never lets a clear past that)
                return ShaderCaches.Clear(g.Folder, notes: false, running: running ? () => true : cachesHeld) == null ? Refusal.Running : Refusal.None;
            case Kind.ShaderStore:
                // (each version's caches see the store changed and start over: ShaderCaches.StoreNames)
                if (p.ShaderStore is { } store && On(store)
                    && ShaderCaches.ClearStore(store, running ? () => true : cachesHeld) == null) return Refusal.Running;
                break;
            case Kind.GraphicsLibraries:
                int removedFolders = 0, skippedFolders = 0, failedFolders = 0;
                foreach (var s in (item.Targets ?? p.Sdks).Where(On))
                {
                    try { if (DeleteFolder(s)) removedFolders++; else skippedFolders++; }
                    catch (Exception e) when (folderOutcome != null && (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)) { failedFolders++; }
                }
                folderOutcome?.Invoke(removedFolders, skippedFolders, failedFolders);
                break;
            case Kind.UnfinishedDownloads:
                foreach (var d in Unfinished(p.Toolchains).Where(On)) DeleteFolder(d);
                break;
            case Kind.Logs:
                var removed = 0;
                var skipped = 0;
                var failed = 0;
                void Logs(string folder, string? keep, Func<string, bool> matches)
                {
                    var result = DeleteFiles(folder, keep, matches);
                    removed += result.Removed;
                    skipped += result.Skipped;
                    failed += result.Failed;
                }
                if (On(p.LauncherLogs)) Logs(p.LauncherLogs, ownLog, LauncherLogName);
                foreach (var g in p.Games.Where(g => !g.Unlisted && On(g.Folder)))
                {
                    Logs(Path.Combine(g.Folder, GameLogs), null, GameLog);
                    Logs(g.Folder, null, GameLog);
                }
                logOutcome?.Invoke(removed, skipped, failed);
                break;
            default:
                if (On(p.Work)) Disk.FreeWork(p.Work, WorkPartsOf(item.Kind), folderOutcome);
                break;
        }
        LauncherLog.Add($"storage: removed {item.Kind}{(item.Game is { } at ? " of " + at.Name : "")}{(item.Drive is { } on ? " on " + on : "")} ({item.Frees} bytes measured)");
        return Refusal.None;
    }

    /// After a build that finished: with keeping off, remove its work and the capped stores.
    public static void AfterBuild(string work, bool keep)
    {
        try { if (!keep) Disk.FreeWork(work, Disk.WorkParts); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// What deleting a version leaves behind to offer with it (Home's delete): the build files' bytes (Disk.WorkBytes, often
    /// many GB) once no version is left, as no update needs them then; 0 while one is (its next update reuses them). Freeing
    /// them is Disk.FreeWork: only what make_game made, never the folder (the player may have chosen one that holds other
    /// things), nor the saves and their backups, which are elsewhere
    public static long LeftAfterLast(string work, int versionsLeft) => versionsLeft > 0 ? 0 : Disk.WorkBytes(work);

    static bool DeleteFolder(string folder)
    {
        try { File.GetAttributes(folder); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        return Toolchains.DeleteTreeIfPresent(folder);
    }

    /// The files right in folder, never a link's target nor a folder inside; keep: one not to delete (the log this run writes)
    static (int Removed, int Skipped, int Failed) DeleteFiles(string folder, string? keep, Func<string, bool> matches)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(folder); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (0, 0, 0); }
        if ((attributes & FileAttributes.Directory) == 0) return (0, 0, 0);
        if (new DirectoryInfo(folder).LinkTarget != null) return (0, 0, 0);
        int removed = 0, skipped = 0, failed = 0;
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
        {
            if (!matches(f.Name)) continue;
            if (keep != null && string.Equals(Path.GetFullPath(f.FullName), Path.GetFullPath(keep), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { skipped++; continue; }
            try { f.Delete(); removed++; }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }
        return (removed, skipped, failed);
    }
}
