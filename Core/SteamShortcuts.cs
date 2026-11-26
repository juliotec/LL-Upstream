using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LongshotLauncher.Core;

/// The Steam tile that makes the game playable from Gaming mode: a non-Steam game in Steam's
/// shortcuts.vdf, the very shortcut Steam itself writes for "Add a Non-Steam Game" in Desktop mode
/// (Valve's documented way onto the Deck). Writing the entry's appid the way Steam computes it,
/// crc32(Exe + AppName) with the high bit set read as signed (the rule ChimeraOS, Heroic and
/// SteamTinkerLaunch all follow), keeps the tile Steam's own: its grid art and controller layer
/// key off that id, so a tile made here and one made by hand in Steam behave the same.
///
/// Steam loads this file when it starts and keeps the library in memory after that: a change made
/// while it runs shows up at its next start, and the library it still holds can be written back over
/// the file when it quits (checked on the Deck 2026-11-15: the tile sat correctly in the file and
/// Gaming mode never showed it until the console restarted). takeDownFirst writes with Steam down
/// instead, the way ChimeraOS and SteamTinkerLaunch do it; nothing here stops a user's Steam without
/// the caller's explicit consent.
public static class SteamShortcuts
{
    public const string Name = Shortcuts.Name;

    /// What the tile runs. The Exe is the launcher itself with no options: Steam starts the
    /// launcher's window, which checks for updates at its start and plays the default build when
    /// the player presses Play, so the tile follows "Play starts this version" and an update's new
    /// folder like the desktop and applications-menu shortcuts do. (Not --play: a game started
    /// directly would skip that check.) The AppImage's mount moves per run; the launcher reads the
    /// registry and does not care where it was started from
    public record Entry(int AppId, string AppName, string Exe, string StartDir, string LaunchOptions, string Icon = "");

    /// Where the tile goes: the entry, the shortcuts.vdf that holds it, and the steam.sh that can
    /// take Steam down for the write (null when there is none to run)
    public record Placement(Entry Entry, string Vdf, string? SteamScript);

    /// The sidecar naming the launcher this file's tile was made for, kept from the first change
    static string Sidecar(string vdf) => vdf + ".longshot";

    /// The file Steam really holds: a shortcuts.vdf that is a symbolic link (a shared or redirected
    /// library, which ChimeraOS and some setups use) is resolved, so the link stays a link and the
    /// file it names is the one written. A path that is a link and cannot be resolved (a loop, a
    /// broken chain) is refused: writing there would replace the link itself with a file, which is
    /// exactly what this is here to prevent
    static string RealVdf(string vdf)
    {
        try
        {
            var target = File.ResolveLinkTarget(vdf, returnFinalTarget: true);
            return target?.FullName ?? vdf;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (IsLink(vdf))
                throw new InvalidDataException("The Steam library is a symbolic link that cannot be resolved: " + vdf + " (" + e.Message + ")", e);
            return vdf;
        }
    }

    /// Whether a path is itself a symbolic link (not whether it leads to one), for the refusal above
    static bool IsLink(string path)
    {
        try { return new FileInfo(path).LinkTarget != null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// A copy of the library as it was before this launcher ever rewrote it, kept once beside the
    /// real file (shortcuts.vdf.bak-longshot). Steam's own backup covers its own writes; this is the
    /// player's way back from ours. Never overwritten: the first copy is the pre-launcher file
    static void KeepFirstBackup(string vdf)
    {
        var backup = vdf + ".bak-longshot";
        try
        {
            if (File.Exists(vdf) && !File.Exists(backup)) File.Copy(vdf, backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Steam's folder for this user: $XDG_DATA_HOME/Steam, which on the Deck is where ~/.steam/steam
    /// points (checked there, not assumed: the Deck's readlink says ~/.local/share/Steam)
    public static string SteamHome(Func<string, string?>? getenv = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(LauncherHome.Home(getenv), ".local", "share");
        return Path.Combine(data, "Steam");
    }

    /// The placement for this launcher, or null when this user has no Steam to add to. The account
    /// is the one Steam itself last used (config/loginusers.vdf's MostRecent, mapped to its
    /// userdata folder); with no such record, the newest numeric userdata folder, as before. A
    /// single-user file at <steam>/config is the fallback the old layout left. Never creates
    /// anything: no Steam, no tile, no complaint
    public static Placement? Locate(string launcher, Func<string, string?>? getenv = null)
    {
        var home = SteamHome(getenv);
        var vdf = LibraryFile(getenv);
        if (vdf == null) return null;
        var script = File.Exists(Path.Combine(home, "steam.sh")) ? Path.Combine(home, "steam.sh") : null;
        return new Placement(GameEntry(launcher), vdf, script);
    }

    /// The library Locate places into: the shortcuts.vdf of the account Steam last used, or null when this user has no Steam
    public static string? LibraryFile(Func<string, string?>? getenv = null)
    {
        var home = SteamHome(getenv);
        var userdata = Path.Combine(home, "userdata");
        var account = MostRecentAccount(Path.Combine(home, "config"), userdata) ?? NewestAccount(userdata);
        return account == null
            ? (File.Exists(Path.Combine(home, "config", "shortcuts.vdf")) ? Path.Combine(home, "config", "shortcuts.vdf") : null)
            : Path.Combine(account, "config", "shortcuts.vdf");
    }

    /// The account Steam itself says is in use: the SteamID64 whose entry in loginusers.vdf holds
    /// MostRecent "1", whose userdata folder is its account id (the low 32 bits of the id). Null
    /// when there is no such record, no file, or no folder of that name: the caller falls back to
    /// the newest folder, which on a shared PC is not always the account signed in
    static string? MostRecentAccount(string config, string userdata)
    {
        var users = Path.Combine(config, "loginusers.vdf");
        if (!File.Exists(users) || !Directory.Exists(userdata)) return null;
        string? id = null;
        try
        {
            foreach (var line in File.ReadLines(users))
            {
                var t = line.Trim();
                if (t.StartsWith("\"7656", StringComparison.Ordinal)) { id = t.Trim('"'); continue; }
                if (id == null || !t.StartsWith("\"MostRecent\"", StringComparison.Ordinal)) continue;
                var value = t[(t.IndexOf('"', 1) + 1)..].Trim().Trim('"');
                if (value != "1" || !ulong.TryParse(id, out var steamId)) continue;
                var dir = Path.Combine(userdata, (steamId & 0xFFFFFFFFUL).ToString());
                if (Directory.Exists(Path.Combine(dir, "config"))) return dir;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// The numeric userdata folder written most recently: the old way of guessing the account in
    /// use, kept as the fallback (and for a Steam that keeps no loginusers.vdf here)
    static string? NewestAccount(string userdata)
    {
        string? account = null;
        var best = DateTime.MinValue;
        if (!Directory.Exists(userdata)) return null;
        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            if (!ulong.TryParse(Path.GetFileName(dir), out _) || !Directory.Exists(Path.Combine(dir, "config"))) continue;
            var written = Directory.GetLastWriteTimeUtc(Path.Combine(dir, "config"));
            if (written > best) { best = written; account = dir; }
        }
        return account;
    }

    /// The placement for a launcher whose tile should carry the logo at iconPath
    public static Placement? Locate(string launcher, string iconPath, Func<string, string?>? getenv = null)
    {
        var p = Locate(launcher, getenv);
        return p == null ? null : p with { Entry = GameEntry(launcher, iconPath) };
    }

    public static Entry GameEntry(string launcher, string? icon = null)
    {
        var exe = Path.GetFullPath(launcher);
        // Steam itself quotes Exe and StartDir in its own file when they hold spaces (checked in the
        // Deck's shortcuts.vdf); the quoting rides inside the value. The appid is computed over the
        // value as written, so the tile's id always matches its own Exe, which is how Steam keys its
        // grid art and controller settings to it
        var dir = Path.GetDirectoryName(exe) ?? Path.GetPathRoot(exe) ?? ".";
        var (qexe, qdir) = (Quote(exe), Quote(dir));
        return new Entry(SignedId(qexe, Name), Name, qexe, qdir, "", icon ?? "");
    }
    static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    // ---- the tile's artwork: Steam draws a Gaming-mode library tile from pre-baked capsule
    // images in grid/ beside the shortcuts.vdf, under TWO names for the same id (checked on a
    // Deck LCD 2026-11-15 against a working tile: EmulationStation's landscape capsule sits
    // byte-identical at 2827843537.png AND 12145495509653520384.png, and 12145495509653520384
    // = (its appid << 32) | 0x02000000 — Steam ROM Manager's lengthenAppId, Ice's
    // filename_for_shortcut, which also spells out the crc32 and the 0x02000000 tail). The new
    // library UI reads the 32-bit names (<id>.png landscape 920x430 for the recently-played row,
    // <id>p.png portrait 600x900 for the library itself — suffix p, no underscore — _hero/_logo
    // on the detail page); the old Big Picture reads the 64-bit names. A tile with no capsules
    // shows the blank default, which is exactly what a bare tile did on the Deck 2026-11-15.
    // Art is only ever written when absent: a capsule the user chose in Steam's own UI is
    // theirs, and this never replaces it. A removed tile keeps its art — the grid folder is
    // the user's library, not ours to prune, and a later re-add gets the art back for free ----

    public static string GridDir(string vdf) => Path.Combine(Path.GetDirectoryName(vdf) ?? ".", "grid");

    /// The 64-bit id Steam also keys a shortcut's art by: (unsigned appid << 32) | 0x02000000
    public static ulong GridId64(int appId) => ((ulong)(uint)appId << 32) | 0x02000000UL;

    /// (suffix, extension) -> bytes, e.g. ("", "png") landscape. Writes every name for every
    /// piece of art that is missing and returns how many landed; a fault (no room, a held file)
    /// leaves what is already there
    public static int WriteArtwork(string vdf, int appId, IEnumerable<(string suffix, string ext, byte[] bytes)> art)
    {
        var dir = GridDir(vdf);
        // the same art under both names the two Steam UIs read
        var names = new[] { unchecked((uint)appId).ToString(), GridId64(appId).ToString() };
        var wrote = 0;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (suffix, ext, bytes) in art)
            {
                // a capsule the user chose in Steam under ANY name or format wins — the Deck's own
                // library shows Steam writes .jpg as readily as .png (4100769614p.jpg). The two
                // names are the same slot seen by two UIs, so a piece held under either name is
                // held, period: filling only the other one would change art the user picked.
                // A half-written .tmp is NOT a capsule — it is an interrupted publish, and
                // counting one as held would freeze the slot blank forever after a crash. It is
                // left where it is rather than deleted: a temp of this shape is also what Steam's
                // own UI leaves mid-publish, and removing it would break the user's save. Once any
                // real file lands in the slot the .tmp is inert
                var held = names.Any(stem =>
                    Directory.EnumerateFiles(dir, stem + suffix + ".*").Any(f => !f.EndsWith(".tmp")));
                if (held) continue;
                foreach (var stem in names)
                {
                    try
                    {
                        // check-and-publish in one step: Steam's own UI can publish a capsule at
                        // any moment, and a plain Exists-then-overwrite-Write would rename over it
                        if (AtomicFile.WriteNew(Path.Combine(dir, $"{stem}{suffix}.{ext}"), bytes)) wrote++;
                    }
                    catch (IOException) { }   // the slot filled between the look and the write: theirs stands
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return wrote; }
        return wrote;
    }

    /// capsules with the hero slot taken by the game folder's own when it has one, and the logo
    /// slot dropped beside it: the same slot under the same name, the player's own game's picture,
    /// carrying the mark the builder drew. Nothing else moves. The input is never touched (the App
    /// caches its capsule list: mutating it would strand one game's hero in the next game's tile),
    /// and a missing, oversized or wrong-shaped hero leaves every capsule exactly as it was.
    /// WriteArtwork's rules still apply over the top: a slot the user's Steam art already holds
    /// stays held, whosever bytes were offered
    public static (string suffix, string ext, byte[] bytes)[] WithGameHero(
        (string suffix, string ext, byte[] bytes)[] capsules, string? gameFolder)
    {
        var own = GameHero(gameFolder);
        if (own == null) return capsules;
        // the hero slot takes the folder's bytes, and the logo slot goes away with it: the hero
        // carries the mark now, and Steam's own logo would sit a second one on top of it. A slot
        // offered nothing is simply never written (checked against the Deck's own client: no
        // _logo file means no logo drawn over the hero, and no default placeholder), so this
        // needs no blank art to ship. Everything else passes through, and the input is not touched
        return [.. capsules
                    .Where(c => c.suffix != "_logo")
                    .Select(c => c.suffix == "_hero" ? ("_hero", "png", own) : c)];
    }

    /// A build whose folder hero carries the mark has no use for a logo capsule: Steam draws it
    /// right over the hero and a second mark lands on a picture that already carries one.
    /// WriteArtwork deliberately never overwrites, so an install from before this change still
    /// holds the logo this launcher generated for it once: retire exactly that file. Under both
    /// names, and only when the bytes are this launcher's own generated logo byte for byte — a
    /// capsule the user chose in Steam's UI, any extension, any content, stays exactly as it is,
    /// and so does anything we cannot prove is ours. Returns how many went
    public static int RetireGeneratedLogo(string vdf, int appId, byte[] generated)
    {
        var dir = GridDir(vdf);
        var gone = 0;
        try
        {
            foreach (var stem in new[] { unchecked((uint)appId).ToString(), GridId64(appId).ToString() })
            {
                var f = Path.Combine(dir, stem + "_logo.png");    // (only the name this launcher
                try                                               //  wrote: user art is never .png ours)
                {
                    if (generated.Length > 0 && new FileInfo(f).Length == generated.Length
                            && File.ReadAllBytes(f).AsSpan().SequenceEqual(generated))
                    { File.Delete(f); gone++; }
                }
                catch (IOException) { }       // (no file, or it is held: the ordinary answer is it stays)
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return gone; }
        return gone;
    }

    // ---- the game's own banner: the builder (Longshot nx2/game_hero.py) keeps one still of the
    // player's own game's own footage in their game folder, longshot_hero.png, beside the title
    // and icon game_title.py keeps. Only that file's bytes fill the hero slot, and only when its
    // header says the hero's exact shape: a damaged or foreign file is no art rather than a
    // broken capsule, and the generated hero stands instead. It never ships anywhere: like the
    // title and icon, it is the player's own game's and lives only in the player's folder ----

    public const string HeroFile = "longshot_hero.png";
    const int HeroMaxBytes = 8 << 20;           // (a hero PNG sits near 1 MB: over this reads as broken)

    /// The game folder's banner bytes when it has a whole one, else null. Whole means whole,
    /// the same way nx2/game_hero.py checks its own writing: signature, the IHDR chunk where a
    /// PNG keeps it with its length and CRC right, and the IEND and its CRC at the very end. A
    /// twenty-five-byte impostor that only draws the header is no art rather than a black
    /// capsule with the logo slot dropped for it, and a file cut mid-write is no art either
    public static byte[]? GameHero(string? gameFolder)
    {
        if (gameFolder is not { Length: > 0 }) return null;
        try
        {
            var f = Path.Combine(gameFolder, HeroFile);
            var size = new FileInfo(f).Length;  // (throws for no file: the ordinary answer is null)
            if (size < 45 || size > HeroMaxBytes) return null;
            var b = File.ReadAllBytes(f);
            if (b.Length < 45 || b.Length > HeroMaxBytes) return null;    // (the size moved between the look and the read)
            byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!b[..8].SequenceEqual(sig)) return null;
            // IHDR: length 13, tag, then width, height, and its CRC over tag+data
            if (Be32(b, 8) != 13 || b[12] != (byte)'I' || b[13] != (byte)'H' || b[14] != (byte)'D' || b[15] != (byte)'R')
                return null;
            if (Be32(b, 16) != 1920 || Be32(b, 20) != 620) return null;
            if (Be32(b, 29) != Crc32([.. b[12..29]])) return null;
            if (!System.Text.Encoding.ASCII.GetString(b, 33, b.Length - 45).Contains("IDAT"))
                return null;                                  // (pixels between the header and
                                                              //  the end, not a header for show)
            // IEND: the last 12 bytes of any PNG, tag and CRC included
            byte[] iend = [0x00, 0x00, 0x00, 0x00, (byte)'I', (byte)'E', (byte)'N', (byte)'D',
                           0xAE, 0x42, 0x60, 0x82];
            return b[^12..].SequenceEqual(iend) ? b : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static uint Be32(byte[] b, int at) => ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    /// The appid the tile in the library actually carries — an updated launcher's tile keeps the
    /// id its marker recorded — or the one this launcher would give a tile it is about to make.
    /// Capsule art keys off this, not off the freshly computed id alone
    public static int TileAppId(Placement p)
    {
        try
        {
            if (!File.Exists(p.Vdf)) return p.Entry.AppId;
            var ours = FindOurs(ShortcutsOf(Parse(File.ReadAllBytes(p.Vdf))), p);
            return ours == null ? p.Entry.AppId : Num(ours, "appid");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return p.Entry.AppId;
        }
    }

    // ---- the tile's logo: the entry's icon field is what Steam draws on the Gaming-mode tile
    // (checked in the Deck's own library: EmulationStation's tile points at its grid .ico and its
    // logo shows in the grid; an empty icon field shows the blank default). It goes under the
    // launcher's own home, not under Steam's: Steam may reorganize grid/, this never moves, and
    // the desktop and applications-menu shortcuts point at the same file ----

    public static string LogoPath(Func<string, string?>? getenv = null) =>
        Path.Combine(LauncherHome.Resolve(Platform.Linux, getenv).Root, "longshot.png");

    /// The file the Steam tile's icon field names. Steam ignores a .png here: the tile pointed at
    /// longshot.png showed the blank default on the Deck after a clean Steam restart (hardware,
    /// 2026-11-15), while every tile in the Deck's own library that does draw art names a .ico.
    /// The desktop and menu shortcuts keep pointing at the .png, which KDE draws correctly; both
    /// files live in the launcher's home under the same name stem.
    public static string LogoIcoPath(Func<string, string?>? getenv = null) =>
        Path.Combine(LauncherHome.Resolve(Platform.Linux, getenv).Root, "longshot.ico");

    /// Write the logo where the shortcuts point. False on a refusal (a read-only home, a file held
    /// busy): a tile without a logo beats no tile, so the caller shrugs and writes the shortcut
    /// with the icon left empty
    public static bool TryWriteLogo(byte[] png, Func<string, string?>? getenv = null)
    {
        try { AtomicFile.Write(LogoPath(getenv), png); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The same, for the .ico the Steam tile names (the launcher's own Assets/longshot.ico, built
    /// from the .png by scripts/make_icon.py)
    public static bool TryWriteLogoIco(byte[] ico, Func<string, string?>? getenv = null)
    {
        try { AtomicFile.Write(LogoIcoPath(getenv), ico); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- the appid: Steam's own rule, verified against the file Steam wrote on the Deck ----

    public static int SignedId(string exe, string appName) =>
        (int)(Crc32(Encoding.UTF8.GetBytes(exe + appName)) | 0x80000000u);

    static readonly uint[] CrcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    static uint Crc32(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(byte)(c ^ b)] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    // ---- the binary VDF, the exact layout Steam's own shortcuts.vdf uses on the Deck: sections
    // type 0 terminated by a lone 0x08, strings 0x01 NUL-terminated, ints 0x02, floats 0x03.
    // (Verified byte for byte against the file Steam wrote there: parse and dump come back equal) ----

    const byte Terminator = 0x08;

    /// A tile as the library holds it (internal: the game-free checks read what the writers wrote)
    internal sealed record Tile(int AppId, string AppName, string Exe, string StartDir, string LaunchOptions);

    internal static IReadOnlyList<Tile> Tiles(string vdf)
    {
        if (!File.Exists(vdf)) return Array.Empty<Tile>();
        var shortcuts = ShortcutsOf(Parse(File.ReadAllBytes(vdf)));
        return shortcuts.Items.Where(it => it.Value is Vdf).Select(it => it.Value!).Cast<Vdf>()
            .Select(e => new Tile(Num(e, "appid"), Str(e, "AppName"), Str(e, "Exe"), Str(e, "StartDir"), Str(e, "LaunchOptions")))
            .ToList();
    }

    sealed class Vdf
    {
        public string? Name;
        public readonly List<(string Key, object? Value)> Items = new();
    }

    static Vdf Parse(byte[] b)
    {
        var i = 0;
        return ParseBody(b, ref i, root: true);
    }

    static Vdf ParseBody(byte[] b, ref int i, bool root = false)
    {
        var v = new Vdf();
        while (i < b.Length)
        {
            var t = b[i];
            if (t == Terminator) { i++; return v; }     // the byte that closes a map: 0x08
            if (t == 0)
            {
                // a child section: 00, the section's name, its body, and the body's own 0x08
                var j = Array.IndexOf(b, (byte)0, i + 1);
                if (j < 0) throw new InvalidDataException("shortcuts.vdf ended mid-name");
                var key = Encoding.UTF8.GetString(b, i + 1, j - i - 1);
                var body = j + 1;
                var inner = ParseBody(b, ref body);
                inner.Name = key;
                v.Items.Add((key, inner));
                i = body;
                continue;
            }
            i++;
            var key2 = ReadString(b, ref i);
            object? value = t switch
            {
                1 => ReadString(b, ref i),
                2 => ReadInt(b, ref i),
                3 => ReadFloat(b, ref i),
                // type 5 we can read but not write back as we found it: a file holding it is
                // refused rather than quietly retyped (real shortcuts.vdf files do not carry it)
                _ => throw new InvalidDataException($"shortcuts.vdf holds an unexpected type {t} at {i}"),
            };
            v.Items.Add((key2, value));
        }
        // (longshot) a map whose bytes run out before its closing 0x08 is a half-written file:
        // Steam rewrites the whole library on every change, so a crash or power loss mid-write
        // leaves exactly this. Reading it leniently and letting Add/Remove publish what was read
        // is how a library loses tiles it never had anything to do with: everything past the cut
        // parses as nothing and the rewrite drops it for good. So a truncated file is refused and
        // left byte-for-byte alone, and the caller is told. (An empty file is not truncated: it is
        // no library yet, and Add starts one. Steam's own reader is forgiving on read; a tool
        // that rewrites must not be.)
        if (root && b.Length > 0)
            throw new InvalidDataException(
                "the Steam library file stops partway through an entry (a write was interrupted at some point). " +
                "Nothing was changed: rewriting it now would lose the entries the cut hides. Restarting Steam usually repairs it.");
        return v;
    }

    static string ReadString(byte[] b, ref int i)
    {
        var j = Array.IndexOf(b, (byte)0, i);
        if (j < 0) throw new InvalidDataException("shortcuts.vdf ended mid-string");
        var s = Encoding.UTF8.GetString(b, i, j - i);
        i = j + 1;
        return s;
    }
    static int ReadInt(byte[] b, ref int i) { Ensure(b, i, 4); var v = BitConverter.ToInt32(b, i); i += 4; return v; }
    static float ReadFloat(byte[] b, ref int i) { Ensure(b, i, 4); var v = BitConverter.ToSingle(b, i); i += 4; return v; }
    static void Ensure(byte[] b, int i, int n) { if (i + n > b.Length) throw new InvalidDataException("shortcuts.vdf ended mid-value"); }

    static byte[] Dump(Vdf root)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, new UTF8Encoding(false), leaveOpen: true);
        foreach (var (key, value) in root.Items) Write(w, key, value);
        w.Write(Terminator);
        w.Flush();
        return ms.ToArray();
    }

    static void Write(BinaryWriter w, string key, object? value)
    {
        switch (value)
        {
            case Vdf section:
                w.Write((byte)0); WriteKey(w, key);
                foreach (var (k, v) in section.Items) Write(w, k, v);
                w.Write(Terminator);
                break;
            case string s:
                w.Write((byte)1); WriteKey(w, key);
                w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0);
                break;
            case int n: w.Write((byte)2); WriteKey(w, key); w.Write(n); break;
            case float f: w.Write((byte)3); WriteKey(w, key); w.Write(f); break;
            default: throw new InvalidDataException("shortcuts.vdf holds a value that cannot be written");
        }
    }
    static void WriteKey(BinaryWriter w, string key) { w.Write(Encoding.UTF8.GetBytes(key)); w.Write((byte)0); }

    static Vdf ShortcutsOf(Vdf root)
    {
        var found = root.Items.FirstOrDefault(it => it.Key == "shortcuts" && it.Value is Vdf).Value as Vdf;
        if (found != null) return found;
        var created = new Vdf { Name = "shortcuts" };
        root.Items.Add(("shortcuts", created));
        return created;
    }

    /// A file with bytes in it must still look like a library before anything is written back over
    /// it. The truncation guard in ParseBody catches a map that ran out of bytes; this catches the
    /// other shape: a byte before the root that closes it (a stray 0x08 — an interrupted write, an
    /// outside edit), which parses as a whole empty library with the real entries sitting unread
    /// past it. Rewriting THAT publishes one tile and deletes every other shortcut in the library —
    /// the data loss the truncation guard exists to prevent, arriving from a different direction.
    /// A file with no shortcuts map in it where a library was expected is refused and left alone;
    /// a file with no bytes at all is no library yet, and Add starts one (that stays allowed).
    static void RequireLibrary(Vdf root, byte[] bytes)
    {
        if (bytes.Length == 0) return;   // (no file, or an empty one: nothing to lose)
        if (root.Items.Any(it => it.Key == "shortcuts" && it.Value is Vdf)) return;
        throw new InvalidDataException(
            "the Steam library file does not read as a library (its entries are not where the file says they are). " +
            "Nothing was changed: rewriting it now would lose the shortcuts it holds. Restarting Steam usually repairs it.");
    }

    /// The entry this launcher made, found by its appid and not by its name: the id is the tile's
    /// identity in Steam (grid art, controller settings and play time all key off it), and matching
    /// on it is what keeps a tile the user made in Steam's own dialog out of reach, whatever it is
    /// called. Ours is the id this launcher path computes plus, when the marker is there, the id the
    /// marker recorded, so a launcher that updated keeps the one tile it made
    static Vdf? FindOurs(Vdf shortcuts, Placement p)
    {
        var exact = SignedId(p.Entry.Exe, p.Entry.AppName);
        var claim = ClaimOf(p);
        foreach (var it in shortcuts.Items)
        {
            if (it.Value is not Vdf e) continue;
            // ours by id AND still pointing at this launcher (or at what the marker recorded): an id
            // match alone would re-claim a tile the user re-pointed at their own script in Steam's
            // Properties — Steam keeps a tile's id through that edit, so the id alone does not prove
            // whose tile it is now
            if (Num(e, "appid") == exact && Str(e, "Exe") == p.Entry.Exe) return e;
            // the marker's claim: the id it recorded (computed from the origin it names — the id
            // never moves, even when an update repoints the Exe at a newer AppImage), and the tile
            // still pointing at the Exe the marker recorded. Steam keeps a tile's id when you edit
            // its target in Properties, so an id match alone would let a stale marker re-aim a
            // tile the user re-pointed somewhere else
            if (claim != null && Num(e, "appid") == SignedId(claim.Origin, p.Entry.AppName)
                              && Str(e, "Exe") == claim.Exe) return e;
        }
        return null;
    }

    static Vdf? FindByName(Vdf shortcuts, string appName) =>
        shortcuts.Items.FirstOrDefault(it => it.Value is Vdf e && Str(e, "AppName") == appName).Value as Vdf;

    internal static List<string> EntryKeys(string vdfPath)
    {
        if (!File.Exists(vdfPath)) return new List<string>();
        return ShortcutsOf(Parse(File.ReadAllBytes(vdfPath))).Items.Select(it => it.Key).ToList();
    }

    static string Str(Vdf e, string key) => e.Items.FirstOrDefault(it => it.Key == key).Value as string ?? "";
    static int Num(Vdf e, string key) => e.Items.FirstOrDefault(it => it.Key == key).Value is int n ? n : 0;
    static void SetStr(Vdf e, string key, string value)
    {
        for (var i = 0; i < e.Items.Count; i++)
            if (e.Items[i].Key == key) { e.Items[i] = (key, value); return; }
        e.Items.Add((key, value));
    }
    static void SetNum(Vdf e, string key, int value)
    {
        for (var i = 0; i < e.Items.Count; i++)
            if (e.Items[i].Key == key) { e.Items[i] = (key, value); return; }
        e.Items.Add((key, value));
    }

    /// Whether this launcher's tile is in this Steam already
    public static bool InLibrary(Placement p)
    {
        if (!File.Exists(p.Vdf)) return false;
        try { return FindOurs(ShortcutsOf(Parse(File.ReadAllBytes(p.Vdf))), p) != null; }
        // no claim on an unreadable library: the answer is false, and the write that follows will
        // raise its own failure where the caller's toast can show it
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// The launcher FILE the tile in the library would start, unquoted — read from the entry that
    /// carries this tile's id (the id is identity, so the entry under it IS the tile), not from what
    /// this launcher's path computes. A tile launch runs that file with that file's environment:
    /// Steam's, not whoever pressed Play (SteamLaunchRoute asks it where the launch would land)
    public static string? TileLauncherPath(Placement p)
    {
        if (!File.Exists(p.Vdf)) return null;
        try
        {
            var id = TileAppId(p);
            var exe = Tiles(p.Vdf).FirstOrDefault(t => t.AppId == id)?.Exe ?? "";
            if (exe.Length == 0) return null;
            return exe.Length >= 2 && exe[0] == '"' && exe[^1] == '"' ? exe[1..^1] : exe;   // (Quote's shape)
        }
        // no path on an unreadable library: the caller's answer is "cannot tell, do not route"
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }


    record Claim(string Vdf, string Exe, string Origin, long? FirstSeen = null, long? ChangedAt = null);

    /// The first-seen time from this vdf's marker, whatever launcher wrote it (LibraryLoaded asks a
    /// question about the file and the running Steam, not about who owns the tile — its caller has
    /// already proven ownership through InLibrary, and a marker written by a DIFFERENT copy of this
    /// launcher is still a true record of when a Longshot tile entered this library). Null when
    /// there is no marker, no fifth field, or the field is not a number
    static long? MarkerFirstSeen(string vdf) => MarkerField(vdf, 4);

    // (round-five L6: the SIXTH marker field is when this tile's LAUNCH-RELEVANT fields last
    // changed — Exe, StartDir, launch options, name. A running Steam holds the library it read at
    // its own start, so an entry whose Exe was repointed after that start is still the OLD file to
    // the running client: routing there would hand the arm to a stale launcher's arrival. The
    // stamp is compared like first-seen (a Steam must have started at or after it to be trusted),
    // which is what makes an update's repoint require a Steam restart before routing resumes)
    static long? MarkerChangedAt(string vdf) => MarkerField(vdf, 5);

    static long? MarkerField(string vdf, int index)
    {
        try
        {
            if (!File.Exists(Sidecar(vdf))) return null;
            var parts = File.ReadAllText(Sidecar(vdf)).Split('\t');
            // (a marker written by a damaged or meddled launcher could carry a nonsense stamp;
            // FromUnixTimeSeconds throws past the year-9999 edge (round-three P3: long.MaxValue
            // parsed fine and crashed the read). Anything outside our own plausible window —
            // later than next year — is not our evidence; fall back to mtime instead)
            if (parts.Length <= index || !long.TryParse(parts[index], out var f)) return null;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return f >= 0 && f <= 4102444800 && f <= now + 31_536_000 ? f : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The marker beside the file, if it is readable and shaped right. A marker from a half-finished
    /// write is no claim at all, and a marker we cannot read is not a reason to fault the shortcut.
    /// Exe is who the tile pointed at when we last wrote it; Origin is the launcher path whose id
    /// the tile CARRIES — the tile's id never moves (its grid art and controller settings key off
    /// it), so once an update repoints the Exe at a new AppImage, the id matches Origin, not Exe.
    /// Old three-field markers, written before ids were preserved, are still valid: their Exe IS
    /// the id's origin.
    static Claim? ClaimOf(Placement p)
    {
        string text;
        try
        {
            var side = Sidecar(RealVdf(p.Vdf));
            if (!File.Exists(side)) return null;
            text = File.ReadAllText(side);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var parts = text.Split('\t');
        // three or four fields, and the id has to be one this rule could have written for the Exe
        // beside it or for the origin it names
        if (parts.Length < 3 || !int.TryParse(parts[2], out var id)) return null;
        var origin = parts.Length >= 4 ? parts[3] : parts[1];
        var claimed = SignedId(parts[1], p.Entry.AppName) == id || SignedId(origin, p.Entry.AppName) == id;
        // (a fifth field, when present: the unix time this tile FIRST entered the library — see
        // TileAdded. Markers written before it exist have none, and that is fine: null)
        var first = parts.Length >= 5 && long.TryParse(parts[4], out var f) ? f : (long?)null;
        // (a sixth, when present: when the entry's launch-relevant fields last changed — see Add)
        var changed = parts.Length >= 6 && long.TryParse(parts[5], out var c) ? c : (long?)null;
        return claimed ? new Claim(parts[0], parts[1], origin, first, changed) : null;
    }

    /// Add the tile. With Steam running the file is right but the tile shows only when Steam next
    /// starts, and Steam can still write its in-memory library back over the change when it quits;
    /// takeDownFirst covers both, the way ChimeraOS and SteamTinkerLaunch write this file only with
    /// Steam down (the core never stops a user's Steam on its own, downloads and all — the caller
    /// decides, and on the Deck a plain console restart achieves the same thing). False when the
    /// tile is already there pointing at this launcher exactly as it would write it now, and when a
    /// tile of that name is not ours (added by hand in Steam: left exactly as the user made it)
    public static bool Add(Placement p, bool takeDownFirst = false)
    {
        if (takeDownFirst && !Down(p))
            throw new InvalidOperationException(
                "Steam could not be quit, so it might write its library back over the new tile when it " +
                "next restarts. Add the game in Steam itself instead: in Desktop mode, Games > Add a " +
                "Game > Add a Non-Steam Game.");
        // a linked library (a shared or redirected shortcuts.vdf) is written through its link: the
        // link stays, and the file it names is the one changed
        var file = RealVdf(p.Vdf);
        var bytes = File.Exists(file) ? File.ReadAllBytes(file) : Array.Empty<byte>();
        var root = Parse(bytes);
        RequireLibrary(root, bytes);
        var shortcuts = ShortcutsOf(root);
        var existing = FindOurs(shortcuts, p);
        // idempotent against the tile's CARRIED id, not the freshly computed one: once an update
        // repoints the Exe the entry keeps the old id, and Same comparing the new one made every
        // later Add look changed (identical bytes rewritten, the restart notice re-fired forever —
        // review P2). The carried id is what the entry holds and what Add would write again
        if (existing != null && Same(existing, p.Entry, Num(existing, "appid"))) return false;
        // (round-five L6: whether this write is launch-relevant must be judged BEFORE Update
        // mutates the entry — comparing afterwards sees our own write and always answers "same",
        // which is exactly the bug the L6 check caught on its first run. Icon-only changes are not
        // launch-relevant: grid art does not change what Steam would start)
        var launchChanged = existing == null || !SameLaunch(existing, p.Entry, Num(existing, "appid"));
        // a tile the user named Longshot themselves, pointed at their own script: another id, and
        // their business. Refusing beats a second tile of the same name in their grid; the caller
        // says so, and the tick goes back to what the library holds
        if (existing == null && FindByName(shortcuts, p.Entry.AppName) != null) return false;
        if (existing == null)
        {
            // the first free key, not the count: Remove leaves the keys otherwise (Steam's own
            // writes renumber, an outside edit may not), and two entries under one key is the one
            // shape that loses a tile that was not ours when the file is read back
            var used = new HashSet<string>(shortcuts.Items.Select(it => it.Key));
            var free = 0;
            while (used.Contains(free.ToString())) free++;
            shortcuts.Items.Add((free.ToString(), NewEntry(p.Entry)));
        }
        else Update(existing, p.Entry);
        // the marker names the tile we wrote: its id is how a launcher that moved finds its tile
        // again (the AppImage's name changes with every update, and with it the computed id). It goes
        // in before the library does, because that order leaves a marker that costs nothing to
        // recheck on a crash; the other way round leaves a claimed tile with no marker to claim it.
        // The fourth field is the path the tile's CARRIED id was computed from: the id never moves,
        // so after an update repoints the Exe, the marker still vouches for the tile by that origin
        var carried = Num(existing ?? NewEntry(p.Entry), "appid");
        var prior = ClaimOf(p);
        var origin = existing != null && prior != null && carried == SignedId(prior.Origin, p.Entry.AppName)
            ? prior.Origin : p.Entry.Exe;
        // the library as it was before we ever rewrote it, once: the player's way back from a
        // launcher's change (Steam's own backup only covers Steam's)
        KeepFirstBackup(file);
        // (when our tile FIRST entered the library: kept from the marker across rewrites, stamped
        // now for a fresh tile. LibraryLoaded asks "did a running Steam start after our tile got
        // here" — and the file's mtime cannot answer that, because Steam itself rewrites the file
        // (play time, properties) and bumps its own file's mtime past its own start. Measured in
        // review: one no-op rewrite flipped the answer from loaded to not-loaded, which would break
        // routing on any Deck that has ever PLAYED the tile. The marker's time is ours and survives
        // — but ONLY for our own standing tile: a marker left behind when Steam overwrote the entry
        // away must not vouch for a re-added one as if it had been loaded all along (round-three
        // P2 probe: remove the entry, keep the marker, re-add, and LibraryLoaded said loaded while
        // Steam never reloaded. A genuinely new entry starts its clock fresh)
        var fresh = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var firstSeen = existing != null ? prior?.FirstSeen ?? fresh : fresh;
        // (round-five L6: a REPOINT is not the same moment as an arrival. The running Steam holds
        // the library it read at its own start, so an entry whose Exe, StartDir or launch options
        // changed after that start is still the OLD launch to the running client — routing to it
        // would arm a launch of the file that tile USED to name. The sixth marker field is when
        // those launch-relevant fields last changed: LibraryLoaded requires a Steam started at or
        // after it, so a repoint suspends routing until Steam has restarted and re-read — the
        // disk-correct/library-stale case, made honest. An icon-only change (grid art) is not
        // launch-relevant and leaves the stamp where it was; markers written before this field
        // exist have none, and the first-seen comparison stands alone)
        var changedAt = launchChanged ? fresh : prior?.ChangedAt ?? firstSeen;
        AtomicFile.Write(Sidecar(file), $"{file}\t{p.Entry.Exe}\t{carried}\t{origin}\t{firstSeen}\t{changedAt}");
        AtomicFile.Write(file, Dump(root));
        return true;
    }

    /// Take the tile away again (the uninstaller's side). Only ours: an entry with our name that
    /// this launcher did not make is left alone. The sidecar goes when our tile does
    public static bool Remove(Placement p, bool takeDownFirst = false)
    {
        // take-down first, then read: Steam writes its in-memory library when it quits, and reading
        // before that (like this did) and writing after would overwrite whatever the quit flushed
        if (takeDownFirst && !Down(p))
            throw new InvalidOperationException(
                "Steam could not be quit, so it might write its library back over the change when it " +
                "next restarts. Remove the game in Steam itself instead, in Desktop mode.");
        var file = RealVdf(p.Vdf);
        if (!File.Exists(file)) return false;
        var bytes = File.ReadAllBytes(file);
        var root = Parse(bytes);
        RequireLibrary(root, bytes);
        var shortcuts = ShortcutsOf(root);
        var entry = FindOurs(shortcuts, p);
        if (entry == null) return false;
        shortcuts.Items.RemoveAll(it => ReferenceEquals(it.Value, entry));
        KeepFirstBackup(file);
        AtomicFile.Write(file, Dump(root));
        if (File.Exists(Sidecar(file))) File.Delete(Sidecar(file));
        return true;
    }

    static Vdf NewEntry(Entry p)
    {
        // The keys and order Steam's own dialog writes, so the file reads the same however the tile
        // arrived. The last two (sortas, tags) are what Steam's library adds on its own writes
        var e = new Vdf();
        void Str(string k, string v) => e.Items.Add((k, v));
        void Num(string k, int v) => e.Items.Add((k, v));
        Num("appid", p.AppId); Str("AppName", p.AppName); Str("Exe", p.Exe);
        Str("StartDir", p.StartDir); Str("icon", p.Icon); Str("ShortcutPath", "");
        Str("LaunchOptions", p.LaunchOptions); Num("IsHidden", 0); Num("AllowDesktopConfig", 1);
        Num("AllowOverlay", 1); Num("OpenVR", 0); Num("Devkit", 0); Str("DevkitGameID", "");
        Num("DevkitOverrideAppID", 0); Num("LastPlayTime", 0); Str("FlatpakAppID", ""); Str("sortas", "");
        e.Items.Add(("tags", new Vdf { Name = "tags" }));
        return e;
    }

    static void Update(Vdf entry, Entry p)
    {
        // the appid is NOT rewritten: it is the tile's identity — its grid art, controller settings
        // and play time all key off the id the tile CARRIES. Recomputing it here (which this did)
        // orphaned all of that every time the launcher's path changed: the sidecar and TileAppId
        // kept finding the tile under the old id while the entry itself moved to a new one, and the
        // art landed under the id nothing else used. The Exe may follow whoever last ticked the box
        // (the everyday launcher update); the id stays. A tile made here got its id from this same
        // rule at its first Add, so a fresh tile's carried id already equals this computation.
        SetStr(entry, "Exe", p.Exe);
        SetStr(entry, "StartDir", p.StartDir);
        SetStr(entry, "LaunchOptions", p.LaunchOptions);
        // an icon only replaces an icon: a caller with nothing to offer (its logo write failed)
        // leaves a working tile's art alone, rather than pointing it at a file never written. A
        // tile added before logos existed gets the art here, and the id does not change: the appid
        // is Exe + name, icon is not part of it
        if (p.Icon.Length > 0) SetStr(entry, "icon", p.Icon);
    }

    static bool Same(Vdf entry, Entry p, int carried) =>
        // the CARRIED id, not the freshly computed one: Add never moves the id (it is the tile's
        // identity), so after a launcher update the entry keeps the old id while p.AppId is new —
        // comparing the fresh one (which this did) made every later Add look like a change,
        // rewriting identical bytes and re-firing the restart notice forever (review P2)
        Num(entry, "appid") == carried && Str(entry, "Exe") == p.Exe &&
        Str(entry, "StartDir") == p.StartDir && Str(entry, "LaunchOptions") == p.LaunchOptions &&
        // an empty icon claims nothing: it matches whatever art the tile already holds
        (p.Icon.Length == 0 || Str(entry, "icon") == p.Icon);

    // (the launch-relevant half of Same, for the changed-at stamp: Exe, StartDir and launch
    // options are what a running Steam would start wrong after a repoint. The icon is grid art and
    // the id is identity Add never moves — neither resets the clock)
    static bool SameLaunch(Vdf entry, Entry p, int carried) =>
        Num(entry, "appid") == carried && Str(entry, "Exe") == p.Exe &&
        Str(entry, "StartDir") == p.StartDir && Str(entry, "LaunchOptions") == p.LaunchOptions;

    // ---- Steam up/down, for a caller that wants the file quiescent before writing: the Deck's own
    // signs in /proc, and Steam's documented stop (steam.sh -shutdown, the way ChimeraOS stops it) ----

    /// Desktop Steam, the gamescope session's Steam, and the runtime scripts: any of them holds the
    /// library in memory. /proc scan, no tools (the Deck's shell has no pgrep guarantee)
    public static bool SteamUp(string proc = "/proc")
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(proc))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
                string first;
                try { first = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', 2)[0]; }
                catch (IOException) { continue; }           // the process ended mid-scan
                catch (UnauthorizedAccessException) { continue; }
                var name = Path.GetFileName(first);
                if (name is "steam" or "steamrt" or "steamrt64" or "gamescope") return true;
            }
        }
        catch (IOException) { }
        return false;
    }

    /// Whether a running Steam has our tile in the library it loaded: Steam reads shortcuts.vdf when
    /// it starts and keeps the library in memory (measured on the Deck 2026-11-15), so a tile written
    /// while Steam runs is not in the running Steam's library until Steam restarts, and a
    /// rungameid URI for a tile Steam never loaded is accepted by xdg-open and goes nowhere. The
    /// comparison wants the moment OUR TILE ENTERED the library, which is the marker's first-seen
    /// time (Add stamps it once and keeps it across rewrites). The file's mtime cannot answer this:
    /// Steam rewrites its own library file (play time, properties), and one such rewrite bumps the
    /// mtime past Steam's own start — a review probe flipped the answer to not-loaded that way, on a
    /// library where the tile was loaded the whole time. A marker with no first-seen field (written
    /// before it existed), or no marker at all (a tile the user made), falls back to the file's
    /// mtime: still the right comparison, just wrong in the conservative direction when Steam's own
    /// writes have moved it. /proc gives the newest start time among the Steam-ish processes (2 s of
    /// slack for clock slop). A file that has never been written counts as loaded (there is no tile
    /// there; the InLibrary check upstream already says so). When the start time cannot be read (a
    /// bare /proc in tests, a locked-down kernel), answer optimistically: this guard steers a choice
    /// between two working paths, it never decides safety
    public static bool LibraryLoaded(string vdf, string proc = "/proc")
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(vdf); }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return true; }
        // (the marker's own record beats the file's mtime; see above)
        var entered = MarkerFirstSeen(vdf);
        if (entered is long seenAt) written = DateTimeOffset.FromUnixTimeSeconds(seenAt).UtcDateTime;
        var changed = MarkerChangedAt(vdf);
        try
        {
            // (round-five L7, reordered after the audit: the library-owning clients are found FIRST,
            // so "no client at all" answers not-loaded on its own evidence — the old order read
            // /proc/uptime first and an unreadable uptime returned optimistic true BEFORE any
            // client was looked for, which made the gamescope-only case fail open on a bare or
            // locked-down /proc. Only the TIMING paths stay optimistic once a client exists)
            var clients = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(proc))
            {
                if (!int.TryParse(Path.GetFileName(dir), out _)) continue;
                string first;
                try { first = File.ReadAllText(Path.Combine(dir, "cmdline")).Split('\0', 2)[0]; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                // (the library-owning client only — the review's P1: a gamescope that started after
                // the tile write proves nothing about the library, it is the compositor; the runtime
                // scripts count because the client execs out of them. A gamescope-session restart
                // must not fake a library reload epoch, so gamescope is deliberately NOT here even
                // though SteamUp counts it for up/down)
                if (Path.GetFileName(first) is "steam" or "steamrt" or "steamrt64") clients.Add(dir);
            }
            // (round-five L7: NO library-owning client at all — a gamescope-session restart where
            // only the compositor is up, a Steam mid-restart — answers NOT loaded. The old answer
            // here was optimistic true, and the review's probe used it: SteamUp counts gamescope,
            // so a cold-start Play routed a URI to a client that was not there yet, waited, and
            // started nothing)
            if (clients.Count == 0) return false;
            // boot time: now minus uptime (the /proc/uptime first field, seconds). Unreadable with
            // clients running is a bare/locked-down /proc: the timing cannot be judged, and the
            // guard steers a choice between two working paths, so it stays optimistic
            var up = File.ReadAllText(Path.Combine(proc, "uptime")).Split(' ', 2)[0];
            if (!double.TryParse(up, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
                return true;
            var boot = DateTime.UtcNow.AddSeconds(-secs);
            var hz = 100.0;                        // (Linux task clock: 100 Hz on every arch SteamOS runs)
            var best = DateTime.MinValue;
            foreach (var dir in clients)
            {
                // (stat: "pid (comm) state ..." — comm can hold spaces and parentheses, so count
                // fields from the LAST ')'; starttime is the 20th field after it, in clock ticks)
                string stat;
                try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var close = stat.LastIndexOf(')');
                var rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (rest.Length < 20 || !long.TryParse(rest[19], out var start)) continue;
                var started = boot.AddSeconds(start / hz);
                if (started > best) best = started;
            }
            // (a client exists but none could be timed: same unjudgeable case, optimistic)
            if (best == DateTime.MinValue) return true;
            // (two comparisons, deliberately different. The tile's ARRIVAL epoch keeps its 2 s of
            // clock slop: file mtimes and /proc start times come from different clocks, and this
            // slack has been the calibrated tolerance since the first round. The REPOINT epoch
            // (round-five L6) gets NO slack — "a Steam started at or after the repoint" is the
            // claim, and the audit measured that -2 s let a client started before the repoint be
            // trusted. A client inside that 2 s window afterwards briefly reads not-loaded, which
            // is the safe direction: direct launch, with the note)
            if (changed is long c && best < DateTimeOffset.FromUnixTimeSeconds(c).UtcDateTime) return false;
            return best >= written.AddSeconds(-2);
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return true; }
    }

    /// Ask Steam to quit and wait for it: -shutdown through its own script, up to half a minute, then
    /// watch /proc. False (Steam stays up) when there is no script to ask with or it will not stop
    static bool Down(Placement p)
    {
        if (p.SteamScript == null) return !SteamUp();
        try
        {
            // no redirection: a helper that talks would fill the pipe and the wait would sit on it
            var psi = new ProcessStartInfo(p.SteamScript, "-shutdown") { UseShellExecute = false };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                return false;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
        for (var waited = 0; waited < 10_000 && SteamUp(); waited += 500) Thread.Sleep(500);
        return !SteamUp();
    }
}
