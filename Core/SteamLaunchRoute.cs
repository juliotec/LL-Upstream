namespace LongshotLauncher.Core;

/// Where Play sends the game: through Steam's tile, or direct. (Research 2026-11-16; the
/// Steam-side rule it rests on, from the longshot source's own host/steam_pads.h: the game HIDES
/// Steam's virtual gamepads and re-adds the physical one, and only recognises a real steam_input
/// device when Steam launched it. A fresh Deck maps the buttons through Steam Input only when the
/// game was started through Steam; direct-launched under a running Steam, Steam's default desktop
/// layout eats the Deck's controller as mouse and keyboard. So: when Steam is up, Play goes through
/// Steam's own launch (rungameid), which brings Steam Input up for the tile; direct stays for
/// Steam-not-running, for inside Game Mode (a gamescope session: routing there would start the
/// game in a session the desktop-mode clicker cannot see), and for the launch a handoff caused.)
///
/// The URI wants the 64-bit form of the tile's CARRIED appid — steam://rungameid/<(unsigned appid
/// << 32) | 0x02000000> — the same lengthenAppId rule this class's own GridId64 sibling already
/// uses for grid art names (checked 2026-11-16 against steam-for-linux#8580 and
/// SteamTinkerLaunch#1117, both working on the Deck). Carried, not freshly computed: after a
/// launcher update InLibrary finds the tile by its marker under the OLD id, and a URI naming the
/// id the new path computes points at a tile Steam does not have. TileAppId is the getter that
/// already draws exactly that distinction.
///
/// The handoff is one-shot and self-describing: the arming writes the requested build folder
/// beside its time. The tile itself runs bare --play, so a handoff that identified only the tile
/// would launch the default build when Play had asked for another row (the review's P1): the
/// routed instance reads the folder from the handoff instead. The consumed handoff doubles as the
/// no-loop guard, not trusting SteamGameId — nowhere a delivery guarantee (the review verified the
/// pinned type-2 AppImage runtime's execv preserves it, but a wrapper may clear it): a launch that
/// finds its own fresh handoff is the routed launch, whatever the environment says.
public static class SteamLaunchRoute
{
    public enum Kind { Direct, RunGameId }
    public readonly record struct Decision(Kind Route, string Uri, string Note)
    {
        public static Decision Direct(string note = "") => new(Kind.Direct, "", note);
    }

    /// What an armed handoff says: the folder that Play asked for (null: it named none), and when
    /// it was armed. TakeHandoff reads and consumes it; there is one handoff per Play
    public sealed record Handoff(string? Folder, long ArmedUnix);

    /// Asked of Steam, not reported as done: an opener exit of zero proves the URI was dispatched,
    /// and Steam may still decline or fail to start anything (measured on the Deck, 2026-11-21)
    public static string ViaTileNote => Tr.T("Steam is starting this build through its tile, so its controller mapping applies.");
    static string NotLoadedNote => Tr.T("Steam has not loaded the new Longshot tile yet, so this game was started directly; restart Steam and later Plays go through it.");
    static string MismatchNote => Tr.T("The Longshot tile in Steam points at a different launcher file than this one, so this game started directly: the tile's launcher is the one Steam would start, not this one.");

    // The handoff lives here inside a launcher home; HandoffPath keeps callers and Decide naming the
    // same thing (Decide proves the token names the home the TILE's launch will arrive at — see ArrivalHome)
    public const string HandoffDirName = "play-handoff";
    public static string HandoffPath(string launcherHomeRoot) => Path.Combine(launcherHomeRoot, HandoffDirName);

    /// The launcher home an instance started FROM the tile resolves: the launcher file the library
    /// holds under the tile's id (a tile launches that file, whatever path this launcher computes
    /// for itself now), portable as the disk says — an AppImage tile with a <file>.home folder keeps
    /// its data beside itself — else the XDG default. Resolved WITHOUT the LONGSHOT_LAUNCHER_HOME
    /// variable on purpose: the arriving instance's environment is Steam's, and this clicker's
    /// variable is no proof Steam has it (the review's P1: a sender with the variable set armed a
    /// home nothing would read, and the tile's launch started another home's default). null when
    /// the tile's file cannot be read
    public static string? ArrivalHome(SteamShortcuts.Placement tile, Func<string, string?> env)
    {
        var exe = SteamShortcuts.TileLauncherPath(tile);
        if (exe is not { Length: > 0 }) return null;
        var layout = LauncherLayout.Of(Platform.Linux, exe, env);
        return LauncherHome.Resolve(Platform.Linux, k => k == LauncherHome.Variable ? null : env(k), layout).Root;
    }

    /// Decide where this Play goes. env names the current environment. tile: this launcher's
    /// Placement, null when there is no Steam install to place a tile in. token: the handoff
    /// directory — one arm file per Play, claimed by the launch each arm was armed for (see Handoff,
    /// TakeHandoff). handoff: this launch's already-consumed handoff, when the caller took one —
    /// it means Steam started us at this launcher's request, and the launch must go direct.
    /// folder: the build this Play asked for, armed into the handoff so the routed instance starts
    /// THAT and not the default (null: the caller asked for no particular row, and the routed
    /// instance's own default pick stands — exactly a bare --play)
    /// Where a tile launch would land: the launcher file the tile starts, and the launcher home an
    /// instance started FROM that tile would resolve (portable: beside the file; else the XDG
    /// default — the sender's own LONGSHOT_LAUNCHER_HOME is not in Steam's environment, so it is
    /// deliberately not consulted here). Routing hands the launch to that instance, so the handoff
    /// must be armed where it will look (the review's P1: a sender with the variable set armed
    /// where nothing would ever read it, and the tile's launch came up with another registry's
    /// default). null from Decide means "this cannot be answered" — no routing
    public sealed record Binding(string LauncherFile, string ArrivalHome);

    // (Steam's own quoting for an Exe holding spaces; the entry value may carry it, and the
    // comparison below wants the bare path — same shape TileLauncherPath unquotes)
    static string Unquote(string exe) =>
        exe.Length >= 2 && exe[0] == '"' && exe[^1] == '"' ? exe[1..^1] : exe;

    public static Decision Decide(Platform p, SteamShortcuts.Placement? tile, Func<string, string?> env,
                                  Func<string, bool?>? openUri = null, string proc = "/proc",
                                  string? token = null, Handoff? handoff = null, string? folder = null,
                                  bool arrived = false, Binding? binding = null, Func<bool>? persist = null)
    {
        // (round-five L1, after two audits: a Direct answer may only reach the caller when nothing
        // an arrival could still consume remains. The evidence is taken at entry AND checked at
        // exit: since a directory cannot be read atomically, a claim landing DURING the enumeration
        // is caught by its receipt — a .played/.taken product with an mtime after this instant
        // proves an arrival claimed while this launcher was deciding (audit #3's readdir race) —
        // and arms published AFTER the snapshot are retired by a second sweep at exit (audit #3's
        // concurrent-sender regression). arrived/handoff: an arrival is not a sender and must never
        // cancel another request — the legacy late-delivery case is handled by evidence in
        // Program's guard, not by retiring an arm this launch does not own)
        Decision decision;
        Snap? snap = null;
        if (p == Platform.Linux && token != null && handoff == null && !arrived) snap = SnapshotArms(token);
        decision = DecideCore(p, tile, env, openUri, proc, token, handoff, folder, arrived, binding, persist);
        if (decision.Route != Kind.Direct || snap == null) return decision;
        return RetireThenDirect(token!, snap, decision);
    }

    // (one arm standing when the decision began, whether it was still young then, and the instant
    // the snapshot was taken — receipts newer than that prove a claim landed during the decision)
    sealed record StandingArm(string Path, bool Young);
    sealed record Snap(DateTime At, List<StandingArm> Arms);

    static Snap? SnapshotArms(string handoffDir)
    {
        var at = DateTime.UtcNow;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var arms = new List<StandingArm>();
            foreach (var arm in Directory.EnumerateFiles(handoffDir))
            {
                var name = Path.GetFileName(arm);
                if (!IsArmName(name) || !long.TryParse(name[..14], out var ms)) continue;
                arms.Add(new StandingArm(arm, now - ms / 1000 <= TokenTtlSeconds));
            }
            return new Snap(at, arms);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static Decision RetireThenDirect(string handoffDir, Snap snap, Decision direct)
    {
        var suspect = false;
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arm in snap.Arms)
        {
            known.Add(arm.Path);
            if (!arm.Young)
            {
                // (expired: undeliverable, so tidying it can never decide this answer either way)
                TryAbandon(arm.Path);
                continue;
            }
            if (!File.Exists(arm.Path)) { suspect = true; continue; }   // (claimed while deciding)
            if (!TryAbandon(arm.Path)) suspect = true;                  // (claim won the rename)
        }
        // (the exit sweep: an arm published after the snapshot is retired too — its own arrival
        // would otherwise start a second game beside this one; and a claim landing DURING the
        // entry enumeration is caught by its evidence even when the enumeration missed the arm:
        // a .played receipt carries the claim time (mtime > snapshot instant), and a .taken file
        // that is YOUNG carries the arm's own age through the rename — either proves an arrival
        // claimed while this launcher was deciding. An old .taken residue (a claim that died) is
        // not evidence and is left to the hourly sweep)
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var f in Directory.EnumerateFiles(handoffDir))
            {
                var name = Path.GetFileName(f);
                if (IsArmName(name))
                {
                    if (known.Contains(f)) continue;
                    if (!long.TryParse(name[..14], out var ms)) continue;
                    var young = now - ms / 1000 <= TokenTtlSeconds;
                    if (young && !TryAbandon(f)) suspect = true;
                    else if (!young) TryAbandon(f);
                    continue;
                }
                if (!IsClaimProduct(name)) continue;
                if (name.EndsWith(".played", StringComparison.Ordinal))
                {
                    try { if (File.GetLastWriteTimeUtc(f) > snap.At) suspect = true; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                else if (name.EndsWith(".taken", StringComparison.Ordinal))
                {
                    // (audit #5: a live claim must count even when its rename happened before the
                    // snapshot, or in the instant before its mtime is stamped. Any .taken younger
                    // than the arm ttl is treated as a claim possibly in flight — the conservative
                    // direction: a crashed receiver's residue can suppress a direct Play until the
                    // ttl passes, which is the accepted price of never starting a second copy
                    // beside a claim that is still running. Older residue is left to the sweep)
                    try
                    {
                        var age = now - new DateTimeOffset(File.GetLastWriteTimeUtc(f)).ToUnixTimeSeconds();
                        if (age is >= 0 and <= TokenTtlSeconds) suspect = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // (cannot re-check: the entry snapshot's arms were handled above, which is the part
            // this decision can still control; the answer stands on that evidence)
        }
        return suspect
            ? new Decision(Kind.RunGameId, "", Tr.T("A game launched through Steam's tile from an earlier Play is already arriving; this Play did not start a second copy."))
            : direct;
    }

    static Decision DecideCore(Platform p, SteamShortcuts.Placement? tile, Func<string, string?> env,
                               Func<string, bool?>? openUri, string proc,
                               string? token, Handoff? handoff, string? folder,
                               bool arrived, Binding? binding, Func<bool>? persist)
    {
        // Windows desktop has no Steam Input layer in the way; Linux is the Deck's case
        if (p != Platform.Linux) return Decision.Direct();
        // this instance ARRIVED for a --play (the tile's launch option, or a command line): its job
        // is to start the game, never to hand off again — handoff or no handoff, arm or no arm.
        // Routing only makes sense for the Play button in a window that is already open (the
        // review's loop probe: a bare receiver that found no arm, or a foreign --play that took
        // one, routed on and asked for a THIRD launch; with this, a handoff can only ever come
        // from a window, and every arrival ends the chain where it stands)
        if (arrived) return Decision.Direct();
        // inside Steam's own session (Game Mode): never hand off from here — desktop and gamescope
        // are different seats, and routing would start the game where the clicker cannot see it.
        // XDG_CURRENT_DESKTOP is a colon-separated list ("gamescope", "gamescope:gamescope", on
        // some builds joined with others), so match the name among the parts (the review's P2)
        if ((env("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => s.Equals("gamescope", StringComparison.OrdinalIgnoreCase))) return Decision.Direct();
        // our own handoff was consumed by this launch: it IS the routed launch — direct, no loop,
        // whatever the environment does or loses
        if (handoff != null) return Decision.Direct();
        // the environment saying SteamGameId: the fast path for the same fact
        if (!string.IsNullOrEmpty(env("SteamGameId"))) return Decision.Direct();
        // no Steam running: direct, which is also what makes Steam's own runtime absent
        if (!SteamShortcuts.SteamUp(proc)) return Decision.Direct();
        // Steam is up but this launcher's tile is not in its library: the button keeps working
        // direct; the note says where the controller behaviour comes from
        if (tile is null || !SteamShortcuts.InLibrary(tile))
            return Decision.Direct(Tr.T("Steam is running but has no Longshot tile yet: the game was started directly, so the controller keeps Steam's desktop mapping. Add the tile from the launcher's ready screen, then Play goes through Steam."));
        // (round-five L2: the library's Exe under the tile's id is the file Steam WILL start. If it
        // is not this launcher's own file, the arm about to be armed would be read by some other
        // launcher's arrival — an older copy still in the tile, or a file since deleted — and it
        // would play ITS default build while this Play's build never starts. The home check below
        // only catches a moved HOME; this catches a moved or stale FILE directly: an update's Ready
        // screen repoints the Exe (and the marker's changed-at stamp below then requires a Steam
        // restart before routing comes back). Compared on full paths: the entry's Exe is the same
        // file GameEntry made for this launcher path when the tile is current
        var heldExe = SteamShortcuts.TileLauncherPath(tile);
        if (heldExe == null || !string.Equals(Path.GetFullPath(heldExe), Path.GetFullPath(Unquote(tile.Entry.Exe)), StringComparison.Ordinal))
            return Decision.Direct(MismatchNote);
        // Steam reads its library when it starts (measured on the Deck 2026-11-15): a tile written
        // while Steam runs sits on disk but is not in the running Steam's in-memory library, and a
        // rungameid for a tile Steam has never loaded fails. Check rather than guess: route only
        // when some running Steam started no earlier than the library file's last write. The
        // direction this can get wrong on purpose: Steam's own rewrite of the file bumps its mtime
        // past Steam's own start, and the answer comes back not-loaded — conservatively false, and
        // a direct launch still works. Being wrongly optimistic here would address a tile the
        // running Steam has never loaded, which is the failure this guard exists to avoid
        if (!SteamShortcuts.LibraryLoaded(tile.Vdf, proc)) return Decision.Direct(NotLoadedNote);
        // Where the tile's launch would land: it starts the launcher file the library holds under
        // its id, in an environment that is Steam's, not this clicker's — LONGSHOT_LAUNCHER_HOME is
        // not inherited across Steam, and a portable AppImage tile keeps its data in the <file>.home
        // folder beside ITSELF. The handoff must therefore be armed in the home the arriving
        // instance will resolve, or the routed launch starts some other home's default registry
        // while this sender's arm sits unread (the review's P1, measured with different
        // sender/receiver homes: requested build A, launched another home's default C). A token
        // that does not name that home's handoff cannot be matched, so: direct
        if (token != null)
        {
            var arrival = ArrivalHome(tile, env);
            if (arrival == null ||
                !string.Equals(Path.GetFullPath(token), Path.Combine(arrival, HandoffDirName), StringComparison.Ordinal))
                return Decision.Direct(Tr.T("The launcher this tile would start keeps its games somewhere else, so this one started directly: the routed start would have gone to the wrong place."));
        }
        // the id the tile in the library CARRIES, not the one this launcher's path computes: after
        // an update the library tile keeps its id (the marker finds it) and a URI naming a new id
        // addresses a tile Steam has never heard of
        var uri = $"steam://rungameid/{SteamShortcuts.GridId64(SteamShortcuts.TileAppId(tile))}";
        openUri ??= DefaultOpenUri;
        // One outstanding handoff at a time (the review's probes: two Plays in flight through one
        // shared slot crossed each other's folders, and a receiver that found nothing armed a third
        // URI). An arm younger than its ttl means an earlier Play's launch has not arrived yet; a
        // second routed URI then cannot be matched to its own arm, so this Play goes direct — a
        // controller-mapped second launch beats a wrong build or a doubled one.
        // (round-five L1, the retry half: this Play is going direct, so the older arm can now only
        // do harm — if Steam delivers it late (signed out at the first Play, signed back in later;
        // a delivery Steam queued and then found), the arrival would start a SECOND copy beside the
        // one this Play is starting direct. Cancelling it is the same atomic rename the arrival
        // honours: a claimed (or already arrived) arm cannot be abandoned and stays routed, but a
        // still-standing arm is retired, and any later arrival finds a claim product and opens the
        // window instead. So "retry after a stuck delivery" is both possible and safe)
        if (token != null && Outstanding(token))
        {
            // (the wrapper retirement decides this answer: the arm was in the entry snapshot, so a
            // claim that lands from here on is detected there — retired → direct alone; claimed →
            // routed, nothing dispatched)
            return Decision.Direct(Tr.T("A game launched through Steam's tile is still on its way from an earlier Play; this one started directly."));
        }
        // (round-four P2, reproduced with the real assemblies: the Settings setters change this
        //  window's VALUES even when the save fails, a direct Play honors them (the tick reads this
        //  window's Settings), but a ROUTED Play runs the tick in the ARRIVAL instance, which reads
        //  the settings FILE — so with a failed save the routed arrival stamps consent this window
        //  has just turned off. Require persistence immediately before dispatch: on failure this
        //  Play stays direct, where the in-memory values are already the player's current choices.
        //  The perf/crash ticks deliberately stay AFTER Decide: moving them above would let a
        //  routed Play tick twice (this instance AND the arrival — two sends per Play))
        if (persist != null && !persist())
            return Decision.Direct(Tr.T("Your settings could not be saved just now, so this game started directly: a launch through Steam's tile would use the saved settings instead of the ones you just changed."));
        // A PUBLISHED arm is a prerequisite for routing, not a nice-to-have (the review's P1: an
        // arming failure was ignored and the URI went out anyway — the selected folder was lost and
        // the delivery evidence was gone before the question was asked). No handoff directory, or
        // an arm that cannot be written (read-only home, a directory in its place): direct.
        // arm the one-shot before asking Steam: the launched instance consumes it (folder included,
        // so the routed launch starts the build this Play asked for). A handoff Steam never
        // delivered ages out and is cleared on the next look
        string? armedAt = null;
        if (token != null) armedAt = ArmToken(token, folder);
        if (token != null && armedAt == null)
            return Decision.Direct(Tr.T("This launcher cannot write its handoff folder, so the game started directly: a routed start it cannot track is one it cannot keep from happening twice."));
        bool? answered = openUri(uri);
        if (answered == null)
        {
            // no opener on PATH at all: dispatch PROVED failed. Abandon the arm (nothing will ever
            // read it) and start direct
            TryAbandon(armedAt);
            return Decision.Direct(Tr.T("Nothing on this system answers a steam:// link, so the game started directly."));
        }
        // (round-three P1: an opener that says no is NOT proof of no launch once an arm is out.
        // The review ran a real app: an authored xdg-open dispatched the launcher, exited 4 three
        // seconds early, and the old fallback here started a second copy — A and DEFAULT both ran
        // on one save folder. With an arm out: only a WON abandonment may start a second copy;
        // abandoning is the SAME atomic rename a claim is, so an arrival that got there first
        // wins the arm and this Play does not double-launch, and an arrival that comes after the
        // rename finds a claim product, never an arm, and opens the window the way any Steam
        // start with no pending Play does (round-five L3: the old check-then-delete raced exactly
        // that way — the arm could be claimed between the standing check and the delete). A
        // claimed arm is ambiguous (its consumer either launched the game or died claiming it),
        // and the wrong guess on ambiguous is two writers at one saves folder, so that guess
        // stays routed. A user who sees a routed toast and no game re-clicks Play and goes direct
        // a moment past the ttl; two copies at the saves is not recoverable that way. With NO arm
        // out there is nothing consuming and nothing to lose: the stated refusal stands)
        if (!answered.Value && (armedAt == null || TryAbandon(armedAt)))
            return Decision.Direct(Tr.T("Steam refused the tile launch (the refusal said no outright), so the game started directly."));
        return new Decision(Kind.RunGameId, uri, ViaTileNote);
    }

    // (the arm PROVES it was never consumed only in one shape: still sitting unclaimed under its
    // own arm name. A claimed arm is gone from that name (a .played receipt or a .taken residue
    // stands in its place), and a vanished arm with no receipt was claimed by a TakeHandoff that
    // then died mid-read — consumed either way, and no consumer that starts the game could leave
    // that state)

    // A WON abandonment: rename the arm to a claim product, exactly as a claim does, so the race
    // has exactly one winner (round-five L3). Decide calls this when the dispatch failed and it
    // wants to fall back direct: if an arrival already claimed the arm, this rename loses and the
    // routed launch stands (the game may still come up — no second copy). If the rename wins, no
    // later arrival can claim the arm, so this Play's direct launch is the only game. The
    // ".abandoned" suffix is a claim product: never a candidate again, and swept by age like the
    // others
    static bool TryAbandon(string? arm)
    {
        if (arm == null) return true;
        try
        {
            var abandoned = arm + ".abandoned";
            File.Move(arm, abandoned);
            // (round-five L3 audit: the receipt's mtime is when the cancellation HAPPENED, not when
            // the arm was written — a 200 s-old arm cancelled just now must protect its delivery,
            // and a rename keeps the original mtime, so it is stamped here)
            try { File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return false; }
    }

    // (round-five L1's retry path: retiring EVERY standing arm before a direct Play, so none can be
    // delivered late beside it — superseded by RetireAll below, which answers whether it won)

    // (an earlier Play's arm, still waiting for its launch to arrive: young enough to count, and
    // ours to judge — Ours recognises our own arm shape, so a stranger's file in the same
    // directory is never read as one (it would otherwise stall routing for a ttl). Those arms age
    // out on their own, so a handoff Steam never delivered stops blocking routing after the ttl)
    static bool Outstanding(string handoffDir)
    {
        try
        {
            foreach (var arm in Directory.EnumerateFiles(handoffDir))
            {
                // (round-five L5 + L3, kept consistent with TakeHandoff's candidate test: a name
                // that is not one ArmToken wrote is none of our business (never read, never
                // counted); and a CLAIM PRODUCT never counts as "a launch still on its way" — a
                // .taken/.played is a launch that already arrived, and a .abandoned is one the
                // sender CANCELLED, whose content still parses fresh for a ttl but stands for
                // nothing. Reading contents and not names would have a cancelled Play block
                // routing for five minutes and a stranger's arm-shaped note do the same)
                var name = Path.GetFileName(arm);
                if (!IsArmName(name)) continue;
                string text;
                try { text = File.ReadAllText(arm); }
                catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { continue; }
                if (!Ours(text)) continue;
                var at = text.LastIndexOf('\t');
                if (at < 0 || !long.TryParse(text[(at + 1)..].Trim(), out var armed)) continue;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - armed is >= 0 and <= TokenTtlSeconds) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    // (shaped like one of our arms: folder field (a path we would write, or the no-folder tag),
    // tab, unix stamp. A stranger's file in the same directory fails this and is never counted)
    const string NoFolder = "\u0000";    // (not legal in a path, so it can only come from ArmToken)
    static bool Ours(string text)
    {
        var at = text.LastIndexOf('\t');
        if (at < 0 || !long.TryParse(text[(at + 1)..].Trim(), out _)) return false;
        var folder = text[..at];
        return folder.Length == 0 || folder == NoFolder || folder.StartsWith('/') || folder.Contains(":\\");
    }

    // (same shape test, without reading the stamp's freshness: used to tell our OWN aged-out arm —
    // whose disappearance is TakeHandoff's ordering answer — from a stranger's file, which is just
    // claimed away and skipped)
    static bool LooksLikeArm(string text) => Ours(text);

    /// Read and consume the oldest outstanding handoff for this launcher home. Each Play arms its
    /// own file (ArmToken), and a handoff is claimed by RENAMING it — whoever wins the rename owns
    /// it, so two launches arriving at once can never both consume one arm (the review's probe: a
    /// shared-slot read-then-delete let two consumers take the same token). Null when none is
    /// outstanding or all are past their ttl (stale ones are cleared, so an abandoned handoff never
    /// strands a later Play). Folder is null when the arm carried none (a bare Play from the tile:
    /// the caller's own pick stands, default included)
    public static Handoff? TakeHandoff(string handoffDir)
    {
        List<string> arms;
        try
        {
            if (!Directory.Exists(handoffDir))
            {
                // (the review's tail): a plain FILE where the handoff directory goes is the old
                // single-slot shape from an early experiment; claim it the same atomic way, answer
                // it if still fresh, and let the next ArmToken make the directory. Leaving it would
                // wedge routing for every later Play (CreateDirectory fails forever)
                if (!File.Exists(handoffDir)) return null;
                var legacy = handoffDir + ".taken";
                string legacyText;
                try { File.Move(handoffDir, legacy); legacyText = File.ReadAllText(legacy); File.Delete(legacy); }
                catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { return null; }
                return ReadArm(legacyText);
            }
            // (arm names start with their arm time in ms, so the sort is oldest-first: Steam
            // services the URIs in the order they were asked, and each arriving launch takes the
            // arm its own Play left. Two Plays, two arms, two launches, matched by order — a shared
            // slot instead made the second Play overwrite the first's folder, and the receiver that
            // found nothing armed a THIRD handoff)
            // (round-three P1: claimed AWAY means claimed by rename, once — the review renames
            // .taken to .taken.taken and read the same handoff twice. A candidate is never a
            // claimed file: the claim suffixes mark claim products, and nothing with any of them
            // is ever a candidate again.
            // (round-five L5, tightening the earlier answer: a candidate must also carry an ARM
            // NAME — the exact shape ArmToken writes. The old rule claimed strangers' files away
            // (a deletion the stranger never authorized; a file holding a plausible tab-stamp could
            // even be read as an arm). Now: anything not named by ArmToken is never touched, never
            // read, and none of its business. A stranger could still leave a file under an arm's
            // exact name — ArmToken's random tail is not a secret — but its CONTENT must still pass
            // ReadArm's shape and ttl, and TakeArrival's folder check; the handoff directory sits
            // inside this launcher's own home, where an attacker able to write there can rewrite
            // the registry directly anyway (round-three P1's "leave a marker you control" answer,
            // unchanged — with L5 the incidental case is gone))
            arms = [.. Directory.EnumerateFiles(handoffDir)
                           .Where(f => IsArmName(Path.GetFileName(f)))
                           .OrderBy(f => Path.GetFileName(f))];
            SweepClaimed(handoffDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var arm in arms)
        {
            var claimed = arm + ".taken";
            string text;
            try
            {
                File.Move(arm, claimed);                       // (the claim: atomic, exactly one winner)
                // (audit #4: the .taken mtime is stamped at the RENAME, so a sender can tell an
                // in-flight claim from a crashed receiver's old residue — a rename otherwise keeps
                // the arm's original mtime, and the exit sweep could not distinguish them)
                try { File.SetLastWriteTimeUtc(claimed, DateTime.UtcNow); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                text = File.ReadAllText(claimed);
            }
            catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException) { continue; }
            var h = ReadArm(text);
            if (h == null)
            {
                // (not our shape: claim it away and try the next. OUR shape but aged out: sweep it
                // and KEEP GOING to the younger arm (round-four P2, reversing the earlier answer:
                // answering nothing here sacrificed the NEXT Play's delivery when the old launch
                // was silently lost — Steam accepted the URI and never launched, far more common
                // than an arrival later than the ttl, which the 300 s window now makes practically
                // impossible on measured Deck timings (seconds). The stolen-arm scenario the
                // round-three review feared needs a >5-minute launch delay; the lost-delivery
                // scenario this choice fixes needs only a silent Steam failure. Wrong-build via a
                // 5-minute-late arrival is the rarer coin, so the younger arm stands and gets its
                // own launch; the dead arm is gone either way and never blocks Outstanding again)
                try { File.Delete(claimed); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                continue;
            }
            // (the receipt of the claim: the arm file is already gone, and a routed Play waiting to
            // see whether its URI actually launched used to poll for exactly that absence. The
            // review caught what that cannot prove — a stale receiver can claim THIS arm behind a
            // still-running opener, and absence then lies about delivery. TakeHandoff leaves this
            // signed receipt instead; a routed Play only trusts the one carrying its own arm time,
            // and old receipts are swept by age by SweepClaimed)
            // (audit #4: the receipt is written BEFORE the .taken residue is removed. The old order
            // deleted .taken first and wrote the receipt second, leaving an interval in which a
            // concurrent sender's directory scan could see NEITHER — the probe paused a receiver
            // exactly there and the sender answered Direct beside the launching game. Now a
            // claim is observable at every instant: .taken exists from the rename until the
            // receipt is on disk; if the receipt write fails, .taken remains as the evidence)
            var receiptWritten = false;
            try
            {
                var receipt = Path.Combine(handoffDir, Path.GetFileName(arm) + ".played");
                File.WriteAllText(receipt, "consumed\t" + h.ArmedUnix);
                receiptWritten = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // (only a claim with its receipt on disk may drop the .taken residue; if the write
            // failed, .taken STAYS as the evidence that a claim happened — audit #5)
            if (receiptWritten)
            {
                try { File.Delete(claimed); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return h;
        }
        return null;
    }

    /// Whether this process was started BY STEAM launching the tile. Measured on a Deck LCD
    /// 2026-11-22 with a probe at the tile's own Exe: Steam hands every process it starts for a
    /// library entry SteamGameId (the 64-bit tile id), SteamAppId (the low 32 bits), SteamClient-
    /// Launch, the overlay LD_PRELOAD and a reaper/pressure-vessel ancestry. A desktop icon, an
    /// applications-menu entry or a terminal run carries none of it (the launcher's own desktop
    /// session has neither SteamGameId nor SteamDeck). Round-five L4: without this, clicking the
    /// desktop icon while a routed Play was pending claimed that Play's arm — the icon's launch
    /// played the game headless where its user expected a window, and the routed arrival then
    /// found nothing. The variable is nowhere a delivery guarantee (see the class note), but
    /// ORIGIN identity is all a claim needs, and a lost variable answers "ordinary window launch",
    /// the safe direction: no arm is ever stolen from anyone
    public static bool FromSteamLaunch(Func<string, string?> env) =>
        !string.IsNullOrEmpty(env("SteamGameId")) || !string.IsNullOrEmpty(env("SteamAppId"));

    /// A launch with no arguments, on Linux, may be Steam bringing back a Play this launcher already
    /// routed to its own tile: the arm is the whole signal, and it is claimed here EXACTLY as a
    /// --play launch claims it, and before the window's instance lock. That order is the fix for
    /// what the Deck showed on 2026-11-21: the sender's window still holds the lock when the routed
    /// launch arrives, so a launch that took the lock first reported "the launcher is already open"
    /// and exited, leaving the arm to expire while the player saw Steam say "Starting game" and
    /// nothing start. Null means "not an arrival" — not started by Steam (round-five L4), no arm, a
    /// stale or foreign one, or one naming a build that is no longer there — and the caller does
    /// what a tile pressed from the couch does: open the window, which is also where the release
    /// check lives (#130). Nothing else here consults Steam or a gamescope session: an already-
    /// armed request from a Steam launch needs no second proof
    public static Handoff? TakeArrival(string launcherHomeRoot, Platform platform, Func<string, string?> env)
    {
        if (platform != Platform.Linux) return null;
        // (the arm alone is not enough: ANY bare launch used to consume it, and a desktop-icon click
        // during a routed Play stole the arm from the launch it belonged to (L4))
        if (!FromSteamLaunch(env)) return null;
        var handoff = TakeHandoff(HandoffPath(launcherHomeRoot));
        if (handoff is null) return null;
        // (a claimed arm naming a build that is gone is consumed, never guessed around: the default
        // build is not what the player asked for, and opening the window is still an answer)
        if (handoff.Folder is { } folder && !GameFolder.IsBuilt(folder, platform)) return null;
        return handoff;
    }

    // (a claim product, never a candidate again: .taken is the rename a claim leaves when its
    // reader died mid-claim, .played the receipt a successful claim writes, .abandoned the
    // cancellation a failed delivery won before any arrival got there)
    static bool IsClaimed(string name) =>
        name.EndsWith(".taken", StringComparison.Ordinal) || name.EndsWith(".played", StringComparison.Ordinal)
        || name.EndsWith(".abandoned", StringComparison.Ordinal);

    // (a candidate file is an unclaimed arm only if its NAME is one ArmToken wrote: 14-digit ms
    // time, dash, GetRandomFileName tail. .taken / .played / anything else is never a handoff to
    // claim, so a claimed-away file can never be claimed again — round-three P1)
    // (an arm name is 14-digit ms, a dash, and a Path.GetRandomFileName tail. The audit's fourth
    // report: .NET's generator draws from base32 "abcdefghijklmnopqrstuvwxyz012345", so digits 6-9
    // cannot occur in the tail; and char.IsDigit accepted Unicode numerals no invariant ASCII stamp
    // can generate. Both are exact ASCII/base32 checks now, so every accepted name is one ArmToken
    // could actually have written)
    static bool IsArmName(string name) =>
        name.Length == 14 + 1 + 12 && name[14] == '-' &&
        name[..14].All(c => c is >= '0' and <= '9') &&
        name[23] == '.' &&
        name[15..23].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '5') &&
        name[24..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '5');

    /// A cancellation this recent that a late Steam launch could still be its delivery (round-five
    /// L3's legacy-tile hole, from the audit): a tile persisted with `--play` launch options makes
    /// its arrival a PlayWithoutWindow, and after a refused routed Play the arm is abandoned — that
    /// late arrival then finds no handoff and used to fall back to the default build, starting a
    /// second game beside the sender's direct one. True when a .abandoned claim product younger
    /// than this window stands in this home's handoff directory. The window is shorter than the arm
    /// ttl on purpose: Steam delivered seconds to tens of seconds after the URI in every measured
    /// run, and a longer window would make an ordinary tile press right after a refused Play look
    /// like a late delivery and do nothing
    public static bool RecentCancellation(string launcherHomeRoot, int windowSeconds = 120)
    {
        try
        {
            var now = DateTime.UtcNow;
            foreach (var f in Directory.EnumerateFiles(HandoffPath(launcherHomeRoot)))
            {
                if (!IsClaimProduct(Path.GetFileName(f)) || !f.EndsWith(".abandoned", StringComparison.Ordinal)) continue;
                // (round-five L3 audit: the cancellation's own time, not the arm name's — a rename
                // keeps the old mtime, so TryAbandon stamps it at the rename. A future stamp is a
                // clock lie and never counts)
                var age = now - File.GetLastWriteTimeUtc(f);
                if (age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(windowSeconds)) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// The sender's own commitment, written immediately after a Direct decision and BEFORE the
    /// pre-spawn work (performance statistics, crash reports): a game is about to start. The
    /// audit's fourth report proved the process-list test alone leaves a gap — a late legacy
    /// delivery arriving between the abandonment and Process.Start sees no game yet and would
    /// start a second copy. This marker closes that interval; it is the sender's statement, not
    /// an inference from the process table
    public static void RecordDirectStart(string launcherHomeRoot, string? folder)
    {
        try
        {
            Directory.CreateDirectory(launcherHomeRoot);
            File.WriteAllText(DirectStartPath(launcherHomeRoot), (folder ?? "") + "\t" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// A Direct start committed this recently: true while the sender is between its Direct
    /// decision and its game becoming visible (and briefly after). Rejects a future stamp
    public static bool DirectStartCommitted(string launcherHomeRoot, int windowSeconds = 30)
    {
        try
        {
            var f = DirectStartPath(launcherHomeRoot);
            if (!File.Exists(f)) return false;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(f);
            return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(windowSeconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string DirectStartPath(string launcherHomeRoot) => Path.Combine(launcherHomeRoot, "play-direct-start");

    /// The commitment is over: the game has spawned (or the spawn failed and nothing is coming).
    /// Clearing it here keeps the marker to the pre-spawn interval only, so a legitimate tile press
    /// after a quick exit or a failed spawn is not suppressed (audit #5 P2)
    public static void ClearDirectStart(string launcherHomeRoot)
    {
        try { File.Delete(DirectStartPath(launcherHomeRoot)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // (leftovers of claims: receipts and rename casualties age out with their own name's time, so
    // the handoff directory never grows without bound and a fresh Play's file is never swept. A
    // bare arm name is not swept here — it ages out through TakeHandoff's own ordering sweep)
    // (round-five L5 audit: ONLY claim products are ever swept — a name that is one ArmToken wrote
    // plus one of the claim suffixes. The old rule deleted any file beginning with 14 digits older
    // than an hour, which the audit proved could delete a stranger's file named e.g.
    // "<14 digits>-personal-notes.txt". A stranger's file in this directory is now never read,
    // claimed, counted or deleted, whatever its name)
    static void SweepClaimed(string handoffDir)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var f in Directory.EnumerateFiles(handoffDir))
            {
                var name = Path.GetFileName(f);
                if (!IsClaimProduct(name)) continue;
                if (!long.TryParse(name[..14], out var ms)) continue;
                if (now - ms > 3_600_000) { try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // (an arm name followed by a claim suffix: <14-digit ms>-<12-char tail>.<taken|played|abandoned>)
    static bool IsClaimProduct(string name)
    {
        foreach (var suffix in ClaimSuffixes)
            if (name.EndsWith(suffix, StringComparison.Ordinal) && IsArmName(name[..^suffix.Length])) return true;
        return false;
    }
    static readonly string[] ClaimSuffixes = [".taken", ".played", ".abandoned"];

    // "<folder>\t<armed-time>" — the time after the LAST tab (a path may hold a tab, a unix
    // stamp never does); an empty folder before the tab, or no tab at all, is an arm that
    // carried no folder. Null when the text is not our shape or the arm has aged out: an aged
    // answer is NOT "nothing new" but "the oldest arm died" — TakeHandoff treats that as its
    // ordering signal (do not let a late receiver steal a younger Play's arm, round-three P2)
    static Handoff? ReadArm(string text)
    {
        var at = text.LastIndexOf('\t');
        var (folder, stamp) = at < 0 ? ((string?)null, text) : ((string?)text[..at], text[(at + 1)..]);
        // (the folder field has to be a path this launcher could have written, the same shape test
        // Ours makes of a standing arm: otherwise a stranger's file with a plausible stamp could
        // name a relative or nonsense folder and be claimed as an arrival. A bare launch now claims
        // before the window's lock, so this is judged once here, for every consumer)
        if (!Ours(text)) return null;
        if (!long.TryParse(stamp.Trim(), out var armed)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - armed is not (>= 0 and <= TokenTtlSeconds)) return null;
        return new Handoff(string.IsNullOrEmpty(folder) ? null : folder, armed);
    }

    // The handoff arm lives in this directory, one file per Play. Fresh for five minutes: the Deck's
    // measured handoff arrived in seconds, but a cold AppImage on slow media mounts slower, and an
    // arm that expires BEFORE its launch arrives makes that launch pick the default build — the
    // wrong-game mistake. The cost of the longer window only lands when xdg-open took the URI and
    // Steam never launched at all: routing stays direct for those minutes (the safe side — the
    // refused-URI path clears the arm immediately, so that hold only covers silent failures)
    const int TokenTtlSeconds = 300;

    // Arm one Play's handoff; the answer is the arm's path, or null when it could not be written
    // (a read-only home: then the arm says nothing about delivery, and Decide must not read its
    // absence as "the launch consumed it")
    static string? ArmToken(string handoffDir, string? folder)
    {
        try
        {
            Directory.CreateDirectory(handoffDir);
            // (the name is this Play's: time first for the FIFO sort, a random tail so two Plays in
            // the same millisecond still leave two arms)
            var arm = Path.Combine(handoffDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():D14}-{Path.GetRandomFileName()}");
            File.WriteAllText(arm, $"{folder}\t{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
            return arm;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// xdg-open, this launcher's own environment kept. Its answer is three-state (null: no opener
    /// on PATH — the dispatch itself proved failed; true: took the URI, exit 0 or still working
    /// past the bounded wait; false: stated refusal). Only null may fall back direct without proof
    /// of a standing arm — see Decide (an opener that dispatched and then exited nonzero must not
    /// buy a second copy of the game)
    public static bool? DefaultOpenUri(string uri) =>
        OpenWith("xdg-open", [uri]);

    /// Start program with args, answer null when the program itself could not start (no xdg-open
    /// on this PATH — dispatch PROVED failed), true when the handler took the URI (exit 0, or still
    /// working past the bounded wait: dispatched, not failed), false only on a refusal the handler
    /// stated outright (nonzero exit). internal so the tests exercise it against real processes
    /// rather than only a delegate
    internal static bool? OpenWith(string program, IReadOnlyList<string> args, int waitMs = 15_000)
    {
        try
        {
            var si = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var a in args) si.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(si);
            if (p is null) return null;
            if (!p.WaitForExit(waitMs)) return true;   // (still working: dispatched, not failed — see above)
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                     or IOException or UnauthorizedAccessException)
        { return null; }   // (the opener never started: not a refusal, an absence — the caller's ?? false answers the fallback either way)
    }
}
