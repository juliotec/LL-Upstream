namespace LongshotLauncher.Core;

/// Where Play sends the game: through Steam's tile, or direct. (Research 2026-11-16; the
/// Steam-side rule it rests on, from the longshot source's own host/steam_pads.h: the game HIDES
/// Steam's virtual gamepads and re-adds the physical one, and only recognises a real steam_input
/// device when Steam launched it. A fresh Deck maps the buttons through Steam Input only when the
/// game was started through Steam; direct-launched under a running Steam, Steam's default desktop
/// layout eats the Deck's controller as mouse and keyboard.)
///
/// So when Play runs outside Steam and Steam is up, Play asks Steam to start the current profile's
/// tile: longshot-builder makes one per profile (SteamTile.NameFor), and that tile runs the
/// builder's own play --profile, so Steam starts the game itself and nothing comes back to the
/// launcher. The tile is found in the library by its name and taken only when its Exe is the builder
/// of the version being played (a tile left by another version would play that version). No tile,
/// no Steam, inside Steam already (SteamGameId/SteamAppId, or a gamescope session), Windows, or the
/// opener failing: the game starts direct, as it always did.
///
/// The URI wants the 64-bit form of the tile's appid: steam://rungameid/<(unsigned appid << 32) |
/// 0x02000000> (SteamShortcuts.GridId64; checked 2026-11-16 against steam-for-linux#8580 and
/// SteamTinkerLaunch#1117, both working on the Deck).
public static class SteamLaunchRoute
{
    public enum Kind { Direct, RunGameId }

    /// Route, the URI asked of Steam (RunGameId only), and why (for the log; ViaTileNote when routed)
    public readonly record struct Decision(Kind Route, string Uri, string Note)
    {
        public static Decision Direct(string why) => new(Kind.Direct, "", why);
    }

    /// Asked of Steam, not reported as done: an opener exit of zero proves the URI was dispatched,
    /// and Steam may still decline or fail to start anything (measured on the Deck, 2026-11-21)
    public static string ViaTileNote => Tr.T("Steam is starting this build through its tile, so its controller mapping applies.");

    /// Whether Steam itself started this process: Steam puts SteamGameId and SteamAppId in what it
    /// launches (measured on a Deck LCD, 2026-11-22); a desktop icon, a menu entry or a terminal run
    /// carries neither
    public static bool FromSteamLaunch(Func<string, string?> env) =>
        !string.IsNullOrEmpty(env("SteamGameId")) || !string.IsNullOrEmpty(env("SteamAppId"));

    // (Steam's own quoting for an Exe holding spaces rides inside the value)
    static string Unquote(string exe) =>
        exe.Length >= 2 && exe[0] == '"' && exe[^1] == '"' ? exe[1..^1] : exe;

    /// Decide where this Play goes. vdf: the library to look in (SteamShortcuts.LibraryFile; null:
    /// no Steam install). tileName: the profile's tile (SteamTile.NameFor, null meaning the builder's
    /// own "Longshot"). builderExe: the played version's builder, the file the tile must run.
    /// openUri: asks the system to open a steam:// URI (null: nothing could, false: it refused)
    public static Decision Decide(Platform p, string? vdf, string? tileName, string builderExe, Func<string, string?> env,
                                  Func<string, bool?>? openUri = null, string proc = "/proc")
    {
        // (Windows desktop has no Steam Input layer in the way; Linux is the Deck's case)
        if (p != Platform.Linux) return Decision.Direct("not Linux");
        // (already inside Steam: Steam's own launch, or Game Mode — routing from there would start
        // the game in a session the clicker cannot see. XDG_CURRENT_DESKTOP is a colon-separated list)
        if (FromSteamLaunch(env)) return Decision.Direct("started by Steam already");
        if ((env("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Equals("gamescope", StringComparison.OrdinalIgnoreCase))) return Decision.Direct("inside Steam's Game Mode");
        if (vdf == null) return Decision.Direct("no Steam library");
        if (!SteamShortcuts.SteamUp(proc)) return Decision.Direct("Steam is not running");
        var name = tileName ?? SteamShortcuts.Name;
        SteamShortcuts.Tile? tile;
        try { tile = SteamShortcuts.Tiles(vdf).FirstOrDefault(t => t.AppName == name); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { return Decision.Direct("the Steam library could not be read: " + e.Message); }
        if (tile == null) return Decision.Direct($"no \"{name}\" tile in Steam");
        if (!SamePath(Unquote(tile.Exe), builderExe))
            return Decision.Direct($"the \"{name}\" tile runs {tile.Exe}, not this version's builder");
        // (Steam reads its library when it starts: a tile written while it runs is not in the running
        // Steam's library yet, and a rungameid for it goes nowhere. Conservative: a direct launch works)
        if (!SteamShortcuts.LibraryLoaded(vdf, proc)) return Decision.Direct($"Steam has not loaded the \"{name}\" tile yet (restart Steam)");
        var uri = $"steam://rungameid/{SteamShortcuts.GridId64(tile.AppId)}";
        var answered = (openUri ?? DefaultOpenUri)(uri);
        if (answered == null) return Decision.Direct("nothing opens a steam:// link here");
        if (answered == false) return Decision.Direct("the steam:// link was refused");
        return new Decision(Kind.RunGameId, uri, ViaTileNote);
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// xdg-open, this launcher's own environment kept: null when there is no opener, true when it
    /// took the URI (exit 0, or still working past the bounded wait), false on a stated refusal
    public static bool? DefaultOpenUri(string uri) =>
        OpenWith("xdg-open", [uri]);

    /// Start program with args, answer null when the program itself could not start, true when the
    /// handler took the URI (exit 0, or still working past the bounded wait), false only on a
    /// nonzero exit. internal so the tests exercise it against real processes
    internal static bool? OpenWith(string program, IReadOnlyList<string> args, int waitMs = 15_000)
    {
        try
        {
            var si = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in args) si.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(si);
            if (p is null) return null;
            if (!p.WaitForExit(waitMs)) return true;
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or IOException or UnauthorizedAccessException)
        { return null; }
    }
}
