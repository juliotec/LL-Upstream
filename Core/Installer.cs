namespace LongshotLauncher.Core;

/// The registry keys the installer writes (HKEY_CURRENT_USER only), behind an interface so that the tests use a dictionary and
/// never the real registry. Values are strings, or ints written as DWORDs
public interface IUserRegistry
{
    IReadOnlyDictionary<string, object>? Read(string key);
    void Write(string key, IReadOnlyDictionary<string, object> values);
    void Delete(string key);
}

/// The Start menu's .lnk files (the window makes them through the shell), behind an interface for the tests
public interface IShortcutMaker
{
    /// appId: its AppUserModelID (Shortcuts.LauncherAppId), so the Start menu tells it from the game's shortcuts
    void Make(string link, string target, string? arguments, string icon, string description, string appId);
    void Remove(string link);
}

/// What the Windows launcher's own installer touches, worked out from the variables (the tests point them into a folder)
public sealed record InstallPlaces(string Program, string StartMenuLink, string Home)
{
    public string Folder => Path.GetDirectoryName(Program)!;

    public static InstallPlaces Of(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var appData = getenv("APPDATA") is { Length: > 0 } a ? a : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var start = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", Installer.DisplayName + ".lnk");
        return new InstallPlaces(LauncherLayout.InstalledExe(getenv), start, LauncherHome.Resolve(Platform.Windows, getenv).Root);
    }
}

/// The Windows launcher installing itself, the simple way: there is no installer program, the launcher is its own (README,
/// "Installing"). Install copies the running program to %LOCALAPPDATA%\Programs\LongshotLauncher\LongshotLauncher.exe (per user, no
/// administrator rights), makes a Start menu shortcut and an "Apps &amp; features" entry (HKCU\...\Uninstall\LongshotLauncher), whose
/// UninstallString is "&lt;program&gt;" --uninstall. Uninstall takes those away again; the launcher's data goes only when asked, and
/// the builds' game folders and the saves never.
public static class Installer
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LongshotLauncher";
    public const string DisplayName = "Longshot Launcher", Publisher = "Magic Bean", UninstallArgument = "--uninstall", InstallArgument = "--install";

    /// The "Apps &amp; features" entry for the installed program
    public static IReadOnlyDictionary<string, object> Entry(InstallPlaces at, string version, long bytes) => new Dictionary<string, object>
    {
        ["DisplayName"] = DisplayName,
        ["DisplayVersion"] = Versions.IsTagName(version) ? Versions.Shown(version).TrimStart('v') : version,
        ["Publisher"] = Publisher,
        ["DisplayIcon"] = at.Program + ",0",
        ["InstallLocation"] = at.Folder,
        ["UninstallString"] = $"\"{at.Program}\" {UninstallArgument}",
        ["QuietUninstallString"] = $"\"{at.Program}\" {UninstallArgument}",
        ["URLInfoAbout"] = Releases.LauncherRepo,
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
        ["EstimatedSize"] = (int)Math.Min(int.MaxValue, bytes / 1024),
    };

    /// source (the running program) copied to the installed program's place (whole or not at all: a copy beside it, then a rename),
    /// with its shortcut and its entry. A program installed before is replaced, the leftovers of its updates go; but never by an
    /// older one (InstalledIsNewer): that one is left as it is, and started instead. Returns where it is
    public static string Install(string source, string version, InstallPlaces at, IUserRegistry registry, IShortcutMaker shortcuts)
    {
        if (InstalledIsNewer(version, at, registry)) return at.Program;
        Directory.CreateDirectory(at.Folder);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(at.Program), StringComparison.OrdinalIgnoreCase))
        {
            var part = $"{at.Program}.{Guid.NewGuid():N}.part";
            try
            {
                File.Copy(source, part, overwrite: true);
                using (var f = new FileStream(part, FileMode.Open, FileAccess.ReadWrite)) f.Flush(flushToDisk: true);
                File.Move(part, at.Program, overwrite: true);
            }
            finally { File.Delete(part); }
        }
        foreach (var left in new[] { SelfUpdate.Staged(at.Program), SelfUpdate.Backup(at.Program), at.Program + ".rolledback" })
            File.Delete(left);
        Register(at, version, registry);
        MakeLink(at, shortcuts);
        LauncherLog.Add($"installed: {version} copied from {source} to {at.Program}, with its Start menu shortcut and its Apps & features entry");
        return at.Program;
    }

    static void MakeLink(InstallPlaces at, IShortcutMaker shortcuts) =>
        shortcuts.Make(at.StartMenuLink, at.Program, null, at.Program, "Install Longshot's builder, build the game from your own copy, and play",
                       Shortcuts.LauncherAppId);

    /// The entry written again with this version (an installed launcher does it at each start, so it follows its own updates)
    public static void Register(InstallPlaces at, string version, IUserRegistry registry)
    {
        var bytes = File.Exists(at.Program) ? new FileInfo(at.Program).Length : 0;
        registry.Write(UninstallKey, Entry(at, version, bytes));
    }

    /// An installed launcher's start: its entry written again with this version (Register), and, at the first start of another
    /// version (the entry named another one), its Start menu shortcut made again. An update moves the running program aside to
    /// &lt;name&gt;.old before the new one takes its name (SelfUpdate.Swap), and Windows' shortcut tracking can follow it there; the
    /// next update deletes that .old, and the Start menu drops a shortcut to nothing. Made again, it starts the program by its name.
    /// Once per version only: a shortcut the player deleted stays deleted. The entry is written after the shortcut, so a shortcut
    /// that could not be made is tried again at the next start. Returns whether it was made again (then the game's own shortcuts
    /// are made again too)
    public static bool Refresh(InstallPlaces at, string version, IUserRegistry registry, IShortcutMaker shortcuts)
    {
        var was = registry.Read(UninstallKey) is { } entry && entry.TryGetValue("DisplayVersion", out var v) ? v as string : null;
        var changed = was != (string)Entry(at, version, 0)["DisplayVersion"];
        if (changed)
        {
            MakeLink(at, shortcuts);
            LauncherLog.Add($"installed launcher {was ?? "(no version)"} -> {version}: its Start menu shortcut made again");
        }
        Register(at, version, registry);
        return changed;
    }

    public static bool IsRegistered(IUserRegistry registry) => registry.Read(UninstallKey) != null;

    static readonly System.Text.RegularExpressions.Regex ShownNightly =
        new(@"^Nightly ([0-9]{1,9}) · ([0-9a-f]{8})(-dirty)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// The version of the launcher installed here, read back from its entry's DisplayVersion (Entry: 1.0.0-rc.9, or
    /// Nightly 100 · 5f30eea3); an installed launcher writes it again at each start (Refresh), the first one after each of its
    /// own updates too, so it follows them. Null when no program is installed, or its entry names no version
    public static string? InstalledVersion(InstallPlaces at, IUserRegistry registry)
    {
        if (!File.Exists(at.Program) || registry.Read(UninstallKey) is not { } entry
            || !entry.TryGetValue("DisplayVersion", out var value) || value is not string shown) return null;
        if (ShownNightly.Match(shown) is { Success: true } m) return $"nightly-{m.Groups[1].Value}-{m.Groups[2].Value}{m.Groups[3].Value}";
        return Versions.IsRelease("v" + shown, Versions.LauncherFirstMajor) ? "v" + shown : null;
    }

    /// Whether the launcher installed here is newer than own (SelfUpdate.Compare): the Windows download started again after the
    /// launcher it installed has updated itself. Its Install would put the older launcher back (#129), so the installed one starts
    /// instead. The same version is not newer (Install puts back the same program, its shortcut and its entry: a repair), nor is
    /// one that has no order with own (a nightly and a release: the player chose that channel's download)
    public static bool InstalledIsNewer(string own, InstallPlaces at, IUserRegistry registry) =>
        InstalledVersion(at, registry) is { } installed && SelfUpdate.Compare(installed, own) > 0;

    /// What Uninstall did: Pending, the program that could not be deleted because it is running (the window deletes it once it has
    /// closed); Kept, what of the data was left because it is a build's or a save's
    public sealed record Removed(string? Pending, IReadOnlyList<string> Kept);

    /// The shortcut, the entry and the program removed; with deleteData, the launcher's home too, but never a folder that is a
    /// build's game folder or holds one, nor keep's (the save backups): those stay where they are. running: the program this
    /// process runs from (it cannot delete itself on Windows: it is Pending)
    public static Removed Uninstall(InstallPlaces at, IUserRegistry registry, IShortcutMaker shortcuts, bool deleteData,
                                    IEnumerable<string> keep, string? running)
    {
        shortcuts.Remove(at.StartMenuLink);
        registry.Delete(UninstallKey);
        foreach (var left in new[] { SelfUpdate.Staged(at.Program), SelfUpdate.Backup(at.Program), at.Program + ".rolledback" })
            File.Delete(left);
        string? pending = null;
        if (running != null && string.Equals(Path.GetFullPath(running), Path.GetFullPath(at.Program), StringComparison.OrdinalIgnoreCase))
            pending = at.Program;
        else
        {
            File.Delete(at.Program);
            DeleteIfEmpty(at.Folder);
        }
        LauncherLog.Add($"uninstalled: {at.Program}{(pending != null ? " (deleted once the window closes)" : "")}, its shortcut and entry; "
                        + (deleteData ? "its data in " + at.Home + " deleted" : "its data kept in " + at.Home));
        var kept = new List<string>();
        if (deleteData && Directory.Exists(at.Home))
        {
            // (the log file is in the home: let go of it before the home goes, and write none after: LauncherLog.CloseFile)
            LauncherLog.CloseFile();
            DeleteKeeping(at.Home, keep.Select(k => Path.TrimEndingDirectorySeparator(Path.GetFullPath(k))).ToList(), kept);
        }
        return new Removed(pending, kept);
    }

    public static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static bool Same(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static bool Holds(string folder, string path) =>
        Same(folder, path) || path.StartsWith(folder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// folder deleted, but not what keep names nor the folders on the way to it (nor anything through a link: a link itself goes)
    static void DeleteKeeping(string folder, IReadOnlyList<string> keep, List<string> kept)
    {
        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (keep.Any(k => Same(k, here)))
        {
            kept.Add(here);
            return;
        }
        if (!keep.Any(k => Holds(here, k)))
        {
            var info = new DirectoryInfo(here);
            if (info.LinkTarget != null) info.Delete();
            else info.Delete(recursive: true);
            return;
        }
        foreach (var entry in new DirectoryInfo(here).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo d && d.LinkTarget == null) DeleteKeeping(d.FullName, keep, kept);
            else entry.Delete();
        }
        DeleteIfEmpty(here);
    }
}
