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

        // (links: the player's own arrangement, never offered)
        var linked = Game("nightly-170-44444444");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(linked, "user"), Folder("leftovers-shared-user"));
            Require(Leftovers.Find(builds, new[] { nightly }, p).All(l => l.Folder != linked), "A leftover whose user/ is a link stays.");
            Directory.CreateSymbolicLink(Path.Combine(builds, "linked-game"), nightly);
            Require(Leftovers.Find(builds, Array.Empty<string>(), p).All(l => !l.Folder.EndsWith("linked-game", StringComparison.Ordinal)),
                    "A builds folder entry that is a link is not a leftover.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("leftovers: the link checks need links (Windows: Developer Mode); skipped: " + e.Message);
        }
    }
}
