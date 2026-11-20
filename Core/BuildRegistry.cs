using System.Globalization;
using System.Text.Json;

namespace LongshotLauncher.Core;

/// One build in the launcher's list: a game folder, the version it was built with, the channel its updates follow
public sealed class BuildEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? Version { get; set; }
    public string Channel { get; set; } = Versions.Stable;
    public bool Default { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    /// What the launcher built it with (null for a build it did not make: a 2.x folder handed over), so that its update can be
    /// built from the same game files and the home can say what it has
    public string? Nup { get; set; }
    public string? Romfs { get; set; }
    public string? Exefs { get; set; }
    public bool? Dlss { get; set; }
    public bool? Fsr { get; set; }
    /// Built with Intel's XeSS library fetched into its folder (null: not by this launcher, or before it could)
    public bool? Xess { get; set; }
    /// Its folder's size in bytes when it was last measured; null: not yet
    public long? Bytes { get; set; }
    /// Of Bytes, measured with the other builds' folders (Disk.Measure): its share of what they take on the disk, a game file
    /// hard-linked into several builds counted once (the home's total), and what deleting it frees, its files with no link
    /// outside its folder (another build of the same .nup may share its game files: BuildRegistry.SameGameFiles); null: not yet
    public long? Counted { get; set; }
    public long? Frees { get; set; }

    /// Measured again: its folder changed
    public void Unmeasured() => Bytes = Counted = Frees = null;
}

public sealed class RegistryFile
{
    public int Format { get; set; } = 1;
    /// (issue 140) The launcher's home when the list was saved, for FollowHome; null in a list saved before it (an older launcher
    /// reads past it)
    public string? Home { get; set; }
    public List<BuildEntry> Builds { get; set; } = new();
}

/// &lt;home&gt;/builds.json, the launcher's list of builds (the launcher alone writes it). Written whole
/// (AtomicFile); exactly one build is the default whenever the list is not empty. One entry per folder.
public sealed class BuildRegistry
{
    readonly string path;
    readonly List<BuildEntry> builds;
    readonly Func<DateTimeOffset> now;
    readonly string? savedHome;

    BuildRegistry(string path, List<BuildEntry> builds, Func<DateTimeOffset>? now, bool existed, string? problem, string? savedHome = null)
    {
        this.path = path;
        this.savedHome = savedHome;
        this.builds = builds;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        Existed = existed;
        Problem = problem;
        Normalize();
    }

    /// builds.json was there when it was loaded (false: no file yet, the launcher's first start)
    public bool Existed { get; }

    /// Why the file could not be read (it was set aside as builds.json.broken); null when it was fine or absent
    public string? Problem { get; }

    public IReadOnlyList<BuildEntry> Builds => builds;

    public BuildEntry? Default => builds.FirstOrDefault(b => b.Default);

    public static BuildRegistry Load(string path, Func<DateTimeOffset>? now = null)
    {
        if (!File.Exists(path)) return new BuildRegistry(path, new List<BuildEntry>(), now, false, null);
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.RegistryFile)
                       ?? throw new JsonException("builds.json is empty");
            if (file.Format != 1) throw new JsonException($"builds.json has format {file.Format}, which this launcher does not know");
            var list = file.Builds.Where(b => b != null && b.Folder.Length > 0).ToList();
            foreach (var b in list)
            {
                if (b.Id.Length == 0) b.Id = NewId();
                if (!Versions.IsChannel(b.Channel)) b.Channel = Versions.TrackOf(b.Version);
            }
            return new BuildRegistry(path, list, now, true, null, file.Home);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // (kept beside it rather than lost: a newer launcher's list, or a broken disk, is not overwritten by an empty one)
            string kept;
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
                kept = "kept as " + path + ".broken";
            }
            catch (Exception c) { kept = "not kept aside: " + c.Message; }
            LauncherLog.Add($"builds.json could not be read ({e.GetType().Name}: {e.Message}): {kept}; the list starts empty");
            return new BuildRegistry(path, new List<BuildEntry>(), now, true, e.Message);
        }
    }

    /// The builds another launcher's builds.json lists, read only (nothing written beside it, as Load does with a broken one):
    /// none when it is absent or cannot be read (OtherLauncher)
    public static IReadOnlyList<BuildEntry> Peek(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.RegistryFile);
            return file is { Format: 1, Builds: { } list } ? list.Where(b => b?.Folder is { Length: > 0 }).ToList() : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
    }

    static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static bool SameFolder(string a, string b)
    {
        static string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { }
            return Path.TrimEndingDirectorySeparator(p);
        }
        // (a Windows path read on Linux, as in a test of Windows paths, compares as Windows compares it)
        var windows = OperatingSystem.IsWindows() || a.Contains('\\') || b.Contains('\\');
        return string.Equals(Norm(a), Norm(b), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// (issue 140) A portable launcher's folder renamed or moved, or a folder above it: the list kept the paths it had, so its builds
    /// in the launcher's folder were "not built: its game is missing" until the old name came back. A path that was in the folder
    /// the launcher and its data are in (the home's parent) is taken to the same place in that folder's new path, when it is not on
    /// the disk and the new one is (a copy of the launcher's folder whose first is still there keeps the first's paths). The home
    /// before: the one the list was saved in (Home); in a list saved before that, the path up to its last folder of the home's
    /// name (LongshotLauncher-data). True when a path changed (Save keeps it)
    public bool FollowHome()
    {
        var here = Path.GetDirectoryName(Path.GetFullPath(path));
        if (here == null) return false;
        var changed = false;
        foreach (var b in builds)
        {
            var was = savedHome ?? HomeIn(b.Folder, Path.GetFileName(here));
            if (was == null || SameFolder(was, here)) continue;
            string? Moved(string? p) => Rebased(p, Path.GetDirectoryName(was), Path.GetDirectoryName(here));
            if (Moved(b.Folder) is { } folder) { b.Folder = folder; changed = true; }
            if (Moved(b.Nup) is { } nup) { b.Nup = nup; changed = true; }
            if (Moved(b.Romfs) is { } romfs) { b.Romfs = romfs; changed = true; }
            if (Moved(b.Exefs) is { } exefs) { b.Exefs = exefs; changed = true; }
        }
        return changed;
    }

    /// The last folder named name that holds path (a home of that name), or null
    static string? HomeIn(string path, string name)
    {
        try
        {
            for (var d = Path.GetDirectoryName(Path.GetFullPath(path)); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                if (string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase)) return d;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    /// path, in from, at the same place in to: when path is not on the disk and that is; null otherwise
    static string? Rebased(string? path, string? from, string? to)
    {
        if (path is not { Length: > 0 } || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            var rest = Path.GetRelativePath(from, full);
            if (rest == "." || rest == ".." || rest.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rest))
                return null;
            var moved = Path.Combine(to, rest);
            return !Path.Exists(full) && Path.Exists(moved) ? moved : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public BuildEntry? Find(string id) => builds.FirstOrDefault(b => b.Id == id);

    public BuildEntry? FindByFolder(string folder) => builds.FirstOrDefault(b => SameFolder(b.Folder, folder));

    /// Another build made from this .nup, whose game files a new build of it can take by hard links (make_game's
    /// LONGSHOT_ROMFS_FROM): one whose folder has what make_game's romfs_from_build wants before it links, its romfs/, its
    /// kept .longshot_exefs/ and a .longshot_romfs stamp of this .nup (UnpackStamp), so that a folder make_game would pass
    /// over (and the whole .nup read again) is not chosen while another would do. That stamp, the .nup's name and size, is
    /// all romfs_from_build compares, so the build's own record of where its .nup was (BuildEntry.Nup) is not: a .nup moved
    /// or copied to another folder still shares, as does a build listed again without that record. Of those, one on the new
    /// build's drive first (a hard link does not cross drives: make_game would copy the ~25 GB), then the one updated last;
    /// never the new build's own folder; null when there is none, the .nup is not there, or the new build's folder has them
    /// already (an update in its own folder: BuildRegistry.FolderOf). driveOf: the tests' stand-in for Disk.DriveName
    public BuildEntry? SameGameFiles(string nup, string folder, Func<string, string>? driveOf = null)
    {
        if (UnpackStamp(nup) is not { } stamp || Unpacked(folder, stamp)) return null;
        driveOf ??= Disk.DriveName;
        var drive = driveOf(folder);
        var drives = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return builds.Where(b => !SameFolder(b.Folder, folder) && Unpacked(b.Folder, stamp))
                     .OrderByDescending(b => drives.Equals(driveOf(b.Folder), drive))
                     .ThenByDescending(b => b.Updated).FirstOrDefault();
    }

    /// The stamp make_game writes in .longshot_romfs for a romfs it unpacked from nup ("unpacked by this builder: <name> <size>",
    /// make_game.py's main), which romfs_from_build compares with the other folder's; null when the .nup cannot be read
    public static string? UnpackStamp(string nup)
    {
        try
        {
            var file = new FileInfo(nup);
            return file.Exists ? $"unpacked by this builder: {Path.GetFileName(nup)} {file.Length.ToString(CultureInfo.InvariantCulture)}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// folder holds a romfs make_game unpacked from the .nup of stamp, with the exefs it kept (romfs_from_build's checks)
    static bool Unpacked(string folder, string stamp)
    {
        try
        {
            var mark = Path.Combine(folder, ".longshot_romfs");
            return Directory.Exists(Path.Combine(folder, "romfs")) && Directory.Exists(Path.Combine(folder, ".longshot_exefs"))
                   && File.Exists(mark) && File.ReadAllText(mark).Trim() == stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// A folder added, or the one already listed brought up to date (its id, name and default kept). makeDefault: it becomes the
    /// default; the first build is the default anyway
    public BuildEntry Add(string folder, string? version, string? channel, string? name = null, bool makeDefault = false)
    {
        var at = now();
        var entry = FindByFolder(folder);
        if (entry == null)
        {
            entry = new BuildEntry
            {
                Id = NewId(),
                Folder = folder,
                Name = name ?? DefaultName(folder),
                Created = at,
            };
            builds.Add(entry);
        }
        else if (name != null) entry.Name = name;
        entry.Version = version ?? entry.Version;
        entry.Channel = Versions.IsChannel(channel) ? channel! : Versions.TrackOf(entry.Version);
        entry.Updated = at;
        if (makeDefault) SetDefault(entry.Id);
        Normalize();
        return entry;
    }

    static string DefaultName(string folder)
    {
        var trimmed = folder.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length > 0 ? name : "Longshot";
    }

    /// The build out of the list (its folder stays on the disk: DeleteFolder is the other, explicit, step). When it was the
    /// default, the first one left becomes it
    public bool Remove(string id)
    {
        var entry = Find(id);
        if (entry == null) return false;
        builds.Remove(entry);
        Normalize();
        return true;
    }

    /// The builds whose folder was deleted outside the launcher, out of the list (as Remove), and returned. Gone: the folder is
    /// not there while the folder that held it is, so a build on a drive that is unplugged, or a network share that is not
    /// connected, keeps its row until it is back. keep: rows left as they are (one the launcher is deleting itself)
    public IReadOnlyList<BuildEntry> ForgetGone(IReadOnlyCollection<string>? keep = null)
    {
        var gone = builds.Where(b => keep?.Contains(b.Id) != true && Gone(b.Folder)).ToList();
        foreach (var b in gone)
        {
            builds.Remove(b);
            LauncherLog.Add($"build {b.Version ?? "(no version)"} dropped from the list: its folder is gone: {b.Folder}");
        }
        if (gone.Count > 0) Normalize();
        return gone;
    }

    /// folder is not on the disk, and the folder it was in is
    public static bool Gone(string folder)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (Path.Exists(full)) return false;
            return Path.GetDirectoryName(full) is { Length: > 0 } parent && Directory.Exists(parent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// Where a build is made. A new build: NewFolder, builds/&lt;track&gt;/&lt;version&gt;. An update, a go-back or a rebuild (replaces:
    /// the build it takes the place of) is made in that build's own folder, so its settings, mods and shader caches stay with the
    /// game and no folder of the version before is left on the disk; when that folder is one the launcher made in this layout
    /// (InstanceFolders.InLayout) and named after another version, it is that folder renamed to the new version's name
    /// (builds/nightly/nightly-171-â€¦ to builds/nightly/nightly-175-â€¦), renamed when the build starts (InstanceFolders.Rename) and
    /// back if it does not finish. A folder the player chose, or one from before this layout, keeps its name
    public string FolderOf(BuildEntry? replaces, string buildsFolder, string channel, string? version, Platform platform)
    {
        if (replaces == null) return NewFolder(buildsFolder, channel, version, platform);
        if (!InstanceFolders.InLayout(replaces.Folder, buildsFolder) || InstanceFolders.NamedFor(replaces.Folder, channel, version))
            return replaces.Folder;
        // (a rename never goes onto anything on the disk, nor into another build's folder)
        return LauncherSettings.FolderFor(buildsFolder, channel, version,
                                          p => !SameFolder(p, replaces.Folder) && (Path.Exists(p) || FindByFolder(p) != null),
                                          t => NotTrack(t, platform));
    }

    /// A new build's folder in parent (LauncherSettings.FolderFor: builds/&lt;track&gt;/&lt;version&gt;), past the folders other builds have
    /// and past anything on the disk that is not a game folder. A game folder no build lists (its row was removed, or the list was
    /// lost), or one a build stopped in, is built in again, as one the player picks is. replaces: the build this one takes the
    /// place of, whose own folder is not another build's
    public string NewFolder(string parent, string channel, string? version, Platform platform, BuildEntry? replaces = null) =>
        LauncherSettings.FolderFor(parent, channel, version,
                                   p => FindByFolder(p) is { } e ? e.Id != replaces?.Id : HoldsOtherThings(p, platform),
                                   t => NotTrack(t, platform));

    /// A track's folder that cannot hold instances: a file, a game folder (an instance from before this layout was made right in
    /// the builds folder, named after its track: builds/nightly), or a listed build's own folder
    bool NotTrack(string track, Platform platform) =>
        File.Exists(track) || FindByFolder(track) != null || Directory.Exists(track) && GameFolder.IsGameFolder(track, platform);

    /// make_game.py's copy of the game files read into a game folder, there long before its stamp and game (a build that stopped
    /// while it compiled): such a folder is built in again, and the next build skips reading the game files
    static readonly string[] StartedMarks = [".longshot_exefs", ".longshot_romfs"];

    static bool HoldsOtherThings(string path, Platform platform)
    {
        if (File.Exists(path)) return true;
        if (!Directory.Exists(path) || GameFolder.IsGameFolder(path, platform)) return false;
        if (StartedMarks.Any(m => Path.Exists(Path.Combine(path, m)))) return false;
        try { return Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// An update takes the row of the build it updates (old): its id, its place in the list and whether it is the default stay;
    /// its version and channel are the new build's. It is built in its build's own folder (FolderOf), renamed to the new
    /// version's name when it is in the builds folder's layout (InstanceFolders.Rename, before the build: folder is that name).
    /// When the player chose another folder for it, the row moves there (a row that already lists that folder goes: one entry
    /// per folder) and the old folder stays on the disk. A row named after its version is named after the new one
    public BuildEntry Replace(string oldId, string folder, string? version, string? channel)
    {
        var entry = Find(oldId) ?? throw new KeyNotFoundException("No build " + oldId);
        if (FindByFolder(folder) is { } other && other != entry)
        {
            builds.Remove(other);
            if (other.Default) entry.Default = true;
        }
        if (version != null && entry.Name == entry.Version) entry.Name = version;
        else if (!SameFolder(entry.Folder, folder)) entry.Name = DefaultName(folder);
        entry.Folder = folder;
        entry.Version = version ?? entry.Version;
        entry.Channel = Versions.IsChannel(channel) ? channel! : Versions.TrackOf(entry.Version);
        entry.Updated = now();
        entry.Unmeasured();
        Normalize();
        return entry;
    }

    public void SetDefault(string id)
    {
        if (Find(id) == null) throw new KeyNotFoundException("No build " + id);
        foreach (var b in builds) b.Default = b.Id == id;
    }

    /// A build's folder renamed on the disk (InstanceFolders.Rename): its row follows it, all else stays (its name, version,
    /// default, measurements: the same files). The game files any row was made from that were in it follow it too
    /// (InstanceFolders.Follow: an update's picked romfs and exefs in the folder it renames)
    public void Moved(string id, string folder)
    {
        var entry = Find(id) ?? throw new KeyNotFoundException("No build " + id);
        var from = entry.Folder;
        entry.Folder = folder;
        foreach (var b in builds)
        {
            b.Nup = InstanceFolders.Follow(b.Nup, from, folder);
            b.Romfs = InstanceFolders.Follow(b.Romfs, from, folder);
            b.Exefs = InstanceFolders.Follow(b.Exefs, from, folder);
        }
    }

    public void SetChannel(string id, string channel)
    {
        if (!Versions.IsChannel(channel)) throw new ArgumentException("Not a channel: " + channel, nameof(channel));
        (Find(id) ?? throw new KeyNotFoundException("No build " + id)).Channel = channel;
    }

    /// Exactly one default when the list is not empty: the first marked one, else the first
    void Normalize()
    {
        var chosen = builds.FirstOrDefault(b => b.Default) ?? builds.FirstOrDefault();
        foreach (var b in builds) b.Default = ReferenceEquals(b, chosen);
    }

    public void Save()
    {
        Normalize();
        AtomicFile.Write(path, JsonSerializer.Serialize(new RegistryFile { Home = Path.GetDirectoryName(Path.GetFullPath(path)), Builds = builds }, CoreJson.Default.RegistryFile));
    }

    /// A build's game folder deleted from the disk: only a folder make_game.py made (its stamp or bin/longshot), never another
    /// folder a list entry might name. Not a link either (deleting it would free nothing), nor the home folder or one that holds
    /// it, nor one that holds a folder of keep: the other builds and the launcher's home. (A 2.x install in ~/Games/Longshot holds
    /// the launcher's own builds, ~/Games/Longshot/&lt;channel&gt;, while that is the builds folder.) A save the folder still holds in
    /// its user\save (Saves.SaveIn: a Windows build started outside the launcher saves there) is never deleted with it: it is
    /// copied whole to saves' backups first and the copy checked (Saves.BackUpForDelete), and the folder stays when that fails, or
    /// when no saves are given; nor is a folder that holds the shared saves deleted. Returns where that copy is (null: there was
    /// no save in it). The entry itself is removed with Remove
    public static string? DeleteFolder(string folder, Platform platform, IEnumerable<string>? keep = null, Func<string, string?>? getenv = null,
                                       SaveEnvironment? saves = null, Func<DateTime>? now = null)
    {
        try
        {
            var backup = DeleteGameFolder(folder, platform, keep, getenv, saves, now);
            LauncherLog.Add("build folder deleted: " + folder + (backup != null ? $"; the save it held is backed up in {backup}" : ""));
            return backup;
        }
        catch (Exception e)
        {
            LauncherLog.Add($"build folder not deleted: {folder}: {e.GetType().Name}: {e.Message}"
                            + (e is FolderNotDeleted { Backup: { } b } ? $"; the save it held is backed up in {b}" : ""));
            throw;
        }
    }

    static string? DeleteGameFolder(string folder, Platform platform, IEnumerable<string>? keep, Func<string, string?>? getenv,
                                    SaveEnvironment? saves, Func<DateTime>? now)
    {
        var full = MayDelete(folder, platform, keep, getenv, saves);
        string? backup = null;
        if (Saves.SaveIn(full) is { } save)
        {
            if (saves == null)
                throw new IOException($"This folder still holds a save, in {save}, so the launcher does not delete it: {folder}");
            try { backup = Saves.BackUpForDelete(saves, full, save, now); }
            catch (SaveFailure e) { throw new IOException(e.Message, e); }
        }
        // (a delete that stops half-way, a file in use: the copy of the save is named still, as it may be gone from the folder now)
        try { Toolchains.DeleteTree(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new FolderNotDeleted(e.Message, backup, e); }
        return backup;
    }

    /// DeleteFolder's refusals, before anything is touched: the full path of a folder it may delete, else IOException, why not
    static string MayDelete(string folder, Platform platform, IEnumerable<string>? keep, Func<string, string?>? getenv, SaveEnvironment? saves)
    {
        var full = Path.GetFullPath(folder);
        if (Path.GetPathRoot(full) == full) throw new IOException("Will not delete a drive's root: " + folder);
        if (!GameFolder.IsGameFolder(full, platform))
            throw new IOException("This folder is not a Longshot game folder, so the launcher does not delete it: " + folder);
        if (new DirectoryInfo(full).LinkTarget != null)
            throw new IOException("This folder is a link to another one, so the launcher does not delete it: " + folder);
        if (Holds(full, LauncherHome.Home(getenv ?? Environment.GetEnvironmentVariable)))
            throw new IOException("This folder holds your home folder, so the launcher does not delete it: " + folder);
        if ((keep ?? Array.Empty<string>()).FirstOrDefault(k => Holds(full, k)) is { } other)
            throw new IOException($"This folder holds {other}, which the launcher keeps, so it does not delete it: {folder}");
        // (on Linux the root given as --user and the user/ in it a newer Longshot keeps its files in: SaveEnvironment.LongshotUser)
        if (saves != null && new[] { saves.LongshotRoot, saves.LongshotUser }.FirstOrDefault(u => Holds(Saves.Real(full), Saves.Real(u))) is { } held)
            throw new IOException($"This folder holds {held}, Longshot's saves, so the launcher does not delete it: {folder}");
        return full;
    }

    /// What the home's Delete does with a version's game folder (DeleteFolder), the save the folder holds itself first: on
    /// Windows brought into the shared one (Saves.Gather on that folder alone, backed up; a build started outside the launcher
    /// saves there, and the gather at the launcher's start may have been skipped or have failed), and whatever is still there
    /// then (the gather failed, could not rename it, or Linux, where nothing is gathered) copied to the backups before the
    /// folder goes. running: what runs now (Saves.Running)
    public static DeletedGame DeleteGame(SaveEnvironment saves, string folder, Platform platform, IEnumerable<string>? keep,
                                         Func<IReadOnlyList<string>> running, Func<DateTime>? now = null, Func<string, string?>? getenv = null)
    {
        // (a folder DeleteFolder refuses is left as it was: its save not gathered, nor renamed aside)
        try { MayDelete(folder, platform, keep, getenv, saves); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Add($"build folder not deleted: {folder}: {e.GetType().Name}: {e.Message}");
            return new DeletedGame(folder, false, null, null, null, e.Message);
        }
        SaveGathered? gathered = null;
        string? gatherProblem = null;
        if (saves.Platform == Platform.Windows && Saves.SaveIn(folder) != null)
        {
            try { gathered = Saves.GatherAlone(saves, new[] { Path.Combine(Path.GetFullPath(folder), "user") }, running, now); }
            catch (Exception e) when (e is SaveFailure or IOException or UnauthorizedAccessException)
            {
                gatherProblem = e.Message;
                LauncherLog.Add($"saves gather before deleting {folder}: not done: {e.GetType().Name}: {e.Message}");
            }
        }
        try
        {
            var backup = DeleteFolder(folder, platform, keep, getenv, saves, now);
            return new DeletedGame(folder, true, gathered, gatherProblem, backup, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DeletedGame(folder, false, gathered, gatherProblem, (e as FolderNotDeleted)?.Backup, e.Message);
        }
    }

    /// path is folder or somewhere under it
    public static bool Holds(string folder, string path)
    {
        if (SameFolder(folder, path)) return true;
        var windows = OperatingSystem.IsWindows() || folder.Contains('\\') || path.Contains('\\');
        string Norm(string p)
        {
            try { p = Path.GetFullPath(p); } catch (Exception) { }
            return Path.TrimEndingDirectorySeparator(p);
        }
        var sep = windows ? "\\" : "/";
        return Norm(path).StartsWith(Norm(folder) + sep, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// A game folder BuildRegistry.DeleteFolder began to delete and could not finish (a file in use): Backup is where the save it
/// held was copied first (null: it held none), named still, as the folder may have lost its save already
public sealed class FolderNotDeleted(string message, string? backup, Exception inner) : IOException(message, inner)
{
    public string? Backup { get; } = backup;
}

/// What BuildRegistry.DeleteGame did: whether the folder is gone (Problem: why not), the gather of its own save into the shared
/// one (Gathered; GatherProblem: why it failed), and where the copy of the save it still held went (Backup; null: none was needed)
public sealed record DeletedGame(string Folder, bool Deleted, SaveGathered? Gathered, string? GatherProblem, string? Backup, string? Problem)
{
    /// What the home says of it, null when nothing needs saying (the toast says it was deleted): what the gather did or why it
    /// could not, where the save the folder held is backed up, why the folder is still there
    public string? Said()
    {
        var lines = new List<string>();
        if (Gathered != null) lines.Add(SaveText.Gathered(Gathered));
        if (GatherProblem != null) lines.Add(Tr.F("Your saves were not brought together: {0}", GatherProblem));
        if (Backup != null) lines.Add(Tr.F("The save in {0} is backed up in {1}.", Path.Combine(Folder, "user", "save"), Backup));
        if (!Deleted) lines.Add(Tr.F("{0} could not be deleted: {1}", Folder, Problem));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
