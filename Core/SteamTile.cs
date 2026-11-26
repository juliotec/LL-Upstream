namespace LongshotLauncher.Core;

/// The Steam tile that makes the game playable from Gaming mode, made by a version folder's builder: `longshot-builder steam-shortcut
/// --profile &lt;the profile's folder&gt; [--remove] --output json` (Longshot's build.md "Steam"): the version folder's program, which
/// plays, added to the signed-in Steam account's library as a non-Steam game with --profile in its launch options, Steam's own id,
/// its icon, and only its own tile ever changed (a tile called Longshot it did not make is left as it is: steam_name_taken). Steam
/// holds its library in memory and reads the file at its start: a change made while it runs says so (steam_running), and shows
/// once Steam starts again. The launcher keeps what the builder does not write: the tile's capsule art (App/Assets/steam/), in
/// Steam's grid/ folder beside the library the builder names, only where a slot is empty
public static class SteamTile
{
    /// The command line: steam-shortcut --profile &lt;the profile's folder&gt; [--name &lt;the tile's name&gt;] [--remove] --output json
    public static IReadOnlyList<string> Arguments(string? profile, bool remove, string? name = null) =>
        ["steam-shortcut", .. profile == null ? [] : new[] { "--profile", Path.GetFullPath(profile) }, .. name == null ? [] : new[] { "--name", name },
         .. remove ? new[] { "--remove" } : [], .. LongshotBuilder.OutputJson];

    /// The tile's name for a profile: the default profile's is the builder's own, Longshot (null); every other profile has a
    /// tile of its own, "Longshot (its name)", so one tile never plays another profile's saves
    public static string? NameFor(LauncherProfile profile) =>
        profile.Id == LauncherProfiles.DefaultId ? null : $"Longshot ({(profile.Name.Trim() is { Length: > 0 } n ? n : profile.Id)})";

    /// What steam-shortcut did: Action (added, updated, unchanged, removed, absent), the tile's id and the library written, and
    /// whether Steam runs (the change shows once it starts again); or why not (Code, Text: steam_not_found, steam_name_taken, ...)
    public sealed record Result(string? Action, uint AppId, string? Library, bool SteamRunning, string? Code = null, string? Text = null)
    {
        public bool Done => Action != null;
        /// Steam's library holds the builder's tile now
        public bool InLibrary => Action is "added" or "updated" or "unchanged";
    }

    /// The builder's tile made (remove: false) or taken out (remove: true) for profile, by builder
    public static async Task<Result> RunAsync(LongshotBuilder builder, string? profile, bool remove, CancellationToken cancel = default,
                                              string? name = null)
    {
        var answer = await builder.AskAsync(Arguments(profile, remove, name), cancel: cancel).ConfigureAwait(false);
        if (answer.Events.OfType<BuildEvent.SteamShortcut>().LastOrDefault() is { } done && answer.Error == null)
            return new Result(done.Action, done.AppId, done.Library, done.SteamRunning);
        if (answer.Error is { } error) return new Result(null, 0, null, false, error.Code, error.Text);
        return new Result(null, 0, null, false, answer.Exit == 2 ? "usage" : "failed",
                          answer.Why ?? Tr.F("The build tools stopped without saying why (exit code {0}).", answer.Exit));
    }

    /// Whether this user has a Steam the builder can add to (its folders as the builder looks for them: $XDG_DATA_HOME/Steam,
    /// ~/.steam/steam, Flatpak's and Snap's), with a userdata/ or config/ folder in it. Only whether to offer the tick: the builder
    /// itself says steam_not_found when it finds none
    public static bool Present(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var home = LauncherHome.Home(getenv);
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".local", "share");
        foreach (var steam in new[]
                 {
                     Path.Combine(data, "Steam"), Path.Combine(home, ".steam", "steam"),
                     Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                     Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
                 })
            try
            {
                if (Directory.Exists(Path.Combine(steam, "userdata")) || Directory.Exists(Path.Combine(steam, "config"))) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    // ---- the tile's artwork: Steam draws a Gaming-mode library tile from pre-baked capsule images in grid/ beside the
    // shortcuts.vdf, under TWO names for the same id (checked on a Deck LCD 2026-11-15 against a working tile: EmulationStation's
    // landscape capsule sits byte-identical at 2827843537.png AND 12145495509653520384.png, and 12145495509653520384 = (its appid
    // << 32) | 0x02000000 — Steam ROM Manager's lengthenAppId). The new library UI reads the 32-bit names (<id>.png landscape
    // 920x430 for the recently-played row, <id>p.png portrait 600x900 for the library itself, _hero/_logo on the detail page);
    // the old Big Picture reads the 64-bit names. A tile with no capsules shows the blank default. Art is only ever written when
    // absent: a capsule the user chose in Steam's own UI is theirs, and this never replaces it. A removed tile keeps its art: the
    // grid folder is the user's library, not ours to prune ----

    public static string GridDir(string library) => Path.Combine(Path.GetDirectoryName(library) ?? ".", "grid");

    /// The 64-bit id Steam also keys a shortcut's art by: (appid << 32) | 0x02000000
    public static ulong GridId64(uint appId) => ((ulong)appId << 32) | 0x02000000UL;

    /// (suffix, extension) -> bytes, e.g. ("", "png") landscape, beside library (the shortcuts.vdf the builder wrote) for the
    /// tile appId. Writes every name for every piece of art that is missing and returns how many landed; a fault (no room, a held
    /// file) leaves what is already there
    public static int WriteArtwork(string library, uint appId, IEnumerable<(string suffix, string ext, byte[] bytes)> art)
    {
        var dir = GridDir(library);
        // the same art under both names the two Steam UIs read
        var names = new[] { appId.ToString(System.Globalization.CultureInfo.InvariantCulture), GridId64(appId).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var wrote = 0;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (suffix, ext, bytes) in art)
            {
                // a capsule the user chose in Steam under ANY name or format wins (Steam writes .jpg as readily as .png); the two
                // names are one slot seen by two UIs, so a piece held under either is held. A half-written .tmp is no capsule: an
                // interrupted publish, left where it is (Steam's own UI leaves the same mid-publish)
                var held = names.Any(stem => Directory.EnumerateFiles(dir, stem + suffix + ".*").Any(f => !f.EndsWith(".tmp", StringComparison.Ordinal)));
                if (held) continue;
                foreach (var stem in names)
                {
                    // (check-and-publish in one step: Steam's own UI can publish a capsule at any moment)
                    try { if (AtomicFile.WriteNew(Path.Combine(dir, $"{stem}{suffix}.{ext}"), bytes)) wrote++; }
                    catch (IOException) { }   // the slot filled between the look and the write: theirs stands
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return wrote; }
        return wrote;
    }
}
