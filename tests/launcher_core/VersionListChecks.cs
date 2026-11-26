using LongshotLauncher.Core;

/// One list of installed versions per launcher (Core/VersionLists.cs): the stable launcher's builds.json and this one's
/// versions.json, split once. No game, no network
static partial class Program
{
    static void VersionListChecks(string repo)
    {
        var p = Platforms.Current;
        var data = Folder("lists-data");
        var games = Folder("lists-games");
        // (the stable launcher's: make_game.py's game; this launcher's: a version folder's, a record's, a row marked NupInPlace)
        var old = Path.Combine(games, "stable", "v3.0.0");
        Game(old, "v3.0.0");
        var inVersion = BuilderVersions.GameFolderOf(BuilderVersions.FolderOf(data, "nightly-500-aaaaaaaa"));
        Game(inVersion, "nightly-500-aaaaaaaa");
        var recorded = Path.Combine(games, "nightly", "nightly-501-bbbbbbbb");
        Game(recorded, "nightly-501-bbbbbbbb");
        File.WriteAllText(Path.Combine(recorded, LongshotBuilder.RecordFile), "{}");
        var marked = Path.Combine(games, "nightly", "marked");
        Game(marked, "nightly-502-cccccccc");
        var gone = Path.Combine(games, "gone");
        var stablePath = Path.Combine(data, VersionLists.StableFile);
        var both = BuildRegistry.Load(stablePath);
        var oldRow = both.Add(old, "v3.0.0", Versions.Stable, makeDefault: true);
        var v = both.Add(inVersion, "nightly-500-aaaaaaaa", Versions.Nightly);
        var r = both.Add(recorded, "nightly-501-bbbbbbbb", Versions.Nightly);
        var m = both.Add(marked, "nightly-502-cccccccc", Versions.Nightly);
        m.NupInPlace = true;
        var goneRow = both.Add(gone, "v2.9.0", Versions.Stable);
        both.Save();

        Require(VersionLists.Split(data) == 3, "The split moves this launcher's three rows.");
        var own = BuildRegistry.Load(Path.Combine(data, VersionLists.OwnFile));
        var stable = BuildRegistry.Load(stablePath);
        Require(own.Builds.Select(b => b.Id).Order().SequenceEqual(new[] { v.Id, r.Id, m.Id }.Order())
                && own.Find(m.Id)?.NupInPlace == true && own.Find(v.Id)?.Folder == inVersion,
                "versions.json has this launcher's rows as they were (ids, folders, marks).");
        Require(stable.Builds.Select(b => b.Id).SequenceEqual(new[] { oldRow.Id, goneRow.Id }) && stable.Default?.Id == oldRow.Id,
                "builds.json keeps the stable launcher's rows, its default, and a row whose folder is gone (not known to be ours).");
        Require(VersionLists.StableFolders(data).SequenceEqual(new[] { old, gone }), "The stable launcher's folders, read from builds.json.");

        // (once: with versions.json there, nothing moves again, even a row of ours put back in builds.json by a stop between saves)
        stable.Take(new BuildEntry { Id = "back", Folder = inVersion, Version = "nightly-500-aaaaaaaa", Channel = Versions.Nightly });
        stable.Save();
        Require(VersionLists.Split(data) == 0 && BuildRegistry.Peek(stablePath).Count == 3, "The split runs once.");

        // (none of ours: versions.json starts empty, builds.json untouched; no builds.json: nothing at all)
        var onlyOld = Folder("lists-only-old");
        var list = BuildRegistry.Load(Path.Combine(onlyOld, VersionLists.StableFile));
        list.Add(old, "v3.0.0", Versions.Stable);
        list.Save();
        var before = File.ReadAllText(Path.Combine(onlyOld, VersionLists.StableFile));
        Require(VersionLists.Split(onlyOld) == 0 && File.Exists(Path.Combine(onlyOld, VersionLists.OwnFile))
                && File.ReadAllText(Path.Combine(onlyOld, VersionLists.StableFile)) == before,
                "With none of ours, builds.json stays exactly as it was.");
        var fresh = Folder("lists-fresh");
        Require(VersionLists.Split(fresh) == 0 && !File.Exists(Path.Combine(fresh, VersionLists.OwnFile)), "No builds.json: nothing written.");

        // (an unreadable builds.json is left as it is: not set aside, not emptied)
        var broken = Folder("lists-broken");
        File.WriteAllText(Path.Combine(broken, VersionLists.StableFile), "{ not json");
        Require(VersionLists.Split(broken) == 0 && File.ReadAllText(Path.Combine(broken, VersionLists.StableFile)) == "{ not json"
                && !File.Exists(Path.Combine(broken, VersionLists.StableFile + ".broken")),
                "An unreadable builds.json is left as it is.");

        // (the window, in its source: the split before the list loads, and the stable launcher's folders listed wherever folders
        // are offered, listed again or deleted)
        var service = File.ReadAllText(Path.Combine(repo, "App", "LauncherService.cs"));
        Require(service.IndexOf("VersionLists.Split(home.Root)", StringComparison.Ordinal) is var split and > 0
                && split < service.IndexOf("Registry = BuildRegistry.Load(home.Builds);", StringComparison.Ordinal),
                "The split runs before this launcher's list is loaded.");
        Require(service.Contains("Leftovers.Find(BuildsFolder, Listed,") && service.Contains("Leftovers.All(BuildsFolder, Listed,")
                && service.Contains("Leftovers.LostUpdate(BuildsFolder, b, Listed,") && service.Contains("var keep = StableFolders.Prepend(Home.Root)")
                && service.Contains("Leftovers.Delete(leftover.Folder, Platform, Listed.Append(Home.Root)") && service.Contains(".Concat(StableFolders).Append(Home.Root)"),
                "The stable launcher's folders are never offered as leftovers, listed again, or deleted with another.");
    }
}
