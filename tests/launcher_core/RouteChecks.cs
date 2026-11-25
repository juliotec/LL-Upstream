using LongshotLauncher.Core;

/// Where Play sends the game (SteamLaunchRoute): the fresh-Deck controller question. Steam up, our
/// tile in its library, and that library loaded by the running Steam: Play hands off through
/// steam://rungameid with the id the tile CARRIES (the same id the grid art keys off — after a
/// launcher update InLibrary finds the tile by its marker under the old id, and a URI naming the
/// id the new path computes addresses a tile Steam has never loaded). The handoff arms a one-shot
/// naming the build this Play asked for; the launched instance consumes it and starts THAT direct,
/// so one handoff can never cause a second and a routed Play of a non-default row never silently
/// launches the default build. Steam not running, no tile, the library not loaded yet, the opener
/// refusing (its exit code checked, not its spawn), inside Game Mode, or Windows: the direct
/// launch, exactly as before. Nothing here talks to a real Steam: /proc folders and uptime are
/// authored, the URI opener is a delegate, and OpenWith runs real processes.
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

    // (arms still standing in a handoff directory: a file that is not a claim product (.taken from
    // a claim whose reader died, .played from a successful claim, .abandoned from a delivery the
    // sender cancelled — round-five L3). Claim products are left behind on purpose and sweep by
    // age; "consumed" means no standing arm, not an empty directory)
    static IEnumerable<string> StandingArms(string dir) =>
        Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".taken") && !f.EndsWith(".played") && !f.EndsWith(".abandoned"));

    // (an arm name in the exact shape ArmToken writes: 14-digit ms time, dash, a 12-character tail.
    // Round-five L5: TakeHandoff's candidate test IS this name, so a fixture that means an arm has
    // to wear its name; anything else is a stranger's file and is left alone)
    static int armSeq;
    // (the exact shape ArmToken writes: 14-digit ms, dash, then a Path.GetRandomFileName tail —
    // 8 chars from base32 "abcdefghijklmnopqrstuvwxyz012345", a dot, 3 more. Digits 6-9 cannot
    // occur in the generator's alphabet, so the fixture's tail stays within 0-5, and each `which`
    // still names a different arm)
    static string ArmName(long ms, int which) => $"{ms:D14}-abcdefgh.{(which / 36) % 6}{(which / 6) % 6}{which % 6}";

    static void RouteChecks(string fixtures)
    {
        var root = Folder("route");

        // a Steam install to place a tile in, one tile ours, plus the authored /proc folders
        Func<string, string?> EnvFor(string xdg) => k => k switch
        {
            "HOME" => Path.Combine(root, "home"),
            "XDG_DATA_HOME" => xdg,
            _ => null,
        };
        var xdg = Path.Combine(root, "data");
        var launcher = Path.Combine(root, "bin", "longshot-launcher");
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(launcher, "");
        // a Steam install Locate can answer for: userdata/<account>/config, the newest account folder
        var steamCfg = Path.Combine(xdg, "Steam", "userdata", "33811576", "config");
        Directory.CreateDirectory(steamCfg);

        var up = Path.Combine(root, "proc-up");           // (no uptime: LibraryLoaded answers optimistic)
        Directory.CreateDirectory(Path.Combine(up, "777"));
        File.WriteAllText(Path.Combine(up, "777", "cmdline"), "/usr/bin/steam\0\0");
        var down = Path.Combine(root, "proc-down");
        Directory.CreateDirectory(down);

        // the tile exists and Steam is up: routed, at the 64-bit id of the id the tile carries
        var placed = SteamShortcuts.Locate(launcher, EnvFor(xdg))!;
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), placed.Vdf, true);   // a real Deck library (fixture)
        Require(SteamShortcuts.Add(placed), "the tile is written into the copied library");
        var opened = new List<string>();
        var d = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up);
        Require(d.Route == SteamLaunchRoute.Kind.RunGameId, "Steam up plus our tile: Play is routed through Steam.");
        Require(opened.Count == 1 && opened[0] == $"steam://rungameid/{SteamShortcuts.GridId64(SteamShortcuts.TileAppId(placed))}",
                "the handoff names the tile's carried id in 64-bit form, the same id the grid art keys off");

        // the review's P2, pinned: a launcher that updated finds the tile by its marker under the
        // OLD id; the URI must name the carried id, not the id the new path would compute.
        // (round-five L2 first: the moment BEFORE the tile is repointed, its Exe names the OLD
        // launcher file — a launch Steam makes goes to that file, whose arrival would play ITS
        // default with this Play's arm. So the mismatched tile answers direct with a note; then
        // the Ready screen's Add repoints the Exe, and routing resumes at the CARRIED id — the id
        // the grid art and controller settings key off, never the fresh one)
        var updated = SteamShortcuts.Locate(Path.Combine(root, "bin", "longshot-launcher v9"), EnvFor(xdg))!;
        Require(updated.Entry.AppId != placed.Entry.AppId, "the renamed launcher computes a different id");
        opened.Clear();
        var staleTile = SteamLaunchRoute.Decide(Platform.Linux, updated, _ => null, uri => { opened.Add(uri); return true; }, up);
        Require(staleTile.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && staleTile.Note.Contains("different launcher file"),
                "a tile whose Exe is not this launcher is not handed off: its arrival would run a different file (round-five L2).");
        Require(SteamShortcuts.Add(updated), "the ready screen repoints the tile at this launcher");
        opened.Clear();
        var updR = SteamLaunchRoute.Decide(Platform.Linux, updated, _ => null, uri => { opened.Add(uri); return true; }, up);
        Require(updR.Route == SteamLaunchRoute.Kind.RunGameId
                && opened[0] == $"steam://rungameid/{SteamShortcuts.GridId64(SteamShortcuts.TileAppId(placed))}"
                && opened[0] != $"steam://rungameid/{SteamShortcuts.GridId64(updated.Entry.AppId)}",
                "once the tile points here again the handoff names the id the tile carries, never the fresh one");
        // (and repoint it back at `placed`, as a second Ready screen would: every check below reuses
        // `placed` as "the launcher whose tile is current", which the L2 comparison now enforces)
        Require(SteamShortcuts.Add(placed), "the tile is repointed at the original launcher for the checks below");

        // the tile launched this launcher already (SteamGameId in the environment): direct, no loop
        var back = SteamLaunchRoute.Decide(Platform.Linux, placed, k => k == "SteamGameId" ? "99999" : null,
                                           uri => { opened.Add(uri); return true; }, up);
        Require(back.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 1,
                "a launch under Steam's tile starts the game direct instead of handing off again.");

        // inside Steam's own session (Game Mode): never hand off across the seat boundary
        var gmsession = SteamLaunchRoute.Decide(Platform.Linux, placed, k => k == "XDG_CURRENT_DESKTOP" ? "gamescope" : null,
                                                uri => { opened.Add(uri); return true; }, up);
        Require(gmsession.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 1,
                "a Play inside Game Mode stays direct; routing would start the game in another seat.");

        // Steam up but no tile: direct with the reason a toast can show
        var notile = SteamLaunchRoute.Decide(Platform.Linux, null, _ => null, uri => { opened.Add(uri); return true; }, up);
        Require(notile.Route == SteamLaunchRoute.Kind.Direct && notile.Note.Contains("no Longshot tile"),
                "no tile in Steam yet: direct, and the note says why.");

        // Steam not running at all: direct, no note (the Deck's controller question is moot)
        var nosteam = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, down);
        Require(nosteam.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 1,
                "Steam not running: the direct launch, and no handoff attempted.");

        // the review's "not loaded yet" case: the Steam process started BEFORE the tile was written
        // (Steam keeps its library in memory from its own start — measured on the Deck 2026-11-15),
        // so a rungameid for it goes nowhere: direct, with the note that says why
        var late = Path.Combine(root, "data-late");
        var lateCfg = Path.Combine(late, "Steam", "userdata", "33811576", "config");
        Directory.CreateDirectory(lateCfg);
        var lateVdf = Path.Combine(lateCfg, "shortcuts.vdf");
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), lateVdf);
        var lateP = SteamShortcuts.Locate(launcher, EnvFor(late))!;
        string lateDir() => SteamLaunchRoute.HandoffPath(SteamLaunchRoute.ArrivalHome(lateP, EnvFor(late))!);
        Require(SteamShortcuts.Add(lateP), "the tile lands");
        File.SetLastWriteTimeUtc(lateVdf, DateTime.UtcNow);           // (the copy kept the fixture's old mtime)
        var startedBefore = Path.Combine(root, "proc-stale");           // (boot+1s: steam up long before the write)
        SteamProc(startedBefore, 100);
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "a library written after Steam started is not in the running Steam's library.");
        var startedAfter = Path.Combine(root, "proc-fresh");            // (boot+1000s: ≈ now, after the write)
        SteamProc(startedAfter, 100_000);
        Require(SteamShortcuts.LibraryLoaded(lateVdf, startedAfter),
                "a Steam started after the write has read it.");
        // the review's probe, pinned: Steam rewrites its own library file (play time, properties),
        // and one such rewrite bumps the mtime past Steam's own start — the answer must not flip.
        // The marker's first-seen time is what LibraryLoaded compares, and Add keeps it across
        // rewrites; a bare mtime comparison would have broken routing on any Deck that has ever
        // played the tile
        File.SetLastWriteTimeUtc(lateVdf, DateTime.UtcNow);             // (content untouched, mtime now)
        Require(SteamShortcuts.LibraryLoaded(lateVdf, startedAfter),
                "a later rewrite of the library does not make a loaded tile look unloaded.");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "and the same rewrite does not rescue the truly-not-loaded case.");

        // (round-three P2, pinned: a marker only vouches for a tile that is STILL in the library.
        // Steam overwrote the file and our entry went with it (the review's probe: re-copy Steam's
        // own library over ours, marker left standing), and a re-Add must start the clock FRESH —
        // keeping the old first-seen time would tell a Steam that never reloaded the tile that it
        // loaded it just now, and route a URI into a library Steam does not have. Age the marker by
        // hand first, so the old behaviour (retain) would answer loaded and this bites)
        var agedSidecar = lateVdf + ".longshot";
        var partsNow = File.ReadAllText(agedSidecar).Split('\t');
        File.WriteAllText(agedSidecar, string.Join('\t', partsNow[..4]) + "\t" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100_000));
        Require(SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "the aged marker says the tile entered long before this Steam: loaded.");
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), lateVdf, true);  // (Steam's write: our entry is gone, marker stands)
        Require(SteamShortcuts.Add(lateP),
                "re-adding our tile to the overwritten library works.");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "a re-added tile does NOT inherit the vanished tile's first-seen time: this Steam never loaded it.");

        // (round-three P3: a nonsense marker stamp used to crash the load check — long.MaxValue
        // parses and FromUnixTimeSeconds throws. A marker we would not have written is not our
        // evidence: answer through the mtime fallback instead, without throwing)
        File.WriteAllText(agedSidecar, string.Join('\t', partsNow[..4]) + "\t" + long.MaxValue);
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "a nonsense marker falls back to the file's own time instead of throwing.");
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), lateVdf, true);
        SteamShortcuts.Add(lateP);
        File.WriteAllText(agedSidecar, string.Join('\t', File.ReadAllText(agedSidecar).Split('\t')[..4]) + "\t-5");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "and a negative marker stamp falls back the same way.");

        // (round-five L6: REPOINTING the tile — the Ready screen writing the entry at a new
        // launcher file — is a launch-relevant change a running Steam has not seen. Its library
        // still holds the OLD Exe until Steam restarts, so the answer must go back to not-loaded
        // even though the tile entered long ago: the sixth marker field, changed-at, is the stamp
        // that makes a repoint wait for the restart)
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), lateVdf, true);
        var relayout = SteamShortcuts.Locate(Path.Combine(root, "bin", "longshot-launcher v9"), EnvFor(late))!;
        Require(SteamShortcuts.Add(relayout), "a tile at the renamed launcher lands");
        // age BOTH stamps by hand: the tile entered long ago, and nothing launch-relevant changed since
        var repSide = lateVdf + ".longshot";
        var repParts = File.ReadAllText(repSide).Split('\t');
        var aged = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 100_000;
        File.WriteAllText(repSide, string.Join('\t', repParts[..4]) + "\t" + aged + "\t" + aged);
        Require(SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "an aged, unchanged tile is loaded for this Steam");
        Require(SteamShortcuts.Add(lateP), "the ready screen repoints the entry at this launcher");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, startedBefore),
                "a REPOINT is launch-relevant: the running Steam still holds the old Exe, so routing waits for a restart (L6).");
        // (round-five L6 audit, pinned exactly: a client started just OVER a second before the
        // repoint used to pass through the -2 s slop and be trusted although it loaded the old
        // Exe. The repoint epoch is compared strictly now; only the arrival epoch keeps its slack)
        var justBefore = Path.Combine(root, "proc-just-before");
        SteamProc(justBefore, 99_900);          // (boot+999 s: ~1 s before the repoint just stamped)
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, justBefore),
                "a Steam started one second before the repoint is not trusted: the repoint epoch is strict (L6 audit).");
        Require(SteamShortcuts.LibraryLoaded(lateVdf, startedAfter),
                "a Steam started after the repoint reads the new Exe: loaded again");

        // (round-five L7: SteamUp counts a gamescope session, but no library-owning client means
        // nothing has this library: the old answer here was optimistic true, and a Play routed a
        // URI to a client that was not there yet)
        var gscOnly = Path.Combine(root, "proc-gsc-only");
        Directory.CreateDirectory(Path.Combine(gscOnly, "900"));
        File.WriteAllText(Path.Combine(gscOnly, "uptime"), "1000.0 500.0");
        File.WriteAllText(Path.Combine(gscOnly, "900", "cmdline"), "/usr/bin/gamescope\0");
        Require(SteamShortcuts.SteamUp(gscOnly), "gamescope counts as Steam up for up/down");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, gscOnly),
                "gamescope alone holds no library: not loaded, whatever its start time (L7).");
        // (round-five L7 audit: the same gamescope-only tree WITHOUT a readable uptime. The old
        // order read /proc/uptime first, so a bare/locked-down /proc answered optimistic true
        // before any client was looked for. The client scan now comes first, so no client answers
        // not-loaded on its own evidence)
        var gscNoUptime = Path.Combine(root, "proc-gsc-no-uptime");
        Directory.CreateDirectory(Path.Combine(gscNoUptime, "900"));
        File.WriteAllText(Path.Combine(gscNoUptime, "900", "cmdline"), "/usr/bin/gamescope\0");
        Require(!SteamShortcuts.LibraryLoaded(lateVdf, gscNoUptime),
                "no client and no readable uptime is still not loaded: the client scan decides first (L7 audit).");
        // (round-five L5 audit: SweepClaimed used to delete ANY file whose name began with 14 digits
        // and was older than an hour — a stranger's file named e.g. <old-ms>-personal-notes.txt was
        // deleted by a plain TakeHandoff. Only real claim products are swept now)
        var strangerNotes = Path.Combine(lateDir(), $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7_200_000:D14}-personal-notes.txt");
        Directory.CreateDirectory(lateDir());
        File.WriteAllText(strangerNotes, "the user's notes, nothing to do with the launcher");
        SteamLaunchRoute.TakeHandoff(lateDir());
        Require(File.Exists(strangerNotes),
                "a stranger's old file that merely begins with 14 digits is never swept: only claim products age out (L5 audit).");
        File.Delete(strangerNotes);

        opened.Clear();
        var notloaded = SteamLaunchRoute.Decide(Platform.Linux, lateP, _ => null, uri => { opened.Add(uri); return true; }, startedBefore);
        Require(notloaded.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && notloaded.Note.Contains("not loaded"),
                "an unloaded tile is not handed off; the note says a restart makes later Plays route.");
        var loadedOk = SteamLaunchRoute.Decide(Platform.Linux, lateP, _ => null, uri => { opened.Add(uri); return true; }, startedAfter);
        Require(loadedOk.Route == SteamLaunchRoute.Kind.RunGameId && opened.Count == 1,
                "once a running Steam has read the library, Play routes again.");

        // the URI refused (no handler, a failed portal, Steam declining): the direct fallback stands
        var refused = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), _ => false, up);
        Require(refused.Route == SteamLaunchRoute.Kind.Direct, "a refused steam:// handoff falls back to the direct launch.");

        // the opener's answer is its exit code, not its spawn (the review's P1): real processes
        Require(SteamLaunchRoute.OpenWith("/bin/sh", ["-c", "exit 0"]) == true, "a handler that took the URI answers true.");
        Require(SteamLaunchRoute.OpenWith("/bin/sh", ["-c", "exit 4"]) == false, "a handler failing with 4 answers false: the fallback runs.");
        Require(SteamLaunchRoute.OpenWith("longshot-no-such-handler", ["x"]) == null, "no opener on PATH answers null, not true.");

        // Windows desktop: never routed (Steam Input's Deck mapping is not the question there)
        var win = SteamLaunchRoute.Decide(Platform.Windows, placed, _ => null, uri => { opened.Add(uri); return true; }, up);
        Require(win.Route == SteamLaunchRoute.Kind.Direct, "Windows Play stays direct.");

        // the handoff, in full (the review's P1s): a routed Play arms its own one-shot naming the
        // build asked for; the launch it caused consumes it, starts THAT folder direct, and never
        // re-arms. Arms live in a directory, one file per Play, claimed by rename. The token must
        // name the home the TILE's launch would arrive at (ArrivalHome): routing that arms anywhere
        // else is a launch no one will answer, and Decide says so — the test asks for the real
        // binding rather than inventing a path, so the check itself is pinned
        var wanted = Path.Combine(root, "builds", "Zelda patch 9");
        var arrival = SteamLaunchRoute.ArrivalHome(placed, EnvFor(xdg))!;
        var token = SteamLaunchRoute.HandoffPath(arrival);
        opened.Clear();
        var first = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted);
        Require(first.Route == SteamLaunchRoute.Kind.RunGameId && Directory.EnumerateFiles(token).Count() == 1,
                "the handoff arms its own file before asking Steam.");
        var armed = SteamLaunchRoute.TakeHandoff(token);
        Require(armed is not null && armed.Folder == wanted && !StandingArms(token).Any(),
                "the armed handoff names the exact build this Play asked for, and is consumed once.");
        Require(SteamLaunchRoute.TakeHandoff(token) is null,
                "the claim receipt is never a handoff to claim again (round-three P1: a .taken file read twice handed two consumers the same launch).");
        // the routed instance (PlayWithoutWindow consumed the handoff): direct whatever the
        // environment says — SteamGameId nowhere present, arm long gone, everything else routable
        var second = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; },
                                             up, token, handoff: armed, folder: wanted);
        Require(second.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 1 && !StandingArms(token).Any(),
                "the routed launch consumes the handoff and starts that build direct — one handoff per Play, no loop, even with SteamGameId lost.");
        // the review's second probe, pinned: a Play while another Play's launch is still outstanding
        // does not route a second URI whose arm could not be matched to it — it goes direct
        var third = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted);
        Require(third.Route == SteamLaunchRoute.Kind.RunGameId, "the first of a pair routes.");
        var whilePending = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted);
        Require(whilePending.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 2 && !StandingArms(token).Any(),
                "while an earlier Play's launch is still outstanding, this Play goes direct rather than arm an unmatchable URI — and retires the older arm so a late delivery cannot start beside it (L1).");
        // (the audit's race, pinned: the retirement is an atomic rename, so if an arrival claims the
        // arm between the Outstanding look and the rename, the retry must answer ROUTED — the
        // claimed game may be starting right now, and a direct start would be the second copy. A
        // directory squatting on the .abandoned name makes the rename lose, which is the same
        // answer a claim winning produces, and works on every test host. No stale-URI danger: the
        // Uri is empty, nothing new is dispatched)
        var locked = Path.Combine(root, "locked-handoff");
        Directory.CreateDirectory(locked);
        var contestedArm = Path.Combine(locked, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, 6));
        File.WriteAllText(contestedArm, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5}");
        Directory.CreateDirectory(contestedArm + ".abandoned");
        opened.Clear();
        var contested = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, locked, folder: wanted);
        Require(contested.Route == SteamLaunchRoute.Kind.RunGameId && contested.Uri.Length == 0 && opened.Count == 0,
                "a retry whose arm cannot be retired (an arrival may have claimed it) answers routed with nothing dispatched: no second copy (L1).");
        // (round-five L1 audit race A, pinned: a claim that lands DURING the decision — between the
        // entry snapshot and the retirement — must change the answer. The env delegate runs inside
        // DecideCore; here it claims the standing arm exactly as an arrival would, and the guard
        // that follows is the Steam-down Direct. The snapshot saw the arm, retirement finds it
        // gone: routed, nothing dispatched)
        Directory.Delete(locked, true);
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        var racedArm = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, 8));
        File.WriteAllText(racedArm, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5}");
        var claimedDuringDecide = false;
        Func<string, string?> racingEnv = k =>
        {
            if (!claimedDuringDecide) { claimedDuringDecide = true; SteamLaunchRoute.TakeHandoff(token); }
            return null;
        };
        opened.Clear();
        var raced = SteamLaunchRoute.Decide(Platform.Linux, placed, racingEnv, uri => { opened.Add(uri); return true; }, down, token, folder: wanted);
        Require(raced.Route == SteamLaunchRoute.Kind.RunGameId && raced.Uri.Length == 0 && opened.Count == 0,
                "a claim landing mid-decision is detected by the entry snapshot: the answer turns routed and nothing is dispatched (L1 race A).");
        // (race B, the identity case: two arms standing at entry, the arrival claims the OLDER one
        // while the decision runs. Retiring the younger one does not fix the stolen older one —
        // the snapshot knows both, sees one gone, and answers routed)
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        var armOld = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 9000, 9));
        var armNew = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 4000, 10));
        File.WriteAllText(armOld, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 9}");
        File.WriteAllText(armNew, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4}");
        var claimedOne = false;
        Func<string, string?> racingEnv2 = k =>
        {
            if (!claimedOne) { claimedOne = true; SteamLaunchRoute.TakeHandoff(token); }   // claims the OLDER arm
            return null;
        };
        opened.Clear();
        var raced2 = SteamLaunchRoute.Decide(Platform.Linux, placed, racingEnv2, uri => { opened.Add(uri); return true; }, down, token, folder: wanted);
        Require(raced2.Route == SteamLaunchRoute.Kind.RunGameId && raced2.Uri.Length == 0,
                "a claim of SOME arm while another still stands is still a possible second game: the snapshot answers routed (L1 race B).");
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        // (the availability regression the audit found in the first fix, pinned shut: an EXPIRED arm
        // that cannot be retired must not block a direct Play — no arrival could ever start from it,
        // because ReadArm refuses aged arms. A directory squatting on its .abandoned name forces the
        // rename to fail; the answer must stay direct)
        var expiredArm = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 400_000, 11));
        File.WriteAllText(expiredArm, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400}");
        Directory.CreateDirectory(expiredArm + ".abandoned");
        opened.Clear();
        var expiredContest = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, down, token, folder: wanted);
        Require(expiredContest.Route == SteamLaunchRoute.Kind.Direct,
                "an expired arm that cannot be retired never blocks a direct Play: no arrival could start from it (audit regression, closed).");
        Directory.Delete(expiredArm + ".abandoned", true);
        File.Delete(expiredArm);
        // (audit #3 regression 1: an arm published by a concurrent sender AFTER the entry snapshot
        // must still be retired by the exit sweep — it used to stay deliverable beside this Direct
        // game. The env delegate runs inside DecideCore, i.e. after the snapshot)
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        var publishedArm = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 16));
        Func<string, string?> publishingEnv = k =>
        {
            if (!File.Exists(publishedArm)) File.WriteAllText(publishedArm, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            return null;
        };
        opened.Clear();
        var published2 = SteamLaunchRoute.Decide(Platform.Linux, placed, publishingEnv, uri => { opened.Add(uri); return true; }, down, token, folder: wanted);
        Require(published2.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && !File.Exists(publishedArm),
                "an arm published after the entry snapshot is retired by the exit sweep: not deliverable beside the direct game (audit #3).");
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        // (audit #3 regression 2: an explicit --play <folder> is the user's own independent request —
        // it never retires another Play's arm, and a blocked abandonment cannot turn it into a no-op)
        var otherArm = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3000, 12));
        File.WriteAllText(otherArm, $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3}");
        Directory.CreateDirectory(otherArm + ".abandoned");
        opened.Clear();
        var explicitPlay = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, down, token, folder: wanted, arrived: true);
        Require(explicitPlay.Route == SteamLaunchRoute.Kind.Direct && File.Exists(otherArm),
                "an explicit --play <folder> neither retires nor is blocked by another request's arm (audit #3).");
        Directory.Delete(otherArm + ".abandoned", true);
        File.Delete(otherArm);
        // (audit #3 L3: RecentCancellation reads the CANCELLATION's time — the .abandoned product's
        // mtime, stamped at the rename — not the arm name's; a future stamp never counts)
        var oldAbandoned = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 200_000, 13) + ".abandoned");
        File.WriteAllText(oldAbandoned, "x");
        File.SetLastWriteTimeUtc(oldAbandoned, DateTime.UtcNow.AddSeconds(-200));
        Require(!SteamLaunchRoute.RecentCancellation(arrival),
                "an OLD-cancelled .abandoned (old mtime) is outside the window (L3 audit).");
        File.SetLastWriteTimeUtc(oldAbandoned, DateTime.UtcNow);
        Require(SteamLaunchRoute.RecentCancellation(arrival),
                "the same file cancelled just now counts: the cancellation time is the rename, not the arm's stamp (L3 audit).");
        File.SetLastWriteTimeUtc(oldAbandoned, DateTime.UtcNow.AddSeconds(60));
        Require(!SteamLaunchRoute.RecentCancellation(arrival),
                "a future-dated .abandoned never counts as a recent cancellation (L3 audit).");
        File.Delete(oldAbandoned);
        // (audit #4 residual: the generator's alphabet is base32 a-z0-5, so a tail with 6-9 is a
        // stranger's file too — the old lowercase-alphanumeric check swept it)
        var notGenerator = Path.Combine(token, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7_200_000:D14}-abcdefgh.678.taken");
        File.WriteAllText(notGenerator, "x");
        SteamLaunchRoute.TakeHandoff(token);
        Require(File.Exists(notGenerator),
                "a claim product whose tail uses generator-impossible digits (6-9) is a stranger's file: never swept (L5 audit #4).");
        File.Delete(notGenerator);
        // (audit #4 R1: the sender's Direct commitment is a marker written before the pre-spawn
        // work, and a future stamp never counts — a late legacy delivery between abandonment and
        // Process.Start must be suppressible WITHOUT a process being visible yet)
        SteamLaunchRoute.RecordDirectStart(arrival, wanted);
        Require(SteamLaunchRoute.DirectStartCommitted(arrival),
                "a just-recorded Direct start is visible to an arrival before any game process exists (audit #4 R1).");
        File.SetLastWriteTimeUtc(Path.Combine(arrival, "play-direct-start"), DateTime.UtcNow.AddSeconds(60));
        Require(!SteamLaunchRoute.DirectStartCommitted(arrival),
                "a future-dated Direct start never counts (audit #4).");
        File.SetLastWriteTimeUtc(Path.Combine(arrival, "play-direct-start"), DateTime.UtcNow.AddSeconds(-31));
        Require(!SteamLaunchRoute.DirectStartCommitted(arrival),
                "a Direct start older than the commit window is not evidence of a pending start (audit #4).");
        File.Delete(Path.Combine(arrival, "play-direct-start"));
        SteamLaunchRoute.RecordDirectStart(arrival, wanted);
        SteamLaunchRoute.ClearDirectStart(arrival);
        Require(!SteamLaunchRoute.DirectStartCommitted(arrival),
                "a cleared Direct start (spawn over or failed) never suppresses a later press (audit #5 P2).");
        // (audit #5 P1: a .taken younger than the ttl may be a claim still in flight — even one
        // whose rename predates this decision — so a Direct answer must turn routed with nothing
        // dispatched. A crashed receiver's residue can suppress until the ttl passes; that is the
        // accepted conservative price, pinned here)
        var youngTaken = Path.Combine(token, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 4000, 17) + ".taken");
        File.WriteAllText(youngTaken, "claimed");
        File.SetLastWriteTimeUtc(youngTaken, DateTime.UtcNow);
        opened.Clear();
        var takenGuard = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, down, token, folder: wanted);
        Require(takenGuard.Route == SteamLaunchRoute.Kind.RunGameId && takenGuard.Uri.Length == 0 && opened.Count == 0,
                "a young .taken (a claim possibly still in flight) answers routed with nothing dispatched (L1 audit #5).");
        File.Delete(youngTaken);
        // (round-four P2, pinned: routing requires the settings to persist first. The arrival
        //  instance reads the settings FILE; a Play whose save just failed must stay direct, where
        //  this window's in-memory telemetry choices are already the player's current ones. The
        //  gate sits BEFORE any arm: a refused persist dispatches nothing and arms nothing)
        foreach (var f in Directory.GetFiles(token)) File.Delete(f);   // (the outstanding arm from the probe above; this pair starts clean)
        opened.Clear();
        var persistRefused = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted, persist: () => false);
        Require(persistRefused.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && !StandingArms(token).Any() && persistRefused.Note.Length > 0,
                "a Play whose settings would not save goes direct, with a note, and never arms or dispatches.");
        var saved = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted, persist: () => true);
        Require(saved.Route == SteamLaunchRoute.Kind.RunGameId && opened.Count == 1,
                "a Play whose settings persist routes exactly as before; the persist gate is a save check, not a tax.");
        // (the arrival leg passes persist: null (App wires arrived ? null : TrySaveSettings): an
        //  arrival must never touch the settings file. Pin that an arrival never calls persist and
        //  stays direct with no note of its own)
        foreach (var f in Directory.GetFiles(token)) File.Delete(f);
        opened.Clear();
        bool persistCalled = false;
        var arr = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token, folder: wanted, arrived: true, persist: () => { persistCalled = true; return false; });
        Require(arr.Route == SteamLaunchRoute.Kind.Direct && !persistCalled && arr.Note.Length == 0 && opened.Count == 0,
                "the arrival instance never persists and never routes; the persist gate belongs to the window's Play only.");
        opened.Clear();
        // (one Play at a time can be in flight through a handoff, by the gate above — so two arms
        // outstanding means two processes armed (a GUI Play and a CLI --play, say). The claim order
        // is still oldest-first, pinned here the way two processes would leave the files)
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var armA = Path.Combine(root, "builds", "A");
        var armB = Path.Combine(root, "builds", "B");
        // (the name shape ArmToken actually writes: 14-digit ms, dash, a GetRandomFileName tail.
        // Round-five L5 made that shape the candidate test, so a fixture naming an arm has to use
        // it — an invented tail is now a stranger's file, not an arm)
        File.WriteAllText(Path.Combine(token, ArmName(now - 5000, 1)), $"{armA}\t{(now - 5000) / 1000}");
        File.WriteAllText(Path.Combine(token, ArmName(now - 4000, 2)), $"{armB}\t{(now - 4000) / 1000}");
        Require(SteamLaunchRoute.TakeHandoff(token)?.Folder == armA && SteamLaunchRoute.TakeHandoff(token)?.Folder == armB
                && SteamLaunchRoute.TakeHandoff(token) is null,
                "two arms in flight claim oldest-first and each launch gets its own build.");
        // the review's loop probe, closed by construction: an instance that ARRIVED for a --play
        // never routes, arm or no arm, handoff or no handoff — the chain ends at the arrival
        opened.Clear();
        var arrivedBare = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; },
                                                  up, token, arrived: true);
        Require(arrivedBare.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0,
                "a --play arrival starts direct even with everything routable and no arm at all.");
        var arrivedArmed = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; },
                                                   up, token, folder: wanted, arrived: true);
        Require(arrivedArmed.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0
                && !StandingArms(token).Any(),
                "an arrival neither routes nor arms.");
        // a handoff Steam never delivered ages out: the next Play routes normally, and a refused
        // handoff clears what it armed rather than stranding it
        Directory.Delete(token, true);
        Directory.CreateDirectory(token);
        File.WriteAllText(Path.Combine(token, ArmName(1, 5)), $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4000}");
        Require(SteamLaunchRoute.TakeHandoff(token) is null && !Directory.EnumerateFiles(token).Any(),
                "a stale handoff is claimed away and answers nothing.");
        // (round-four P2, pinned, superseding the round-three answer: an expired arm is swept and
        // the YOUNGER arm answers. The old stop-at-dead rule sacrificed the next Play's real
        // delivery when the old launch was silently lost (Steam accepted the URI and never
        // launched) — the common failure. The stolen-arm scenario the sweep re-exposes needs an
        // arrival more than a ttl (300 s) late, which measured Deck handoffs (seconds) make rare.
        // The dead arm is swept either way and never blocks Outstanding)
        var t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(token, ArmName(t1 - 400_000, 3)), $"{wanted}\t{(t1 - 400_000) / 1000}");
        File.WriteAllText(Path.Combine(token, ArmName(t1 - 10_000, 4)), $"{armB}\t{(t1 - 10_000) / 1000}");
        Require(SteamLaunchRoute.TakeHandoff(token)?.Folder == armB,
                "an expired arm is swept past: the asking arrival gets the live arm rather than nothing.");
        Require(SteamLaunchRoute.TakeHandoff(token) is null && !StandingArms(token).Any(),
                "the swept dead arm and the claimed live arm both leave no standing arm.");
        var asked = 0;
        var stale = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), _ => { asked++; return false; }, up, token, folder: wanted);
        Require(stale.Route == SteamLaunchRoute.Kind.Direct && asked == 1 && !StandingArms(token).Any(),
                "a refused handoff abandons the one-shot it armed (claim products may stand; they sweep by age).");
        // (round-five L3, the race the review drew: the opener declined, so this Play fell back
        // direct — but Steam may still deliver LATE. The abandonment is an atomic claim, so the
        // late arrival must find a claim product and NOT an arm: it opens the window instead of
        // starting a second copy of the game beside the direct one)
        Require(SteamLaunchRoute.TakeArrival(arrival, Platform.Linux,
                                             k => k == "SteamGameId" ? "9" : null) is null,
                "an arrival after the fallback finds the abandoned product, claims nothing, and opens the window: no late second launch (L3).");
        // a stranger's file in the same directory is none of our business: it does not count as
        // outstanding, and (round-five L5) it is never even claimed away — a file the launcher did
        // not name with ArmToken's own shape is never touched, read, or deleted
        File.WriteAllText(Path.Combine(token, "someone-elses-state"), "not our shape at all");
        var stranger = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token);
        Require(stranger.Route == SteamLaunchRoute.Kind.RunGameId
                && File.Exists(Path.Combine(token, "someone-elses-state")),
                "a stranger's file does not stall routing, and is left exactly where it is (L5).");
        // a stranger's file that carries a plausible fresh tab-stamp is still never read as an arm:
        // the NAME has to be one ArmToken wrote (L5: the old rule claimed and parsed these)
        File.WriteAllText(Path.Combine(token, "user-notes.backup"), $"{wanted}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
        Require(SteamLaunchRoute.TakeHandoff(token) is not null, "the routed Play's own arm claims first (oldest arm name order)");
        var lookalike = SteamLaunchRoute.TakeHandoff(token);
        Require(lookalike is null && File.Exists(Path.Combine(token, "user-notes.backup")),
                "a stranger's file with an arm-plausible stamp is never read as a handoff and never deleted (L5).");
        File.Delete(Path.Combine(token, "someone-elses-state"));
        File.Delete(Path.Combine(token, "user-notes.backup"));
        // a bare Play (no particular row asked for): the handoff carries no folder, and the routed
        // instance's own default pick stands — exactly a bare --play
        Directory.Delete(token, true);
        opened.Clear();
        var bare = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; }, up, token);
        Require(bare.Route == SteamLaunchRoute.Kind.RunGameId, "a bare Play routes too.");
        Require(SteamLaunchRoute.TakeHandoff(token) is { Folder: null }, "a bare Play's handoff carries no folder.");

        // the review's measured double-start: a handler that DISPATCHES and then exits nonzero (real
        // xdg-open shapes do this). The arm consumed by the launched instance is the evidence the
        // launch happened; falling back direct here starts the game twice at one save folder
        var dispatched = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg),
                                                 _ => { SteamLaunchRoute.TakeHandoff(token); return false; },
                                                 up, token, folder: wanted);
        Require(dispatched.Route == SteamLaunchRoute.Kind.RunGameId,
                "a launch that arrived behind a nonzero exit is not answered with a second direct launch.");
        // an arm that cannot be published (a plain FILE sitting where the handoff directory goes —
        // the review's probe shape): routing is refused BEFORE the URI is asked, no second URI on a
        // later Play, and the opener is never consulted
        Directory.Delete(token, true);
        File.WriteAllText(token, "not a directory");
        asked = 0;
        var unwritable = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), _ => { asked++; return true; },
                                                 up, token, folder: wanted);
        Require(unwritable.Route == SteamLaunchRoute.Kind.Direct && asked == 0,
                "no published arm, no URI asked: routing requires the handoff to exist first.");
        File.Delete(token);
        // the review's home-binding P1, pinned: the tile would start ITS Exe in ITS home (Steam's
        // environment, not this clicker's). A token naming any other home cannot be matched by the
        // arriving instance — direct. And the variable-overridden sender: LONGSHOT_LAUNCHER_HOME
        // here does not move where the TILE's launch resolves its home (Steam would not have the
        // variable), so arming the overridden home still cannot be matched — direct
        opened.Clear();
        var stray = SteamLaunchRoute.Decide(Platform.Linux, placed, EnvFor(xdg), uri => { opened.Add(uri); return true; },
                                            up, Path.Combine(root, "other-home", "play-handoff"), folder: wanted);
        Require(stray.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0 && stray.Note.Contains("somewhere else"),
                "a handoff the tile's launch could never read is refused before the URI is asked.");
        var overEnv = SteamLaunchRoute.Decide(Platform.Linux, placed,
                                              k => k == "LONGSHOT_LAUNCHER_HOME" ? Path.Combine(root, "override-home") : EnvFor(xdg)(k),
                                              uri => { opened.Add(uri); return true; }, up,
                                              SteamLaunchRoute.HandoffPath(Path.Combine(root, "override-home")), folder: wanted);
        Require(overEnv.Route == SteamLaunchRoute.Kind.Direct && opened.Count == 0,
                "a sender whose home is overridden still arms where the TILE would arrive: mismatched, direct.");
        // and the arrival home itself: the XDG default beside Steam, not the sender's override
        Require(SteamLaunchRoute.ArrivalHome(placed, EnvFor(xdg)) == Path.GetFullPath(Path.Combine(xdg, "longshot", "launcher")),
                "the tile's launch resolves the plain XDG home; the sender's variable has no bearing.");
        // (round-four P1, pinned: a tile whose Exe is a SYMLINK. The AppImage runtime resolves the
        // link before it sets $APPIMAGE, and the <name>.home it uses is the TARGET's (measured in
        // review against the runtime this repo pins). ArrivalHome must resolve too: an alias tile
        // with real.AppImage.home beside the target is portable on the target's folder, and arming
        // the alias-name's XDG home arms where the arrival would never look. The library holds the
        // ALIAS as the tile's Exe, exactly as a user's stable-name tile would)
        var linkDir = Path.Combine(root, "links");
        Directory.CreateDirectory(linkDir);
        var targetFile = Path.Combine(linkDir, "real.AppImage");
        File.WriteAllText(targetFile, "#!/bin/sh\n");           // (an authored stand-in: the layout asks the disk, not the file)
        Directory.CreateDirectory(targetFile + ".home");
        var aliasFile = Path.Combine(linkDir, "stable.AppImage");
        File.CreateSymbolicLink(aliasFile, targetFile);
        var linkLayout = LauncherLayout.Of(Platform.Linux, aliasFile, _ => null);
        Require(linkLayout.Kind == LauncherKind.Portable && linkLayout.PortableHome == targetFile + ".home",
                "Linux portable detection follows the symlink: the AppImage runtime runs the target and its .home, not the alias's.");
        var linkVdfDir = Path.Combine(root, "data-link", "Steam", "userdata", "33811576", "config");
        Directory.CreateDirectory(linkVdfDir);
        var linkVdf = Path.Combine(linkVdfDir, "shortcuts.vdf");
        File.Copy(Path.Combine(fixtures, "steam", "shortcuts.vdf"), linkVdf);
        var linkP = new SteamShortcuts.Placement(SteamShortcuts.GameEntry(aliasFile), linkVdf, null);
        Require(SteamShortcuts.Add(linkP), "the symlinked tile lands in the library");
        Require(SteamShortcuts.TileLauncherPath(linkP) == Path.GetFullPath(aliasFile),
                "the library holds the alias path as the tile's Exe (what the marker and entry carry)");
        Require(SteamLaunchRoute.ArrivalHome(linkP, EnvFor(xdg)) == Path.GetFullPath(targetFile + ".home"),
                "the arrival home of a symlinked tile is the target's .home — the only place its arrival will read the arm.");

        // The arrival (Deck, 2026-11-21). #130 writes the tile with NO launch options, so the launch
        // Steam starts for a routed Play carries no arguments at all. That bare launch has to claim
        // the arm and play the build the Play asked for, without a window; taking the window's lock
        // first is what made it answer "the launcher is already open" and leave the arm to expire
        // while Steam said "Starting game" and nothing started. TakeArrival is that claim, and it is
        // the only question Main asks before the lock, so it is checked here directly.
        var arriveHome = Path.Combine(root, "arrival-home");
        var arriveDir = SteamLaunchRoute.HandoffPath(arriveHome);
        Directory.CreateDirectory(arriveDir);
        string Built(string name)
        {
            var folder = Path.Combine(root, "arrival-builds", name);
            Directory.CreateDirectory(Path.Combine(folder, "bin"));
            File.WriteAllText(Path.Combine(folder, "bin", Platforms.GameExe(Platform.Linux)), "");
            return folder;
        }
        var builtA = Built("A");
        var builtB = Built("B");
        long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        void Arm(string text) =>
            File.WriteAllText(Path.Combine(arriveDir, ArmName(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ++armSeq)), text);

        // (round-five L4: the claim wants STEAM's mark. Measured on a Deck LCD 2026-11-22 with a
        // probe at the tile's Exe: Steam hands SteamGameId (64-bit tile id) and SteamAppId to
        // every process it launches; a desktop icon, menu entry or terminal run carries neither)
        System.Func<string, string?> SteamEnv = k => k switch
        {
            "SteamGameId" => "9456229749039824896",
            "SteamAppId" => "2201700059",
            _ => null,
        };
        System.Func<string, string?> DesktopEnv = _ => null;

        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv) is null,
                "no arm: a bare launch is the tile pressed from the couch, nothing is claimed, and the window (with its release check) opens.");

        Arm($"{builtA}\t{Now()}");
        // L4, the exact scenario the review drew: a desktop-icon click while a routed Play's arm is
        // pending must NOT take it — the icon's user wants a window, and the routed arrival still
        // needs its arm
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, DesktopEnv) is null && StandingArms(arriveDir).Any(),
                "a bare launch Steam did not start (desktop icon, menu entry, terminal) never claims an arm: it opens the window and leaves the arm for its routed arrival (L4).");
        var claimA = SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv);
        Require(claimA is not null && claimA.Folder == builtA,
                "the armed Play's Steam-started bare arrival claims its arm and names exactly the build that Play asked for — the routed launch plays it headlessly.");
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv) is null && !StandingArms(arriveDir).Any(),
                "the arrival's claim is once: a second bare launch finds no arm and is an ordinary window launch.");

        Arm($"{builtB}\t{Now()}");
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Windows, SteamEnv) is null && StandingArms(arriveDir).Any(),
                "Windows never claims a Linux handoff, and leaves the arm standing for the launch it belongs to.");
        var leftBehind = SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv);
        Require(leftBehind is not null && leftBehind.Folder == builtB,
                "the arm Windows left standing is exactly the one the next Linux launch claims.");

        Arm($"builds/relative\t{Now()}");
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv) is null,
                "a stranger's file naming a relative folder is not an arm: the folder field has to be a path this launcher could have written.");

        Arm($"\t{Now()}");
        var bareArm = SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv);
        Require(bareArm is not null && bareArm.Folder is null,
                "an arm that carried no folder claims, and the caller's default build stands (a routed Play of the default row).");

        Arm($"{Path.Combine(root, "arrival-builds", "gone")}\t{Now()}");
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv) is null && !StandingArms(arriveDir).Any(),
                "an arm naming a build that is no longer there is consumed and answers nothing: the window opens rather than the default build starting unasked.");

        Arm($"{builtA}\t{Now() - 100_000}");
        Require(SteamLaunchRoute.TakeArrival(arriveHome, Platform.Linux, SteamEnv) is null,
                "an aged-out arm is not an arrival — a launch that late is treated as a couch launch, not as this Play's continuation.");
    }
}
