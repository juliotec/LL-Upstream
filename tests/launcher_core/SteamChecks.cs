using System.Text;
using LongshotLauncher.Core;

/// The Steam tile (Core/SteamShortcuts.cs), game-free: the binary library file read and written in
/// the grammar Steam's own shortcuts.vdf uses on the Deck, the appid Steam computes for a
/// non-Steam game (checked against the id Steam itself wrote on Angela's Deck's file), add and
/// remove around an existing library without disturbing the other tiles, a launcher that moved or
/// updated keeping its one tile, and a tile the user made by hand under the same name left exactly
/// as they made it. Steam itself is never consulted from here down: SteamUp and the shutdown stay
/// the core's helpers, and the window decides whether to ask the user to quit Steam.
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

        // Steam's own rule for a tile's id, checked against the id Steam itself wrote on the Deck
        // for its EmulationStation tile (the fixture keeps that tile's values and id exactly)
        const string esExe = "\"/home/deck/Emulation/tools/EmulationStation-DE-x64_SteamDeck.AppImage\"";
        Require(SteamShortcuts.SignedId(esExe, "EmulationStation-DE-x64_SteamDeck") == -1467123759,
                "The appid rule does not reproduce the id Steam wrote itself.");

        // one Steam account, the library from the fixture: two tiles, no Longshot
        var xdg = Path.Combine(root, "data");
        var vdf = Path.Combine(xdg, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), vdf);
        File.WriteAllText(Path.Combine(xdg, "Steam", "steam.sh"), "#!/bin/bash\n");
        var env = EnvFor(xdg);

        // the library reads: both tiles, the quoted paths and the tags sections intact
        var before = SteamShortcuts.Tiles(vdf);
        Require(before.Count == 2
                && before.Any(t => t.AppId == -1467123759 && t.StartDir == "\"/home/deck/Emulation/tools/\"")
                && before.Any(t => t.AppName == "Half-Life" && t.Exe == "\"steam steam://run/220\""),
                "The two fixture tiles read: " + before.Count);

        // where the tile goes: the newest account's file and Steam's own stop script
        var launcher = Path.Combine(root, "Longshot Launcher.AppImage");
        var p = SteamShortcuts.Locate(launcher, env);
        Require(p is not null && p.Vdf == vdf && p.SteamScript == Path.Combine(xdg, "Steam", "steam.sh"),
                "Locate found the account's file and Steam's own script.");
        Require(SteamShortcuts.Locate("/no/launcher", EnvFor(Path.Combine(root, "no-steam"))) == null,
                "No Steam, no placement, and nothing created there.");

        // our tile: the launcher itself with no options (its window, which checks for updates and
        // plays), paths quoted the way Steam quotes spaced paths in its own file, and an id that
        // hashes the tile's own Exe and name the way Steam does
        Require(p!.Entry.Exe == "\"" + launcher + "\"" && p.Entry.StartDir == "\"" + root + "\""
                && p.Entry.LaunchOptions == ""
                && p.Entry.AppId == SteamShortcuts.SignedId(p.Entry.Exe, SteamShortcuts.Name),
                "The tile starts the launcher's window, quoted and hashed as Steam does: " + p.Entry.Exe);
        Require(!SteamShortcuts.InLibrary(p), "Not in the library yet.");
        Require(SteamShortcuts.Add(p), "Added.");
        var ours = SteamShortcuts.Tiles(vdf).Single(t => t.AppName == SteamShortcuts.Name);
        Require(ours.AppId == SteamShortcuts.SignedId(p.Entry.Exe, SteamShortcuts.Name)
                && ours.Exe == p.Entry.Exe && ours.LaunchOptions == ""
                && SteamShortcuts.Tiles(vdf).Count == 3
                && SteamShortcuts.Tiles(vdf).Any(t => t.AppName == "Half-Life"),
                "Added ours among the two that were there, without disturbing them.");
        Require(SteamShortcuts.InLibrary(p), "In the library now.");
        Require(!SteamShortcuts.Add(p), "Added twice: once.");
        Require(SteamShortcuts.Tiles(vdf).Count == 3 && File.Exists(vdf + ".longshot"),
                "Still ours once, and the marker names the tile.");

        // the capsule art keys by the id the LIBRARY carries, not the id a newer launcher name
        // would compute — checked where the two actually differ: the marker names this tile, a
        // renamed AppImage computes a different id, and only the marker's route finds the tile.
        // (Every other TileAppId check runs where computed == carried, and says nothing between
        // the two; this one is what stops the getter from quietly becoming Entry.AppId)
        var renamed = SteamShortcuts.Locate(Path.Combine(root, "Longshot Launcher 9.AppImage"), env)!;
        Require(renamed.Entry.AppId != ours.AppId
                && SteamShortcuts.TileAppId(renamed) == ours.AppId,
                "a launcher that updated keys its art by the id the tile in the library carries, not the id its new name would compute");

        // the file Steam would read back: whole, ours included
        var again = SteamShortcuts.Tiles(vdf);
        Require(again.Count == 3 && again.Any(t => t.AppName == "Half-Life" && t.Exe == "\"steam steam://run/220\""),
                "The library re-reads whole after our write.");

        // a newer copy of the launcher in the same Steam, the everyday case (the AppImage's name
        // changes with every update): the one tile stays, pointed at whoever last ticked the box
        var moved = SteamShortcuts.Locate(Path.Combine(root, "Longshot Launcher 2.AppImage"), env)!;
        Require(SteamShortcuts.Add(moved), "The tile follows the launcher that ticked the box last.");
        Require(SteamShortcuts.Tiles(vdf).Single(t => t.AppName == SteamShortcuts.Name).Exe
                == "\"" + Path.Combine(root, "Longshot Launcher 2.AppImage") + "\""
                && SteamShortcuts.Tiles(vdf).Count == 3,
                "One tile, now starting the newer launcher; the tiles that were there untouched.");
        Require(SteamShortcuts.InLibrary(moved) && SteamShortcuts.InLibrary(p),
                "Whichever copy of the launcher asks, it sees the one tile in that Steam.");

        // remove: back to the library that was there, byte for byte, and the marker is gone
        var original = File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts.vdf"));
        Require(SteamShortcuts.Remove(moved), "Removed.");
        var back = SteamShortcuts.Tiles(vdf);
        Require(back.Count == 2 && back.Any(t => t.AppId == -1467123759) && back.Any(t => t.AppName == "Half-Life")
                && !File.Exists(vdf + ".longshot"),
                "Back to the two original tiles, marker gone.");
        Require(File.ReadAllBytes(vdf).SequenceEqual(original),
                "Our add and remove leave Steam's own file exactly as it was, byte for byte.");
        Require(!SteamShortcuts.Remove(moved), "Removed twice: once.");

        // a tile the user made by hand in Steam under the same name, for their own script: the
        // appid Steam computed names their script, no marker vouches for it, so it is theirs
        var hm = Path.Combine(root, "handmade");
        var hmVdf = Path.Combine(hm, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(hmVdf)!);
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts-handmade.vdf"), hmVdf);
        var hp = SteamShortcuts.Locate(launcher, EnvFor(hm))!;
        Require(!SteamShortcuts.InLibrary(hp), "Their tile is not ours.");
        Require(!SteamShortcuts.Add(hp), "And not replaced: the name is theirs now.");
        Require(SteamShortcuts.Tiles(hmVdf).Single(t => t.AppName == SteamShortcuts.Name).Exe
                == "/home/deck/Games/Longshot.sh",
                "Left exactly as they made it.");
        Require(!SteamShortcuts.Remove(hp), "Not ours to remove.");

        // a library file Steam has not written yet: made whole with just our tile in it
        var fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(Path.Combine(fresh, "Steam", "userdata", "12345678", "config"));
        var fp = SteamShortcuts.Locate("/games/Longshot/Longshot", EnvFor(fresh))!;
        Require(SteamShortcuts.Add(fp) && SteamShortcuts.Tiles(fp.Vdf).Count == 1
                && SteamShortcuts.Tiles(fp.Vdf)[0].LaunchOptions == "",
                "A new library file with just our tile.");
        Require(SteamShortcuts.Remove(fp) && SteamShortcuts.Tiles(fp.Vdf).Count == 0,
                "And empty again, still readable.");

        // a library whose numbered keys have a hole (Steam renumbers on its own writes, an outside
        // edit may not): the tile lands in the free key, never a second entry under a taken one
        var holed = File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts.vdf"));
        for (var at = 0; at + 10 < holed.Length; at++)
            if (holed[at] == 0 && holed[at + 1] == (byte)'2' && holed[at + 2] == 0 && holed[at + 3] == 0
                && holed[at + 4] == 2 && holed[at + 5] == (byte)'a')
            { holed[at + 1] = (byte)'7'; break; }
        var gap = Path.Combine(root, "gap");
        var gapVdf = Path.Combine(gap, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(gapVdf)!);
        File.WriteAllBytes(gapVdf, holed);
        var gp = SteamShortcuts.Locate(launcher, EnvFor(gap))!;
        Require(SteamShortcuts.Add(gp)
                && SteamShortcuts.EntryKeys(gapVdf).Distinct().Count() == SteamShortcuts.EntryKeys(gapVdf).Count
                && SteamShortcuts.EntryKeys(gapVdf).Contains("1"),
                "The tile took the first free key, no key twice: " + string.Join(",", SteamShortcuts.EntryKeys(gapVdf)));

        // a stale marker beside a relabelled tile: the id Steam kept for the user's original script,
        // with the user's Exe swapped for ours afterwards in Steam's own Properties. The marker's id
        // only vouches for a tile still pointing where the marker says, and this one's id is not the
        // id our path computes: the relabelled tile stays the user's, untouched
        var stale = Path.Combine(root, "stale");
        var staleVdf = Path.Combine(stale, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(staleVdf)!);
        var theirScript = "/home/deck/Games/Longshot.sh";
        var bytes = new List<byte>(File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts-handmade.vdf")));
        var exeKey = new byte[] { 1, (byte)'E', (byte)'x', (byte)'e', 0 };
        var value = Encoding.UTF8.GetBytes(theirScript);
        var at2 = 0;
        while (at2 + exeKey.Length + value.Length < bytes.Count
               && !(bytes.GetRange(at2, exeKey.Length).SequenceEqual(exeKey)
                    && bytes.GetRange(at2 + exeKey.Length, value.Length).SequenceEqual(value))) at2++;
        Require(at2 + exeKey.Length + value.Length < bytes.Count, "The fixture's Longshot Exe was found.");
        // the user's relabel: the Exe now runs our launcher, the id stays the one Steam gave the
        // tile when their script made it
        var rebuilt = bytes.GetRange(0, at2 + exeKey.Length);
        rebuilt.AddRange(Encoding.UTF8.GetBytes(launcher)); rebuilt.Add(0);
        rebuilt.AddRange(bytes.GetRange(at2 + exeKey.Length + value.Length + 1, bytes.Count - (at2 + exeKey.Length + value.Length + 1)));
        File.WriteAllBytes(staleVdf, rebuilt.ToArray());
        var sp = SteamShortcuts.Locate(launcher, EnvFor(stale))!;
        File.WriteAllText(staleVdf + ".longshot", staleVdf + "	" + theirScript + "	" + SteamShortcuts.SignedId(theirScript, SteamShortcuts.Name));
        Require(!SteamShortcuts.InLibrary(sp), "A stale marker does not claim a tile that no longer points where it says.");
        Require(!SteamShortcuts.Add(sp), "And no write over it.");
        Require(SteamShortcuts.Tiles(staleVdf).Single(t => t.AppName == SteamShortcuts.Name).AppId
                == SteamShortcuts.SignedId(theirScript, SteamShortcuts.Name),
                "Their tile keeps the id Steam gave their script.");

        // a type the file's grammar knows but we cannot write back as found: the file is refused,
        // never quietly retyped on the way through
        var typed = File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts.vdf"));
        for (var at3 = 0; at3 + 5 < typed.Length; at3++)
            if (typed[at3] == 2 && typed[at3 + 1] == (byte)'a' && typed[at3 + 2] == (byte)'p' && typed[at3 + 3] == (byte)'p'
                && typed[at3 + 4] == (byte)'i') { typed[at3] = 5; break; }
        var type5 = Path.Combine(root, "type5");
        var type5Vdf = Path.Combine(type5, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(type5Vdf)!);
        File.WriteAllBytes(type5Vdf, typed);
        var tp = SteamShortcuts.Locate(launcher, EnvFor(type5))!;
        try { SteamShortcuts.Add(tp); Require(false, "A type we cannot write back sailed through Add."); }
        catch (InvalidDataException) { }
        Require(File.ReadAllBytes(type5Vdf).SequenceEqual(typed), "Refused, and the file stands exactly as it was.");

        // (longshot) a half-written library: Steam rewrites the whole file on every change, so an
        // interrupted write cuts the file off mid-entry. Reading that leniently and publishing
        // what survived is how tiles the user never touched vanish from the grid for good. Every
        // entry before the cut must survive, the cut must be refused everywhere, and the file must
        // not be rewritten by any of it.
        var whole = File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts.vdf"));
        // cut inside a section body: drop the tail (its closing bytes and everything after)
        var cutAt = whole.Length - 24;
        var half = whole[..cutAt];
        var trunc = Path.Combine(root, "truncated");
        var truncVdf = Path.Combine(trunc, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(truncVdf)!);
        File.WriteAllBytes(truncVdf, half);
        var cutP = SteamShortcuts.Locate(launcher, EnvFor(trunc))!;
        try { SteamShortcuts.Add(cutP); Require(false, "Add rewrote a library cut off mid-write."); }
        catch (InvalidDataException) { }
        Require(File.ReadAllBytes(truncVdf).SequenceEqual(half), "Refused, and the half file stands byte for byte.");
        try { SteamShortcuts.Remove(cutP); Require(false, "Remove rewrote a library cut off mid-write."); }
        catch (InvalidDataException) { }
        Require(File.ReadAllBytes(truncVdf).SequenceEqual(half), "Refused there too, and the file still stands.");
        try { SteamShortcuts.Tiles(truncVdf); Require(false, "Tiles read a truncated library as if it were whole."); }
        catch (InvalidDataException) { }
        // the root itself intact but a section body short: the same refusal from a cleaner cut
        var innerCut = whole[..(whole.Length - 1)];   // the root's own closing 0x08 gone
        File.WriteAllBytes(truncVdf, innerCut);
        try { SteamShortcuts.Add(cutP); Require(false, "Add sailed through a library missing its terminator."); }
        catch (InvalidDataException) { }
        Require(File.ReadAllBytes(truncVdf).SequenceEqual(innerCut), "That refusal leaves the file alone too.");
        // an empty file is not a truncation: a library not started yet is ours to start
        var emptylib = Path.Combine(root, "emptylib2");
        var emptyVdf = Path.Combine(emptylib, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(emptyVdf)!);
        File.WriteAllBytes(emptyVdf, Array.Empty<byte>());
        var ep = SteamShortcuts.Locate(launcher, EnvFor(emptylib))!;
        Require(SteamShortcuts.Add(ep), "An empty library is no truncation: the tile goes in.");
        Require(SteamShortcuts.Tiles(emptyVdf).Any(t => t.AppName == SteamShortcuts.Name), "And reads back from the file we wrote.");

        // (longshot) real files from Steam's own writer end with an extra closing byte past the
        // root (seen on the Deck 2026-11-16: four 0x08 at EOF, one for the virtual top level).
        // Padding after the terminator is normal and must parse; a real cut-off must not.
        var padded = Path.Combine(root, "padded");
        var paddedVdf = Path.Combine(padded, "Steam", "userdata", "33811576", "config", "shortcuts.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(paddedVdf)!);
        File.WriteAllBytes(paddedVdf, [.. whole, (byte)8]);
        var pp = SteamShortcuts.Locate(launcher, EnvFor(padded))!;
        Require(SteamShortcuts.Add(pp), "Steam's own trailing terminator parses: the tile goes in.");
        var backPadded = File.ReadAllBytes(paddedVdf);
        Require(SteamShortcuts.Tiles(paddedVdf).Count(t => t.AppName == SteamShortcuts.Name) == 1,
                "And exactly one Longshot, no duplicate from the padding.");

        // the up/down sign the window leans on: a /proc of our own making, because the difference
        // between the scan answering and the scan being blind is the whole point of it
        var proc = Path.Combine(root, "fake-proc");
        Directory.CreateDirectory(Path.Combine(proc, "9001"));
        Directory.CreateDirectory(Path.Combine(proc, "9002"));
        Directory.CreateDirectory(Path.Combine(proc, "not-a-pid"));
        File.WriteAllText(Path.Combine(proc, "9001", "cmdline"), "/usr/bin/steam\0-x\0");
        File.WriteAllText(Path.Combine(proc, "9002", "cmdline"), "/usr/bin/anythingelse\0");
        Require(SteamShortcuts.SteamUp(proc), "Steam in a /proc we made is seen.");
        var bare = Path.Combine(root, "fake-proc-empty");
        Directory.CreateDirectory(bare);
        Require(!SteamShortcuts.SteamUp(bare), "And its absence is believed.");

        // the small icon. The entry's icon field names it, and Steam wants a .ico there: the tile
        // naming longshot.png showed the blank default on the Deck after a full restart (hardware
        // 2026-11-15), while Heroic and Steam ROM Manager name an .ico for the same field. (The
        // full-size library art is a different mechanism entirely: the capsule checks below.) The
        // tile carries the launcher's own longshot.ico, an older tile of ours gets the icon on the
        // next write, and the appid never moves: Steam's id rule is Exe + name, icon is not part
        // of it. The .png stays for the desktop and menu files, which KDE draws correctly
        // EnvFor names XDG_DATA_HOME: the tile file lives under <xdg>/Steam, the logo under <xdg>/longshot/launcher
        var logoXdg = Path.Combine(root, "logo-data");
        var logoEnv = EnvFor(logoXdg);
        var lvc = Path.Combine(logoXdg, "Steam", "userdata", "1", "config");
        Directory.CreateDirectory(lvc);
        var lp = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", SteamShortcuts.LogoIcoPath(logoEnv), logoEnv)!;
        Require(lp.Entry.Icon == SteamShortcuts.LogoIcoPath(logoEnv), "Locate with an icon path carries it in the entry.");
        Require(SteamShortcuts.Add(lp), "add with a logo");
        Require(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(lp.Vdf)).Contains(SteamShortcuts.LogoIcoPath(logoEnv)),
            "the icon field in the file points at the launcher's .ico");
        // a tile that went in before this build knew about the logo: same appid, icon filled in,
        // and a later write with nothing to offer (its own logo write failed) leaves that art alone
        var lpPlain = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", logoEnv)!;
        Require(!SteamShortcuts.Add(lpPlain), "a plain placement over an art-bearing tile says nothing to do");
        Require(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(lp.Vdf)).Contains(SteamShortcuts.LogoIcoPath(logoEnv)),
            "and the tile keeps the art it has: a caller with no icon never points it at a missing file");
        // the other order, the one a Deck that upgraded from an earlier build hits: the tile went in
        // with no icon, and the logo write that follows gives it one without moving the id
        var upgradeHome = Path.Combine(root, "logo-upgrade");
        var uEnv = EnvFor(upgradeHome);
        Directory.CreateDirectory(Path.Combine(upgradeHome, "Steam", "userdata", "1", "config"));
        var up0 = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", uEnv)!;
        Require(SteamShortcuts.Add(up0), "the first add, no logo to offer");
        Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(up0.Vdf)).Contains(SteamShortcuts.LogoIcoPath(uEnv)),
            "no icon asked for, none written");
        var idNoLogo = SteamShortcuts.Tiles(up0.Vdf).Select(t => t.AppId).Single();
        var up1 = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", SteamShortcuts.LogoIcoPath(uEnv), uEnv)!;
        Require(SteamShortcuts.Add(up1), "the same tile, logo offered");
        Require(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(up1.Vdf)).Contains(SteamShortcuts.LogoIcoPath(uEnv)),
            "an icon-less tile of ours gets the icon on the next write");
        var idAfter = SteamShortcuts.Tiles(up1.Vdf).Select(t => t.AppId).ToList();
        Require(idAfter.Count == 1 && idAfter[0] == idNoLogo
                && idNoLogo == SteamShortcuts.SignedId(up1.Entry.Exe, up1.Entry.AppName),
            "the logo changed the tile, not its Steam id");
        Require(SteamShortcuts.TryWriteLogo(new byte[] { 1, 2, 3 }, logoEnv), "TryWriteLogo writes the .png for the desktop and menu files");
        Require(File.ReadAllBytes(SteamShortcuts.LogoPath(logoEnv)).SequenceEqual(new byte[] { 1, 2, 3 }), "png logo bytes land");
        Require(SteamShortcuts.TryWriteLogoIco(new byte[] { 4, 5, 6 }, logoEnv), "TryWriteLogoIco writes the tile's .ico");
        Require(File.ReadAllBytes(SteamShortcuts.LogoIcoPath(logoEnv)).SequenceEqual(new byte[] { 4, 5, 6 }), "ico bytes land");
        Require(Path.GetDirectoryName(SteamShortcuts.LogoPath(logoEnv)) == Path.GetDirectoryName(SteamShortcuts.LogoIcoPath(logoEnv))
                && !SteamShortcuts.LogoIcoPath(logoEnv).Contains("Steam"), "both logos sit beside each other, outside Steam's folders");
        // Remove takes the tile without touching the logo: a later add wants it there
        Require(SteamShortcuts.Remove(lp), "remove with a logo");
        Require(File.Exists(SteamShortcuts.LogoPath(logoEnv)) && File.Exists(SteamShortcuts.LogoIcoPath(logoEnv)),
            "both logos outlive the tile; the next Add points at them again");

        // the tile's capsule artwork: Steam draws a Gaming-mode library tile from grid/ images
        // keyed by the appid read UNSIGNED (the library tile is <id>p.png, the recently-played row
        // <id>.png; Heroic and Steam ROM Manager write the same names). The empty-icon tile above
        // showed the blank default on the Deck with none of these present
        var artXdg = Path.Combine(root, "art-data");
        var artEnv = EnvFor(artXdg);
        var avc = Path.Combine(artXdg, "Steam", "userdata", "7", "config");
        Directory.CreateDirectory(avc);
        var ap = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", artEnv)!;
        Require(SteamShortcuts.Add(ap), "add for the artwork checks");
        var signed = ap.Entry.AppId;
        Require(signed < 0, "our tile's id reads signed negative in the VDF (the fixture's Exe)");
        var unsigned = unchecked((uint)signed).ToString();
        // a .jpg already in a slot holds it against our .png: the Deck's library shows Steam's
        // own art in .jpg (4100769614p.jpg), and two files in one slot is a coin flip in Steam
        Directory.CreateDirectory(Path.Combine(avc, "grid"));   // Steam makes this itself when it saves art
        File.WriteAllBytes(Path.Combine(avc, "grid", $"{unsigned}_hero.jpg"), new byte[] { 11 });
        var wrote = SteamShortcuts.WriteArtwork(ap.Vdf, signed,
            [("", "png", new byte[] { 9 }), ("p", "png", new byte[] { 8 }), ("_hero", "png", new byte[] { 7 })]);
        // every free piece lands under BOTH names the Deck's own library carries: the 32-bit id
        // the new UI reads and (id << 32) | 0x02000000 the old Big Picture reads — proven on the
        // Deck by a working tile whose landscape capsule sits byte-identical under both names
        Require(wrote == 4, "each free capsule slot fills, under both names, in its own extension");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}_hero.jpg")).SequenceEqual(new byte[] { 11 })
                && !File.Exists(Path.Combine(avc, "grid", $"{unsigned}_hero.png"))
                && !File.Exists(Path.Combine(avc, "grid", $"{SteamShortcuts.GridId64(signed)}_hero.png")),
            "a slot a .jpg already holds is left exactly as it is, under either name: no competing .png");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}.png")).SequenceEqual(new byte[] { 9 }),
            "the landscape capsule is keyed by the UNSIGNED id (not the negative decimal)");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}p.png")).SequenceEqual(new byte[] { 8 }),
            "the library tile is <id>p.png: suffix p, no underscore");
        // a half-written temp must not be mistaken for a capsule: AtomicFile publishes as
        // <target>.<guid>.tmp then renames, so a launch interrupted mid-write leaves one
        // behind, and reading it as "the user's art" would leave the tile blank forever
        var grid = SteamShortcuts.GridDir(ap.Vdf);
        File.WriteAllBytes(Path.Combine(grid, $"{unsigned}_logo.png.deadbeef.tmp"), new byte[] { 12 });
        var afterTmp = SteamShortcuts.WriteArtwork(ap.Vdf, signed, [("_logo", "png", new byte[] { 6 })]);
        Require(afterTmp == 2
                && File.ReadAllBytes(Path.Combine(grid, $"{unsigned}_logo.png")).SequenceEqual(new byte[] { 6 })
                && File.Exists(Path.Combine(grid, $"{SteamShortcuts.GridId64(signed)}_logo.png")),
            "an interrupted temp does not hold the slot: the tile's art still lands");
        Require(File.ReadAllBytes(Path.Combine(grid, $"{unsigned}_logo.png.deadbeef.tmp")).SequenceEqual(new byte[] { 12 })
                && !File.Exists(Path.Combine(grid, $"{unsigned}_logo.jpg")),
            "a temp we did not make is left alone (it may be Steam mid-saving the user's own art)");
        Require(File.Exists(Path.Combine(avc, "grid", $"{unsigned}p.png"))
                && File.Exists(Path.Combine(avc, "grid", $"{SteamShortcuts.GridId64(signed)}p.png")),
            "the free portrait slot filled under both names");
        Require(!File.Exists(Path.Combine(avc, "grid", $"{signed}.png")), "never a file named for the signed id");
        // the user's own art wins: an existing capsule is never replaced, and a later run
        // re-offering all of them says so by writing nothing
        File.WriteAllBytes(Path.Combine(avc, "grid", $"{unsigned}p.png"), new byte[] { 42 });
        File.WriteAllBytes(Path.Combine(avc, "grid", $"{SteamShortcuts.GridId64(signed)}p.png"), new byte[] { 43 });
        var wrote2 = SteamShortcuts.WriteArtwork(ap.Vdf, signed,
            [("", "png", new byte[] { 9 }), ("p", "png", new byte[] { 8 })]);
        Require(wrote2 == 0, "nothing rewritten when the capsules are already there, under either name");
        Require(File.ReadAllBytes(Path.Combine(avc, "grid", $"{unsigned}p.png")).SequenceEqual(new byte[] { 42 })
                && File.ReadAllBytes(Path.Combine(avc, "grid", $"{SteamShortcuts.GridId64(signed)}p.png")).SequenceEqual(new byte[] { 43 }),
            "capsules under both names survive our write: art is only ever filled in");
        // the 64-bit name, verified against the Deck's own working tile: EmulationStation's art
        // sits at 2827843537.png and 12145495509653520384.png, which is exactly this formula
        Require(SteamShortcuts.GridId64(-1467123759) == 12145495509653520384UL,
            "GridId64 reproduces the id the Deck's working tile's art is keyed by");

        // the game's own banner: the builder keeps one still of the player's own game's footage
        // in their game folder (Longshot nx2/game_hero.py, longshot_hero.png); the hero slot
        // takes its bytes when the folder has a whole one. Nothing game-derived ships in the
        // launcher: only this reader of a file the player's own build wrote is here
        byte[] Png(int w, int h)   // (a real PNG of w x h: what GameHero reads the header of)
        {
            var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            byte[] Chunk(string tag, byte[] data)
            {
                var c = new List<byte>();
                c.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
                c.AddRange(tag.Select(ch => (byte)ch));
                c.AddRange(data);
                uint crc = 0xFFFFFFFFu;
                foreach (var x in tag.Select(ch => (byte)ch).Concat(data))
                {
                    crc ^= x;
                    for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
                crc ^= 0xFFFFFFFFu;
                c.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
                return c.ToArray();
            }
            byte[] ihdr = [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w,
                           (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h,
                           8, 0, 0, 0, 0];
            b.AddRange(Chunk("IHDR", ihdr));
            b.AddRange(Chunk("IDAT", [0x78, 0x01, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]));
            b.AddRange(Chunk("IEND", []));
            return b.ToArray();
        }
        var heroHome = Path.Combine(root, "hero-folder");
        Directory.CreateDirectory(heroHome);
        Require(SteamShortcuts.GameHero(null) == null && SteamShortcuts.GameHero("") == null
                && SteamShortcuts.GameHero(heroHome) == null,
            "no folder, no file: no hero, no fuss");
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 620));
        Require(SteamShortcuts.GameHero(heroHome)?.Length == new FileInfo(Path.Combine(heroHome, SteamShortcuts.HeroFile)).Length,
            "a whole hero at the hero's exact shape reads");
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 1080));
        Require(SteamShortcuts.GameHero(heroHome) == null, "the whole frame is not a hero: the generated one stands");
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), "not a png"u8.ToArray());
        Require(SteamShortcuts.GameHero(heroHome) == null, "foreign bytes are no hero rather than a broken capsule");
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Array.Empty<byte>());
        Require(SteamShortcuts.GameHero(heroHome) == null, "an empty file is no hero");
        // a header drawn for show: signature, IHDR and IEND with their CRCs right, no IDAT
        // between them. The old check read this as art and offered it to Steam, dropping the
        // logo slot for a black capsule: whole means whole, both ends, same rules
        byte[] head = Png(1920, 620);
        byte[] impostor = [.. head[..33], .. head[^12..]];
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), impostor);
        Require(SteamShortcuts.GameHero(heroHome) == null, "a PNG that only draws its header is no hero");
        byte[] cut = Png(1920, 620);
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), cut[..(cut.Length - 6)]);
        Require(SteamShortcuts.GameHero(heroHome) == null, "a hero cut mid-write is no hero");
        // WithGameHero: one slot changed, the rest untouched, and the input array never mutated
        // (the App caches its capsules: a mutated cache would strand this game's hero in the next)
        (string suffix, string ext, byte[] bytes)[] gen =
            [("", "png", new byte[] { 1 }), ("p", "png", new byte[] { 2 }),
             ("_hero", "png", new byte[] { 3 }), ("_logo", "png", new byte[] { 4 })];
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 620));
        var own = SteamShortcuts.GameHero(heroHome)!;
        var with = SteamShortcuts.WithGameHero(gen, heroHome);
        // the hero takes its slot and the logo slot is dropped with it: the hero carries the mark
        // now, and a second mark drawn over it is the look the whole change exists to stop
        Require(with.Length == 3
                && with[2].suffix == "_hero" && with[2].ext == "png" && with[2].bytes.SequenceEqual(own)
                && with[0].suffix == "" && with[1].suffix == "p"
                && !with.Any(c => c.suffix == "_logo")
                && ReferenceEquals(with[0].bytes, gen[0].bytes) && ReferenceEquals(with[1].bytes, gen[1].bytes),
            "the hero slot changes to the folder's bytes, the logo slot goes away, the rest pass through");
        Require(gen[2].bytes.SequenceEqual(new byte[] { 3 }) && !ReferenceEquals(with, gen),
            "the launcher's cached capsules are never mutated: no hero leaks between games");
        Require(ReferenceEquals(SteamShortcuts.WithGameHero(gen, null), gen),
            "a build with no hero file offers the generated capsules as before");
        // end to end, in a Steam home of its own: a whole hero takes its slot under both names; a
        // broken one never does (the generated capsule lands there instead); a whole hero offered
        // again after its slot came free lands again
        var heroXdg = Path.Combine(root, "hero-data");
        Directory.CreateDirectory(Path.Combine(heroXdg, "Steam", "userdata", "7", "config"));
        var heroProbe = SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", EnvFor(heroXdg))!;
        Require(SteamShortcuts.Add(heroProbe), "add for the hero's own checks");
        var hi = unchecked((uint)heroProbe.Entry.AppId).ToString();
        var heroGrid = SteamShortcuts.GridDir(heroProbe.Vdf);
        var heroWrote = SteamShortcuts.WriteArtwork(heroProbe.Vdf, heroProbe.Entry.AppId, SteamShortcuts.WithGameHero(gen, heroHome));
        Require(heroWrote == 6                                        // (three slots, two names: no logo offered)
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_hero.png")).SequenceEqual(own)
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{SteamShortcuts.GridId64(heroProbe.Entry.AppId)}_hero.png")).SequenceEqual(own)
                && !File.Exists(Path.Combine(heroGrid, $"{hi}_logo.png"))
                && !File.Exists(Path.Combine(heroGrid, $"{SteamShortcuts.GridId64(heroProbe.Entry.AppId)}_logo.png")),
            "the game's own hero fills its slot under both names, the logo slot stays unwritten beside it: "
            + "the hero carries the mark, and the Deck's own client draws no logo and no placeholder for a slot with no file");
        File.Delete(Path.Combine(heroGrid, $"{hi}_hero.png"));
        File.Delete(Path.Combine(heroGrid, $"{SteamShortcuts.GridId64(heroProbe.Entry.AppId)}_hero.png"));
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 630));   // (broken: wrong shape)
        Require(SteamShortcuts.WriteArtwork(heroProbe.Vdf, heroProbe.Entry.AppId, SteamShortcuts.WithGameHero(gen, heroHome)) == 4
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_hero.png")).SequenceEqual(gen[2].bytes)
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_logo.png")).SequenceEqual(gen[3].bytes),
            "a wrong-shape hero reaches no capsule: the generated set fills every open slot beside it, the logo back in with them");
        File.Delete(Path.Combine(heroGrid, $"{hi}_hero.png"));
        File.Delete(Path.Combine(heroGrid, $"{SteamShortcuts.GridId64(heroProbe.Entry.AppId)}_hero.png"));
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 620));
        Require(SteamShortcuts.WriteArtwork(heroProbe.Vdf, heroProbe.Entry.AppId, SteamShortcuts.WithGameHero(gen, heroHome)) == 2
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_hero.png")).SequenceEqual(own),
            "a whole hero fills a slot that came free, and lands under both names again");
        // the upgrade path: an install from before the marked hero holds this launcher's
        // generated logo in the grid (the block above put it back), and WriteArtwork never
        // overwrites: beside a folder hero that file is a second mark over the first. The
        // launcher retires its own bytes under both names and changes nothing else
        var g64 = SteamShortcuts.GridId64(heroProbe.Entry.AppId).ToString();
        Require(SteamShortcuts.RetireGeneratedLogo(heroProbe.Vdf, heroProbe.Entry.AppId, gen[3].bytes) == 2
                && !File.Exists(Path.Combine(heroGrid, $"{hi}_logo.png"))
                && !File.Exists(Path.Combine(heroGrid, $"{g64}_logo.png"))
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_hero.png")).SequenceEqual(own),
            "the launcher retires its own generated logo beside a folder hero, under both names, the hero standing");
        Require(SteamShortcuts.WriteArtwork(heroProbe.Vdf, heroProbe.Entry.AppId, SteamShortcuts.WithGameHero(gen, heroHome)) == 0
                && !File.Exists(Path.Combine(heroGrid, $"{hi}_logo.png")),
            "the retired logo stays retired: a folder-hero build re-run offers no logo to put back");
        // ours byte for byte or not at all: a capsule the user chose in Steam's UI survives, and
        // so does anything a byte off our own
        File.WriteAllBytes(Path.Combine(heroGrid, $"{hi}_logo.png"), [4, 4, 4]);
        Require(SteamShortcuts.RetireGeneratedLogo(heroProbe.Vdf, heroProbe.Entry.AppId, gen[3].bytes) == 0
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_logo.png")).SequenceEqual(new byte[] { 4, 4, 4 }),
            "art that is not the launcher's own logo byte for byte is never touched");
        // and no folder hero, no retirement logic at all: the generated set fills everything,
        // logo included, as it always did
        File.Delete(Path.Combine(heroGrid, $"{hi}_logo.png"));
        File.Delete(Path.Combine(heroGrid, $"{g64}_hero.png"));
        File.Delete(Path.Combine(heroGrid, $"{hi}_hero.png"));
        File.WriteAllBytes(Path.Combine(heroHome, SteamShortcuts.HeroFile), Png(1920, 630));   // (broken: no folder hero)
        Require(SteamShortcuts.WriteArtwork(heroProbe.Vdf, heroProbe.Entry.AppId, SteamShortcuts.WithGameHero(gen, heroHome)) == 4
                && File.ReadAllBytes(Path.Combine(heroGrid, $"{hi}_logo.png")).SequenceEqual(gen[3].bytes),
            "with no whole hero in the folder the generated logo fills its slot as before");

        // a tile an updated launcher inherited keeps the id its marker recorded; the art keys off
        // the id the LIBRARY carries, not the freshly computed one
        Require(SteamShortcuts.TileAppId(ap) == signed, "TileAppId reports the id the tile in the library carries");
        var noTileHome = Path.Combine(root, "art-none");
        Directory.CreateDirectory(Path.Combine(noTileHome, "Steam", "userdata", "7", "config"));
        Require(SteamShortcuts.TileAppId(SteamShortcuts.Locate("/opt/Longshot Launcher.AppImage", EnvFor(noTileHome))!)
                == SteamShortcuts.SignedId($"\"/opt/Longshot Launcher.AppImage\"", SteamShortcuts.Name),
            "with no tile in the library yet, TileAppId predicts the id the Add would give it");

        // the account Steam itself says it used (loginusers.vdf's MostRecent, mapped to the low
        // 32 bits of the id), not the folder some other account's write touched last
        var shared = Path.Combine(root, "shared");
        var sharedUsers = Path.Combine(shared, "Steam", "userdata");
        foreach (var id in new[] { "11111111", "22222222" })
        {
            Directory.CreateDirectory(Path.Combine(sharedUsers, id, "config"));
            File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), Path.Combine(sharedUsers, id, "config", "shortcuts.vdf"));
        }
        Directory.CreateDirectory(Path.Combine(shared, "Steam", "config"));
        // (22222222's folder is the newer one; 11111111 is the account signed in)
        File.SetLastWriteTimeUtc(Path.Combine(sharedUsers, "11111111", "config"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(sharedUsers, "22222222", "config"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        const ulong steamIdBase = 76561197960265728;
        File.WriteAllText(Path.Combine(shared, "Steam", "config", "loginusers.vdf"),
            "\"users\"\n{\n\t\"" + (steamIdBase + 11111111) + "\"\n\t{\n\t\t\"AccountName\"\t\t\"alice\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n\t\""
            + (steamIdBase + 22222222) + "\"\n\t{\n\t\t\"AccountName\"\t\t\"bob\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t}\n}\n");
        Require(SteamShortcuts.Locate(launcher, EnvFor(shared))?.Vdf
                == Path.Combine(sharedUsers, "11111111", "config", "shortcuts.vdf"),
            "the account in use is the one loginusers.vdf says was last used, not the newest folder");
        // (no loginusers.vdf there: the newest folder still answers, as it did)
        File.Delete(Path.Combine(shared, "Steam", "config", "loginusers.vdf"));
        Require(SteamShortcuts.Locate(launcher, EnvFor(shared))?.Vdf
                == Path.Combine(sharedUsers, "22222222", "config", "shortcuts.vdf"),
            "with no loginusers.vdf, the newest folder answers (the fallback)");

        // the first rewrite keeps the library as it was, byte for byte, beside it
        Require(File.Exists(vdf + ".bak-longshot")
                && File.ReadAllBytes(vdf + ".bak-longshot").SequenceEqual(File.ReadAllBytes(Path.Combine(fixtures, "steam", "shortcuts.vdf"))),
            "the first rewrite of a library kept it as it was (" + vdf + ".bak-longshot)");

        // a symbolic-linked library (a shared or redirected one) is written THROUGH the link
        if (!OperatingSystem.IsWindows())
        {
            var linked = Path.Combine(root, "linked");
            Directory.CreateDirectory(linked);
            var real = Path.Combine(linked, "real-shortcuts.vdf");
            File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), real);
            var link = Path.Combine(linked, "shortcuts.vdf");
            File.CreateSymbolicLink(link, real);
            var linkPlacement = new SteamShortcuts.Placement(SteamShortcuts.GameEntry(launcher), link, null);
            var linkedBefore = File.ReadAllBytes(real);
            Require(SteamShortcuts.Add(linkPlacement), "Added through a symbolic link.");
            Require((File.ResolveLinkTarget(link, returnFinalTarget: false)?.FullName ?? "") == real
                    && SteamShortcuts.Tiles(real).Any(t => t.AppName == SteamShortcuts.Name),
                "the link is still a link, and the file it names holds the tile");
            Require(File.Exists(real + ".bak-longshot") && File.ReadAllBytes(real + ".bak-longshot").SequenceEqual(linkedBefore)
                    && !File.Exists(link + ".bak-longshot"),
                "the library as it was before us is kept beside the real file, not beside the link");
            Require(SteamShortcuts.Remove(linkPlacement), "Removed through the link.");
            Require(File.ReadAllBytes(real + ".bak-longshot").SequenceEqual(linkedBefore),
                "the first backup is never overwritten by a later change");

            // a link that cannot be resolved (a loop) is refused, never replaced by a file of ours
            var loopA = Path.Combine(linked, "loop-a");
            var loopB = Path.Combine(linked, "loop-b");
            File.CreateSymbolicLink(loopA, loopB);
            File.CreateSymbolicLink(loopB, loopA);
            var refused = false;
            try { SteamShortcuts.Add(new SteamShortcuts.Placement(SteamShortcuts.GameEntry(launcher), loopA, null)); }
            catch (InvalidDataException) { refused = true; }
            Require(refused && new FileInfo(loopA).LinkTarget != null,
                "a symbolic link that cannot be resolved is refused, and stays a link");
        }
    }
}
