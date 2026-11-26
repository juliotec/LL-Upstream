namespace LongshotLauncher.Core;

/// The installed versions, one list per launcher. The stable Longshot Launcher (1.x, make_game.py's versions) and this one, the
/// nightly from the clean break (longshot-builder's), use the same data folder. They kept one list, builds.json, so each listed
/// the other's versions as installed: the stable launcher offered Play on versions it can't run. Now this launcher keeps its own,
/// versions.json, and builds.json is the stable launcher's alone. A launcher before the split put its rows in builds.json: they
/// move once, at the first start with no versions.json (Split). This launcher reads builds.json only to leave its folders
/// alone (never offered as leftovers to delete) and to say that the stable launcher has versions
public static class VersionLists
{
    public const string OwnFile = "versions.json", StableFile = "builds.json";

    /// Whether a row of builds.json is this launcher's: built by longshot-builder, known for sure (in a version folder of
    /// launcherData, marked as the builder's by its row, or holding the builder's record). One that can't be told (its folder gone,
    /// a pull request built with make_game.py) stays the stable launcher's
    public static bool Ours(BuildEntry entry, string launcherData)
    {
        try
        {
            return BuilderVersions.InVersions(entry.Folder, launcherData) || entry.NupInPlace == true
                   || LongshotBuilder.RecordFiles.Any(f => File.Exists(Path.Combine(entry.Folder, f)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// The split, once: with no versions.json yet, builds.json's rows that are this launcher's (Ours) move to versions.json; the
    /// others stay. versions.json is written first, so a stop between the two saves leaves a row in both lists, never in neither
    /// (the next start finds versions.json and does nothing more; the stable launcher refuses that row, as #272 does). An unreadable
    /// builds.json is left as it is. The rows moved
    public static int Split(string launcherData, Func<DateTimeOffset>? now = null)
    {
        var own = Path.Combine(launcherData, OwnFile);
        var stablePath = Path.Combine(launcherData, StableFile);
        if (File.Exists(own) || !File.Exists(stablePath)) return 0;
        var rows = BuildRegistry.Peek(stablePath);
        var ours = rows.Where(b => Ours(b, launcherData)).ToList();
        var mine = BuildRegistry.Load(own, now);
        foreach (var b in ours) mine.Take(b);
        mine.Save();
        if (ours.Count == 0)
        {
            LauncherLog.Add($"versions: {OwnFile} started; {StableFile} has none of this launcher's versions, so it stays the stable launcher's as it is");
            return 0;
        }
        var stable = BuildRegistry.Load(stablePath, now);
        foreach (var b in ours) stable.Remove(b.Id);
        stable.Save();
        LauncherLog.Add($"versions: {ours.Count} of {rows.Count} versions in {StableFile} were built by longshot-builder: moved to {OwnFile}; the others stay the stable launcher's");
        return ours.Count;
    }

    /// The stable launcher's versions' folders (builds.json, read only): none when there is no list or it can't be read
    public static IReadOnlyList<string> StableFolders(string launcherData) =>
        BuildRegistry.Peek(Path.Combine(launcherData, StableFile)).Select(b => b.Folder).ToList();
}
