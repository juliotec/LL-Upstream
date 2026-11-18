using LongshotLauncher.Core;

/// The old versions' folders updates left on the disk (Core/Leftovers.cs): which the home offers to delete, which it leaves
static partial class Program
{
    static void LeftoverChecks()
    {
        var builds = Folder("leftovers");
        string Game(string name, string? version = null)
        {
            var dir = Path.Combine(builds, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, GameFolder.StampName), $"version={version ?? name}\n");
            return dir;
        }
        void Write(string path, string text = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        string Names(IEnumerable<Leftover> found) => string.Join(" ", found.Select(l => Path.GetFileName(l.Folder)));
        var p = Platform.Linux;

        var nightly = Game("nightly", "nightly-189-aa95d35f");            // (the build's own folder, updated in place)
        Game("nightly-130-0caf0618");                                       // (an update's leftover, its saves already taken in,
        Write(Path.Combine(builds, "nightly-130-0caf0618", "user", "save.moved-20261115-121040", "account.sav"));
        Write(Path.Combine(builds, "nightly-130-0caf0618", ".longshot_romfs", "x"));   // and the game files make_game.py read kept)
        Game("nightly-150-11111111");                                       // (one whose save was not taken in yet)
        Write(Path.Combine(builds, "nightly-150-11111111", "user", "save", "account.sav"));
        Game("nightly-160-22222222");                                       // (one with the player's mods)
        Write(Path.Combine(builds, "nightly-160-22222222", "mods", "texture-pack", "a.dds"));
        Game("nightly-161-33333333");                                       // (an empty mods/ is nothing of the player's)
        Directory.CreateDirectory(Path.Combine(builds, "nightly-161-33333333", "mods", "empty"));
        Write(Path.Combine(builds, "rc", ".longshot_romfs", "x"));         // (a first build that stopped: no stamp, no game folder)
        Directory.CreateDirectory(Path.Combine(builds, "photos"));          // (not a game folder)
        Write(Path.Combine(builds, "photos", "a.png"));
        Game("v3.0.0-rc.2");                                                // (kept by the player)
        Write(Path.Combine(builds, "loose-file.txt"));

        var found = Leftovers.Find(builds, new[] { nightly }, p, kept: new[] { Path.Combine(builds, "v3.0.0-rc.2") });
        Require(Names(found) == "nightly-130-0caf0618 nightly-161-33333333",
                "Leftovers: the unlisted game folders with nothing of the player's (got: " + Names(found) + ").");
        Require(found[0].Version == "nightly-130-0caf0618" && found[0].Shown == "Nightly 130 · 0caf0618",
                "A leftover's version from its stamp, shown as players see it.");
        Require(Names(Leftovers.Find(builds, new[] { nightly }, p)).Contains("v3.0.0-rc.2", StringComparison.Ordinal),
                "A kept folder is offered again only when it is no longer kept.");

        // (a build's folder inside another game folder, as a 2.x install's ~/Games/Longshot holds the builds; and the reverse)
        var outer = Game("outer");
        var inner = Game(Path.Combine("outer", "stable"), "v3.0.0");
        Require(!Leftovers.Find(builds, new[] { nightly, inner }, p).Any(l => l.Folder == outer),
                "A game folder that holds a build's folder is not a leftover.");
        Require(Leftovers.Find(builds, new[] { nightly }, p, keep: new[] { Path.Combine(outer, "home") }).All(l => l.Folder != outer),
                "Nor one that holds the launcher's home.");
        Require(Leftovers.Find(Path.Combine(builds, "nowhere"), new[] { nightly }, p).Count == 0, "No builds folder: no leftovers.");

        // (Delete, as the home does it: LauncherService.DeleteLeftover keeps the builds' folders and the launcher's home)
        var keepAll = new[] { nightly, inner };
        BuildRegistry.DeleteFolder(found[0].Folder, p, keepAll);
        Require(!Directory.Exists(found[0].Folder) && GameFolder.IsGameFolder(nightly, p), "Delete takes the leftover, its save.moved-* with it, and leaves the build.");
        var refused = false;
        try { BuildRegistry.DeleteFolder(outer, p, keepAll); }
        catch (IOException) { refused = true; }
        Require(refused && Directory.Exists(inner), "A folder that holds a build's folder is never deleted, even if asked.");

        // (a stamp with no version: shown by its folder's name)
        var noVersion = Path.Combine(builds, "old build");
        Directory.CreateDirectory(noVersion);
        File.WriteAllText(Path.Combine(noVersion, GameFolder.StampName), "builder=x\n");
        Require(Leftovers.Find(builds, new[] { nightly }, p).FirstOrDefault(l => l.Folder == noVersion) is { Version: null, Shown: "old build" },
                "A leftover with no version in its stamp: its folder's name.");

        // (issue #102: an update before #104 built v3.0.0-rc.4 in a new folder and its row stayed on rc.3, whose game is gone. The
        // rc.4 game is the row's again (LostUpdate), and never offered for deletion as an old version)
        var lost = Folder("leftovers-lost");
        string Built(string name, string version)
        {
            var dir = Path.Combine(lost, name);
            Directory.CreateDirectory(Path.Combine(dir, "bin"));
            File.WriteAllText(Path.Combine(dir, GameFolder.StampName), $"version={version}\n");
            File.WriteAllText(Path.Combine(dir, "longshot"), "setup");
            File.WriteAllText(Path.Combine(dir, "bin", "longshot"), "game");
            return dir;
        }
        var rc3 = Built("v3.0.0-rc.3", "v3.0.0-rc.3");
        File.Delete(Path.Combine(rc3, "bin", "longshot"));                   // (its game gone)
        var rc4 = Built("v3.0.0-rc.4", "v3.0.0-rc.4");
        var rc2 = Built("v3.0.0-rc.2", "v3.0.0-rc.2");                       // (older than the row's: not it)
        var night = Built("nightly-200-55555555", "nightly-200-55555555");  // (another channel: not it)
        var row = new BuildEntry { Id = "a", Folder = rc3, Version = "v3.0.0-rc.3", Channel = Versions.Candidate };
        Require(Leftovers.LostUpdate(lost, row, new[] { rc3 }, p) is { Version: "v3.0.0-rc.4" } l4 && l4.Folder == rc4,
                "A row whose game is gone takes back the newer game of its channel its update left in a new folder.");
        Require(Leftovers.LostUpdate(lost, row, new[] { rc3, rc4 }, p) == null, "Not a folder another build lists.");
        Require(Leftovers.LostUpdate(lost, new BuildEntry { Id = "b", Folder = rc4, Version = "v3.0.0-rc.4", Channel = Versions.Candidate },
                                     new[] { rc4 }, p) == null, "Nor for a row whose game plays.");
        var foundLost = Names(Leftovers.Find(lost, new[] { rc3 }, p));
        Require(!foundLost.Contains("rc.4", StringComparison.Ordinal) && !foundLost.Contains("nightly", StringComparison.Ordinal),
                "While no listed build of its channel plays, a channel's games are never offered as old versions (got: " + foundLost + ").");
        var reg = BuildRegistry.Load(Path.Combine(lost, "builds.json"));
        var entry = reg.Add(rc3, "v3.0.0-rc.3", Versions.Candidate, name: "v3.0.0-rc.3");
        reg.Replace(entry.Id, rc4, "v3.0.0-rc.4", entry.Channel);
        Require(Names(Leftovers.Find(lost, reg.Builds.Select(b => b.Folder), p)) == "v3.0.0-rc.2 v3.0.0-rc.3",
                "Once the row has it back, the rc.3 folder and the older rc.2 are the old versions offered.");
        _ = (rc2, night);

        // (links: the player's own arrangement, never offered)
        var linked = Game("nightly-170-44444444");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(linked, "user"), Folder("leftovers-shared-user"));
            Require(Leftovers.Find(builds, new[] { nightly }, p).All(l => l.Folder != linked), "A leftover whose user/ is a link stays.");
            Directory.CreateSymbolicLink(Path.Combine(builds, "linked-game"), nightly);
            Require(Leftovers.Find(builds, Array.Empty<string>(), p).All(l => !l.Folder.EndsWith("linked-game", StringComparison.Ordinal)),
                    "A builds folder entry that is a link is not a leftover.");

            // (the launcher's own link to the shared save, which a gather leaves where it took a save in (Saves.LinkSaves), holds nothing of
            // the player's: the folder is offered as one whose save was renamed aside. A link to anywhere else is still the player's own)
            var windows = new SaveEnvironment(Platform.Windows, k => k == "APPDATA" ? Folder("leftovers-appdata") : null);
            var sharedSave = Path.Combine(windows.LongshotUser, "save");
            Directory.CreateDirectory(sharedSave);
            var own = Game("nightly-171-55555555");
            Directory.CreateDirectory(Path.Combine(own, "user"));
            Directory.CreateSymbolicLink(Path.Combine(own, "user", "save"), sharedSave);
            var theirs = Game("nightly-172-66666666");
            Directory.CreateDirectory(Path.Combine(theirs, "user"));
            Directory.CreateSymbolicLink(Path.Combine(theirs, "user", "save"), Folder("leftovers-synced"));
            var withMods = Game("nightly-173-77777777");
            Directory.CreateDirectory(Path.Combine(withMods, "user"));
            Directory.CreateSymbolicLink(Path.Combine(withMods, "user", "save"), sharedSave);
            Write(Path.Combine(withMods, "mods", "texture-pack", "a.dds"));
            var offered = Names(Leftovers.Find(builds, new[] { nightly }, p, saves: windows));
            Require(offered.Contains("nightly-171-55555555", StringComparison.Ordinal) && !offered.Contains("nightly-172-66666666", StringComparison.Ordinal)
                    && !offered.Contains("nightly-173-77777777", StringComparison.Ordinal),
                    "A folder with the launcher's own link to the shared save is offered; the player's own link, and mods, keep one.");
            Require(!Names(Leftovers.Find(builds, new[] { nightly }, p)).Contains("nightly-171-55555555", StringComparison.Ordinal),
                    "Without the launcher's save folder to compare, a link is the player's own.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("leftovers: the link checks need links (Windows: Developer Mode); skipped: " + e.Message);
        }
    }
}
