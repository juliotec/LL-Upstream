namespace LongshotLauncher.Core;

/// Going back from the nightly launcher to the stable Longshot Launcher. The nightly builds and plays only versions of
/// longshot-builder's kind (OldVersions) and keeps each profile's saves in its own folder (profiles/&lt;p&gt;/saves); the stable
/// launcher plays the versions built by make_game.py, which read their saves from the --user folder every version shares
/// (SaveEnvironment.LongshotUser), and never runs a version built by longshot-builder. Switching the launcher's own channel from
/// nightly to stable is therefore offered as one step: replace this launcher with the newest stable one (its own update, reversed:
/// SelfUpdate's download, check and swap), the current profile's saves copied back to the --user folder first when the player
/// wants them there (SavesCopyBack)
public static class StableSwitch
{
    /// Whether a change of the launcher's channel offers the replacement: only nightly to stable
    public static bool Offers(string from, string to) => from == Versions.Nightly && to == Versions.Stable;
}

/// A profile's saves (its saves/ folder's save/ and save-work/, ProfileMigration.SaveParts) copied back to the --user folder the
/// move to profile folders copied them from, where the stable launcher's versions read them. A copy: the profile's stay where they
/// are. A file there already is replaced only when it is older than the profile's; one that is newer there (played since by an
/// older launcher or version) is kept unless the player says to replace it too (Copy's replaceNewer); one that is the same is left.
/// Paths are relative to the two folders, '/' between their parts
public sealed record SavesCopyBack(string From, string To, IReadOnlyList<string> New, IReadOnlyList<string> Older, IReadOnlyList<string> Newer)
{
    /// Nothing to copy: the profile has no save, or the --user folder has every file as it is
    public bool Empty => New.Count == 0 && Older.Count == 0 && Newer.Count == 0;

    /// What copying from (a profile's saves folder: ProfileStore.SavesOf) to to (SaveEnvironment.LongshotUser) would do: each
    /// file of its save parts new there, older there (replaced), or newer there (kept unless asked). A link is never followed
    public static SavesCopyBack Plan(string from, string to)
    {
        List<string> fresh = [], older = [], newer = [];
        foreach (var part in ProfileMigration.SaveParts)
        {
            var root = new DirectoryInfo(Path.Combine(from, part));
            if (!root.Exists || root.LinkTarget != null) continue;
            foreach (var file in Files(root))
            {
                var rel = Path.GetRelativePath(from, file.FullName).Replace('\\', '/');
                var there = new FileInfo(Path.Combine(to, Native(rel)));
                if (!there.Exists) fresh.Add(rel);
                else if (there.LinkTarget != null || Same(file, there)) continue;
                else if (there.LastWriteTimeUtc > file.LastWriteTimeUtc) newer.Add(rel);
                else older.Add(rel);
            }
        }
        return new SavesCopyBack(from, to, fresh, older, newer);
    }

    /// The copy: the new files and the older ones replaced, and the newer ones too only with replaceNewer; each written beside
    /// itself first and moved in place, keeping the profile's write time. The files copied
    public int Copy(bool replaceNewer)
    {
        var copied = 0;
        foreach (var rel in New.Concat(Older).Concat(replaceNewer ? Newer : []))
        {
            var source = Path.Combine(From, Native(rel));
            var target = Path.Combine(To, Native(rel));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            File.Copy(source, part, overwrite: true);
            File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(source));
            File.Move(part, target, overwrite: true);
            copied++;
        }
        return copied;
    }

    static string Native(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    /// Every file under a folder, never through a link
    static IEnumerable<FileInfo> Files(DirectoryInfo dir)
    {
        foreach (var entry in dir.EnumerateFileSystemInfos())
            if (entry.LinkTarget != null) continue;
            else if (entry is DirectoryInfo sub) foreach (var f in Files(sub)) yield return f;
            else if (entry is FileInfo f) yield return f;
    }

    static bool Same(FileInfo a, FileInfo b)
    {
        if (a.Length != b.Length) return false;
        try { return File.ReadAllBytes(a.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(b.FullName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
