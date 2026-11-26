using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// Instances' folders: builds/&lt;track&gt;/&lt;version&gt; (builds/nightly/nightly-2029-c544828f, builds/stable/v3.0.1), so a folder says what
/// is in it (LauncherSettings.FolderFor). An update or a go-back renames its instance's folder to the new version's name when the
/// build starts, and back when the build does not finish (BuildRegistry.FolderOf, Rename, MoveBack): the same folder renamed, so
/// everything in it stays (the game, its settings, shader caches, mods, a save in its own user/save). A folder the player chose
/// elsewhere keeps its name. Instances from before this layout are right in the builds folder, named after their track (nightly,
/// nightly (2): launchers v1.0.0-rc.7 and on) or after the version they were first built with (v3.0.0-rc.5: launchers before);
/// the home offers, once, to move them into it (Plan, Rename), only when the player says so
public static class InstanceFolders
{
    /// A planned move: the build, its folder now, and the folder it gets
    public sealed record Move(BuildEntry Build, string From, string To)
    {
        /// The folders as the offer shows them, from the builds folder on: nightly (2), nightly/nightly-2029-c544828f
        public string FromName => Tail(From, 1);
        public string ToName => Tail(To, 2);
    }

    static readonly Regex Numbered = new(@"^(.+?)(?: \(\d+\))?$", RegexOptions.CultureInvariant);

    /// A name without its " (2)" and on
    static string Base(string name) => Numbered.Match(name) is { Success: true } m ? m.Groups[1].Value : name;

    static string Name(string folder) => Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));

    static string? Parent(string folder)
    {
        try { return Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// path at its place in to when it is in from (or is from itself), a folder that was moved there; any other path as it is.
    /// The game files a build is made from can be in an instance's own folder (a player picks its romfs and .longshot_exefs for
    /// the next update, so the dump is not needed again): when that folder is renamed, the paths to them follow it (the
    /// list's rows: BuildRegistry.Moved; the remembered ones: LauncherSettings.Follow; the build's own: FoundGameFile.Follow).
    /// Before, an update renamed the folder, then gave make_game.py the old path, and it stopped: "does not hold the game's files"
    public static string? Follow(string? path, string from, string to)
    {
        if (string.IsNullOrEmpty(path) || !BuildRegistry.Holds(from, path)) return path;
        try
        {
            var rest = Path.GetRelativePath(Path.GetFullPath(from), Path.GetFullPath(path));
            return rest == "." ? to : Path.Combine(to, rest);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// The last count names of path, joined by the separator it has
    static string Tail(string path, int count)
    {
        var sep = path.Contains('\\') ? '\\' : '/';
        var names = Path.TrimEndingDirectorySeparator(path).Split('\\', '/').Where(n => n.Length > 0).ToList();
        return string.Join(sep, names.Skip(Math.Max(0, names.Count - count)));
    }

    /// A track's folder's name: stable, nightly, pr or Longshot (LauncherSettings.TrackFolder), with " (2)" and on
    public static bool TrackName(string name)
    {
        var b = Base(name);
        return Versions.IsTrack(b) || b == "Longshot";
    }

    /// folder is in the builds folder's layout: builds/&lt;track&gt;/&lt;a name&gt;. Only such a folder is renamed when it updates
    public static bool InLayout(string folder, string buildsFolder)
    {
        if (Parent(folder) is not { } track || !TrackName(Name(track))) return false;
        return Parent(track) is { } builds && BuildRegistry.SameFolder(builds, buildsFolder);
    }

    /// The channel a game folder's name says it was built on: its track's folder's (builds/nightly/&lt;version&gt;), else, for one
    /// from before the layout right in the builds folder, its own (builds/nightly); " (2)" and on left out either way (builds/nightly (2),
    /// builds/nightly (2)/&lt;version&gt;). Null when the name is no channel (Longshot, a name the player gave)
    public static string? ChannelNamed(string folder, string buildsFolder)
    {
        var named = InLayout(folder, buildsFolder) ? Parent(folder) : folder;
        return named != null && Base(Name(named)) is var b && Versions.IsChannel(b) ? b : null;
    }

    /// folder is named for that version on that track: &lt;track&gt;/&lt;version&gt;, either with " (2)" and on
    public static bool NamedFor(string folder, string channel, string? version) =>
        Parent(folder) is { } track && Base(Name(track)) == LauncherSettings.TrackFolder(channel)
        && Base(Name(folder)) == LauncherSettings.VersionFolder(version);

    /// The folders in the builds folder an instance can be: those right in it (before this layout) and those in its tracks'
    /// folders (builds/nightly/*). What Leftovers and the saves' gather look through
    public static IReadOnlyList<string> Folders(string buildsFolder, Platform platform, Action? partial = null)
    {
        var found = new List<string>();
        try
        {
            try { File.GetAttributes(buildsFolder); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return found; }
            foreach (var dir in Directory.EnumerateDirectories(buildsFolder).Order(StringComparer.Ordinal))
            {
                found.Add(dir);
                if (!TrackName(Name(dir)) || (partial == null ? GameFolder.IsGameFolder(dir, platform) : GameFolder.IsGameFolder(dir, platform, partial)) || new DirectoryInfo(dir).LinkTarget != null) continue;
                try { found.AddRange(Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial?.Invoke(); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial?.Invoke(); }
        return found;
    }

    /// The instances to offer: listed, right in the builds folder (where launchers before this layout made them; a folder the
    /// player picked elsewhere is theirs to name), still on the disk, and not one the player chose to keep. Each one's new folder
    /// is builds/&lt;its track&gt;/&lt;its version&gt; (LauncherSettings.FolderFor) past every name that is taken: anything on the disk, another
    /// build's folder, and the folders planned before it. The ones named after their track come first: builds/nightly becomes the
    /// nightly's folder, and the others go in it
    public static IReadOnlyList<Move> Plan(BuildRegistry registry, string buildsFolder, Platform platform, IEnumerable<string>? kept = null)
    {
        var keep = (kept ?? Array.Empty<string>()).ToList();
        var old = registry.Builds
            .Where(b => Directory.Exists(b.Folder) && Parent(b.Folder) is { } p && BuildRegistry.SameFolder(p, buildsFolder)
                        && !keep.Any(k => BuildRegistry.SameFolder(k, b.Folder)))
            .OrderBy(b => TrackName(Name(b.Folder)) ? 0 : 1).ThenBy(b => b.Folder, StringComparer.Ordinal)
            .ToList();
        var planned = new List<string>();
        var moves = new List<Move>();
        foreach (var b in old)
        {
            var to = LauncherSettings.FolderFor(buildsFolder, b.Channel, b.Version,
                p => Path.Exists(p) || registry.FindByFolder(p) != null || planned.Any(q => BuildRegistry.SameFolder(q, p)),
                t => !old.Any(o => BuildRegistry.SameFolder(o.Folder, t))
                     && (File.Exists(t) || registry.FindByFolder(t) != null || Directory.Exists(t) && GameFolder.IsGameFolder(t, platform)));
            planned.Add(to);
            moves.Add(new Move(b, b.Folder, to));
        }
        return moves;
    }

    /// An instance's folder moved as planned (the home's offer), or renamed to its new version's when an update or a go-back
    /// starts (BuildStepViewModel): the same folder, so everything in it stays as it is: the game, its settings
    /// (longshot_options.ini), its shader caches, mods, and a save in its own user/save. The launcher's shortcuts open it and play
    /// the default build by its row (Windows' shortcut names the game directly with --play), so they follow; a shortcut or a
    /// Steam entry made to the game's own program does not.
    /// Refused (IOException, nothing changed) while Longshot runs (its files are open), when the folder is a link, holds the
    /// launcher's home or Longshot's shared saves, when its row changed since the plan, when the new name is taken, or when it
    /// would go inside another instance's folder. A file in it held open (a File Explorer window in it, a text editor, an
    /// antivirus scan) refuses the rename on Windows: IOException, nothing changed. The list is saved with the new folder; if
    /// that fails, the folder goes back and the row too
    public static void Rename(BuildRegistry registry, Move move, Platform platform, SaveEnvironment? saves = null,
                              Func<IReadOnlyList<string>>? running = null, Func<string, string?>? getenv = null)
    {
        if ((running ?? (() => Saves.Running(platform)))().Any(n => n.Contains(Saves.Longshot.Display, StringComparison.Ordinal)))
            throw new IOException("Longshot is running: close it, then build or move its folder again.");
        if (registry.Find(move.Build.Id) is not { } entry || !BuildRegistry.SameFolder(entry.Folder, move.From))
            throw new IOException("This version's folder changed since, so it is left as it is: " + move.From);
        var from = Path.GetFullPath(move.From);
        if (!Directory.Exists(from)) throw new IOException("The folder is gone: " + move.From);
        if (new DirectoryInfo(from).LinkTarget != null)
            throw new IOException("This folder is a link to another one, so the launcher leaves it as it is: " + move.From);
        if (BuildRegistry.Holds(from, LauncherHome.Home(getenv ?? Environment.GetEnvironmentVariable)))
            throw new IOException("This folder holds your home folder, so the launcher leaves it as it is: " + move.From);
        if (saves != null && new[] { saves.LongshotRoot, saves.LongshotUser }.FirstOrDefault(u => BuildRegistry.Holds(Saves.Real(from), Saves.Real(u))) is { } held)
            throw new IOException($"This folder holds {held}, Longshot's saves, so the launcher leaves it as it is: {move.From}");
        if (Path.Exists(move.To)) throw new IOException("A folder of that name is there already: " + move.To);
        if (Parent(move.To) is { } into && !BuildRegistry.SameFolder(into, from) && Directory.Exists(into)
            && (registry.FindByFolder(into) != null || GameFolder.IsGameFolder(into, platform)))
            throw new IOException($"{move.To} would be inside another version's folder, {into}: move that one first.");
        Place(from, move.To);
        try
        {
            registry.Moved(entry.Id, move.To);
            registry.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            registry.Moved(entry.Id, move.From);
            try { Place(move.To, from); }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                registry.Moved(entry.Id, move.To);   // (the row says where the folder is)
                throw new IOException($"The list could not be saved ({e.Message}), and {move.To} could not go back to {move.From}: {back.Message}", e);
            }
            throw;
        }
    }

    /// An update's or a go-back's rename undone (the build failed or was stopped): its folder back to its old name, so the row's
    /// folder says the version the row still has. A file the stopped build still holds for a moment refuses it: tried again until
    /// wait is up. False when it could not go back: the row then lists the folder where it is (saved), and nothing is lost
    public static bool MoveBack(BuildRegistry registry, Move moved, TimeSpan wait, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                // (taken since, unless it is the track's folder it went into: builds/nightly holding builds/nightly/<version>)
                if (Path.Exists(moved.From) && !BuildRegistry.Holds(moved.From, moved.To)) return false;
                Place(moved.To, moved.From);
                registry.Moved(moved.Build.Id, moved.From);
                try { registry.Save(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    LauncherLog.Add($"instance folder: back to {moved.From}, the list not saved: {e.Message}");
                }
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= until)
                {
                    LauncherLog.Add($"instance folder: {moved.To} could not go back to {moved.From}: {e.Message}");
                    return false;
                }
                sleep(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// from moved to to on the disk. In one folder, or into another track's folder (made first, and taken away again when the move
    /// fails); to inside from (builds/nightly becoming builds/nightly/&lt;version&gt;), or from inside to (back), by way of a name beside
    /// it. A track's folder left empty by the move is taken away
    static void Place(string from, string to)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        var aside = from + ".moving-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var left = Parent(from);
        if (BuildRegistry.Holds(from, to))   // (builds/nightly to builds/nightly/<version>: it becomes its own track's folder)
        {
            Directory.Move(from, aside);
            try
            {
                Directory.CreateDirectory(Parent(to)!);
                Directory.Move(aside, to);
            }
            catch (Exception) when (Directory.Exists(aside))
            {
                TryDeleteEmpty(Parent(to)!, stopAt: Parent(from)!);
                Directory.Move(aside, from);
                throw;
            }
            return;
        }
        if (BuildRegistry.Holds(to, from))   // (builds/nightly/<version> back to builds/nightly: its track's folder must hold only it)
        {
            var back = to + aside[from.Length..];
            Directory.Move(from, back);
            try
            {
                Directory.Delete(to);   // (only when empty: IOException otherwise)
                Directory.Move(back, to);
            }
            catch (Exception) when (Directory.Exists(back))
            {
                Directory.CreateDirectory(Parent(from)!);
                Directory.Move(back, from);
                throw;
            }
            return;
        }
        var into = Parent(to)!;
        var made = !Directory.Exists(into);
        Directory.CreateDirectory(into);
        try { Directory.Move(from, to); }
        catch (Exception)
        {
            if (made) TryDeleteEmpty(into, stopAt: Parent(into)!);
            throw;
        }
        if (left != null && TrackName(Name(left))) TryDeleteEmpty(left, stopAt: Parent(left)!);
    }

    /// folder taken away when it is empty (not stopAt, nor anything above it)
    static void TryDeleteEmpty(string folder, string stopAt)
    {
        if (BuildRegistry.SameFolder(folder, stopAt)) return;
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
