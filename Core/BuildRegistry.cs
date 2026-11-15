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
    public List<BuildEntry> Builds { get; set; } = new();
}

/// &lt;home&gt;/builds.json, the launcher's list of builds (the launcher alone writes it). Written whole
/// (AtomicFile); exactly one build is the default whenever the list is not empty. One entry per folder.
public sealed class BuildRegistry
{
    readonly string path;
    readonly List<BuildEntry> builds;
    readonly Func<DateTimeOffset> now;

    BuildRegistry(string path, List<BuildEntry> builds, Func<DateTimeOffset>? now, bool existed, string? problem)
    {
        this.path = path;
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
            return new BuildRegistry(path, list, now, true, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // (kept beside it rather than lost: a newer launcher's list, or a broken disk, is not overwritten by an empty one)
            try { File.Copy(path, path + ".broken", overwrite: true); } catch (Exception) { }
            return new BuildRegistry(path, new List<BuildEntry>(), now, true, e.Message);
        }
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

    public BuildEntry? Find(string id) => builds.FirstOrDefault(b => b.Id == id);

    public BuildEntry? FindByFolder(string folder) => builds.FirstOrDefault(b => SameFolder(b.Folder, folder));

    /// Another build made from this .nup, whose game files a new build of it can take by hard links (make_game's
    /// LONGSHOT_ROMFS_FROM): one whose folder has what make_game's romfs_from_build wants before it links, its romfs/, its kept
    /// .longshot_exefs/ and a .longshot_romfs stamp of this .nup (UnpackStamp), so that a folder make_game would pass over (and
    /// the whole .nup read again) is not chosen while another would do. Of those, one on the new build's drive first (a hard
    /// link does not cross drives: make_game would copy the ~25 GB), then the one updated last; never the new build's own
    /// folder; null when there is none, the .nup is not there, or the new build's folder has them already (an update in its own
    /// folder: BuildRegistry.FolderOf). driveOf: the tests' stand-in for Disk.DriveName
    public BuildEntry? SameGameFiles(string nup, string folder, Func<string, string>? driveOf = null)
    {
        if (UnpackStamp(nup) is not { } stamp || Unpacked(folder, stamp)) return null;
        driveOf ??= Disk.DriveName;
        var drive = driveOf(folder);
        var drives = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return builds.Where(b => b.Nup is { Length: > 0 } n && SameFolder(n, nup) && !SameFolder(b.Folder, folder) && Unpacked(b.Folder, stamp))
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
        foreach (var b in gone) builds.Remove(b);
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

    /// Where a build is made. An update or a rebuild (replaces: the build it takes the place of) is made in that build's own
    /// folder: the game keeps its path (a GPU driver's settings for the program follow the path), its settings, mods and
    /// shader caches stay with it, and no folder of the version before is left on the disk. A new build goes in NewFolder
    public string FolderOf(BuildEntry? replaces, string buildsFolder, string channel, Platform platform) =>
        replaces != null ? replaces.Folder : NewFolder(buildsFolder, channel, platform);

    /// A new build's folder in parent (LauncherSettings.FolderFor: its channel's name), past the folders other builds have and
    /// past anything on the disk that is not a game folder. A game folder no build lists (its row was removed, or the list was
    /// lost), or one a build stopped in, is built in again, as one the player picks is. replaces: the build this one takes the
    /// place of, whose own folder is not another build's
    public string NewFolder(string parent, string channel, Platform platform, BuildEntry? replaces = null) =>
        LauncherSettings.FolderFor(parent, channel, p => FindByFolder(p) is { } e ? e.Id != replaces?.Id : HoldsOtherThings(p, platform));

    /// The folder a build is made in when the player picks one: a folder that holds other things gets the build's own folder
    /// in it (NewFolder); an empty one, one not made yet, or a game folder, is used as it is
    public string PickedFolder(string picked, string channel, Platform platform, BuildEntry? replaces = null) =>
        HoldsOtherThings(picked, platform) ? NewFolder(picked, channel, platform, replaces) : picked;

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
    /// its version and channel are the new build's. It is built in its build's own folder (FolderOf), so the folder stays too.
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
        AtomicFile.Write(path, JsonSerializer.Serialize(new RegistryFile { Builds = builds }, CoreJson.Default.RegistryFile));
    }

    /// A build's game folder deleted from the disk: only a folder make_game.py made (its stamp or bin/longshot), never another
    /// folder a list entry might name. Not a link either (deleting it would free nothing), nor the home folder or one that holds
    /// it, nor one that holds a folder of keep: the other builds and the launcher's home. (A 2.x install in ~/Games/Longshot holds
    /// the launcher's own builds, ~/Games/Longshot/&lt;channel&gt;, while that is the builds folder.) The entry itself is removed with
    /// Remove
    public static void DeleteFolder(string folder, Platform platform, IEnumerable<string>? keep = null, Func<string, string?>? getenv = null)
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
        Toolchains.DeleteTree(full);
    }

    /// path is folder or somewhere under it
    static bool Holds(string folder, string path)
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
