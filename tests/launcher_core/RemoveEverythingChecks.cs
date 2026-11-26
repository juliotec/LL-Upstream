using LongshotLauncher.Core;

/// Remove everything (Core/RemoveEverything.cs, #200), on a made-up home in the scratch folder, never the PC's: everything Longshot
/// put there goes, but the saves and their backups stay unless ticked, a folder the player chose keeps what else it holds, a
/// folder holding mods of theirs stays, and nothing is deleted through a link. Fake saves of text files only
static partial class Program
{
    static void RemoveEverythingChecks()
    {
        string Tree(string folder) =>
            !Directory.Exists(folder) ? "" : string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal));
        var nothingRuns = () => (IReadOnlyList<string>)Array.Empty<string>();
        var when = new DateTime(2026, 11, 21, 9, 0, 0);

        // (a Linux PC with one listed version, an old one no row lists, an old one holding the player's mods, the builds folder and
        // the build files folder chosen by the player with things of theirs in them, and the saves, their backups and the
        // script's shared backups)
        (RemoveEverything.Places P, Dictionary<string, string> At) Make(string name)
        {
            var root = Folder(name);
            var user = Path.Combine(root, "user-home");
            var data = Path.Combine(user, ".local", "share");
            var cache = Path.Combine(user, ".cache");
            string? Env(string k) => k switch
            {
                "HOME" => user,
                "XDG_DATA_HOME" => data,
                "XDG_CACHE_HOME" => cache,
                _ => null,
            };
            var env = new SaveEnvironment(Platform.Linux, Env);
            var home = LauncherHome.Resolve(Platform.Linux, Env);
            Touch(Path.Combine(home.Root, "builds.json"), "{}");
            Touch(Path.Combine(home.Root, "settings.json"), "{}");
            Touch(Path.Combine(home.Logs, "launcher.log"), "log");
            Touch(Path.Combine(home.Toolchains, "nightly-1-aaaaaaaa", "make_game.py"), "builder");
            // (longshot-builder's licence notes: the launcher's runs', in its home (LONGSHOT_DATA), and a developer's own run's in
            // the builder's default data folder, which is not the launcher's)
            Touch(Path.Combine(home.BuilderData, "licenses", "0123456789abcdef0123.json"), """{"folder":"/games/L","accepted":[]}""");
            Touch(Path.Combine(data, "longshot-builder", "licenses", "fedcba9876543210fedc.json"), """{"folder":"/dev/out","accepted":[]}""");
            Touch(Path.Combine(home.Root, "longshot.png"), "logo");
            Touch(Path.Combine(home.NgxSdk, "nvngx_dlss.so"), "dlss");
            var games = Path.Combine(root, "Games");
            string Game(string at, string version)
            {
                Touch(Path.Combine(at, GameFolder.StampName), $"version={version}\n");
                Touch(Path.Combine(at, "bin", "longshot"), "program");
                Touch(Path.Combine(at, "cache", "longshot_programs.bin"), "shaders");
                return at;
            }
            var listed = Game(Path.Combine(games, "nightly", "nightly-2-bbbbbbbb"), "nightly-2-bbbbbbbb");
            Touch(Path.Combine(listed, "mods", "listed.txt"), "a version's mod");
            var unlisted = Game(Path.Combine(games, "nightly", "nightly-1-aaaaaaaa"), "nightly-1-aaaaaaaa");
            var withMods = Game(Path.Combine(games, "v3.0.0"), "v3.0.0");
            Touch(Path.Combine(withMods, "mods", "mine.txt"), "the player's mod");
            Touch(Path.Combine(games, "Other game", "readme.txt"), "not Longshot's");
            var work = Path.Combine(root, "work");
            Touch(Path.Combine(work, "objcache", "a.o"), "object");
            Touch(Path.Combine(work, "sa", "gen", "code_1.c"), "code");
            Touch(Path.Combine(work, "system_headers.txt"), "stamp");
            Touch(Path.Combine(work, "My notes.txt"), "the player's");
            var store = env.ShaderStore!;
            Touch(Path.Combine(store, "spirv.pack"), "store");
            var save = Path.Combine(env.LinuxUserFiles, "save", Saves.UserId);
            Touch(Path.Combine(save, "account.sav"), "account");
            Touch(Path.Combine(save, "slot_00", "progress.sav"), "progress");
            Touch(Path.Combine(env.LinuxUserFiles, "longshot_options.ini"), "options");
            Touch(Path.Combine(env.LinuxUserFiles, "cache", "c.bin"), "game cache");
            Touch(Path.Combine(env.BackupRoot, "gather-20261120-090000", "shared", "account.sav"), "backup");
            Touch(Path.Combine(env.OldLinuxBackups, "20261101", "account.sav"), "script's backup");
            var nfc = NfcTags.Folder(Platform.Linux, Env);
            Touch(Path.Combine(nfc, "Link.bin"), "tag");
            // (a link in the launcher's home to a folder outside it: the link goes, never what it points at)
            var outside = Path.Combine(root, "outside");
            Touch(Path.Combine(outside, "precious.txt"), "precious");
            var linked = FileLinks.TryDirectoryLink(Path.Combine(home.Root, "linked"), outside);
            var p = new RemoveEverything.Places(env, home, new[] { listed }, games, BuildsChosen: true, work, WorkChosen: true, nfc);
            return (p, new()
            {
                ["listed"] = listed, ["unlisted"] = unlisted, ["mods"] = withMods, ["games"] = games, ["work"] = work, ["store"] = store,
                ["save"] = Path.Combine(env.LinuxUserFiles, "save"), ["backups"] = env.BackupRoot, ["shared"] = env.OldLinuxBackups,
                ["outside"] = outside, ["linked"] = linked ? "yes" : "", ["nfc"] = nfc, ["user"] = env.LinuxUserFiles,
                ["track"] = Path.Combine(games, "nightly"), ["devData"] = Path.Combine(data, "longshot-builder"),
            });
        }

        // (Longshot running: refused, nothing touched)
        var (busy, busyAt) = Make("remove-everything-running");
        var before = Tree(busy.Home.Root) + Tree(busyAt["games"]) + Tree(busyAt["work"]);
        Throws<IOException>(() => RemoveEverything.Run(busy, saves: false, () => new[] { Saves.Longshot.Display }, () => when),
                            "Remove everything refuses while Longshot runs.");
        Require(Tree(busy.Home.Root) + Tree(busyAt["games"]) + Tree(busyAt["work"]) == before, "Remove everything refused touches nothing.");
        Throws<IOException>(() => RemoveEverything.Run(busy, saves: false, () => Array.Empty<string>(), () => when, building: () => true),
                            "Remove everything refuses while Longshot's builder runs.");
        Require(Tree(busy.Home.Root) + Tree(busyAt["games"]) + Tree(busyAt["work"]) == before, "Refused for a build: nothing touched.");

        // (saves not ticked)
        var (p1, at) = Make("remove-everything");
        var saveTree = Tree(at["save"]);
        var backupTree = Tree(at["backups"]);
        var sharedTree = Tree(at["shared"]);
        var nfcTree = Tree(at["nfc"]);
        var done = RemoveEverything.Run(p1, saves: false, nothingRuns, () => when);
        Require(!Directory.Exists(at["listed"]) && !Directory.Exists(at["unlisted"]) && !Directory.Exists(at["track"]),
                "Remove everything deletes the listed versions, the folders no row lists, and the tracks' folders left empty.");
        Require(File.Exists(Path.Combine(at["mods"], "mods", "mine.txt"))
                && done.Kept.Any(k => k.Reason == RemoveEverything.Reason.Yours && BuildRegistry.SameFolder(k.Path, at["mods"])),
                "Remove everything keeps a folder no row lists that holds mods of the player's, and says so.");
        Require(File.ReadAllText(Path.Combine(at["games"], "Other game", "readme.txt")) == "not Longshot's"
                && done.Kept.Any(k => k.Reason == RemoveEverything.Reason.Others && BuildRegistry.SameFolder(k.Path, at["games"])),
                "Remove everything keeps the builds folder the player chose, and what else it holds.");
        Require(File.ReadAllText(Path.Combine(at["work"], "My notes.txt")) == "the player's" && !Directory.Exists(Path.Combine(at["work"], "objcache"))
                && !Directory.Exists(Path.Combine(at["work"], "sa")) && !File.Exists(Path.Combine(at["work"], "system_headers.txt")),
                "Remove everything deletes only make_game's parts of a build files folder the player chose.");
        Require(!Directory.Exists(p1.Home.Root) && !Directory.Exists(p1.Home.Cache) && !File.Exists(Path.Combine(at["store"], "spirv.pack"))
                && !Directory.Exists(Path.Combine(at["user"], "cache")),
                "Remove everything deletes the launcher's home and cache folders (its list, settings, builders, logs, SDKs), the shader store and the game's cache.");
        Require(!Directory.Exists(p1.Home.BuilderData) && File.Exists(Path.Combine(at["devData"], "licenses", "fedcba9876543210fedc.json")),
                "Remove everything deletes longshot-builder's licence notes of the launcher's runs (LONGSHOT_DATA, in its home), never a developer's own.");
        Require(Tree(at["save"]) == saveTree && Tree(at["backups"]) == backupTree && Tree(at["shared"]) == sharedTree && Tree(at["nfc"]) == nfcTree
                && File.Exists(Path.Combine(at["user"], "longshot_options.ini")),
                "Remove everything with the saves not ticked keeps the saves, their backups, the game's settings and the NFC tags.");
        Require(done.Kept.Any(k => k.Reason == RemoveEverything.Reason.Yours && BuildRegistry.SameFolder(k.Path, at["backups"])) && !done.Problems.Any(),
                "Remove everything says what of the player's it kept, and had no problem.");
        if (at["linked"] == "yes")
            Require(File.ReadAllText(Path.Combine(at["outside"], "precious.txt")) == "precious", "Remove everything never deletes through a link.");

        // (saves ticked: they go too, but not the backups folder Longshot's scripts share, nor the chosen folders' other things)
        var (p2, at2) = Make("remove-everything-saves");
        var shared2 = Tree(at2["shared"]);
        var done2 = RemoveEverything.Run(p2, saves: true, nothingRuns, () => when);
        Require(!Directory.Exists(at2["save"]) && !Directory.Exists(at2["backups"]) && !Directory.Exists(at2["nfc"])
                && !Directory.Exists(p2.Saves.LongshotRoot),
                "Remove everything with the saves ticked deletes the saves, the launcher's backups, the game's settings and the NFC tags.");
        Require(Tree(at2["shared"]) == shared2 && File.Exists(Path.Combine(at2["games"], "Other game", "readme.txt"))
                && File.Exists(Path.Combine(at2["work"], "My notes.txt")) && File.Exists(Path.Combine(at2["mods"], "mods", "mine.txt")),
                "Remove everything with the saves ticked keeps the backups shared with Longshot's scripts, and what is not Longshot's.");
        Require(!done2.Problems.Any(), "Remove everything with the saves ticked had no problem.");

        // (a builds folder and a build files folder that are the launcher's own: gone once empty)
        var (p3, at3) = Make("remove-everything-defaults");
        Directory.Delete(Path.Combine(at3["games"], "Other game"), true);
        Directory.Delete(at3["mods"], true);
        File.Delete(Path.Combine(at3["work"], "My notes.txt"));
        RemoveEverything.Run(p3 with { BuildsChosen = false, WorkChosen = false }, saves: false, nothingRuns, () => when);
        Require(!Directory.Exists(at3["games"]) && !Directory.Exists(at3["work"]),
                "Remove everything deletes the builds folder and the build files folder when they are its own and left empty.");

        // (the confirmation's lines: the saves going only when ticked, a folder holding mods of the player's never)
        var g = new Storage.Game("a", "Nightly 2", "/g/a");
        var held = new Storage.Game("unlisted:/g/b", "v3.0.0", "/g/b", Unlisted: true, HoldsYours: true);
        var items = new[]
        {
            new Storage.Item(Storage.Kind.GameFiles, 100, 100, g), new Storage.Item(Storage.Kind.Programs, 10, 10, g),
            new Storage.Item(Storage.Kind.ModsAndSettings, 1, 1, g), new Storage.Item(Storage.Kind.ModsAndSettings, 2, 2),
            new Storage.Item(Storage.Kind.ShaderCache, 20, 20, g), new Storage.Item(Storage.Kind.RecentCode, 30, 30),
            new Storage.Item(Storage.Kind.Builders, 40, 40), new Storage.Item(Storage.Kind.Logs, 3, 3),
            new Storage.Item(Storage.Kind.Saves, 5, 5), new Storage.Item(Storage.Kind.Unlisted, 50, 50, held),
        };
        var kept = RemoveEverything.Summary(items, saves: false);
        Require(kept.Single(s => s.Line == RemoveEverything.Line.Versions).Bytes == 111
                && kept.Single(s => s.Line == RemoveEverything.Line.Saves) is { Bytes: 7, Goes: false }
                && kept.Single(s => s.Line == RemoveEverything.Line.HeldByYou) is { Goes: false }
                && kept.Where(s => s.Line is not (RemoveEverything.Line.Saves or RemoveEverything.Line.HeldByYou)).All(s => s.Goes),
                "Remove everything's confirmation: versions with their game files and mods go; the saves and settings stay unless ticked; a folder with mods of yours stays.");
        Require(RemoveEverything.Summary(items, saves: true).Single(s => s.Line == RemoveEverything.Line.Saves).Goes
                && RemoveEverything.Names.Count == Enum.GetValues<RemoveEverything.Line>().Length,
                "Remove everything's confirmation: ticked, the saves go; every line is named.");

        // (the Linux shortcuts that start this launcher go; the launcher's own menu entry starting another AppImage stays)
        var sroot = Folder("remove-everything-shortcuts");
        string? SEnv(string k) => k switch { "HOME" => sroot, "XDG_DATA_HOME" => Path.Combine(sroot, "data"), _ => null };
        var launcher = Path.Combine(sroot, "Longshot_Launcher.AppImage");
        Touch(launcher, "appimage");
        Directory.CreateDirectory(Path.Combine(sroot, "Desktop"));
        Shortcuts.WriteLinux(launcher, null, desktop: true, menu: true, getenv: SEnv);
        var other = Path.Combine(sroot, "Other.AppImage");
        Touch(other, "another launcher");
        Shortcuts.AddLauncherToMenu(other, new byte[] { 1 }, SEnv);
        RemoveEverything.RemoveLinuxShortcuts(launcher, SEnv);
        var (desk, menu) = Shortcuts.LinuxPaths(SEnv);
        Require(!File.Exists(desk) && !File.Exists(menu) && Shortcuts.LauncherInMenu(SEnv) == other,
                "Remove everything takes away the game's shortcuts that start it, and leaves another launcher's menu entry.");
        Shortcuts.AddLauncherToMenu(launcher, new byte[] { 1 }, SEnv);
        RemoveEverything.RemoveLinuxShortcuts(launcher, SEnv);
        Require(Shortcuts.LauncherInMenu(SEnv) == null && !File.Exists(Shortcuts.LauncherMenuPaths(SEnv).Icon),
                "Remove everything takes away its own applications menu entry and icon.");
    }
}
