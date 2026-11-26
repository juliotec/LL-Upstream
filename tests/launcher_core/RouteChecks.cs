using LongshotLauncher.Core;

/// Where Play sends the game (SteamLaunchRoute): the fresh-Deck controller question. Outside Steam,
/// Steam up, and the current profile's tile in its library (found by its name, SteamTile.NameFor)
/// running the played version's builder: Play asks Steam for steam://rungameid/<the tile's id in
/// 64-bit form> and starts nothing itself. A tile of another version, no tile, no library, Steam not
/// running, the library not loaded yet, the opener refusing or missing, already inside Steam
/// (SteamGameId, SteamAppId, Game Mode), or Windows: the direct launch. Nothing here talks to a real
/// Steam: /proc folders and uptime are authored, the URI opener is a delegate, and OpenWith runs
/// real processes.
static partial class Program
{
    /// an authored /proc with one steam process started at starttimeTicks (clock ticks) after boot
    static void SteamProc(string proc, long starttimeTicks)
    {
        Directory.CreateDirectory(Path.Combine(proc, "777"));
        File.WriteAllText(Path.Combine(proc, "uptime"), "1000.0 500.0");
        File.WriteAllText(Path.Combine(proc, "777", "cmdline"), "/usr/bin/steam\0\0");
        var fields = new List<string> { "S", "1", "777", "777", "0", "-1" };
        while (fields.Count < 19) fields.Add("0");
        fields.Add(starttimeTicks.ToString());
        // (stat: the 20th field after the last ')' is starttime; comm can hold spaces, so the
        // reader counts from ')' — the authored shape must match that contract exactly)
        File.WriteAllText(Path.Combine(proc, "777", "stat"), $"777 (steam) {string.Join(" ", fields)} 0 0");
    }

    static void RouteChecks(string fixtures)
    {
        var root = Folder("route");
        Func<string, string?> EnvFor(string xdg, string? key = null, string? value = null) => k => k switch
        {
            "HOME" => Path.Combine(root, "home"),
            "XDG_DATA_HOME" => xdg,
            _ when k == key => value,
            _ => null,
        };
        var xdg = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(xdg, "Steam", "userdata", "33811576", "config"));
        var vdf = SteamShortcuts.LibraryFile(EnvFor(xdg));
        Require(vdf == Path.Combine(xdg, "Steam", "userdata", "33811576", "config", "shortcuts.vdf"),
                "the library looked in is the account's shortcuts.vdf, as Locate places tiles");
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), vdf!, true);   // a real Deck library (fixture)

        // the builder's tile for a profile named Alt: Longshot (Alt), running version v1's builder (a path with a space: Steam quotes it)
        var builder = Path.Combine(root, "versions", "v 1", BuilderVersions.ExeName(Platform.Linux));
        var other = Path.Combine(root, "versions", "v2", BuilderVersions.ExeName(Platform.Linux));
        var name = SteamTile.NameFor(new LauncherProfile { Id = "alt", Name = "Alt" });
        Require(name == "Longshot (Alt)", "a profile's tile is named after it");
        var appId = SteamShortcuts.SignedId(builder, name!);
        Require(SteamShortcuts.Add(new SteamShortcuts.Placement(
                    new SteamShortcuts.Entry(appId, name!, $"\"{builder}\"", $"\"{Path.GetDirectoryName(builder)}\"", "play --profile alt"), vdf!, null)),
                "the builder's tile is written into the copied library");

        var up = Path.Combine(root, "proc-up");           // (no uptime: LibraryLoaded answers optimistic)
        Directory.CreateDirectory(Path.Combine(up, "777"));
        File.WriteAllText(Path.Combine(up, "777", "cmdline"), "/usr/bin/steam\0\0");
        var down = Path.Combine(root, "proc-down");
        Directory.CreateDirectory(down);

        var opened = new List<string>();
        bool? Open(string uri) { opened.Add(uri); return true; }
        SteamLaunchRoute.Decision Route(string? tile = "Longshot (Alt)", string? exe = null, Func<string, string?>? env = null,
                                        Func<string, bool?>? opener = null, string? proc = null, Platform p = Platform.Linux, string? lib = "")
            => SteamLaunchRoute.Decide(p, lib == "" ? vdf : lib, tile, exe ?? builder, env ?? EnvFor(xdg), opener ?? Open, proc ?? up);

        // the tile found, running this version's builder, Steam up: routed at the tile's id in 64-bit form
        var d = Route();
        Require(d.Route == SteamLaunchRoute.Kind.RunGameId && d.Note == SteamLaunchRoute.ViaTileNote,
                "Steam up plus the profile's tile: Play is routed through Steam.");
        Require(opened.Count == 1 && opened[0] == $"steam://rungameid/{SteamShortcuts.GridId64(appId)}" && d.Uri == opened[0],
                "the URI names the tile's own id in 64-bit form");

        // the tile of another version's builder: that tile would play the other version, so direct
        opened.Clear();
        var wrong = Route(exe: other);
        Require(wrong.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && wrong.Note.Contains("not this version's builder"),
                "a tile running another version's builder is not used: direct.");

        // no tile of this profile's name (the default profile's is Longshot, which this library lacks): direct
        Require(SteamTile.NameFor(new LauncherProfile { Id = LauncherProfiles.DefaultId, Name = "Anything" }) == null, "the default profile's tile is the builder's own");
        var none = Route(tile: null);
        Require(none.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && none.Note.Contains("no \"Longshot\" tile"),
                "no tile of the profile's name: direct.");
        Require(Route(tile: "Longshot (Other)").Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0,
                "another profile's tile is not this profile's: direct.");

        // already inside Steam: Steam's own launch (either variable) or Game Mode
        foreach (var (key, value) in new[] { ("SteamGameId", "99999"), ("SteamAppId", "0"), ("XDG_CURRENT_DESKTOP", "KDE:gamescope") })
            Require(Route(env: EnvFor(xdg, key, value)).Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0,
                    $"inside Steam ({key}): the direct launch, no URI.");

        // no Steam install, Steam not running, the opener missing or refusing, Windows: direct
        Require(Route(lib: null).Route == SteamLaunchRoute.Kind.Direct, "no Steam library: direct.");
        Require(Route(proc: down).Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0, "Steam not running: direct, no URI.");
        Require(Route(opener: _ => null).Route == SteamLaunchRoute.Kind.Direct, "nothing answers a steam:// link: direct.");
        Require(Route(opener: _ => false).Route == SteamLaunchRoute.Kind.Direct, "a refused steam:// link: direct.");
        Require(Route(p: Platform.Windows).Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0, "Windows Play stays direct.");

        // the opener's answer is its exit code, not its spawn: real processes
        Require(SteamLaunchRoute.OpenWith("/bin/sh", ["-c", "exit 0"]) == true, "a handler that took the URI answers true.");
        Require(SteamLaunchRoute.OpenWith("/bin/sh", ["-c", "exit 4"]) == false, "a handler failing with 4 answers false: the fallback runs.");
        Require(SteamLaunchRoute.OpenWith("longshot-no-such-handler", ["x"]) == null, "no opener on PATH answers null, not true.");

        // not loaded yet: Steam keeps its library from its own start (measured on the Deck 2026-11-15), so a tile written
        // after a running Steam started is not in it, and a rungameid for it goes nowhere: direct until Steam restarts
        File.SetLastWriteTimeUtc(vdf!, DateTime.UtcNow);
        var startedBefore = Path.Combine(root, "proc-stale");           // (boot+1s: steam up long before the write)
        SteamProc(startedBefore, 100);
        var startedAfter = Path.Combine(root, "proc-fresh");            // (boot+1000s: ≈ now, after the write)
        SteamProc(startedAfter, 100_000);
        var notLoaded = Route(proc: startedBefore);
        Require(notLoaded.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && notLoaded.Note.Contains("restart Steam"),
                "a tile the running Steam has not loaded: direct.");
        Require(Route(proc: startedAfter).Route == SteamLaunchRoute.Kind.RunGameId && opened.Count == 1,
                "once a running Steam has read the library, Play routes.");

        // a library that cannot be read: direct, never a throw
        var broken = Path.Combine(root, "broken.vdf");
        File.WriteAllBytes(broken, [0x00, 0x73, 0x68]);
        Require(Route(lib: broken).Route == SteamLaunchRoute.Kind.Direct, "an unreadable library: direct.");
    }
}
