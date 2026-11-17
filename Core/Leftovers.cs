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
    ///   a link, or a folder that holds a build's folder or is in one (builds), or that holds the launcher's home (keep; a
    ///     portable launcher's builds folder is in its home, so a folder in it is no reason)
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
        var listed = builds.ToList();
        return Unlisted(buildsFolder, listed, (keep ?? Array.Empty<string>()).ToList(), platform, (kept ?? Array.Empty<string>()).ToList(),
                        dir => GameFolder.IsGameFolder(dir, platform) && PlayerLeftNothing(dir)
                               && !NewestOfItsChannel(dir, GameFolder.Version(dir), listed, platform))
               .Select(f => new Leftover(f.Dir, f.Version)).ToList();
    }

    /// The folders an instance can be in buildsFolder (right in it, and in its tracks' folders: builds/nightly/&lt;version&gt;,
    /// InstanceFolders.Folders), in ordinal order, with the version their stamp names: no link, no listed build's folder nor one
    /// holding or in one, none holding the launcher's home (homes), none the player kept (kept), and passing want. One that cannot
    /// be read is left out. The one scan Find, LostUpdate and Relist share
    static IEnumerable<(string Dir, string? Version)> Unlisted(string buildsFolder, IReadOnlyList<string> listed, IReadOnlyList<string> homes,
                                                               Platform platform, IReadOnlyList<string> kept, Func<string, bool> want)
    {
        foreach (var dir in InstanceFolders.Folders(buildsFolder, platform).Order(StringComparer.Ordinal))
        {
            string? version;
            try
            {
                if (new DirectoryInfo(dir).LinkTarget != null || Taken(dir, listed, homes)) continue;
                if (kept.Any(k => BuildRegistry.SameFolder(k, dir)) || !want(dir)) continue;
                version = GameFolder.Version(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            yield return (dir, version);
        }
    }

    /// Whether the home lists again the games whose row went (Relist). A recovery, once: when the list was not there (the first
    /// start, or a builds.json that was lost), and once on a launcher that never ran it (#141: rows a portable launcher lost
    /// before #153). Never at every start, which would undo the player's own removals (Delete, a folder deleted by hand), and
    /// never when the list could not be read: listing games in it would write builds.json over the one set aside as
    /// builds.json.broken (a newer launcher's list, or a damaged one)
    public static bool ShouldRelist(BuildRegistry registry, bool relistedBefore) =>
        registry.Problem == null && (!registry.Existed || !relistedBefore);

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
    /// there. listed: the builds' folders; keep: the launcher's home, as Find's
    public static Leftover? LostUpdate(string buildsFolder, BuildEntry row, IEnumerable<string> listed, Platform platform, IEnumerable<string>? keep = null)
    {
        if (GameFolder.IsBuilt(row.Folder, platform)) return null;
        var channel = UpdatePolicy.ChannelOf(row.Channel, row.Version);
        Leftover? best = null;
        foreach (var (dir, version) in Unlisted(buildsFolder, listed.ToList(), (keep ?? Array.Empty<string>()).ToList(), platform,
                                                Array.Empty<string>(), d => GameFolder.IsBuilt(d, platform)))
        {
            if (version is not { } v || !OnChannel(v, channel) || !Versions.Newer(v, row.Version)) continue;
            if (best == null || Versions.Newer(v, best.Version)) best = new Leftover(dir, v);
        }
        return best;
    }

    /// The game folders in buildsFolder (in it, and in its tracks' folders: builds/nightly/&lt;version&gt;, InstanceFolders.Folders) whose row
    /// went, listed again in registry (issue #141: a portable launcher's rows
    /// dropped by ForgetGone after its folder was renamed, before FollowHome, its games still in its builds folder; or a list lost
    /// with builds.json). Each one that plays, with a version in its stamp, that is no listed build's folder nor holds one or the
    /// launcher's home (keep; a portable launcher's builds folder is in its home: no reason), nor kept by the player when the home
    /// offered it for deletion (kept), whose version is a release or a nightly, and that is its channel's newest that no listed
    /// build plays nor any row of its channel has (its game there or not), each row on its own channel. Of two on one channel, the
    /// newer: the older stays unlisted, an old version Find offers as before. Its channel: the one its folder's name says
    /// (InstanceFolders.ChannelNamed: builds/rc/&lt;version&gt;, builds/rc, builds/rc (2)) when its version is one that channel follows,
    /// else its version's. In the order of Versions.Channels (stable, release candidate, nightly), so that the first, the default
    /// when the list has none, is a release; the default the list has stays. A row whose folder was deleted outside the launcher
    /// (BuildRegistry.Gone: ForgetGone drops it at the home's Refresh) counts as gone already, so the result does not depend on
    /// which ran first. The entries added; registry is not saved. Run it when ShouldRelist says so, not at every start
    public static IReadOnlyList<BuildEntry> Relist(BuildRegistry registry, string buildsFolder, Platform platform, IEnumerable<string>? keep = null,
                                                   IEnumerable<string>? kept = null)
    {
        var found = Unlisted(buildsFolder, registry.Builds.Select(b => b.Folder).ToList(), (keep ?? Array.Empty<string>()).ToList(), platform,
                             (kept ?? Array.Empty<string>()).ToList(), d => GameFolder.IsBuilt(d, platform))
            .Where(f => f.Version is { } v && (Versions.IsRelease(v) || Versions.TrackOf(v) == Versions.Nightly))
            .Select(f => (f.Dir, Version: f.Version!, Channel: ChannelOf(f.Dir, f.Version!, buildsFolder)))
            // (the newest first within a channel, so that an older one meets the newer one already listed)
            .OrderBy(f => Rank(f.Channel)).ThenByDescending(f => f.Version, Comparer<string>.Create(Versions.Compare))
            .ThenBy(f => f.Dir, StringComparer.Ordinal).ToList();
        // (every row, its game there or not: a row on an unplugged drive, or whose game an antivirus took, never brings an older
        // one back beside it; read once, and the ones listed here join it)
        var rows = registry.Builds.Where(b => !BuildRegistry.Gone(b.Folder))
                                  .Select(b => (Channel: UpdatePolicy.ChannelOf(b.Channel, b.Version), b.Version)).ToList();
        var added = new List<BuildEntry>();
        foreach (var (dir, version, channel) in found)
        {
            if (rows.Any(r => r.Channel == channel && r.Version is { } rv && !Versions.Newer(version, rv))) continue;
            added.Add(registry.Add(dir, version, channel));
            rows.Add((channel, version));
        }
        return added;
    }

    /// The channel a game folder was built on: the one its name says (InstanceFolders.ChannelNamed) when its version is one that
    /// channel follows, else its version's
    static string ChannelOf(string dir, string version, string buildsFolder) =>
        InstanceFolders.ChannelNamed(dir, buildsFolder) is { } named && OnChannel(version, named) ? named : Versions.TrackOf(version);

    /// A channel's place in Versions.Channels (stable, release candidate, nightly)
    static int Rank(string channel)
    {
        for (var i = 0; i < Versions.Channels.Count; i++) if (Versions.Channels[i] == channel) return i;
        return Versions.Channels.Count;
    }

    /// dir is a build's folder, holds one or is in one (builds), or holds the launcher's home (homes: one way only, a portable
    /// launcher's builds folder being in its home)
    static bool Taken(string dir, IReadOnlyList<string> builds, IReadOnlyList<string> homes) =>
        builds.Any(b => BuildRegistry.Holds(dir, b) || BuildRegistry.Holds(b, dir)) || homes.Any(h => BuildRegistry.Holds(dir, h));

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
