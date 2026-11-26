using LongshotLauncher.Core;

/// The Steam tile (Core/SteamTile.cs), game-free: made and taken out by a version folder's builder (steam-shortcut, a fake builder
/// script here saying its events), its answers and refusals read, whether there is a Steam to offer the tick for, and the
/// capsule art the launcher keeps for the tile, written beside the library the builder names, under both names Steam reads, only
/// where a slot is empty
static partial class Program
{
    static void SteamChecks(string fixtures)
    {
        var root = Folder("steam-tile");
        Func<string, string?> EnvFor(string xdg) => k => k switch
        {
            "HOME" => Path.Combine(root, "home"),
            "XDG_DATA_HOME" => xdg,
            _ => null,
        };

        // ---- the command and its answer
        Require(SteamTile.Arguments("/p/x", remove: false).SequenceEqual(new[] { "steam-shortcut", "--profile", Path.GetFullPath("/p/x"), "--output", "json" })
                && SteamTile.Arguments(null, remove: true).SequenceEqual(new[] { "steam-shortcut", "--remove", "--output", "json" })
                && SteamTile.Arguments("/p/x", remove: false, "Longshot (Ann)").SequenceEqual(new[] { "steam-shortcut", "--profile", Path.GetFullPath("/p/x"), "--name", "Longshot (Ann)", "--output", "json" })
                && SteamTile.NameFor(new LauncherProfile()) == null
                && SteamTile.NameFor(new LauncherProfile { Id = "p2", Name = "Ann" }) == "Longshot (Ann)"
                && SteamTile.NameFor(new LauncherProfile { Id = "p3", Name = " " }) == "Longshot (p3)",
                "steam-shortcut --profile <the profile's folder> --output json; --remove takes it out.");
        Require(BuildEvents.Parse("""{"v":1,"type":"steam_shortcut","action":"added","name":"Longshot","app_id":2827843537,"library":"/s/shortcuts.vdf","command":"x","artwork":1,"steam_running":true}""")
                    is BuildEvent.SteamShortcut { Action: "added", AppId: 2827843537u, Library: "/s/shortcuts.vdf", SteamRunning: true },
                "Its answer: the action, Steam's id (unsigned), the library, whether Steam runs.");

        // ---- a Steam to add to: its folders as the builder looks for them
        Require(!SteamTile.Present(EnvFor(Path.Combine(root, "no-steam"))), "No Steam: no tick.");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "Steam", "userdata"));
        Require(SteamTile.Present(EnvFor(data)), "Steam's folder with its userdata: a tick.");
        var flatpak = Path.Combine(root, "home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "config");
        Directory.CreateDirectory(flatpak);
        Require(SteamTile.Present(EnvFor(Path.Combine(root, "no-steam"))), "Flatpak's Steam too.");
        Directory.Delete(flatpak);

        // ---- the tile's capsule artwork: Steam draws a Gaming-mode library tile from grid/ images keyed by the appid read
        // unsigned (the library tile is <id>p.png, the recently-played row <id>.png; Heroic and Steam ROM Manager write the same)
        var avc = Path.Combine(root, "art", "Steam", "userdata", "7", "config");
        var library = Path.Combine(avc, "shortcuts.vdf");
        Directory.CreateDirectory(Path.Combine(avc, "grid"));
        const uint id = 2827843537u;
        var unsigned = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // (a .jpg already in a slot holds it against our .png: Steam's own art comes in .jpg too)
        File.WriteAllBytes(Path.Combine(avc, "grid", $"{unsigned}_hero.jpg"), new byte[] { 11 });
        var wrote = SteamTile.WriteArtwork(library, id, [("", "png", new byte[] { 9 }), ("p", "png", new byte[] { 8 }), ("_hero", "png", new byte[] { 7 })]);
        Require(wrote == 4, "each free capsule slot fills, under both names, in its own extension");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}_hero.jpg")).SequenceEqual(new byte[] { 11 })
                && !File.Exists(Path.Combine(avc, "grid", $"{unsigned}_hero.png")) && !File.Exists(Path.Combine(avc, "grid", $"{SteamTile.GridId64(id)}_hero.png")),
                "a slot a .jpg already holds is left exactly as it is, under either name: no competing .png");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}.png")).SequenceEqual(new byte[] { 9 })
                && File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}p.png")).SequenceEqual(new byte[] { 8 })
                && File.Exists(Path.Combine(avc, "grid", $"{SteamTile.GridId64(id)}p.png")),
                "the landscape and library capsules under the unsigned id and the 64-bit one");
        // (a half-written temp is no capsule: AtomicFile publishes <target>.<guid>.tmp then renames)
        var grid = SteamTile.GridDir(library);
        File.WriteAllBytes(Path.Combine(grid, $"{unsigned}_logo.png.deadbeef.tmp"), new byte[] { 12 });
        Require(SteamTile.WriteArtwork(library, id, [("_logo", "png", new byte[] { 6 })]) == 2
                && File.ReadAllBytes(Path.Combine(grid, $"{unsigned}_logo.png")).SequenceEqual(new byte[] { 6 })
                && File.ReadAllBytes(Path.Combine(grid, $"{unsigned}_logo.png.deadbeef.tmp")).SequenceEqual(new byte[] { 12 }),
                "an interrupted temp does not hold the slot, and is left alone");
        // (the user's own art wins: an existing capsule is never replaced)
        File.WriteAllBytes(Path.Combine(grid, $"{unsigned}p.png"), new byte[] { 42 });
        Require(SteamTile.WriteArtwork(library, id, [("", "png", new byte[] { 9 }), ("p", "png", new byte[] { 8 })]) == 0
                && File.ReadAllBytes(Path.Combine(grid, $"{unsigned}p.png")).SequenceEqual(new byte[] { 42 }),
                "capsules already there survive: art is only ever filled in");
        // (the 64-bit name, verified against the Deck's own working tile: EmulationStation's art sits at 2827843537.png and
        // 12145495509653520384.png)
        Require(SteamTile.GridId64(2827843537u) == 12145495509653520384UL, "GridId64 reproduces the id the Deck's working tile's art is keyed by");
        Require(Shortcuts.TryWriteLogo(new byte[] { 1, 2, 3 }, EnvFor(data)) && File.ReadAllBytes(Shortcuts.LogoPath(EnvFor(data))).SequenceEqual(new byte[] { 1, 2, 3 })
                && !Shortcuts.LogoPath(EnvFor(data)).Contains("Steam", StringComparison.Ordinal),
                "The desktop and menu shortcuts' logo, in the launcher's home, outside Steam's folders.");

        if (OperatingSystem.IsWindows()) return;
        // ---- made and taken out by the version folder's builder
        var version = Path.Combine(root, "versions", "v4.0.0");
        var exe = Path.Combine(version, "Longshot.x86_64");
        Directory.CreateDirectory(version);
        var builder = new LongshotBuilder(new Toolchain("v4.0.0", version, "Longshot.x86_64"), exe, Platform.Linux, new LauncherHome(Path.Combine(root, "launcher")),
                                          "/L/launcher", _ => null);
        ScriptBuilder(exe, "printf '%s\\n' \"$@\" > \"$(dirname \"$0\")/args.txt\"\necho '{\"v\":1,\"type\":\"steam_shortcut\",\"action\":\"added\",\"name\":\"Longshot\",\"app_id\":2827843537,\"library\":\"" + library + "\",\"steam_running\":true}'");
        var made = SteamTile.RunAsync(builder, "/p/x", remove: false).GetAwaiter().GetResult();
        Require(made is { Done: true, InLibrary: true, AppId: 2827843537u, SteamRunning: true } && made.Library == library
                && File.ReadAllLines(Path.Combine(version, "args.txt")).SequenceEqual(new[] { "steam-shortcut", "--profile", Path.GetFullPath("/p/x"), "--output", "json" }),
                "The builder makes the tile and says its id, library and that Steam runs.");
        ScriptBuilder(exe, "echo '{\"v\":1,\"type\":\"error\",\"code\":\"steam_name_taken\",\"text\":\"a tile called Longshot is not ours\"}'\nexit 1");
        var taken = SteamTile.RunAsync(builder, "/p/x", remove: false).GetAwaiter().GetResult();
        Require(taken is { Done: false, InLibrary: false, Code: "steam_name_taken", Text: "a tile called Longshot is not ours" },
                "A tile of that name the builder did not make: refused, said.");
        ScriptBuilder(exe, "echo '{\"v\":1,\"type\":\"steam_shortcut\",\"action\":\"absent\",\"name\":\"Longshot\",\"app_id\":0,\"library\":\"\",\"steam_running\":false}'");
        Require(SteamTile.RunAsync(builder, null, remove: true).GetAwaiter().GetResult() is { Done: true, InLibrary: false, Action: "absent" },
                "Taken out when there is none of its own: done, nothing there.");
        ScriptBuilder(exe, "echo 'longshot-builder: unknown command: steam-shortcut' >&2\nexit 2");
        Require(SteamTile.RunAsync(builder, null, remove: false).GetAwaiter().GetResult() is { Done: false, Code: "usage" } u
                && u.Text!.Contains("unknown command", StringComparison.Ordinal), "A builder from before steam-shortcut: refused, said.");
    }
}
