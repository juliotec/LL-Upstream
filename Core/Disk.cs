namespace LongshotLauncher.Core;

/// Space on the disk: what a folder holds, what is free where a build goes, and what a build needs
public static class Disk
{
    /// The free space Longshot's requirements ask for (the advisory check, SystemRequirements): about 60 GB, room for a dump of
    /// the game beside the builds. What a build itself needs is Needs'
    public const long BuildBytes = 60L << 30;

    /// What longshot-builder writes, by Longshot's build.md ("about 5 GB free" for a first build): about 4 GB on the work
    /// folder's drive (its low_disk_space 3 GB for the objects and the executable, the lift keeps no texts, and the ~0.6 GB a
    /// release's first run unpacks into --cache, its sources and tools, LongshotKept), the game folder about 1 GB (the game,
    /// ~0.6 GB, and the romfs's index). Its game reads the .nup in place: no game files are copied
    public const long LongshotWorkBytes = 4L << 30, LongshotGameBytes = 1L << 30;

    /// What a build needs free, by the folder whose drive must have it: LongshotGameBytes where the game goes and LongshotWorkBytes
    /// where its build files go (work), added up when both are on one drive. drive: the tests' stand-in for DriveName
    public static IReadOnlyList<(string Folder, long Bytes)> Needs(string game, string work, Func<string, string>? drive = null)
    {
        drive ??= DriveName;
        return Storage.SameDrive(drive(game), drive(work)) ? [(game, LongshotGameBytes + LongshotWorkBytes)]
            : [(game, LongshotGameBytes), (work, LongshotWorkBytes)];
    }

    /// longshot-builder's cache in the work folder (its --cache: LongshotBuilder; build-driver's layout.rs): a work folder per
    /// game folder (work/), the object and lift stores every one shares (store/), and what is kept for every later build
    /// (LongshotKept): each release's carried tree and tools unpacked (sources/, tools/) and the vendors' files it downloaded
    /// under the licences accepted (runtimes/)
    public const string LongshotBuildCache = "longshot-builder";
    public static readonly string LongshotWork = Path.Combine(LongshotBuildCache, "work"), LongshotStore = Path.Combine(LongshotBuildCache, "store");
    public static readonly IReadOnlyList<string> LongshotKept = [Path.Combine(LongshotBuildCache, "sources"), Path.Combine(LongshotBuildCache, "tools"),
                                                                 Path.Combine(LongshotBuildCache, "runtimes")];

    /// The launcher's rows' work folders (LongshotBuilder.WorkFolder, launcher-&lt;id&gt;) left in the build files folder the player
    /// moved away from (oldWork; newWork the one now), deleted: no build of the launcher's looks there any more, and a work folder
    /// is a gigabyte or more. The rest of oldWork's cache (its stores, what releases unpacked) stays for the Storage page, and a
    /// developer's own work folders there (not launcher-) are never touched. Skipped: a folder whose lock a build holds
    /// (LongshotBuilder.WorkLockFile: one running from it, the launcher's or not), one building names (the launcher's own build,
    /// between its runs, which hold no lock), any that is or holds newWork, and any reached through a link. Nothing when the two
    /// are one folder. Returns the folders deleted; failed: one that could not be (still in use)
    public static IReadOnlyList<string> LeaveLongshotWork(string oldWork, string newWork, IReadOnlyCollection<string> building,
                                                          Action<string, Exception>? failed = null)
    {
        var gone = new List<string>();
        if (BuildRegistry.SameFolder(oldWork, newWork)) return gone;
        var cache = Path.Combine(oldWork, LongshotBuildCache);
        var works = Path.Combine(oldWork, LongshotWork);
        try
        {
            if (!Directory.Exists(works) || new DirectoryInfo(cache).LinkTarget != null || new DirectoryInfo(works).LinkTarget != null) return gone;
            foreach (var dir in Directory.EnumerateDirectories(works, "launcher-*"))
            {
                try
                {
                    if (new DirectoryInfo(dir).LinkTarget != null || building.Any(b => BuildRegistry.SameFolder(b, dir)) || Within(Path.GetFullPath(newWork), Path.GetFullPath(dir))
                        || LongshotBuilder.Held(Path.Combine(dir, LongshotBuilder.WorkLockFile)))
                        continue;
                    Toolchains.DeleteTree(dir);
                    gone.Add(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed?.Invoke(dir, e); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed?.Invoke(works, e); }
        return gone;
    }

    /// What a build makes in its build files folder (make_game.py --work) that Free up space lets go, as the 2.x Builder's
    /// Storage.Freeable: the compiled objects and their system headers' stamp, the translation, the unpacked exefs and the check's
    /// probe; and the launcher's object and lift stores in it (MakeGameCommand: LONGSHOT_OBJ_STORE, LONGSHOT_LIFT_STORE); and of
    /// longshot-builder's cache its work folders and stores (LongshotWork, LongshotStore), never what its releases unpacked and
    /// downloaded there (LongshotKept), which only Remove everything takes (AllParts). Never the folder itself: the player may
    /// have chosen one that holds other things
    public static readonly IReadOnlyList<string> WorkParts = new[] { "objcache", "sa", "exefs", "probe", "system_headers.txt", ObjectStore, LiftStore, LongshotWork, LongshotStore };

    /// WorkParts and the whole of longshot-builder's cache: what Remove everything lets go in the build files folder
    public static readonly IReadOnlyList<string> AllParts = [.. WorkParts, LongshotBuildCache];

    /// What only make_game.py's builds use in the build files folder: its objects, translation, exefs, check's probe and
    /// headers' stamp, and its object and lift stores
    public static readonly IReadOnlyList<string> MakeGameParts = ["objcache", "sa", "exefs", "probe", "system_headers.txt", ObjectStore, LiftStore];

    /// make_game.py's build files (MakeGameParts) and the SDKs it fetched (sdks: the launcher's ngx-sdk, ffx-sdk and xess-sdk)
    /// let go once no old version is listed (oldListed: OldVersions; the stable launcher builds and updates those, and an older
    /// launcher's build files and SDKs serve it after going back to it). longshot-builder carries its own and fetches the vendors'
    /// files into its cache. False, and nothing touched, while one is
    public static bool PruneMakeGame(string work, IEnumerable<string> sdks, bool oldListed)
    {
        if (oldListed) return false;
        if (MakeGameParts.Any(p => Path.Exists(Path.Combine(work, p)))) FreeWork(work, MakeGameParts);
        foreach (var sdk in sdks) Toolchains.DeleteTreeIfPresent(sdk);
        return true;
    }

    /// The object store's and the lift store's folders in the work folder (Longshot's nx2/object_store.py, lift_store.py)
    public const string ObjectStore = "obj-store";
    public const string LiftStore = "lift-store";

    /// The bytes freeing all of the build files would let go in work (Settings' note; the Storage page frees them by kind:
    /// Storage.WorkPartsOf)
    public static long WorkBytes(string work) => WorkParts.Sum(p => PartBytes(work, p));

    /// The bytes one of WorkParts takes in work: a file's, a folder's (a link counts nothing), the object store's less what is
    /// one file with objcache (ObjectStoreBytes)
    static long PartBytes(string work, string part)
    {
        var path = Path.Combine(work, part);
        var file = new FileInfo(path);
        return file.Exists ? (file.LinkTarget == null ? file.Length : 0) : part == ObjectStore ? ObjectStoreBytes(work) : FolderBytes(path);
    }

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
    public static void FreeWork(string work) => FreeWork(work, WorkParts);

    /// Some of WorkParts let go (the Storage page: one group at a time; Remove everything: AllParts); a name that is not one of
    /// AllParts is never touched, so nothing else in a folder the player chose can go this way. A part inside longshot-builder's
    /// cache is not looked for through a link in its place
    public static void FreeWork(string work, IEnumerable<string> parts, Action<int, int, int>? outcome = null)
    {
        var gone = new List<string>();
        int skipped = 0, failed = 0;
        try
        {
            foreach (var p in parts.Where(AllParts.Contains).Distinct())
            {
                var path = Path.Combine(work, p);
                try
                {
                    if (p != LongshotBuildCache && p.StartsWith(LongshotBuildCache + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && new DirectoryInfo(Path.Combine(work, LongshotBuildCache)).LinkTarget != null)
                    {
                        skipped++;
                        continue;
                    }
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (Toolchains.DeleteTreeIfPresent(path)) gone.Add(p); else skipped++;
                    }
                    else { File.Delete(path); gone.Add(p); }
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { skipped++; }
                catch (Exception e) when (outcome != null && (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)) { failed++; }
            }
            outcome?.Invoke(gone.Count, skipped, failed);
            LauncherLog.Add($"free up space: deleted in {work}: {(gone.Count > 0 ? string.Join(", ", gone) : "nothing there")}");
        }
        catch (Exception e)
        {
            LauncherLog.Add($"free up space: stopped in {work} ({e.GetType().Name}: {e.Message}); deleted: {(gone.Count > 0 ? string.Join(", ", gone) : "nothing")}");
            throw;
        }
    }

    /// The bytes of the files under folder; links are counted as links, never followed; what cannot be read counts nothing
    public static long FolderBytes(string folder)
    {
        try { if (new DirectoryInfo(folder).LinkTarget != null) return 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return 0; }
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
    /// an older launcher's builds of one .nup) counted in the first only, so that the Counted of all the folders add up to what they take.
    /// Frees: what deleting it frees, its files with no hard link outside it
    public readonly record struct FolderSize(long Bytes, long Counted, long Frees);

    /// folders measured together, each file once by its identity (FileId): one listing of each folder, and on Linux one statx of
    /// each file, the call FileInfo.Length makes. A file's links outside the folders count too (its link count): an update
    /// built into a new folder leaves the old one on the disk with no row, holding links to the game files it gave the new
    /// one. Windows' listing does not say a file's links, so there the files that could be freed (all their links seen in one
    /// folder) are asked, on up to 8 threads: a game folder of ~187,000 files took 3.0 to 4.0 s, the listing alone 0.4 to 0.7 s
    /// (Ryzen 5 5600, NVMe; one thread took about 16 s). Links are not followed; what cannot be read counts nothing. skip: what
    /// of the entries right inside folders[i] (by name) is left out of it, so that one folder can be measured as several parts
    /// (Storage: a game folder's game files, shader caches and the rest) with each file still counted once across all of them
    public static IReadOnlyList<FolderSize> Measure(IReadOnlyList<string> folders, Func<int, string, bool>? skip = null) =>
        MeasureCopies(folders, skip).Sizes;

    /// Measure's sizes, with the copies among the folders copies asks about (Storage: each game folder's romfs/): folders that
    /// share a file (a hard link) are one copy. Copy[i]: the first folder of i's copy (-1: one not asked about); CopyFrees[c]:
    /// what deleting every folder of copy c frees, its files with no hard link outside them; CopyHeld[c]: its largest file
    /// another folder links to too, outside the copy's (none: every file is freed with it), to name those folders by
    public sealed record Measured(IReadOnlyList<FolderSize> Sizes, IReadOnlyList<int> Copy, IReadOnlyDictionary<int, long> CopyFrees,
                                 IReadOnlyDictionary<int, string>? CopyHeld = null, bool Partial = false);

    /// Measure, telling apart the copies among the folders copies says (null: none); see Measured
    public static Measured MeasureCopies(IReadOnlyList<string> folders, Func<int, string, bool>? skip = null, Func<int, bool>? copies = null)
    {
        var bytes = new long[folders.Count];
        var counted = new long[folders.Count];
        var frees = new long[folders.Count];
        // (each folder's files that are its alone: no identity, or one link)
        var sole = new long[folders.Count];
        // (a file that may be linked elsewhere: its bytes, its links, 0 when unknown, and how many of its links each folder has;
        // where it is in all of folders, and the folder that counts it)
        var shared = new Dictionary<FileId, (long Size, uint Links, int First, int Seen, string Path)>();
        var here = new Dictionary<FileId, int>[folders.Count];
        var partial = false;
        for (var i = 0; i < folders.Count; i++)
        {
            var mine = here[i] = new Dictionary<FileId, int>();
            var at = i;
            Walk(folders[i], skip == null ? null : name => skip(at, name), (path, size, id, links) =>
            {
                bytes[at] += size;
                if (id is not { } key || links == 1)
                {
                    counted[at] += size;
                    frees[at] += size;
                    sole[at] += size;
                    return;
                }
                mine[key] = mine.GetValueOrDefault(key) + 1;
                shared[key] = shared.TryGetValue(key, out var s) ? s with { Seen = s.Seen + 1 } : (size, links, at, 1, path);
            }, () => partial = true);
        }
        // (the copies: the folders asked about that share a file, joined; each file's first such folder, and how many of its
        // links they have)
        var asked0 = Enumerable.Range(0, folders.Count).Select(i => copies?.Invoke(i) == true).ToArray();
        var parent = Enumerable.Range(0, folders.Count).ToArray();
        int Root(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var firstAsked = new Dictionary<FileId, int>();
        var askedSeen = new Dictionary<FileId, int>();
        for (var i = 0; i < folders.Count; i++)
        {
            if (!asked0[i]) continue;
            foreach (var (key, n) in here[i])
            {
                if (firstAsked.TryGetValue(key, out var f))
                {
                    var (a, b) = (Root(f), Root(i));
                    if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                }
                else firstAsked[key] = i;
                askedSeen[key] = askedSeen.GetValueOrDefault(key) + n;
            }
        }
        // (Windows: a file seen only in one folder, all its links there, may have more outside every folder; one seen in two is
        // freed by deleting neither, whatever its link count; one of a copy's, whose folders may hold all its links, asked too)
        var ask = shared.Where(p => p.Value.Links == 0 && p.Value.Size > 0
                                    && (here[p.Value.First].GetValueOrDefault(p.Key) == p.Value.Seen || firstAsked.ContainsKey(p.Key)))
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
        var copyFrees = new Dictionary<int, long>();
        for (var i = 0; i < folders.Count; i++)
            if (asked0[i]) copyFrees[Root(i)] = copyFrees.GetValueOrDefault(Root(i)) + sole[i];
        // (a file of a copy is freed with it when every link it has is in the copy's folders; of the others, the largest kept)
        var held = new Dictionary<int, (long Size, string Path)>();
        foreach (var (key, f) in firstAsked)
        {
            var s = shared[key];
            if (askedSeen[key] == s.Seen && s.Seen >= (s.Links > 0 ? s.Links : (uint)s.Seen)) copyFrees[Root(f)] += s.Size;
            else if (!held.TryGetValue(Root(f), out var h) || s.Size > h.Size) held[Root(f)] = (s.Size, s.Path);
        }
        return new Measured(Enumerable.Range(0, folders.Count).Select(i => new FolderSize(bytes[i], counted[i], frees[i])).ToList(),
                            Enumerable.Range(0, folders.Count).Select(i => asked0[i] ? Root(i) : -1).ToList(), copyFrees,
                            held.ToDictionary(h => h.Key, h => h.Value.Path), partial);
    }

    /// What a build holds of its own, as its row says it and its Delete frees: its files with no hard link outside its folder
    /// (BuildEntry.Frees: the game files it shares with another build are not its own), else its folder's bytes; null when it
    /// is not measured yet
    public static long? Own(BuildEntry e) => e.Bytes == null ? null : e.Frees ?? e.Bytes;

    /// The home's footer: what the instances take in folder, each file once (BuildEntry.Counted), and the shader store every one
    /// of them uses (store: its bytes, ShaderCaches.StoreBytes; it is not in folder), then what of it they share: the game files
    /// hard-linked between them (the total less each one's own, Own) and the store. "2 instances use 48.2 GB in
    /// D:\Games\Longshot: 24.4 GB of it is the game's files, which they share."
    public static string InstancesUse(IReadOnlyList<BuildEntry> builds, long store, string folder)
    {
        var n = builds.Count;
        var measured = builds.Where(b => b.Bytes != null).ToList();
        var total = measured.Sum(b => b.Counted ?? b.Bytes!.Value);
        var games = Math.Max(0, total - measured.Sum(b => Own(b)!.Value));
        total += store;
        if (total <= 0) return Tr.P(n, "1 version in {1}.", "{0} versions in {1}.", folder);
        string Size(long b) => Durations.Size(b);
        return games > 0 && store > 0
            ? Tr.P(n, "1 version uses {1} in {2}: {3} of it is the game's files, which it shares with a folder not listed here, and {4} the shader store.",
                   "{0} versions use {1} in {2}: {3} of it is the game's files and {4} the shader store, which they share.", Size(total), folder, Size(games), Size(store))
            : games > 0
            ? Tr.P(n, "1 version uses {1} in {2}: {3} of it is the game's files, which it shares with a folder not listed here.",
                   "{0} versions use {1} in {2}: {3} of it is the game's files, which they share.", Size(total), folder, Size(games))
            : store > 0
            ? Tr.P(n, "1 version uses {1} in {2}: {3} of it is the shader store.",
                   "{0} versions use {1} in {2}: {3} of it is the shader store, which they share.", Size(total), folder, Size(store))
            : Tr.P(n, "1 version uses {1} in {2}.", "{0} versions use {1} in {2}.", Size(total), folder);
    }

    /// Each file under folder: its path, its bytes, its identity (null: unknown) and its hard links (0: unknown). Links are not
    /// followed. skip: the entries right inside folder (by name) to leave out
    static void Walk(string folder, Func<string, bool>? skip, Action<string, long, FileId?, uint> file, Action partial)
    {
        try { if (new DirectoryInfo(folder).LinkTarget != null) return; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial(); return; }
        var pending = new Stack<string>();
        var listing = new List<FileLinks.Listed>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            var top = skip != null && ReferenceEquals(dir, folder);
            try
            {
                if (FileLinks.TryWindowsListing(dir, listing, out var volume))
                {
                    foreach (var e in listing)
                    {
                        if (top && skip!(e.Name)) continue;
                        if ((e.Attributes & FileLinks.AttributeReparsePoint) != 0 && e.Tag is FileLinks.TagSymlink or FileLinks.TagMountPoint) continue;
                        if ((e.Attributes & FileLinks.AttributeDirectory) != 0) pending.Push(Path.Combine(dir, e.Name));
                        // (0 and all ones: no id the volume can give, as ReFS's longer ones)
                        else file(Path.Combine(dir, e.Name), e.Size, e.Id is 0 or ulong.MaxValue ? null : new FileId(volume, e.Id), 0);
                    }
                    continue;
                }
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (top && skip!(entry.Name)) continue;
                    if (entry.LinkTarget != null) continue;
                    if (entry is DirectoryInfo d) pending.Push(d.FullName);
                    else if (entry is FileInfo f)
                    {
                        if (FileLinks.TryUnixFile(f.FullName, out var size, out var id, out var links)) file(f.FullName, size, id, links);
                        else file(f.FullName, f.Length, null, 1);
                    }
                }
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { if (dir != folder) partial(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial(); }
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

    /// (Longshot#1010) The highest folder at or above path that a program may not list, or null when each one may. The build's
    /// clang and lld (LLVM) look up their own long name when they start (GetLongPathNameW), which lists every folder on the way,
    /// and in one that denies it they stop with "permission denied" and nothing else. C:\Users\&lt;name&gt;\Cookies and the other
    /// junctions Windows keeps for old programs deny everyone listing them, and a Desktop moved into one took a portable launcher
    /// there. listable: the tests' stand-in for the disk
    public static string? Unlistable(string path, Func<string, bool>? listable = null)
    {
        listable ??= Listable;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var chain = new List<string>();
        for (var at = full; at != null; at = Path.GetDirectoryName(at)) chain.Add(at);
        chain.Reverse();   // (the drive first: the folder named is the one to move out of)
        return chain.FirstOrDefault(d => !listable(d));
    }

    /// Whether a program may list folder: only a refusal counts (a folder not there, or another error, is not this one's to say)
    static bool Listable(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException) { return true; }
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
