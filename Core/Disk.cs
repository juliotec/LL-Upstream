namespace LongshotLauncher.Core;

/// Space on the disk: what a folder holds, what is free where a build goes, and what a build needs
public static class Disk
{
    /// What a build needs while it builds: the game folder and the build files (the 2.x Builders' "Needs 45 GB"). The stores
    /// (MakeGameCommand) add at most a new translation's copy while it runs (~1.6 GB): what they keep is on the disk already
    public const long BuildBytes = 45L << 30;

    /// What a build makes in its build files folder (make_game.py --work) that Free up space lets go, as the 2.x Builder's
    /// Storage.Freeable: the compiled objects and their system headers' stamp, the translation, the unpacked exefs and the check's
    /// probe; and the launcher's object and lift stores in it (MakeGameCommand: LONGSHOT_OBJ_STORE, LONGSHOT_LIFT_STORE). Never the
    /// folder itself: the player may have chosen one that holds other things
    public static readonly IReadOnlyList<string> WorkParts = new[] { "objcache", "sa", "exefs", "probe", "system_headers.txt", ObjectStore, LiftStore };

    /// The object store's and the lift store's folders in the work folder (Longshot's nx2/object_store.py, lift_store.py)
    public const string ObjectStore = "obj-store";
    public const string LiftStore = "lift-store";

    /// The bytes Free up space would let go in work
    public static long WorkBytes(string work) => WorkParts.Sum(p =>
    {
        var path = Path.Combine(work, p);
        var file = new FileInfo(path);
        return file.Exists ? (file.LinkTarget == null ? file.Length : 0) : p == ObjectStore ? ObjectStoreBytes(work) : FolderBytes(path);
    });

    /// The object store's bytes, less its objects that are one file with the work folder's objcache's, hard-linked: counted with
    /// objcache. They are in the store's folder for the compiler and headers the work folder last built with, named by the first
    /// 16 characters of its system_headers.txt, lowercase hex (object_store.py folder: another stamp has no folder), under the
    /// same name as in objcache (their key). Where the disk makes no hard links (exFAT, FAT, some network shares) the store holds
    /// a copy (object_store.py _place), which takes its own bytes: only the same file (FileLinks.SameFile) counts once
    static long ObjectStoreBytes(string work)
    {
        var store = Path.Combine(work, ObjectStore);
        var bytes = FolderBytes(store);
        try
        {
            var stamp = File.ReadAllText(Path.Combine(work, "system_headers.txt")).Trim();
            if (stamp.Length < 16 || !stamp[..16].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                || new DirectoryInfo(store).LinkTarget != null) return bytes;
            var mine = new DirectoryInfo(Path.Combine(store, stamp[..16]));
            if (!mine.Exists || mine.LinkTarget != null) return bytes;
            foreach (var o in mine.EnumerateFiles("*.o"))
            {
                var cached = new FileInfo(Path.Combine(work, "objcache", o.Name));
                if (o.LinkTarget == null && cached.Exists && cached.LinkTarget == null && FileLinks.SameFile(o.FullName, cached.FullName)) bytes -= o.Length;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        return bytes;
    }

    /// WorkParts let go (a link among them is removed, never followed); the rest of work stays
    public static void FreeWork(string work)
    {
        foreach (var p in WorkParts)
        {
            var path = Path.Combine(work, p);
            if (Directory.Exists(path) || new DirectoryInfo(path).LinkTarget != null) Toolchains.DeleteTree(path);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    /// The bytes of the files under folder; links are counted as links, never followed; what cannot be read counts nothing
    public static long FolderBytes(string folder)
    {
        long sum = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget != null) continue;
                    if (entry is DirectoryInfo d) pending.Push(d.FullName);
                    else if (entry is FileInfo f) sum += f.Length;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return sum;
    }

    /// What a folder holds, measured with the others (Measure). Bytes: its files' bytes, as FolderBytes. Counted: its share of
    /// what the folders take on the disk, a file hard-linked into several (a build's game files linked from another's,
    /// BuildRegistry.SameGameFiles) counted in the first only, so that the Counted of all the folders add up to what they take.
    /// Frees: what deleting it frees, its files with no hard link outside it
    public readonly record struct FolderSize(long Bytes, long Counted, long Frees);

    /// folders measured together, each file once by its identity (FileId): one listing of each folder, and on Linux one statx of
    /// each file, the call FileInfo.Length makes. A file's links outside the folders count too (its link count): an update
    /// built into a new folder leaves the old one on the disk with no row, holding links to the game files it gave the new
    /// one. Windows' listing does not say a file's links, so there the files that could be freed (all their links seen in one
    /// folder) are asked, on up to 8 threads: a game folder of ~187,000 files took 3.0 to 4.0 s, the listing alone 0.4 to 0.7 s
    /// (Ryzen 5 5600, NVMe; one thread took about 16 s). Links are not followed; what cannot be read counts nothing
    public static IReadOnlyList<FolderSize> Measure(IReadOnlyList<string> folders)
    {
        var bytes = new long[folders.Count];
        var counted = new long[folders.Count];
        var frees = new long[folders.Count];
        // (a file that may be linked elsewhere: its bytes, its links, 0 when unknown, and how many of its links each folder has;
        // where it is in all of folders, and the folder that counts it)
        var shared = new Dictionary<FileId, (long Size, uint Links, int First, int Seen, string Path)>();
        var here = new Dictionary<FileId, int>[folders.Count];
        for (var i = 0; i < folders.Count; i++)
        {
            var mine = here[i] = new Dictionary<FileId, int>();
            var at = i;
            Walk(folders[i], (path, size, id, links) =>
            {
                bytes[at] += size;
                if (id is not { } key || links == 1)
                {
                    counted[at] += size;
                    frees[at] += size;
                    return;
                }
                mine[key] = mine.GetValueOrDefault(key) + 1;
                shared[key] = shared.TryGetValue(key, out var s) ? s with { Seen = s.Seen + 1 } : (size, links, at, 1, path);
            });
        }
        // (Windows: a file seen only in one folder, all its links there, may have more outside every folder; one seen in two is
        // freed by deleting neither, whatever its link count)
        var ask = shared.Where(p => p.Value.Links == 0 && p.Value.Size > 0 && here[p.Value.First].GetValueOrDefault(p.Key) == p.Value.Seen)
                        .Select(p => (p.Key, p.Value.Path)).ToList();
        var asked = new uint[ask.Count];
        Parallel.For(0, ask.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) },
                     n => asked[n] = FileLinks.WindowsLinks(ask[n].Path, ask[n].Key));
        for (var n = 0; n < ask.Count; n++)
            if (asked[n] > 0) shared[ask[n].Key] = shared[ask[n].Key] with { Links = asked[n] };
        for (var i = 0; i < folders.Count; i++)
            foreach (var (key, n) in here[i])
            {
                var s = shared[key];
                if (s.First == i) counted[i] += s.Size;
                if (n >= (s.Links > 0 ? s.Links : (uint)s.Seen)) frees[i] += s.Size;
            }
        return Enumerable.Range(0, folders.Count).Select(i => new FolderSize(bytes[i], counted[i], frees[i])).ToList();
    }

    /// Each file under folder: its path, its bytes, its identity (null: unknown) and its hard links (0: unknown). Links are not
    /// followed
    static void Walk(string folder, Action<string, long, FileId?, uint> file)
    {
        var pending = new Stack<string>();
        var listing = new List<FileLinks.Listed>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                if (FileLinks.TryWindowsListing(dir, listing, out var volume))
                {
                    foreach (var e in listing)
                    {
                        if ((e.Attributes & FileLinks.AttributeReparsePoint) != 0 && e.Tag is FileLinks.TagSymlink or FileLinks.TagMountPoint) continue;
                        if ((e.Attributes & FileLinks.AttributeDirectory) != 0) pending.Push(Path.Combine(dir, e.Name));
                        // (0 and all ones: no id the volume can give, as ReFS's longer ones)
                        else file(Path.Combine(dir, e.Name), e.Size, e.Id is 0 or ulong.MaxValue ? null : new FileId(volume, e.Id), 0);
                    }
                    continue;
                }
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget != null) continue;
                    if (entry is DirectoryInfo d) pending.Push(d.FullName);
                    else if (entry is FileInfo f)
                    {
                        if (FileLinks.TryUnixFile(f.FullName, out var size, out var id, out var links)) file(f.FullName, size, id, links);
                        else file(f.FullName, f.Length, null, 1);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    /// The free bytes on the drive that holds path (or would: the nearest folder above it that exists); null when unknown
    public static long? FreeBytes(string path)
    {
        try
        {
            var at = Existing(path);
            if (at == null) return null;
            // Unix asks the filesystem that holds the folder itself, so links and mounts are followed by the OS
            if (!OperatingSystem.IsWindows()) return new DriveInfo(at).AvailableFreeSpace;
            // (the mount that holds it: the longest drive root it starts with)
            var drive = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady && Within(at, d.RootDirectory.FullName); } catch (IOException) { return false; } })
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
            return drive?.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// The nearest folder at or above path that exists, as a full path; on Unix with its links followed, so it names the
    /// mount FreeBytes measures (on Bazzite /home is a link to /var/home); null when none does
    static string? Existing(string path)
    {
        var at = Path.GetFullPath(path);
        while (!Directory.Exists(at))
        {
            var up = Path.GetDirectoryName(at);
            if (up == null || up == at) return null;
            at = up;
        }
        return OperatingSystem.IsWindows() ? at : Real(at);
    }

    /// path with every link along it followed, as realpath does (a link's target may itself pass through links)
    static string Real(string path, int depth = 0)
    {
        if (depth > 40) return path;   // (a loop of links: give up and keep the path as it is)
        var at = "/";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(at, part);
            var target = new DirectoryInfo(next).LinkTarget == null ? null : new DirectoryInfo(next).ResolveLinkTarget(true);
            at = target == null ? next : Real(target.FullName, depth + 1);
        }
        return at;
    }

    static bool Within(string path, string root)
    {
        var c = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root);
        // (TrimEndingDirectorySeparator keeps a drive root's separator: C:\ must not become C:\\ for the prefix check)
        var prefix = Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar;
        return r.Length == 0 || path.Equals(r, c) || path.StartsWith(prefix, c) || root == "/";
    }

    /// The screenshots' stand-in for the drive every path is on (tools/screenshots: nothing of this PC's in the picture); null:
    /// this PC's drives
    public static string? ShownDrive { get; set; }

    /// The drive a path is on, as the window names it: "D:" on Windows, the mount point on Linux ("/" , "/home", "/mnt/games")
    public static string DriveName(string path)
    {
        if (ShownDrive is { } shown) return shown;
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) return (Path.GetPathRoot(full) ?? full).TrimEnd('\\', '/');
            full = Existing(full) ?? full;   // (the folder FreeBytes measures, its links followed)
            var mount = DriveInfo.GetDrives()
                .Select(d => d.RootDirectory.FullName)
                .Where(r => Within(full, r))
                .OrderByDescending(r => r.Length).FirstOrDefault();
            return mount ?? "/";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return path; }
    }
}
