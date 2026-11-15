namespace LongshotLauncher.Core;

/// A game folder no build lists (Leftovers.Find): where it is, and the version its stamp names (null: none)
public sealed record Leftover(string Folder, string? Version)
{
    /// The version as players see it, else the folder's name
    public string Shown => Version is { } v ? Versions.Shown(v) : Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
}

/// The old versions' folders an update left on the disk. Before an update stayed in its build's folder, each update built the
/// new version in a new folder named after it, and left the one before with no row in the list: a whole copy of the game
/// (~40 GB) for each update. The home offers to delete them (Keep or Delete), once nothing of the player's is left in them.
public static class Leftovers
{
    /// The game folders directly in buildsFolder that no build lists, with nothing of the player's left in them. Left out:
    ///   a link, or a folder that holds a build's folder or is in one (builds), or the launcher's home (keep)
    ///   one whose user/save still holds a save, or whose user/ or user/save is a link (the player's own arrangement): on Windows
    ///     Saves.Gather takes the old folders' saves into the shared one at the start (renamed save.moved-*), so a folder whose
    ///     saves were not taken in yet stays until they are
    ///   one whose mods/ holds anything: an update in a new folder left the player's mods behind in the old one
    /// (A build that stopped before its first finish left no stamp: no game folder, so not one of these. A finished one keeps
    /// make_game.py's .longshot_exefs and .longshot_romfs, the game files it read, so those say nothing here)
    ///   one the player chose to keep (kept)
    public static IReadOnlyList<Leftover> Find(string buildsFolder, IEnumerable<string> builds, Platform platform,
                                               IEnumerable<string>? kept = null, IEnumerable<string>? keep = null)
    {
        var listed = builds.Concat(keep ?? Array.Empty<string>()).ToList();
        var keptList = (kept ?? Array.Empty<string>()).ToList();
        var found = new List<Leftover>();
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(buildsFolder) ? Directory.EnumerateDirectories(buildsFolder).ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return found; }
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            try
            {
                if (new DirectoryInfo(dir).LinkTarget != null || !GameFolder.IsGameFolder(dir, platform)) continue;
                if (listed.Any(b => BuildRegistry.Holds(dir, b) || BuildRegistry.Holds(b, dir))) continue;
                if (keptList.Any(k => BuildRegistry.SameFolder(k, dir))) continue;
                if (!PlayerLeftNothing(dir)) continue;
                found.Add(new Leftover(dir, GameFolder.Version(dir)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// Nothing of the player's in the folder: no save in user/save, neither user/ nor user/save a link, nothing in mods/
    static bool PlayerLeftNothing(string dir)
    {
        var user = Path.Combine(dir, "user");
        var save = Path.Combine(user, "save");
        if (IsLink(user) || IsLink(save)) return false;
        if (Directory.Exists(save) && Saves.Find(save).Count > 0) return false;   // (only save/: Gather leaves the ones it took in as save.moved-*)
        var mods = Path.Combine(dir, "mods");
        if (IsLink(mods) || Directory.Exists(mods) && Directory.EnumerateFileSystemEntries(mods, "*", SearchOption.AllDirectories)
                                                               .Any(e => File.Exists(e))) return false;
        return true;
    }

    static bool IsLink(string path) => (Directory.Exists(path) || File.Exists(path)) && new DirectoryInfo(path).LinkTarget != null;
}
