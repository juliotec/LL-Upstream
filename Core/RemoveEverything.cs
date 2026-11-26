namespace LongshotLauncher.Core;

/// Remove everything Longshot put on this PC, in one go (#200): the Storage page's "Remove everything…" and the Windows
/// uninstaller's "remove everything else" tick. What goes: every version's folder and the ones no row lists (each as Home's delete
/// does it, its save brought out and backed up first: BuildRegistry.DeleteGame, Leftovers.Delete), the build files (only what
/// make_game made: Disk.FreeWork), the builders and the graphics libraries, the shader store (only its own files), the game's
/// caches, the logs, and the launcher's own home and cache folders (its list, its settings, its logos, longshot-builder's
/// notes of the licences accepted: LauncherHome.BuilderData, which the launcher's builds are given as LONGSHOT_DATA). What stays: the saves,
/// their backups, the game's settings and the NFC tags, unless the player ticks for them too (Saves: then they go with nothing
/// copied first, as asked: the page says so and offers Saves > Export before); a folder the player chose (the builds folder, the
/// build files folder) when it holds other things; a folder no row lists that holds mods of theirs; and Hookshot's folders,
/// never touched (clean break). Nothing is deleted through a link: a link itself goes, never what it points at.
public static class RemoveEverything
{
    /// Where everything is: the launcher's home, the listed versions' folders, the builds folder and the build files folder (and
    /// whether the player chose them: a chosen one is never deleted itself, only what Longshot made in it), the NFC folder.
    /// EarlierWork: build files folders chosen before (LauncherSettings.EarlierWork), whose build files go too; Launcher: this
    /// launcher's program, whose kept previous version (SelfUpdate.Backup, &lt;name&gt;.old) goes
    public sealed record Places(SaveEnvironment Saves, LauncherHome Home, IReadOnlyList<string> Versions, string BuildsFolder, bool BuildsChosen,
                                string Work, bool WorkChosen, string? Nfc = null, IReadOnlyList<string>? EarlierWork = null, string? Launcher = null)
    {
        public Platform Platform => Saves.Platform;
    }

    /// What is left afterwards and why: Yours (the saves and the like, not ticked), Others (a folder holding other things),
    /// Problem (it could not be deleted: Why says why)
    public enum Reason { Yours, Others, Problem }

    public sealed record Left(string Path, Reason Reason, string? Why = null);

    /// What Run did: what is left, and where the saves found in game folders were backed up (null-free)
    public sealed record Done(IReadOnlyList<Left> Kept, IReadOnlyList<string> Backups)
    {
        public IEnumerable<Left> Problems => Kept.Where(k => k.Reason == Reason.Problem);
    }

    /// The lines of the confirmation, from the Storage page's measure: what goes with its size, and what stays
    public enum Line { Versions, ShaderCaches, BuildFiles, BuildTools, Logs, Saves, HeldByYou }

    public sealed record Sum(Line Line, long Bytes, bool Goes);

    public static readonly IReadOnlyDictionary<Line, string> Names = new Dictionary<Line, string>
    {
        [Line.Versions] = Tr.N("Installed versions, their game files and their mods"),
        [Line.ShaderCaches] = Tr.N("Shader caches"),
        [Line.BuildFiles] = Tr.N("Build files"),
        [Line.BuildTools] = Tr.N("Build tools and graphics libraries"),
        [Line.Logs] = Tr.N("Logs"),
        [Line.Saves] = Tr.N("Saves, save backups and game settings"),
        [Line.HeldByYou] = Tr.N("Old version folders holding mods of yours"),
    };

    /// The confirmation's lines from the Storage page's items: each kind in its line, the saves' going only when ticked, a folder
    /// no row lists that holds the player's mods always kept. A line with nothing in it is left out
    public static IReadOnlyList<Sum> Summary(IReadOnlyList<Storage.Item> items, bool saves)
    {
        Line Of(Storage.Item i) => i.Kind switch
        {
            Storage.Kind.Unlisted when i.Game is { HoldsYours: true } => Line.HeldByYou,
            Storage.Kind.GameFiles or Storage.Kind.Programs or Storage.Kind.Unlisted => Line.Versions,
            Storage.Kind.ModsAndSettings when i.Game != null => Line.Versions,
            Storage.Kind.ShaderCache or Storage.Kind.ShaderStore => Line.ShaderCaches,
            Storage.Kind.RecentCode or Storage.Kind.RecentTranslation or Storage.Kind.OlderCode or Storage.Kind.OlderTranslations
                or Storage.Kind.Unfinished or Storage.Kind.BuilderFiles => Line.BuildFiles,
            Storage.Kind.Builders or Storage.Kind.GraphicsLibraries or Storage.Kind.UnfinishedDownloads => Line.BuildTools,
            Storage.Kind.Logs => Line.Logs,
            _ => Line.Saves,
        };
        return items.GroupBy(Of).Where(g => g.Sum(i => i.Bytes) > 0).OrderBy(g => g.Key)
                    .Select(g => new Sum(g.Key, g.Sum(i => i.Bytes), g.Key switch { Line.Saves => saves, Line.HeldByYou => false, _ => true }))
                    .ToList();
    }

    /// Everything removed, as the type says, off the window's thread. Refused, with nothing touched, while Longshot runs (running:
    /// Saves.Running). saves: the saves, their backups, the game's settings and the NFC tags too, as the player ticked. A file that
    /// cannot be deleted is one Left with its reason; the rest goes on. The launcher's log is let go before its home goes
    /// (LauncherLog.CloseFile): nothing is logged after
    /// building: whether Longshot's builder runs (Saves.BuilderRunning; none: not asked), which writes the shader store and
    /// the build files: refused too
    public static Done Run(Places p, bool saves, Func<IReadOnlyList<string>> running, Func<DateTime>? now = null, Func<bool>? building = null)
    {
        if (running().Contains(Saves.Longshot.Display)) throw new IOException("Close Longshot first: nothing is removed while it runs.");
        if (building?.Invoke() == true) throw new IOException(Tr.T("A Longshot build is running: nothing is removed until it ends."));
        var env = p.Saves;
        var kept = new List<Left>();
        var backups = new List<string>();
        void Try(string path, Action act)
        {
            try { act(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                kept.Add(new Left(path, Reason.Problem, e.Message));
            }
        }
        LauncherLog.Add($"remove everything: began{(saves ? ", the saves too" : ", the saves kept")}");

        // (the versions, then the folders no row lists, as Home's delete and its offer do them: each one's save brought out first)
        var leftovers = Leftovers.All(p.BuildsFolder, p.Versions, p.Platform, new[] { p.Home.Root }, env);
        foreach (var v in p.Versions)
        {
            if (!Exists(v)) continue;
            var keep = p.Versions.Where(o => !Same(o, v)).Append(p.Home.Root).ToList();
            var d = BuildRegistry.DeleteGame(env, v, p.Platform, keep, running, now, env.Getenv);
            if (d.Backup != null) backups.Add(d.Backup);
            if (!d.Deleted) kept.Add(new Left(v, Reason.Problem, d.Problem));
        }
        foreach (var l in leftovers)
        {
            if (l.HoldsYours)
            {
                kept.Add(new Left(l.Folder, Reason.Yours));
                continue;
            }
            Try(l.Folder, () =>
            {
                try { if (Leftovers.Delete(l.Folder, p.Platform, p.Versions.Append(p.Home.Root).ToList(), env) is { } b) backups.Add(b); }
                catch (FolderNotDeleted e) when (e.Backup != null) { backups.Add(e.Backup); throw; }
            });
        }
        // (the tracks' folders the launcher made in the builds folder, once empty; the builds folder itself unless the player chose it)
        Try(p.BuildsFolder, () =>
        {
            foreach (var track in Subfolders(p.BuildsFolder).Where(t => InstanceFolders.TrackName(Path.GetFileName(t)))) DeleteIfEmpty(track);
            if (!p.BuildsChosen) DeleteIfEmpty(p.BuildsFolder);
        });

        // (the build files: only what make_game made, never the folder when the player chose it)
        Try(p.Work, () =>
        {
            if (Exists(p.Work) && !IsLink(p.Work)) Disk.FreeWork(p.Work, Disk.AllParts);
            if (!p.WorkChosen) DeleteIfEmpty(p.Work);
        });
        // (the build files of folders chosen before: only what Longshot made in them, each kept itself)
        foreach (var earlier in (p.EarlierWork ?? []).Where(w => !Same(w, p.Work)).Distinct())
            Try(earlier, () => { if (Exists(earlier) && !IsLink(earlier)) Disk.FreeWork(earlier, Disk.AllParts); });
        // (longshot-builder's own default cache, when the launcher's runs left one: LongshotBuilder.LauncherLeftDefaultCache)
        if (LongshotBuilder.DefaultCache(p.Platform, env.Getenv) is { } builderCache && LongshotBuilder.LauncherLeftDefaultCache(builderCache))
            Try(builderCache, () => Toolchains.DeleteTreeIfPresent(builderCache));
        // (the launcher's previous version, kept for --rollback beside it)
        if (p.Launcher is { Length: > 0 } launcher && SelfUpdate.Backup(launcher) is var old && File.Exists(old))
            Try(old, () => File.Delete(old));
        foreach (var sdk in new[] { p.Home.NgxSdk, p.Home.FfxSdk, p.Home.XessSdk }) Try(sdk, () => Toolchains.DeleteTreeIfPresent(sdk));
        // (the shader store: its own files only, LONGSHOT_SHADER_STORE may name a folder holding other things)
        if (env.ShaderStore is { } store)
            Try(store, () =>
            {
                if (Exists(store) && !IsLink(store) && ShaderCaches.ClearStore(store, () => running().Contains(Saves.Longshot.Display) || building?.Invoke() == true) == null)
                    throw new IOException("Longshot is running.");
            });

        // (the game's own caches in its user folder, regardless; the rest of it is the player's unless ticked)
        foreach (var user in new[] { env.LongshotRoot, env.LinuxUserFiles }.Distinct())
            foreach (var cache in new[] { "cache", "temp" })
            {
                var c = Path.Combine(user, cache);
                Try(c, () => Toolchains.DeleteTreeIfPresent(c));
            }

        // (what stays: the chosen folders that still hold something, the folders kept above; the player's own unless ticked)
        var stays = new List<string>();
        if (Exists(p.BuildsFolder)) stays.Add(p.BuildsFolder);
        if (Exists(p.Work)) stays.Add(p.Work);
        stays.AddRange(kept.Select(k => k.Path));
        // (the backups only the launcher writes go when ticked; oot-save-backups is Longshot's scripts' too, and Hookshot's)
        var shared = env.OldBackupRoots.Where(b => Path.GetFileName(b) == "oot-save-backups").ToList();
        var yours = new[] { env.LongshotRoot }.Concat(env.BackupRoots).Concat(p.Nfc != null ? new[] { p.Nfc } : Array.Empty<string>()).Distinct().ToList();
        stays.AddRange(shared);
        if (!saves) stays.AddRange(yours);
        var stayFull = stays.Select(Full).Distinct().ToList();

        LauncherLog.Add($"remove everything: the launcher's own folders next ({p.Home.Root}, {p.Home.Cache}); the log ends here");
        LauncherLog.CloseFile();
        foreach (var folder in new[] { p.Home.Root, p.Home.Cache }.Distinct()) Sweep(folder, stayFull, kept, env.Getenv);
        if (saves)
            foreach (var folder in yours.Where(y => !shared.Any(s => Same(s, y)))) Sweep(folder, stayFull, kept, env.Getenv);

        // (the folders that held only Longshot's, once empty: %LOCALAPPDATA%\Longshot, ~/.cache/longshot, ~/.local/share/longshot)
        foreach (var parent in new[] { Path.GetDirectoryName(env.BackupRoot), env.ShaderStore is { } s ? Path.GetDirectoryName(s) : null,
                                       Path.GetDirectoryName(p.Home.Cache), env.LongshotRoot })
            if (parent != null && string.Equals(Path.GetFileName(parent), "longshot", StringComparison.OrdinalIgnoreCase)) DeleteIfEmpty(parent);

        // (what the player keeps on purpose, said as kept: the saves and the like when they are there)
        foreach (var y in (saves ? shared : yours).Where(Exists))
            if (!kept.Any(k => Same(k.Path, y))) kept.Add(new Left(y, Reason.Yours));
        foreach (var o in new[] { p.BuildsFolder, p.Work }.Distinct().Where(Exists))
            if (!kept.Any(k => Same(k.Path, o))) kept.Add(new Left(o, Reason.Others));
        return new Done(kept, backups);
    }

    /// folder deleted, but not what stays names nor the folders on the way to it, nor anything through a link (a link itself
    /// goes). Never a drive's root nor a folder holding the user's home. A file that cannot go is one Left; the rest goes on
    static void Sweep(string folder, IReadOnlyList<string> stays, List<Left> kept, Func<string, string?> getenv)
    {
        var here = Full(folder);
        if (!Exists(here)) return;
        if (Path.GetPathRoot(here) == here || BuildRegistry.Holds(here, LauncherHome.Home(getenv)))
        {
            kept.Add(new Left(here, Reason.Problem, "Will not delete a drive's root or a folder holding your home."));
            return;
        }
        if (stays.Any(k => Same(k, here))) return;
        try
        {
            if (IsLink(here) || File.Exists(here))
            {
                if (File.Exists(here)) File.Delete(here);
                else Directory.Delete(here);
                return;
            }
            if (!stays.Any(k => BuildRegistry.Holds(here, k)))
            {
                Toolchains.DeleteTree(here);
                return;
            }
            foreach (var entry in new DirectoryInfo(here).EnumerateFileSystemInfos().ToList()) Sweep(entry.FullName, stays, kept, getenv);
            DeleteIfEmpty(here);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            kept.Add(new Left(here, Reason.Problem, e.Message));
        }
    }

    /// The Linux shortcuts the launcher made that start it (or a launcher no longer there): the game's on the desktop and in the
    /// applications menu (Shortcuts.WriteLinux, both unticked: one starting another launcher stays), and the launcher's own menu
    /// entry with its icon. Steam's tile and the Windows ones are the window's (SteamTile, WindowsShortcuts)
    public static void RemoveLinuxShortcuts(string launcher, Func<string, string?>? getenv = null)
    {
        Shortcuts.WriteLinux(launcher, null, desktop: false, menu: false, getenv: getenv);
        if (Shortcuts.LauncherInMenu(getenv) is { } target && (Shortcuts.Starts(target, launcher) || !File.Exists(target)))
            Shortcuts.RemoveLauncherFromMenu(getenv);
    }

    static IEnumerable<string> Subfolders(string folder)
    {
        try { return Exists(folder) && !IsLink(folder) ? Directory.EnumerateDirectories(folder).ToList() : Array.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !IsLink(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// There, as a file, a folder or a link (a broken one too)
    static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException) { return false; }
    }

    static bool IsLink(string path)
    {
        var info = new DirectoryInfo(path);
        return info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static bool Same(string a, string b) => BuildRegistry.SameFolder(a, b);
}
