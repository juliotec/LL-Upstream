using LongshotLauncher.Core;

/// What longshot-builder keeps on the disk and how the launcher frees it (Core/Disk.cs, Core/Storage.cs, Core/RemoveEverything.cs):
/// a row's own work folder, the room a build needs on each drive, Free up space and Keep build files taking its work folders and
/// store but never its unpacked tools or downloaded vendors' files, make_game.py's leftovers once no version builds with it, the
/// delta bases' row, and Remove everything taking all of it, a build files folder chosen before and the launcher's .old
static partial class Program
{
    static void BuilderStorageChecks()
    {
        static void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        // ---- a row's own work folder: an update renames its folder, the row keeps its id
        var home = new LauncherHome(Folder("bs-home"));
        var tools = new Toolchain("v4.0.0", Path.Combine(home.Toolchains, "v4.0.0"), "longshot-builder");
        var builder = new LongshotBuilder(tools, tools.BuilderExe!, Platform.Linux, home, "L", _ => null);
        var update = builder.Build(new BuildRequest(new BuildSource.Update("/games/nightly-2"), null, Work: "/w", WorkId: "0123abcd4567"));
        var expected = Path.Combine("/w", Disk.LongshotBuildCache, "work", "launcher-0123abcd4567");
        Require(update.Command.Arguments.SkipWhile(a => a != "--work").Skip(1).FirstOrDefault() == expected
                && LongshotBuilder.WorkFolder("/w", "../x") == Path.Combine("/w", Disk.LongshotBuildCache, "work", "launcher-x"),
                "--work: the row's own work folder in the launcher's cache, named by its id's letters and digits only.");
        Require(!builder.Build(new BuildRequest(new BuildSource.Nup("/g.nup"), "/games/a")).Command.Arguments.Contains("--work"),
                "No row: the builder's own work folder.");
        var registry = BuildRegistry.Load(Path.Combine(Folder("bs-registry"), "builds.json"));
        var id = BuildRegistry.NewId();
        var added = registry.Add("/games/a", "v4.0.0", Versions.Stable, id: id);
        Require(added.Id == id && registry.Add("/games/a", "v4.0.1", Versions.Stable, id: BuildRegistry.NewId()).Id == id
                && registry.Add("/games/b", "v4.0.0", Versions.Stable, id: id).Id != id,
                "A new row takes the id its build's work folder was named for; a row of that folder keeps its own; an id never twice.");

        // ---- the room a build needs: longshot-builder's own figures on each drive
        Require(Disk.Needs("/g", "/w", _ => "one").SequenceEqual([("/g", Disk.LongshotGameBytes + Disk.LongshotWorkBytes)])
                && Disk.Needs("/g", "/w", f => f).SequenceEqual([("/g", Disk.LongshotGameBytes), ("/w", Disk.LongshotWorkBytes)])
                && Disk.LongshotGameBytes + Disk.LongshotWorkBytes < 8L << 30,
                "longshot-builder: a few GB, on the game's drive and the build files', added up when they are one.");
        // (build.md's "about 5 GB free" for a first build: 4 GB on the work drive, the ~0.6 GB a release's first run unpacks into
        // --cache with the objects, and 1 GB on the game's; disk_full's words say the same figures)
        Require(Disk.LongshotWorkBytes == 4L << 30 && Disk.LongshotGameBytes == 1L << 30
                && Problems.FromCode("disk_full")!.Detail.Contains("about 5 GB free", StringComparison.Ordinal)
                && Problems.FromCode("disk_full")!.Detail.Contains("about 4 GB where the build files go and 1 GB where the game goes", StringComparison.Ordinal),
                "longshot-builder: 4 GB where the build files go, 1 GB where the game goes, and disk_full says so.");

        // ---- Free up space and Keep build files: its work folders and store, never its unpacked tools or vendors' files
        string Cache(string work, string part) => Path.Combine(work, Disk.LongshotBuildCache, part);

        // ---- the build files folder changed: the launcher's rows' work folders in the old one go, but not one a build holds
        // (its lock), the launcher's own running build's, a developer's own, the old cache's stores, or anything when it is the same
        var left = Folder("bs-left");
        var moved = Folder("bs-left-new");
        string Row(string id)
        {
            var dir = LongshotBuilder.WorkFolder(left, id);
            Put(Path.Combine(dir, "build", "a.o"), "o");
            return dir;
        }
        var (idle, held, running, deep) = (Row("aaaa"), Row("bbbb"), Row("cccc"), Row("dddd"));
        var lockFile = Path.Combine(held, LongshotBuilder.WorkLockFile);
        File.WriteAllText(lockFile, "");
        Put(Path.Combine(Cache(left, "work"), "Longshot-1234", "build.log"), "a developer's");
        Put(Path.Combine(Cache(left, "store"), "obj", "x"), "store");
        Require(Disk.LeaveLongshotWork(left, left, []).Count == 0 && Directory.Exists(idle), "The same folder: nothing goes.");
        IReadOnlyList<string> gone;
        using (var hold = OperatingSystem.IsWindows() ? new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)
                                                     : new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            if (OperatingSystem.IsWindows()) hold.Lock(0, long.MaxValue);
            gone = Disk.LeaveLongshotWork(left, Path.Combine(deep, "nested"), [running]);
        }
        Require(gone.SequenceEqual([idle]) && !Directory.Exists(idle) && File.Exists(Path.Combine(held, "build", "a.o"))
                && File.Exists(Path.Combine(running, "build", "a.o")) && File.Exists(Path.Combine(deep, "build", "a.o"))
                && File.Exists(Path.Combine(Cache(left, "work"), "Longshot-1234", "build.log")) && File.Exists(Path.Combine(Cache(left, "store"), "obj", "x")),
                "A new build files folder: the old one's idle launcher- work folders go; held, running, holding the new one, a developer's and the stores stay: "
                + string.Join(", ", gone));
        Require(Disk.LeaveLongshotWork(left, moved, []).Count == 3 && !Directory.Exists(held) && !Directory.Exists(running),
                "Let go, unheld: they go too.");
        Require(Disk.LeaveLongshotWork(Folder("bs-left-none"), moved, []).Count == 0, "An old folder with no cache: nothing.");
        string MakeWork(string name)
        {
            var work = Folder(name);
            foreach (var part in new[] { "work", "store", "sources", "tools", "runtimes" }) Put(Path.Combine(Cache(work, part), "f"), part);
            Put(Path.Combine(work, "objcache", "a.o"), "o");
            Put(Path.Combine(work, "obj-store", "b.o"), "o");
            Put(Path.Combine(work, "My notes.txt"), "the player's");
            return work;
        }
        var freed = MakeWork("bs-work");
        Storage.AfterBuild(freed, keep: false);
        Require(!Directory.Exists(Cache(freed, "work")) && !Directory.Exists(Cache(freed, "store"))
                && new[] { "sources", "tools", "runtimes" }.All(p => File.Exists(Path.Combine(Cache(freed, p), "f")))
                && File.Exists(Path.Combine(freed, "My notes.txt")),
                "Keep build files off: longshot-builder's work folders and store go; its releases' unpacked tools and the vendors' files stay.");
        Require(Storage.WorkPartsOf(Storage.Kind.BuilderFiles).SequenceEqual([Disk.LongshotWork]) && Storage.WorkPartsOf(Storage.Kind.OlderCode).Contains(Disk.LongshotStore)
                && !Disk.WorkParts.Contains(Disk.LongshotBuildCache) && Disk.LongshotKept.All(k => !Disk.WorkParts.Contains(k)),
                "Storage: its work folders a row of their own, its store with the older builds' code, its tools never freed from there.");
        var measured = MakeWork("bs-measure");
        var items = Storage.Measure(new Storage.Places(measured, Folder("bs-tools"), Folder("bs-logs"), [], [], []));
        Require(items.Where(i => i.Kind == Storage.Kind.Builders).Sum(i => i.Bytes) == "sources".Length + "tools".Length + "runtimes".Length
                && items.Where(i => i.Kind == Storage.Kind.BuilderFiles).Sum(i => i.Bytes) == "work".Length,
                "Its unpacked tools and vendors' files are counted with the builders, not as build files to free.");

        // ---- make_game.py's leftovers, once no old version is listed (OldVersions: the stable launcher's builds use them)
        var old = MakeWork("bs-makegame");
        var sdk = Path.Combine(Folder("bs-sdk"), "ffx-sdk");
        Put(Path.Combine(sdk, "x"), "sdk");
        Require(!Disk.PruneMakeGame(old, [sdk], oldListed: true) && Directory.Exists(Path.Combine(old, "objcache")) && Directory.Exists(sdk),
                "An old version still listed: its build files and SDKs stay.");
        Require(Disk.PruneMakeGame(old, [sdk], oldListed: false) && !Directory.Exists(Path.Combine(old, "objcache"))
                && !Directory.Exists(Path.Combine(old, "obj-store")) && !Directory.Exists(sdk) && Directory.Exists(Cache(old, "work"))
                && File.Exists(Path.Combine(old, "My notes.txt")), "None: make_game.py's build files and SDKs go; longshot-builder's and the player's stay.");

        // ---- the version folders' builders: each one's program and record, apart from its game folders, and an older launcher's
        var versionsRoot = Folder("bs-versions");
        var versionFolders = new[] { "v4.0.0", "v4.1.0" }.Select(t => Path.Combine(versionsRoot, t)).ToList();
        foreach (var v in versionFolders)
        {
            Put(Path.Combine(v, "Longshot.x86_64"), "builder");
            Put(Path.Combine(v, Toolchains.ReleaseRecord), "{}");
            Put(Path.Combine(v, "builds", "default", "Longshot"), "a game folder's, not the builder's");
        }
        var oldTools = Folder("bs-old-toolchains");
        Put(Path.Combine(oldTools, "v3.9.0", "longshot-builder"), "old");
        var places = new Storage.Places(Folder("bs-bases-work"), oldTools, Folder("bs-bases-logs"), [], [], [], VersionFolders: versionFolders);
        var builders = Storage.Measure(places).Where(i => i.Kind == Storage.Kind.Builders).Sum(i => i.Bytes);
        Require(builders == 2 * (7 + 2) + 3 && Storage.Info(Storage.Kind.Builders).Cost == null,
                $"The builders: each version folder's program and record, and an older launcher's, never removed here: {builders}.");

        // ---- Remove everything: all of its cache, a build files folder chosen before, and the launcher's .old
        var settings = new LauncherSettings { Work = "/first" };
        settings.Work = "/second";
        settings.Work = null;
        Require(settings.EarlierWork.SequenceEqual(["/first", "/second"]), "Each build files folder chosen before is kept: " + string.Join(", ", settings.EarlierWork));
        Require(Disk.AllParts.Contains(Disk.LongshotBuildCache), "Remove everything takes all of longshot-builder's cache.");
        var root = Folder("bs-remove");
        var user = Path.Combine(root, "user-home");
        string? Env(string k) => k switch { "HOME" => user, "XDG_DATA_HOME" => Path.Combine(user, "data"), "XDG_CACHE_HOME" => Path.Combine(user, "cache"), _ => null };
        var env = new SaveEnvironment(Platform.Linux, Env);
        var rHome = LauncherHome.Resolve(Platform.Linux, Env);
        Put(Path.Combine(rHome.Root, "builds.json"), "{}");
        var work = MakeWork("bs-remove-work");
        var earlier = MakeWork("bs-remove-earlier");
        var launcher = Path.Combine(root, "Longshot Launcher.AppImage");
        Put(launcher, "me");
        Put(SelfUpdate.Backup(launcher), "my previous version");
        var defaultCache = LongshotBuilder.DefaultCache(Platform.Linux, Env)!;
        Require(defaultCache == Path.Combine(user, "cache", "longshot-builder") && LongshotBuilder.DefaultCache(Platform.Linux, k => k == "LONGSHOT_CACHE" ? "/dev/cache" : Env(k)) == null
                && LongshotBuilder.DefaultCache(Platform.Windows, k => k == "LOCALAPPDATA" ? @"C:\U\AppData\Local" : null) == Path.Combine(@"C:\U\AppData\Local", "Longshot", "Builder"),
                "longshot-builder's own default cache, none when LONGSHOT_CACHE names a developer's.");
        Put(Path.Combine(defaultCache, "sources", "s"), "unpacked by a perf-report given no --cache");
        Require(LongshotBuilder.LauncherLeftDefaultCache(defaultCache), "Only unpacked tools: the launcher's runs left it.");
        Put(Path.Combine(defaultCache, "work", "dev-1", "build.log"), "a developer's build");
        Require(!LongshotBuilder.LauncherLeftDefaultCache(defaultCache), "A work folder in it: a developer's, kept.");
        Directory.Delete(Path.Combine(defaultCache, "work"), true);
        var p = new RemoveEverything.Places(env, rHome, [], Folder("bs-remove-games"), true, work, true, null, [earlier, "/never/there"], launcher);
        var done = RemoveEverything.Run(p, saves: false, () => [], () => DateTime.UnixEpoch);
        Require(!Directory.Exists(Path.Combine(work, Disk.LongshotBuildCache)) && !Directory.Exists(Path.Combine(earlier, Disk.LongshotBuildCache))
                && !Directory.Exists(Path.Combine(earlier, "objcache")) && File.Exists(Path.Combine(earlier, "My notes.txt")) && File.Exists(Path.Combine(work, "My notes.txt")),
                "Remove everything: all of longshot-builder's cache, in the build files folder and one chosen before, nothing else of theirs.");
        Require(!Directory.Exists(defaultCache) && !File.Exists(SelfUpdate.Backup(launcher)) && File.Exists(launcher),
                "Remove everything: the builder's default cache the launcher left, and the launcher's previous version; never itself.");
        Require(!done.Problems.Any(), "Remove everything had no problem: " + string.Join("; ", done.Problems.Select(x => x.Path + ": " + x.Why)));

        // ---- perf-report given the launcher's cache, so it never unpacks a release into the builder's default one
        var perf = PerfReports.SendCommand(tools, "/games/a", Path.Combine("/w", Disk.LongshotBuildCache));
        Require(perf.ArgumentList.SequenceEqual(["perf-report", "/games/a", "--send", "--output", "json", "--cache", Path.Combine("/w", Disk.LongshotBuildCache)]),
                "perf-report --cache: the launcher's.");
    }
}
