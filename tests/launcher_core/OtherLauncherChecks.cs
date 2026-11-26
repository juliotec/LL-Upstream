using LongshotLauncher.Core;

/// Another launcher's builds and saves on this PC (Core/OtherLauncher.cs): a player who went from the installed launcher to a
/// portable one, or back, found neither, as each keeps its own list and (Windows) its own save folder
static partial class Program
{
    static void OtherLauncherChecks()
    {
        var root = Folder("other-launcher");
        var env = new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["USERPROFILE"] = Path.Combine(root, "home"),
            ["APPDATA"] = Path.Combine(root, "home", "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(root, "home", "AppData", "Local"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "home", ".local", "share"),
            [LauncherHome.Variable] = Path.Combine(root, "set-home"),   // (ignored: the installed launcher's default home is looked at)
        };
        string? Getenv(string n) => env.TryGetValue(n, out var v) ? v : null;
        var p = Platform.Windows;
        const string uid = "0123456789abcdef0123456789abcdef";
        string Built(string folder, string version)
        {
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            File.WriteAllText(Path.Combine(folder, GameFolder.StampName), $"version={version}\n");
            File.WriteAllText(Path.Combine(folder, "bin", Platforms.GameExe(p)), "game");
            return folder;
        }
        void MakeSave(string user, string tag)
        {
            var save = Path.Combine(user, "save", uid);
            Directory.CreateDirectory(Path.Combine(save, "slot_00"));
            File.WriteAllText(Path.Combine(save, "account.sav"), "account-" + tag);
            File.WriteAllText(Path.Combine(save, "slot_00", "progress.sav"), "progress-" + tag);
        }
        static string Tree(string folder) =>
            !Directory.Exists(folder) ? "" : string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));
        string Names(IEnumerable<string> folders) => string.Join(" ", folders.Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // (the installed launcher: its list in %LOCALAPPDATA%\LongshotLauncher, its saves in %APPDATA%\Longshot; one row's game gone)
        var installedHome = Path.Combine(env["LOCALAPPDATA"]!, "LongshotLauncher");
        var games = Path.Combine(root, "home", "Games", "Longshot");
        var stable = Built(Path.Combine(games, "stable"), "v3.0.0");
        var night = Built(Path.Combine(games, "nightly"), "nightly-150-aaaaaaaa");
        var installedList = BuildRegistry.Load(Path.Combine(installedHome, VersionLists.OwnFile));
        installedList.Add(stable, "v3.0.0", Versions.Stable);
        installedList.Add(night, "nightly-150-aaaaaaaa", Versions.Nightly, makeDefault: true);
        installedList.Add(Path.Combine(games, "gone"), "v2.9.0", Versions.Stable);
        installedList.Save();
        var installedUser = Path.Combine(env["APPDATA"]!, "Longshot");
        MakeSave(installedUser, "installed");
        File.WriteAllText(Path.Combine(installedUser, "mouse_sens.txt"), "2.5");

        // (a portable launcher beside it, nothing in it yet)
        var portableHome = Path.Combine(root, "usb", "Longshot", LauncherLayout.PortableData);
        var portableEnv = new SaveEnvironment(p, Getenv) { UserFolder = Path.Combine(portableHome, "user") };
        var other = OtherLauncher.InstalledOne(p, Getenv, portableHome);
        Require(other is { Installed: true } && BuildRegistry.SameFolder(other.Home, installedHome) && Names(other.Builds.Select(b => b.Folder)) == "nightly stable"
                && other.Profiles.Count == 1 && other.HasAnything,
                "A portable launcher finds the installed one's builds whose game is there, and its saves: " + Names(other?.Builds.Select(b => b.Folder) ?? []));
        Require(OtherLauncher.InstalledOne(p, Getenv, installedHome) == null, "The installed launcher is no other launcher to itself.");

        var reg = BuildRegistry.Load(Path.Combine(portableHome, VersionLists.OwnFile));
        var taken = other!.TakeInto(reg, portableEnv, () => []);
        Require(Names(taken.Added.Select(b => b.Folder)) == "nightly stable" && reg.Default?.Folder == night
                && reg.Builds.Single(b => b.Folder == stable) is { Version: "v3.0.0", Channel: Versions.Stable },
                "Its builds are listed where they are, its default this one's default.");
        Require(taken.SavesCopied && Tree(Path.Combine(portableEnv.LongshotUser, "save")) == Tree(Path.Combine(installedUser, "save"))
                && File.ReadAllText(Path.Combine(portableEnv.LongshotUser, "mouse_sens.txt")) == "2.5"
                && Tree(Path.Combine(installedUser, "save")).Contains("progress-installed"),
                "Its saves are copied into this launcher's empty save folder, with its own settings; the originals stay.");
        reg.Save();
        var again = other.TakeInto(reg, portableEnv, () => []);
        Require(again.Added.Count == 0 && !again.SavesCopied && again.SavesLeft == null && reg.Builds.Count == 2,
                "Taken again: no row twice, and the same saves copied before are nothing to say.");
        MakeSave(installedUser, "installed, played on");
        File.WriteAllText(Path.Combine(installedUser, "save", uid, "slot_00", "progress.sav"), "progress-later");
        var later = other.TakeInto(reg, portableEnv, () => []);
        Require(!later.SavesCopied && later.SavesLeft != null && Tree(Path.Combine(portableEnv.LongshotUser, "save")).Contains("progress-installed")
                && !Tree(Path.Combine(portableEnv.LongshotUser, "save")).Contains("progress-later"),
                "Never a save over a save: different saves stay as they are, and it says Manage saves imports them.");

        // (a save of its own already: never written over, whatever it holds)
        var portable2 = Path.Combine(root, "usb2", LauncherLayout.PortableData);
        var env2 = new SaveEnvironment(p, Getenv) { UserFolder = Path.Combine(portable2, "user") };
        Directory.CreateDirectory(Path.Combine(env2.LongshotUser, "save", uid));
        File.WriteAllText(Path.Combine(env2.LongshotUser, "save", uid, "option.sav"), "mine");
        var kept = other.TakeInto(BuildRegistry.Load(Path.Combine(portable2, VersionLists.OwnFile)), env2, () => []);
        Require(!kept.SavesCopied && kept.SavesLeft != null && Tree(Path.Combine(env2.LongshotUser, "save")) == $"{uid}{Path.DirectorySeparatorChar}option.sav=mine",
                "A save folder with any file in it is left as it is.");
        var portable3 = Path.Combine(root, "usb3", LauncherLayout.PortableData);
        var env3 = new SaveEnvironment(p, Getenv) { UserFolder = Path.Combine(portable3, "user") };
        var running = other.TakeInto(BuildRegistry.Load(Path.Combine(portable3, VersionLists.OwnFile)), env3, () => ["Longshot"]);
        Require(running.Added.Count == 2 && !running.SavesCopied && running.SavesLeft != null && !Directory.Exists(Path.Combine(env3.LongshotUser, "save")),
                "While Longshot runs, the builds are listed and the saves not copied.");

        // (the other way: the installed launcher, its list empty, is given the portable one's data folder, or the folder beside it)
        var moved = Built(Path.Combine(portableHome, "builds", "nightly"), "nightly-300-12345678");   // (in its builds folder, in no row)
        var layout = Built(Path.Combine(portableHome, "builds", "stable", "v3.1.0"), "v3.1.0");        // (builds/<track>/<version>, #184)
        foreach (var picked in new[] { portableHome, Path.GetDirectoryName(portableHome)! })
        {
            var at = OtherLauncher.At(picked, p, Getenv);
            Require(at is { Installed: false } && BuildRegistry.SameFolder(at.Home, portableHome) && at.User == Path.Combine(portableHome, "user")
                    && Names(at.Builds.Select(b => b.Folder)) == "nightly nightly stable v3.1.0" && at.Builds.Single(b => b.Folder == moved).Channel == Versions.Nightly
                    && at.Builds.Single(b => b.Folder == layout).Channel == Versions.Stable,
                    "A portable launcher's data folder, picked or the folder beside it: its list's builds, and the games in its builds folder: "
                    + Names(at?.Builds.Select(b => b.Folder) ?? []));
        }
        Require(OtherLauncher.ChannelNamed(Path.Combine(root, "b", "nightly (2)", "nightly-1-aaaaaaaa"), "nightly-1-aaaaaaaa") == Versions.Nightly
                && OtherLauncher.ChannelNamed(Path.Combine(root, "b", "stable", "v3.0.0"), "v3.0.0") == Versions.Stable
                && OtherLauncher.ChannelNamed(Path.Combine(root, "b", "nightly", "v3.1.0"), "v3.1.0") == null
                && OtherLauncher.ChannelNamed(Path.Combine(root, "b", "rc", "v3.0.0"), "v3.0.0") == null   // (rc: no channel now)
                && OtherLauncher.ChannelNamed(Path.Combine(root, "b", "Longshot", "v3.0.0"), "v3.0.0") == null,
                "A game's channel is its track folder's (or, before that layout, its own folder's name), only for a version on that channel.");
        Require(OtherLauncher.At(installedHome, p, Getenv) is { Installed: true, User: var u } && u == Path.GetFullPath(installedUser),
                "The installed launcher's home, picked: its saves are %APPDATA%\\Longshot.");
        Require(OtherLauncher.At(games, p, Getenv) == null, "A folder with no list of versions is no launcher's.");
        var hookshot = Built(Path.Combine(games, "hookshot"), "v2.9.0");   // (Hookshot's numbers: no Longshot game, a clean break)
        var dev = Built(Path.Combine(games, "dev"), "dev");
        var oldRc = Built(Path.Combine(games, "old-rc"), "v3.0.0-rc.5");   // (an old release candidate: no version now)
        Require(Names(OtherLauncher.GamesIn(games, p)) == "nightly stable" && OtherLauncher.GamesIn(night, p).SequenceEqual([night])
                && OtherLauncher.GamesIn(hookshot, p).Count == 0 && OtherLauncher.GamesIn(dev, p).Count == 0 && OtherLauncher.GamesIn(oldRc, p).Count == 0
                && OtherLauncher.GamesIn(Path.Combine(root, "nothing"), p).Count == 0,
                "A game folder is itself; a folder of them, each that plays; never a Hookshot game or one with no version (an old rc's too).");

        // (a list that cannot be read is no list, and nothing is written beside it, as Load would)
        var brokenHome = Path.Combine(root, "broken", LauncherLayout.PortableData);
        Directory.CreateDirectory(brokenHome);
        File.WriteAllText(Path.Combine(brokenHome, VersionLists.OwnFile), "{ not json");
        Require(OtherLauncher.At(brokenHome, p, Getenv) is { Builds.Count: 0 } && !File.Exists(Path.Combine(brokenHome, VersionLists.OwnFile + ".broken")),
                "Another launcher's broken list reads as none, and is never touched.");

        // (a gather cut short between its two renames: no save\, the player's in .save.longshot-old beside it. That is a save to
        // keep, never an empty folder to copy into)
        var portable4 = Path.Combine(root, "usb4", LauncherLayout.PortableData);
        var env4 = new SaveEnvironment(p, Getenv) { UserFolder = Path.Combine(portable4, "user") };
        var aside = Saves.Beside(Path.Combine(env4.LongshotUser, "save")).Aside;
        MakeSave(Path.Combine(aside, ".."), "aside");   // (writes ..\save\<uid>: moved to the aside's place below)
        Directory.Move(Path.Combine(env4.LongshotUser, "save"), aside);
        var asideTree = Tree(aside);
        Require(!other.SavesFitIn(env4), "A save a gather cut short set aside is a save: none offered over it.");
        var cut = other.TakeInto(BuildRegistry.Load(Path.Combine(portable4, VersionLists.OwnFile)), env4, () => []);
        Require(!cut.SavesCopied && cut.SavesLeft != null && !Directory.Exists(aside) && Tree(Path.Combine(env4.LongshotUser, "save")) == asideTree,
                "A save a gather cut short set aside is put back in its place, nothing copied over it.");

        // (the gather lock held by another (a gather, a Delete's, another launcher's --play): nothing copied, nothing touched)
        var portable5 = Path.Combine(root, "usb5", LauncherLayout.PortableData);
        var env5 = new SaveEnvironment(p, Getenv) { UserFolder = Path.Combine(portable5, "user") };
        Saves.GatherWait = TimeSpan.FromMilliseconds(200);
        try
        {
            using (Saves.HoldGatherLock(env5))
            {
                var locked = other.TakeInto(BuildRegistry.Load(Path.Combine(portable5, VersionLists.OwnFile)), env5, () => []);
                Require(!locked.SavesCopied && locked.Busy && locked.SavesLeft != null && !Directory.Exists(Path.Combine(env5.LongshotUser, "save")),
                        "While another holds the gather lock, the saves are not copied, and it says to try again.");
            }
        }
        finally { Saves.GatherWait = TimeSpan.FromSeconds(10); }

        // (a copy that fails part way (a full disk): the save folder as it was, empty, and nothing left beside it)
        Directory.CreateDirectory(Path.Combine(env5.LongshotUser, "save", "empty"));
        Saves.Fault = s => { if (s == "file") throw new IOException("disk full"); };
        try
        {
            var full = other.TakeInto(BuildRegistry.Load(Path.Combine(portable5, VersionLists.OwnFile)), env5, () => []);
            var (staging, _) = Saves.Beside(Path.Combine(env5.LongshotUser, "save"));
            Require(!full.SavesCopied && full.SavesLeft != null && !Directory.Exists(staging) && Directory.Exists(Path.Combine(env5.LongshotUser, "save", "empty")),
                    "A copy that fails part way leaves the save folder as it was and no staging folder.");
        }
        finally { Saves.Fault = null; }
        var copied = other.TakeInto(BuildRegistry.Load(Path.Combine(portable5, VersionLists.OwnFile)), env5, () => []);
        Require(copied.SavesCopied && !Directory.Exists(Path.Combine(env5.LongshotUser, "save", "empty")),
                "A save folder with only empty folders in it is no save: the copy takes its place.");

        // (another launcher's list with no builds, or a row with no folder: no list, no crash)
        var odd = Path.Combine(root, "odd", LauncherLayout.PortableData);
        Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, VersionLists.OwnFile), "{ \"format\": 1, \"builds\": null }");
        var oddOne = OtherLauncher.At(odd, p, Getenv);
        File.WriteAllText(Path.Combine(odd, VersionLists.OwnFile), "{ \"format\": 1, \"builds\": [ null, { \"folder\": null }, { \"folder\": \"\\u0000bad\" } ] }");
        Require(oddOne is { Builds.Count: 0 } && OtherLauncher.At(odd, p, Getenv) is { Builds.Count: 0 }, "An odd list reads as none.");

        // (Linux: every launcher's builds share one save folder already: no saves to copy)
        Require(OtherLauncher.InstalledOne(Platform.Linux, Getenv, portableHome) is { User: null, Save: null },
                "On Linux there are no saves to copy.");
    }
}
