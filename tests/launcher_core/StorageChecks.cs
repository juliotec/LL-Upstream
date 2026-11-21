using LongshotLauncher.Core;

/// The Storage page's core (Core/Storage.cs): what Longshot keeps on the PC, by what it is for, each file counted once; what may
/// go, one item at a time and only what it is; and what stays after a build (only that build's work)
static partial class Program
{
    static void StorageChecks()
    {
        // (every kind in one group with a name, only the ones that may go with a cost; every part of the work folder in one kind)
        Require(Storage.Kinds.Select(k => k.Kind).Distinct().Count() == Enum.GetValues<Storage.Kind>().Length
                && Storage.Kinds.All(k => Storage.Groups.Any(g => g.Group == k.Group) && k.About.Length > 0 && (k.Name.Length > 0 || k.Kind is Storage.Kind.ShaderCache or Storage.Kind.Programs or Storage.Kind.Unlisted))
                && Storage.Groups.Select(g => g.Group).SequenceEqual(Enum.GetValues<Storage.Group>()),
                "Storage: every kind once, in one of the groups, named (a version's items by the version).");
        Require(Disk.WorkParts.All(p => Enum.GetValues<Storage.Kind>().Count(k => Storage.WorkPartsOf(k).Contains(p)) == 1),
                "Storage: every part of the build files folder is one kind's.");
        Require(!Storage.Info(Storage.Kind.GameFiles).Cost.HasValueText() && !Storage.Info(Storage.Kind.Saves).Cost.HasValueText()
                && !Storage.Info(Storage.Kind.ModsAndSettings).Cost.HasValueText() && Storage.Info(Storage.Kind.Programs).Cost.HasValueText()
                && !Storage.Info(Storage.Kind.Builders).Cost.HasValueText(),
                "Storage: the game files, the builders, the saves, the mods and the settings never go from here; a version does, through Home's delete.");

        // (two versions of one dump, their game files one copy by hard links; their own programs, shader caches, mods, logs)
        var root = Folder("storage");
        var linkedTarget = Path.Combine(root, "linked-target");
        Touch(Path.Combine(linkedTarget, "data"), "linked data");
        var linkedRoot = Path.Combine(root, "linked-root");
        if (FileLinks.TryDirectoryLink(linkedRoot, linkedTarget))
            Require(Disk.FolderBytes(linkedRoot) == 0 && Disk.Measure(new[] { linkedRoot })[0].Bytes == 0,
                    "Storage: a partition root that is a symlink or junction is not followed.");
        string Game(string name, string romfs)
        {
            var g = Path.Combine(root, "builds", name);
            Touch(Path.Combine(g, "longshot.exe"), new string('p', 300));
            Touch(Path.Combine(g, "bin", "longshot_vk.dll"), new string('p', 200));
            Touch(Path.Combine(g, ".longshot_exefs", "main"), new string('e', 50));
            Touch(Path.Combine(g, "cache", "longshot_programs.bin"), new string('s', 1000));
            Touch(Path.Combine(g, "cache", "longshot_pipelines.bin"), new string('n', 40));   // (the pipeline notes: the program's)
            Touch(Path.Combine(g, "mods", "m.txt"), new string('m', 7));
            Touch(Path.Combine(g, "logs", "longshot_log.txt"), new string('l', 11));
            if (romfs.Length == 0) Touch(Path.Combine(g, "romfs", "data.bin"), new string('r', 5000));
            else
            {
                Directory.CreateDirectory(Path.Combine(g, "romfs"));
                Require(FileLinks.TryHardLink(romfs, Path.Combine(g, "romfs", "data.bin")), "A hard link in the test folder.");
            }
            return g;
        }
        var a = Game("v3.0.3", "");
        var b = Game("nightly-492", Path.Combine(a, "romfs", "data.bin"));
        var work = Path.Combine(root, "work");
        var stamp = "0123456789abcdef0123\n";
        Touch(Path.Combine(work, "system_headers.txt"), stamp);
        Touch(Path.Combine(work, "objcache", "a.o"), new string('o', 100));
        Touch(Path.Combine(work, "sa", "gen", "image.json"), new string('c', 130));
        var mine = Path.Combine(work, Disk.ObjectStore, "0123456789abcdef");
        Directory.CreateDirectory(mine);
        Require(FileLinks.TryHardLink(Path.Combine(work, "objcache", "a.o"), Path.Combine(mine, "a.o")), "A hard link in the test folder.");
        Touch(Path.Combine(mine, "old.o"), new string('o', 60));
        Touch(Path.Combine(work, Disk.LiftStore, "k", "sa", "gen", "code_1.c"), new string('c', 160));
        Touch(Path.Combine(work, "exefs", "main"), new string('e', 25));
        Touch(Path.Combine(work, "My notes.txt"), "the player's");
        var tools = Path.Combine(root, "toolchains");
        Touch(Path.Combine(tools, "v3.0.3", "nx2", "make_game.py"), new string('t', 90));
        Touch(Path.Combine(tools, ".downloads", "abc.zip.part"), new string('d', 33));
        Touch(Path.Combine(tools, "nightly-493.part", "x"), new string('d', 17));
        var ngx = Path.Combine(root, "ngx-sdk");
        Touch(Path.Combine(ngx, "nvngx_dlss.dll"), new string('g', 70));
        var saves = Path.Combine(root, "saves");
        Touch(Path.Combine(saves, "save", "0", "file1.sav"), new string('v', 9));
        var logs = Path.Combine(root, "launcher-logs");
        Touch(Path.Combine(logs, LauncherLog.FileName), new string('L', 5));
        Touch(Path.Combine(logs, LauncherLog.PreviousName), new string('L', 6));
        var places = new Storage.Places(work, tools, logs, new[] { ngx, Path.Combine(root, "xess-sdk") },
                                        new[] { new Storage.Game("a", "v3.0.3", a), new Storage.Game("b", "Nightly 492", b) }, new[] { saves });

        var items = Storage.Measure(places);
        long Of(Storage.Kind k) => items.Where(i => i.Kind == k).Sum(i => i.Bytes);
        Require(Of(Storage.Kind.GameFiles) == 5000 + 50 + 50, "Storage: the game files one copy for both versions, their programs' unpacked exefs each: " + Of(Storage.Kind.GameFiles));
        Require(Of(Storage.Kind.Programs) == 2 * (300 + 200 + 40), "Storage: each version's program, with its pipeline notes, without its shader caches: " + Of(Storage.Kind.Programs));
        var programs = items.Where(i => i.Kind == Storage.Kind.Programs).ToList();
        Require(programs.Count == 2 && programs.All(x => x.Bytes == 540 && x.Game != null) && programs.Select(x => x.Game!.Id).OrderBy(x => x).SequenceEqual(new[] { "a", "b" }),
                "Storage: one installed game item a version, for its row's delete.");
        Require(Storage.Remove(programs[0], places, false, false) == Storage.Refusal.NotRemovable && Directory.Exists(a),
                "Storage: a version is never deleted by Storage.Remove (Home's delete takes it, its saves first).");
        var caches = items.Where(i => i.Kind == Storage.Kind.ShaderCache).ToList();
        Require(caches.Count == 2 && caches.All(c => c.Bytes == 1000 && c.Frees == 1000 && c.Game != null) && caches.Select(c => c.Game!.Id).OrderBy(x => x).SequenceEqual(new[] { "a", "b" }),
                "Storage: one shader cache item a version.");
        var linkedCache = Path.Combine(b, "cache", "longshot_programs.bin");
        File.Delete(linkedCache);
        Require(FileLinks.TryHardLink(Path.Combine(a, "cache", "longshot_programs.bin"), linkedCache), "A shared shader cache.");
        var sharedCaches = Storage.Measure(places).Where(i => i.Kind == Storage.Kind.ShaderCache).ToList();
        Require(sharedCaches.Sum(i => i.Bytes) == 1000 && sharedCaches.All(i => i.Frees == 0 && i.HasClearableFiles),
                "Hard-linked caches count once and neither version frees the shared file alone.");
        File.Delete(linkedCache);
        Touch(linkedCache, new string('s', 1000));
        var directoryStore = Path.Combine(root, "directory-store");
        Touch(Path.Combine(directoryStore, "spirv.pack"), "store");
        Touch(Path.Combine(directoryStore, "spirv.pack.backup", "keep"), "not a store file");
        Require(Storage.Measure(places with { ShaderStore = directoryStore }).Where(i => i.Kind == Storage.Kind.ShaderStore).Sum(i => i.Bytes) == 5,
                "A store-named directory is not a clearable store file.");
        Require(Of(Storage.Kind.RecentCode) == stamp.Length + 100 && Of(Storage.Kind.RecentTranslation) == 130,
                "Storage: the most recent build's compiled code (with its headers' stamp) and translation.");
        Require(Of(Storage.Kind.OlderCode) == 60 && Of(Storage.Kind.OlderTranslations) == 160 && Of(Storage.Kind.Unfinished) == 25,
                "Storage: the object store less what is one file with objcache, the lift store, a stopped build's leftovers: " + Of(Storage.Kind.OlderCode));
        Require(Of(Storage.Kind.Builders) == 90 && Of(Storage.Kind.UnfinishedDownloads) == 33 + 17 && Of(Storage.Kind.GraphicsLibraries) == 70,
                "Storage: the versions' builders without what a fetch left half made; the SDKs.");
        Require(Of(Storage.Kind.Saves) == 9 && Of(Storage.Kind.ModsAndSettings) == 14 && Of(Storage.Kind.Logs) == 2 * 11 + 5 + 6,
                "Storage: saves, mods and logs, the launcher's and each version's.");
        var sharedUser = Path.Combine(root, "shared-user");
        Touch(Path.Combine(sharedUser, "mods.json"), "shared");
        Touch(Path.Combine(sharedUser, "save", "file"), "save");
        Touch(Path.Combine(a, "longshot_options.ini"), "options");
        Touch(Path.Combine(sharedUser, "mouse_sens.txt"), "mouse");
        Touch(Path.Combine(a, "user", "mouse_sens.txt"), "sensitivity");
        Touch(Path.Combine(a, "user", "present.txt"), "present");
        var withSettings = Storage.Measure(places with { SettingsFolders = new[] { sharedUser } });
        Require(withSettings.Where(i => i.Kind == Storage.Kind.ModsAndSettings).Sum(i => i.Bytes) == 14 + 6 + 7 + 5 + 11 + 7
                && withSettings.Where(i => i.Kind == Storage.Kind.Saves).Sum(i => i.Bytes) == 9
                && withSettings.Where(i => i.Kind == Storage.Kind.Programs).Sum(i => i.Bytes) == 1080,
                "Shared and older per-game settings are counted as player data, without the shared save or program files.");
        File.Delete(Path.Combine(a, "longshot_options.ini"));
        File.Delete(Path.Combine(a, "user", "mouse_sens.txt"));
        File.Delete(Path.Combine(a, "user", "present.txt"));
        Directory.Delete(sharedUser, true);
        var sharedMods = Path.Combine(root, "linked-user");
        Touch(Path.Combine(sharedMods, "mods", "pack"), "shared mod");
        var linkedGame = Path.Combine(root, "linked-game");
        Directory.CreateDirectory(linkedGame);
        if (FileLinks.TryDirectoryLink(Path.Combine(linkedGame, "user"), sharedMods))
        {
            var linkedPlaces = places with { Games = new[] { new Storage.Game("linked", "linked", linkedGame) }, SettingsFolders = new[] { sharedMods } };
            Require(Storage.Measure(linkedPlaces).Where(i => i.Kind == Storage.Kind.ModsAndSettings).Sum(i => i.Bytes) == 10,
                    "A linked user directory does not count the shared mods twice.");
        }
        Require(items.Sum(i => i.Bytes) == 5100 + 1080 + 2000 + stamp.Length + 100 + 130 + 60 + 160 + 25 + 90 + 50 + 70 + 9 + 14 + 33,
                "Storage: every byte once in all: " + items.Sum(i => i.Bytes));
        Require(items.Where(i => i.Game == null).Select(i => i.Kind).Distinct().Count() == items.Count(i => i.Game == null)
                && items.All(i => (i.Game != null) == (i.Kind is Storage.Kind.ShaderCache or Storage.Kind.Programs or Storage.Kind.GameFiles)),
                "Storage: one item a kind, but for a version's program and shader caches (one a version) and the game files (one a copy).");
        var copy = items.Single(i => i.Kind == Storage.Kind.GameFiles);
        Require(copy.Bytes == 5100 && copy.Frees == 5100 && copy.Users!.Select(u => u.Id).SequenceEqual(new[] { "a", "b" }) && copy.Game!.Id == "a",
                "Storage: two versions of one dump are one copy of the game files, used by both, freed once both go.");

        // (what may go now: never while Longshot runs for what it holds, never the tools' staging while a fetch runs)
        Storage.Item Item(Storage.Kind k) => Storage.Measure(places).First(i => i.Kind == k);
        Require(Storage.CanRemove(caches[0], running: true, downloading: false) == Storage.Refusal.Running
                && Storage.CanRemove(Item(Storage.Kind.Logs), running: true, downloading: false) == Storage.Refusal.Running
                && Storage.CanRemove(Item(Storage.Kind.Programs), running: true, downloading: false) == Storage.Refusal.Running
                && Storage.CanRemove(Item(Storage.Kind.Programs), running: false, downloading: false) == Storage.Refusal.None
                && Storage.CanRemove(Item(Storage.Kind.UnfinishedDownloads), running: false, downloading: true) == Storage.Refusal.Downloading
                && Storage.CanRemove(Item(Storage.Kind.OlderCode), running: true, downloading: true) == Storage.Refusal.None
                && Storage.CanRemove(Item(Storage.Kind.GameFiles), running: false, downloading: false) == Storage.Refusal.NotRemovable
                && Storage.CanRemove(Item(Storage.Kind.Saves), running: false, downloading: false) == Storage.Refusal.NotRemovable,
                "Storage: refused while Longshot or a download holds it, and never the game files or the saves.");
        Require(Storage.Remove(caches[0], places, running: true, downloading: false) == Storage.Refusal.Running
                && File.Exists(Path.Combine(caches[0].Game!.Folder, "cache", "longshot_programs.bin")), "Storage: nothing cleared while Longshot runs.");

        // (each item on its drive: one of another drive lets nothing on this one go)
        var here = Disk.DriveName(root);
        Require(Storage.Measure(places).All(i => Storage.SameDrive(i.Drive, here)), "Storage: each item tagged with the drive its files are on.");
        Require(Storage.Remove(Item(Storage.Kind.Logs) with { Drive = "elsewhere" }, places, false, false) == Storage.Refusal.None
                && File.Exists(Path.Combine(logs, LauncherLog.PreviousName)) && File.Exists(Path.Combine(a, "logs", "longshot_log.txt"))
                && Storage.Remove(Item(Storage.Kind.OlderCode) with { Drive = "elsewhere" }, places, false, false) == Storage.Refusal.None
                && Directory.Exists(Path.Combine(work, Disk.ObjectStore)),
                "Storage: an item of another drive lets nothing on this one go.");

        // (one item at a time, and only what it is)
        Require(Storage.Remove(Item(Storage.Kind.OlderCode), places, false, false) == Storage.Refusal.None
                && !Directory.Exists(Path.Combine(work, Disk.ObjectStore)) && File.Exists(Path.Combine(work, "objcache", "a.o"))
                && Directory.Exists(Path.Combine(work, Disk.LiftStore)) && File.Exists(Path.Combine(work, "My notes.txt")),
                "Storage: older builds' code goes alone, the most recent build's objects stay.");
        var cacheA = caches.First(c => c.Game!.Id == "a");
        Require(Storage.Remove(cacheA, places, false, false, cachesHeld: () => false) == Storage.Refusal.None
                && !File.Exists(Path.Combine(a, "cache", "longshot_programs.bin")) && File.Exists(Path.Combine(a, "cache", "longshot_pipelines.bin"))
                && File.Exists(Path.Combine(b, "cache", "longshot_programs.bin")),
                "Storage: one version's shader caches go, its pipeline notes and the other version's caches stay.");
        Require(Storage.Remove(Item(Storage.Kind.UnfinishedDownloads), places, false, false) == Storage.Refusal.None
                && !Directory.Exists(Path.Combine(tools, ".downloads")) && !Directory.Exists(Path.Combine(tools, "nightly-493.part"))
                && File.Exists(Path.Combine(tools, "v3.0.3", "nx2", "make_game.py")),
                "Storage: a fetch's leftovers go, the versions' builders stay.");
        Touch(Path.Combine(logs, "notes.txt"), "notes");
        Touch(Path.Combine(a, "logs", "export.txt"), "export");
        Touch(Path.Combine(a, "longshot_log.1.txt"), "old");
        Require(Storage.Measure(places).Where(i => i.Kind == Storage.Kind.Logs).Sum(i => i.Bytes) == 36,
                "Storage: only known log files, including the loose rotation, are measured.");
        var logResult = (Removed: 0, Skipped: 0, Failed: 0);
        Require(Storage.Remove(Item(Storage.Kind.Logs), places, false, false, ownLog: Path.Combine(logs, LauncherLog.FileName),
                    logOutcome: (removed, skipped, failed) => logResult = (removed, skipped, failed)) == Storage.Refusal.None
                && logResult == (4, 1, 0)
                && File.Exists(Path.Combine(logs, LauncherLog.FileName)) && !File.Exists(Path.Combine(logs, LauncherLog.PreviousName))
                && !File.Exists(Path.Combine(a, "logs", "longshot_log.txt")) && !File.Exists(Path.Combine(a, "longshot_log.1.txt"))
                && File.Exists(Path.Combine(logs, "notes.txt")) && File.Exists(Path.Combine(a, "logs", "export.txt")) && File.Exists(Path.Combine(a, "mods", "m.txt")),
                "Storage: earlier logs go, the one this run writes and the mods stay.");
        if (OperatingSystem.IsWindows())
        {
            var lockedLog = Path.Combine(logs, LauncherLog.PreviousName);
            Touch(lockedLog, "locked");
            var release = HoldOpen(lockedLog);
            try
            {
                Storage.Remove(Item(Storage.Kind.Logs), places, false, false, ownLog: Path.Combine(logs, LauncherLog.FileName),
                    logOutcome: (removed, skipped, failed) => logResult = (removed, skipped, failed));
                Require(logResult.Failed == 1 && logResult.Skipped == 1 && File.Exists(lockedLog),
                    "Storage: a locked log is reported as failed and the current log as kept.");
            }
            finally { release(); }
        }
        Require(Storage.Remove(Item(Storage.Kind.GraphicsLibraries), places, false, false) == Storage.Refusal.None && !Directory.Exists(ngx),
                "Storage: the SDKs go, downloaded again when a build needs them.");

        var folderResult = (Removed: 0, Skipped: 0, Failed: 0);
        Storage.Remove(items.First(i => i.Kind == Storage.Kind.GraphicsLibraries), places, false, false,
            folderOutcome: (removed, skipped, failed) => folderResult = (removed, skipped, failed));
        Require(folderResult.Removed == 0 && folderResult.Skipped == 1, "Storage: the vanished SDK is skipped; an SDK absent at measurement is not a target.");
        Touch(Path.Combine(ngx, "nvngx_dlss.dll"), "sdk");
        var oneSdk = Storage.Measure(places).First(i => i.Kind == Storage.Kind.GraphicsLibraries);
        Storage.Remove(oneSdk, places, false, false, folderOutcome: (removed, skipped, failed) => folderResult = (removed, skipped, failed));
        Require(folderResult == (1, 0, 0), "Storage: an SDK absent throughout is not reported as skipped.");
        var missingWork = places with { Work = Path.Combine(root, "missing-work") };
        Storage.Remove(new Storage.Item(Storage.Kind.OlderCode, 10, 10), missingWork, false, false,
            folderOutcome: (removed, skipped, failed) => folderResult = (removed, skipped, failed));
        Require(folderResult.Removed == 0 && folderResult.Skipped > 0, "Storage: vanished work is reported as skipped.");

        var imported = Path.Combine(root, "imported");
        Touch(Path.Combine(imported, "logs"), "not a directory");
        Touch(Path.Combine(imported, "longshot_log.txt"), "loose log");
        var importedPlaces = places with { Games = new[] { new Storage.Game("imported", "Imported", imported) } };
        Storage.Remove(new Storage.Item(Storage.Kind.Logs, 1, 1), importedPlaces, false, false);
        Require(File.Exists(Path.Combine(imported, "logs")) && !File.Exists(Path.Combine(imported, "longshot_log.txt")),
                "Storage: a file named logs does not stop loose log cleanup.");

        // (after a build: only its own work stays; turned off, none of it, and never the player's own files)
        Touch(Path.Combine(work, Disk.ObjectStore, "0123456789abcdef", "b.o"), "o");
        Storage.AfterBuild(work, keep: true);
        Require(Directory.Exists(Path.Combine(work, Disk.ObjectStore))
                && File.Exists(Path.Combine(work, "objcache", "a.o")) && Directory.Exists(Path.Combine(work, "sa")) && File.Exists(Path.Combine(work, "system_headers.txt")),
                "Storage: after a build the stores and the build's own work stay.");
        Storage.AfterBuild(work, keep: false);
        Require(!Directory.Exists(Path.Combine(work, "objcache")) && !Directory.Exists(Path.Combine(work, "sa")) && !File.Exists(Path.Combine(work, "system_headers.txt"))
                && File.Exists(Path.Combine(work, "My notes.txt")), "Storage: keeping off, the build's work goes too, the player's files stay.");
        Require(Storage.Measure(places with { Work = Path.Combine(root, "none"), Toolchains = Path.Combine(root, "none2") }).All(i => i.Bytes >= 0),
                "Storage: folders that are not there measure nothing.");

        // (game folders no row lists: an old version an update left, its game files linked to a listed one's; one with mods)
        var old = Game("nightly-130", Path.Combine(a, "romfs", "data.bin"));
        var modded = Game("nightly-120", "");
        var withOld = places with
        {
            Games = places.Games.Concat(new[] { new Storage.Game("unlisted:" + old, "Nightly 130", old, Unlisted: true),
                                                new Storage.Game("unlisted:" + modded, "Nightly 120", modded, Unlisted: true, HoldsYours: true) }).ToList(),
        };
        var measured = Storage.Measure(withOld);
        var unlisted = measured.Where(i => i.Kind == Storage.Kind.Unlisted).ToList();
        var oldItem = unlisted.FirstOrDefault(i => i.Game!.Folder == old);
        var moddedItem = unlisted.First(i => i.Game!.Folder == modded);
        Require(unlisted.Count == 2 && unlisted.All(i => i.Group == Storage.Group.Games && i.Removable) && oldItem != null
                && oldItem.Bytes == 300 + 200 + 1000 + 40 + 7 + 11 && oldItem.Frees == oldItem.Bytes + 50
                && measured.Where(i => i.Kind == Storage.Kind.GameFiles).Sum(i => i.Bytes) == 5000 + 50 + 50 + 50 + 5050,
                "Storage: a folder no row lists is one item of Installed games; its game files are counted with the game files, the ones it "
                + "shares with a listed version not freed with it: " + (oldItem?.Bytes ?? -1) + " " + (oldItem?.Frees ?? -1));
        Require(moddedItem.Bytes == 1558 && moddedItem.Frees == 1558 + 5050,
                "Storage: deleting a folder no row lists frees its own copy of the game files too: " + moddedItem.Frees);
        var copies = measured.Where(i => i.Kind == Storage.Kind.GameFiles).ToList();
        Require(copies.Count == 2
                && copies.Any(c => c.Users!.Select(u => u.Folder).SequenceEqual(new[] { a, b, old }) && c.Bytes == 5150 && c.Frees == 5150)
                && copies.Any(c => c.Users!.Select(u => u.Folder).SequenceEqual(new[] { modded }) && c.Bytes == 5050 && c.Frees == 5050),
                "Storage: each copy of the game files its own item, with the versions using it, listed or not: "
                + string.Join("; ", copies.Select(c => string.Join(",", c.Users!.Select(u => u.Name)) + " " + c.Bytes + "/" + c.Frees)));
        Require(Storage.CanRemove(oldItem!, running: true, downloading: false) == Storage.Refusal.Running
                && Storage.CanRemove(oldItem!, running: false, downloading: false) == Storage.Refusal.None
                && Storage.CanRemove(moddedItem, running: false, downloading: false) == Storage.Refusal.HoldsYours
                && Storage.Remove(moddedItem, withOld, false, false, deleteFolder: _ => Directory.Delete(modded, true)) == Storage.Refusal.HoldsYours
                && Directory.Exists(modded),
                "Storage: a folder no row lists is not deleted while Longshot runs, nor one with mods of the player's in it.");
        Require(Storage.Remove(oldItem! with { Drive = "elsewhere" }, withOld, false, false, deleteFolder: _ => Directory.Delete(old, true)) == Storage.Refusal.NotRemovable
                && Storage.Remove(oldItem!, withOld, false, false) == Storage.Refusal.NotRemovable && Directory.Exists(old),
                "Storage: nor one of another drive, nor without the launcher's delete.");
        Storage.Game? deleted = null;
        Require(Storage.Remove(oldItem!, withOld, false, false, deleteFolder: g => { deleted = g; Directory.Delete(g.Folder, true); }) == Storage.Refusal.None
                && deleted?.Folder == old && !Directory.Exists(old) && File.Exists(Path.Combine(a, "romfs", "data.bin")),
                "Storage: a folder no row lists goes whole through the launcher's delete; the listed version's game files stay.");

        // (a copy whose files another folder links to, outside what the page measures (a developer's base): not freed with its versions)
        var elsewhere = Path.Combine(root, "base", "romfs");
        Directory.CreateDirectory(elsewhere);
        Require(FileLinks.TryHardLink(Path.Combine(modded, "romfs", "data.bin"), Path.Combine(elsewhere, "data.bin")), "A hard link in the test folder.");
        var again = Storage.Measure(withOld with { Games = withOld.Games.Where(g => g.Folder != old).ToList() });
        Require(again.Single(i => i.Kind == Storage.Kind.GameFiles && i.Users![0].Folder == modded) is { Bytes: 5050, Frees: 50 }
                && again.Single(i => i.Kind == Storage.Kind.Unlisted) is { Bytes: 1558, Frees: 1558 + 50 },
                "Storage: game files linked from a folder outside the page's are not freed with the versions that use them.");
        var held = again.Single(i => i.Kind == Storage.Kind.GameFiles && i.Users![0].Folder == modded).Elsewhere ?? Array.Empty<string>();
        Require(OperatingSystem.IsWindows() ? held.SequenceEqual(new[] { Path.Combine(root, "base") }, StringComparer.OrdinalIgnoreCase) : held.Count == 0,
                "Storage: the folders outside the page's a copy is linked from, named where the system can tell (Windows): " + string.Join("; ", held));
        Require(again.Single(i => i.Kind == Storage.Kind.GameFiles && i.Users![0].Folder == a).Elsewhere is null or { Count: 0 },
                "Storage: a copy nothing else links to names no other folder.");
        var sep = Path.DirectorySeparatorChar;
        string P(params string[] parts) => (OperatingSystem.IsWindows() ? "E:" : "") + sep + string.Join(sep, parts);
        Require(Storage.CopyHolders(P("games", "nightly", "romfs", "Actor", "a.bgyml"), new[] { P("games", "nightly") }, _ => new[]
                {
                    P("games", "nightly", "romfs", "Actor", "a.bgyml"), P("dev", "bases", "4743", "game", "romfs", "Actor", "a.bgyml"),
                    P("dev", "bases", "4743", "game-out", "romfs", "Actor", "a.bgyml"),
                }).SequenceEqual(new[] { P("dev", "bases", "4743", "game"), P("dev", "bases", "4743", "game-out") })
                && Storage.CopyHolders("x", Array.Empty<string>(), _ => new[] { P("a", "romfs", "f") }).Count == 0,
                "Storage: a copy's other holders are the folders its file's names are in, less the place in romfs/ they share and the page's own games.");
        Require(Storage.CopyHolders(P("games", "nightly", "romfs", "Actor", "a.bgyml"), new[] { P("games", "nightly") }, _ => new[]
                { P("games", "nightly", "romfs", "Actor", "a.bgyml"), P("outside", "renamed") })
                .SequenceEqual(new[] { P("outside") }), "A differently named link is shown by its containing folder.");
        Require(Storage.CopyHolders(P("games", "nightly", "romfs", "f"), new[] { P("games", "nightly") }, _ => new[]
                { P("games", "nightly", "romfs", "f"), P("romfs", "f") }).SequenceEqual(new[] { P() }),
                "A holder at the filesystem root keeps its root separator.");

        // (the shader store every version shares: its own files only, in a folder that may hold others; cleared on its own)
        var shaderStore = Path.Combine(root, "shader-store");
        Touch(Path.Combine(shaderStore, "spirv.pack"), new string('s', 100));
        Touch(Path.Combine(shaderStore, "spirv.idx"), new string('i', 10));
        Touch(Path.Combine(shaderStore, "notes.txt"), "the player's");
        var withStore = places with { ShaderStore = shaderStore };
        var storeItem = Storage.Measure(withStore).SingleOrDefault(i => i.Kind == Storage.Kind.ShaderStore);
        Require(storeItem is { Bytes: 110, Frees: 110, Group: Storage.Group.ShaderCaches } && storeItem.Removable,
                "Storage: the shared shader store is an item of Shader caches, its own files only: " + storeItem?.Bytes);
        var restoreStore = OperatingSystem.IsWindows() ? DenyListing(shaderStore) : NoPermissions(shaderStore);
        if (restoreStore != null)
        {
            try
            {
                var failed = false;
                try { Storage.Remove(storeItem!, withStore, false, false, cachesHeld: () => false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failed = true; }
                Require(failed, "Storage: an unreadable shader store reports failure.");
            }
            finally { restoreStore(); }
        }
        Require(Storage.CanRemove(storeItem!, running: true, downloading: false) == Storage.Refusal.Running
                && Storage.Remove(storeItem!, withStore, false, false, cachesHeld: () => true) == Storage.Refusal.Running
                && File.Exists(Path.Combine(shaderStore, "spirv.pack")),
                "Storage: the shader store is not cleared while Longshot runs.");
        Require(Storage.Remove(storeItem!, withStore, false, false, cachesHeld: () => false) == Storage.Refusal.None
                && !File.Exists(Path.Combine(shaderStore, "spirv.pack")) && !File.Exists(Path.Combine(shaderStore, "spirv.idx"))
                && File.Exists(Path.Combine(shaderStore, "notes.txt")),
                "Storage: clearing the shader store takes its files and leaves the rest of its folder.");
    }

    static bool HasValueText(this string? s) => !string.IsNullOrEmpty(s);
}
