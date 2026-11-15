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
                var version = GameFolder.Version(dir);
                if (NewestOfItsChannel(dir, version, listed, platform)) continue;
                found.Add(new Leftover(dir, version));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// A game no listed build matches: it plays, and no listed build that plays has its channel's version as new as it. Not an
    /// old version an update left but the newest game of its channel, which an older launcher's update built in a new folder and
    /// whose row did not follow it (LostUpdate), or whose row went: never offered for deletion
    static bool NewestOfItsChannel(string dir, string? version, IEnumerable<string> listed, Platform platform)
    {
        if (version == null || !GameFolder.IsBuilt(dir, platform)) return false;
        return !listed.Where(b => GameFolder.IsBuilt(b, platform)).Select(GameFolder.Version)
                      .Any(v => v != null && OnChannel(v, Versions.TrackOf(version)) && !Versions.Newer(version, v));
    }

    /// version is one a build on channel follows: its own track, or a stable on the rc channel (which follows the newest of both)
    static bool OnChannel(string version, string channel) =>
        Versions.TrackOf(version) == channel || channel == Versions.Candidate && Versions.TrackOf(version) == Versions.Stable;

    /// The game an update built for row that row lost: before an update stayed in its build's folder (#104), it built the new
    /// version in a new folder of the builds folder and moved its row there at the end; when the row did not follow (the launcher
    /// closed, or builds.json was not written), the row kept the old folder and the new game stayed unlisted. Seen as a row whose
    /// folder has no game to play, and an unlisted game folder directly in buildsFolder that plays, on the row's channel and newer
    /// than its version: the newest of them, which the row takes back (BuildRegistry.Replace). Null when the row plays, or none is
    /// there. listed: the builds' folders and the launcher's home, as Find's
    public static Leftover? LostUpdate(string buildsFolder, BuildEntry row, IEnumerable<string> listed, Platform platform)
    {
        if (GameFolder.IsBuilt(row.Folder, platform)) return null;
        var channel = UpdatePolicy.ChannelOf(row.Channel, row.Version);
        var all = listed.ToList();
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(buildsFolder) ? Directory.EnumerateDirectories(buildsFolder).ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        Leftover? best = null;
        foreach (var dir in dirs)
        {
            try
            {
                if (new DirectoryInfo(dir).LinkTarget != null || !GameFolder.IsBuilt(dir, platform)) continue;
                if (all.Any(b => BuildRegistry.Holds(dir, b) || BuildRegistry.Holds(b, dir))) continue;
                if (GameFolder.Version(dir) is not { } v || !OnChannel(v, channel) || !Versions.Newer(v, row.Version)) continue;
                if (best == null || Versions.Newer(v, best.Version)) best = new Leftover(dir, v);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return best;
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
