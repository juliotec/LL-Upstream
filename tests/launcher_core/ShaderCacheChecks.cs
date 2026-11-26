using LongshotLauncher.Core;

/// The shader caches an update keeps (Core/ShaderCaches.cs): what a version's builder translates shaders with (its manifest's
/// stamps), and the copy from the build an update replaces, over authored files (no game's caches)
static partial class Program
{
    /// A version's builder as far as the shader caches go: its version folder's record's stamps, its release's manifest's (the
    /// translator's and its compiler's: Toolchains.BuilderStamp); record: false, a folder with no record
    static Toolchain TranslatorTools(string name, string translator = "7a11", string compiler = "c0ffee", bool record = true)
    {
        var chain = new Toolchain("v4.0.0", Folder(name), "Longshot.x86_64");
        if (record) ReleaseRecord(chain.Folder, "v4.0.0", translator, compiler);
        return chain;
    }

    static void ShaderCacheChecks()
    {
        Require(ShaderCaches.IsCache("longshot_programs.bin") && ShaderCaches.IsCache("longshot_vk_pipelines.stamp")
                && ShaderCaches.IsCache("longshot_vk_pipelines_play_12.bin"), "The caches the game reads back.");
        Require(!ShaderCaches.IsCache("longshot_prepared.bin.warming") && !ShaderCaches.IsCache("longshot_vk_pipelines_play_.bin")
                && !ShaderCaches.IsCache("longshot_vk_pipelines_play_1.bin.tmp") && !ShaderCaches.IsCache("longshot_vk_pipelines_play_x.bin")
                && !ShaderCaches.IsCache("longshot_log.txt"), "Not a file being written, nor anything else in the folder.");

        // (the stamp: the translator's and its compiler's, as the version's release names them; two versions share caches only with both)
        Require(ShaderCaches.Stamp(TranslatorTools("caches-stamp")) == "longshot-builder-7a11-c0ffee"
                && ShaderCaches.Stamp(TranslatorTools("caches-stamp-translator", translator: "7a12")) != ShaderCaches.Stamp(TranslatorTools("caches-stamp-2"))
                && ShaderCaches.Stamp(TranslatorTools("caches-stamp-compiler", compiler: "c0ffef")) != ShaderCaches.Stamp(TranslatorTools("caches-stamp-3"))
                && ShaderCaches.Stamp(TranslatorTools("caches-stamp-none", record: false)) == null,
                "The stamp: the manifest's translator and compiler; another of either, another stamp; no record, none.");

        // ---- the copy ----
        var same = TranslatorTools("caches-same");
        var same2 = TranslatorTools("caches-same-2");
        var other = TranslatorTools("caches-other", translator: "7a13");
        var none = TranslatorTools("caches-none", record: false);
        var llvm = TranslatorTools("caches-llvm", compiler: "c0fff0");
        var unread = TranslatorTools("caches-unread", record: false);
        var games = Folder("caches-games");
        string old = Path.Combine(games, "v3.0.0"), next = Path.Combine(games, "v3.0.1");
        string Old(string name) => Path.Combine(old, "cache", name);
        string Next(string name) => Path.Combine(next, "cache", name);
        Func<string, long?> roomy = _ => long.MaxValue;
        Func<bool> idle = () => false;
        Func<string, string, bool> otherDrive = (_, _) => false;   // (no hard link from one drive to another: a copy)
        Directory.CreateDirectory(next);

        Require(ShaderCaches.Keep(old, next, same, same2, roomy, idle).What == CachesKept.NoCaches, "No old build, no caches.");
        Touch(Old("longshot_pipelines.bin"), "pipelines");
        Touch(Old("longshot_programs.bin"), "");
        Require(ShaderCaches.Keep(old, next, same, same2, roomy, idle).What == CachesKept.NoCaches,
                "An old build that translated no program has nothing worth a copy.");
        Touch(Old("longshot_programs.bin"), "programs");
        Touch(Old("longshot_programs.idx"), "index");
        Touch(Old("longshot_vk_pipelines.stamp"), "12345678");
        Touch(Old("longshot_vk_pipelines_play_3.bin"), "play");
        Touch(Old("longshot_prepared.bin.warming"), "");
        Touch(Old("longshot_computes.bin"), "");
        Touch(Path.Combine(old, "cache", "prompts", "a.bin"), "prompt");
        Touch(Path.Combine(old, "longshot_options.ini"), "options");
        var bytes = "pipelines".Length + "programs".Length + "index".Length + "12345678".Length + "play".Length;

        Require(ShaderCaches.Keep(old, old, same, same, roomy, idle).What == CachesKept.NoCaches
                && ShaderCaches.Keep(old, old + Path.DirectorySeparatorChar, same, same, roomy, idle).What == CachesKept.NoCaches,
                "A rebuild in its own folder keeps its caches by itself.");
        Require(ShaderCaches.Keep(old, next, same, other, roomy, idle).What == CachesKept.OtherTranslator
                && ShaderCaches.Keep(old, next, null, same2, roomy, idle).What == CachesKept.OtherTranslator
                && ShaderCaches.Keep(old, next, same, null, roomy, idle).What == CachesKept.OtherTranslator
                && ShaderCaches.Keep(old, next, none, none, roomy, idle).What == CachesKept.OtherTranslator,
                "Another translator's caches stay where they are, and so do those of a version whose tools are gone.");
        Require(ShaderCaches.Keep(old, next, same, llvm, roomy, idle).What == CachesKept.OtherTranslator
                && ShaderCaches.Keep(old, next, unread, same2, roomy, idle).What == CachesKept.OtherTranslator,
                "Nor are they kept for another compiler, or one that cannot be read.");
        Require(ShaderCaches.Keep(old, next, same, same2, roomy, () => true).What == CachesKept.Running, "Not while Longshot runs.");
        Require(ShaderCaches.Room(bytes) == bytes + Disk.LongshotGameBytes + Disk.LongshotWorkBytes + ShaderCaches.Margin, "A copy made before the build leaves it its room.");
        var tight = ShaderCaches.Keep(old, next, same, same2, _ => ShaderCaches.Room(bytes) - 1, idle, link: otherDrive);
        Require(tight.What == CachesKept.NoSpace && tight.Bytes == bytes, "Another drive: not without room for the copy, the build and the margin.");
        Require(!Directory.EnumerateFiles(Path.Combine(next, "cache")).Any(), "Nothing was written by a copy that did not happen.");

        // (a copy that stops leaves nothing behind)
        using (var stop = new CancellationTokenSource())
        {
            Throws<OperationCanceledException>(() => ShaderCaches.Keep(old, next, same, same2, roomy, idle, (done, _) => stop.Cancel(), stop.Token, otherDrive),
                                               "A cancelled copy stops.");
            Require(!Directory.EnumerateFiles(Path.Combine(next, "cache")).Any(), "A cancelled copy leaves no file, whole or part.");
        }

        // (the game made its cache folder and empty files at a start that was closed at once: still no caches of its own)
        Touch(Next("longshot_programs.bin"), "");
        Touch(Next("longshot_prepared.bin.warming"), "");
        var seen = new List<(long Done, long All)>();
        var kept = ShaderCaches.Keep(old, next, same, same2, _ => ShaderCaches.Room(bytes), idle, (done, total) => seen.Add((done, total)), link: otherDrive);
        Require(kept.What == CachesKept.Kept && !kept.Moved && kept.Files == 5 && kept.Bytes == bytes
                && kept.Taken != null && kept.Taken.SequenceEqual(new[] { "longshot_pipelines.bin", "longshot_programs.bin", "longshot_programs.idx",
                                                                          "longshot_vk_pipelines.stamp", "longshot_vk_pipelines_play_3.bin" }),
                "The same translator, another drive: the caches are copied, and their names said.");
        Require(seen.Count > 0 && seen[^1] == (bytes, bytes) && seen.All(s => s.All == bytes), "The copy says how far it is.");
        Require(File.ReadAllText(Next("longshot_programs.bin")) == "programs" && File.ReadAllText(Next("longshot_programs.idx")) == "index"
                && File.ReadAllText(Next("longshot_pipelines.bin")) == "pipelines" && File.ReadAllText(Next("longshot_vk_pipelines.stamp")) == "12345678"
                && File.ReadAllText(Next("longshot_vk_pipelines_play_3.bin")) == "play", "Each cache, as it was.");
        Require(!File.Exists(Next("longshot_computes.bin")) && !Directory.Exists(Path.Combine(next, "cache", "prompts"))
                && !File.Exists(Path.Combine(next, "longshot_options.ini")) && new FileInfo(Next("longshot_prepared.bin.warming")).Length == 0
                && !Directory.EnumerateFiles(Path.Combine(next, "cache"), "*.part").Any(),
                "Only the caches: no empty one, no file being written, nothing else of the folder.");
        Require(File.ReadAllText(Old("longshot_programs.bin")) == "programs" && File.Exists(Old("longshot_vk_pipelines_play_3.bin")),
                "The old build keeps its own after a copy.");

        File.WriteAllText(Old("longshot_programs.bin"), "newer programs");
        Require(ShaderCaches.Keep(old, next, same, same2, roomy, idle).What == CachesKept.HasCaches
                && File.ReadAllText(Next("longshot_programs.bin")) == "programs", "A build with caches of its own keeps them.");

        // ---- another translator: the pipeline side alone (the driver's pipelines and the notes), the programs translated again ----
        {
            var tg = Folder("caches-translator");
            string from = Path.Combine(tg, "v3.1.0");
            string From(string n) => Path.Combine(from, "cache", n);
            string Into(string folder, string n) => Path.Combine(folder, "cache", n);
            foreach (var (n, t) in new[] { ("longshot_programs.bin", "programs"), ("longshot_programs.idx", "index"),
                                           ("longshot_prepared.bin", "prepared"), ("longshot_module_ids.bin", "ids"),
                                           ("longshot_pipelines.bin.built", "built"), ("longshot_vk_pipelines.bin", "driver"),
                                           ("longshot_vk_pipelines.stamp", "12345678"), ("longshot_vk_pipelines.base", "base base base 16"),
                                           ("longshot_vk_pipelines_play_2.bin", "play"), ("longshot_pipelines.bin", "notes") })
                Touch(From(n), t);
            var side = new[] { "longshot_pipelines.bin", "longshot_vk_pipelines.base", "longshot_vk_pipelines.bin", "longshot_vk_pipelines.stamp",
                               "longshot_vk_pipelines_play_2.bin" };
            var to = Path.Combine(tg, "v3.2.0");
            Directory.CreateDirectory(to);
            var carried = ShaderCaches.Keep(from, to, same, other, roomy, idle, link: otherDrive);
            Require(carried.What == CachesKept.Kept && carried.PipelinesOnly && !carried.Moved && carried.Taken != null && carried.Taken.SequenceEqual(side)
                    && File.ReadAllText(Into(to, "longshot_vk_pipelines.bin")) == "driver" && File.ReadAllText(Into(to, "longshot_pipelines.bin")) == "notes",
                    "Another translator: the driver's pipeline cache, its stamp, base and play files, and the notes are copied: " +
                    string.Join(", ", carried.Taken ?? Array.Empty<string>()));
            Require(new[] { "longshot_programs.bin", "longshot_programs.idx", "longshot_prepared.bin", "longshot_module_ids.bin",
                            "longshot_pipelines.bin.built" }.All(n => !File.Exists(Into(to, n))),
                    "The translation's own (the programs, the prepare's record, the module identifiers, the warm's record) stay behind.");
            var toolsGone = Path.Combine(tg, "v3.2.1");
            Directory.CreateDirectory(toolsGone);
            var unknown = ShaderCaches.Keep(from, toolsGone, null, same2, roomy, idle, link: otherDrive);
            Require(unknown.What == CachesKept.Kept && unknown.PipelinesOnly && unknown.Taken!.SequenceEqual(side),
                    "A version whose tools are gone: the pipeline side alone, as for another translator.");
            var whole = Path.Combine(tg, "v3.1.1");
            Directory.CreateDirectory(whole);
            var sameKept = ShaderCaches.Keep(from, whole, same, same2, roomy, idle, link: otherDrive);
            Require(sameKept.What == CachesKept.Kept && !sameKept.PipelinesOnly && sameKept.Files == 10, "The same translator: every cache, as before.");
            // (no driver's pipeline cache: the notes alone are not worth a copy, another translator keeps nothing)
            var bare = Path.Combine(tg, "v3.0.9");
            Touch(Into(bare, "longshot_programs.bin"), "programs");
            Touch(Into(bare, "longshot_pipelines.bin"), "notes");
            var bareTo = Path.Combine(tg, "v3.3.0");
            Directory.CreateDirectory(bareTo);
            Require(ShaderCaches.Keep(bare, bareTo, same, other, roomy, idle).What == CachesKept.OtherTranslator
                    && !Directory.Exists(Path.Combine(bareTo, "cache")), "Another translator and no driver's pipeline cache: nothing kept.");
            // (one drive: the pipeline side is copied too, so an older target cannot destroy the old cache)
            var moveTo = Path.Combine(tg, "v3.4.0");
            Directory.CreateDirectory(moveTo);
            var movedSide = ShaderCaches.Keep(from, moveTo, same, other, roomy, idle, link: Link);
            Require(movedSide.What == CachesKept.Kept && movedSide.PipelinesOnly && !movedSide.Moved && File.Exists(From("longshot_vk_pipelines.bin"))
                    && File.Exists(From("longshot_pipelines.bin")) && File.Exists(From("longshot_programs.bin"))
                    && File.Exists(From("longshot_vk_pipelines.stamp")) && File.Exists(From("longshot_vk_pipelines.base")),
                    "One drive: the pipeline side is copied, the translation's own left where they are.");
            // (the new build's game carries the cache and writes its own stamp and base; the build then does not finish)
            File.Delete(Into(moveTo, "longshot_vk_pipelines.bin"));
            File.WriteAllText(Into(moveTo, "longshot_vk_pipelines_play_2.bin"), "replacement");
            File.WriteAllText(Into(moveTo, "longshot_vk_pipelines.stamp"), "87654321");
            File.WriteAllText(Into(moveTo, "longshot_vk_pipelines.base"), "another base 16b");
            Require(ShaderCaches.Undo(from, moveTo, movedSide) > 0 && File.ReadAllText(From("longshot_vk_pipelines.bin")) == "driver"
                    && File.ReadAllText(From("longshot_vk_pipelines_play_2.bin")) == "play"
                    && !File.Exists(Into(moveTo, "longshot_vk_pipelines_play_2.bin")), "Undo preserves the old cache after the target deletes or replaces files.");
            Require(File.ReadAllText(From("longshot_vk_pipelines.stamp")) == "12345678" && File.ReadAllText(From("longshot_vk_pipelines.base")) == "base base base 16"
                    && !File.Exists(Into(moveTo, "longshot_vk_pipelines.stamp")),
                    "Undo leaves the old build its own stamp and base, not the new build's: it would drop its pipeline cache.");
        }

        // ---- the move: both folders on one drive, where a file can have a second name ----
        var moved = Path.Combine(games, "v3.0.2");
        string Moved(string name) => Path.Combine(moved, "cache", name);
        Touch(Moved("longshot_programs.idx.part"), "a part a closed launcher left");
        var asked = false;
        seen.Clear();
        var move = ShaderCaches.Keep(old, moved, same, same2, _ => { asked = true; return 0; }, idle, (done, total) => seen.Add((done, total)));
        var moreBytes = bytes - "programs".Length + "newer programs".Length;
        Require(move.What == CachesKept.Kept && move.Moved && move.Files == 5 && move.Bytes == moreBytes && !asked,
                "The same drive: the caches are moved, whatever room the drive has.");
        Require(seen.Count == 1 && seen[0] == (moreBytes, moreBytes), "A move is done at once.");
        Require(File.ReadAllText(Moved("longshot_programs.bin")) == "newer programs" && File.ReadAllText(Moved("longshot_programs.idx")) == "index"
                && File.ReadAllText(Moved("longshot_pipelines.bin")) == "pipelines" && File.ReadAllText(Moved("longshot_vk_pipelines.stamp")) == "12345678"
                && File.ReadAllText(Moved("longshot_vk_pipelines_play_3.bin")) == "play"
                && !Directory.EnumerateFiles(Path.Combine(moved, "cache"), "*.part").Any(), "Each cache, as it was, and no part left.");
        Require(!File.Exists(Old("longshot_programs.bin")) && !File.Exists(Old("longshot_programs.idx"))
                && !File.Exists(Old("longshot_vk_pipelines.stamp")) && !File.Exists(Old("longshot_vk_pipelines_play_3.bin")),
                "The old folder no longer has them.");
        File.WriteAllText(Moved("longshot_pipelines.bin"), "the shipped list");   // (make_game.py's seed_pipelines, in place)
        Require(File.ReadAllText(Old("longshot_pipelines.bin")) == "pipelines",
                "But the pipeline notes, which the build writes over: copied, and the old folder keeps its own.");
        Require(File.Exists(Old("longshot_computes.bin")) && File.Exists(Old("longshot_prepared.bin.warming"))
                && File.Exists(Path.Combine(old, "cache", "prompts", "a.bin")) && File.ReadAllText(Path.Combine(old, "longshot_options.ini")) == "options",
                "Everything else of the old folder stays.");
        Require(ShaderCaches.Keep(old, Path.Combine(games, "v3.0.3"), same, same2, roomy, idle).What == CachesKept.NoCaches,
                "Moved once: the old build has none to give again.");

        // (one that cannot take its place, here a folder in its way: those placed before it go again, and the old names stay)
        var blocked = Path.Combine(games, "v3.0.5");
        Touch(Path.Combine(blocked, "cache", "longshot_programs.idx", "in the way"), "x");
        Throws<Exception>(() => ShaderCaches.Keep(moved, blocked, same, same2, roomy, idle, link: Link), "A cache that cannot take its place stops.");
        Require(!File.Exists(Path.Combine(blocked, "cache", "longshot_pipelines.bin")) && !File.Exists(Path.Combine(blocked, "cache", "longshot_programs.bin"))
                && !Directory.EnumerateFiles(Path.Combine(blocked, "cache"), "*.part").Any(), "None of them placed, no part left.");
        Require(File.ReadAllText(Moved("longshot_programs.bin")) == "newer programs" && File.ReadAllText(Moved("longshot_programs.idx")) == "index"
                && File.Exists(Moved("longshot_pipelines.bin")), "The old folder keeps them all.");

        // (a drive that links only some of them, or a link that stops working: all are copied, and the old folder keeps its own)
        var links = 0;
        var half = ShaderCaches.Keep(moved, Path.Combine(games, "v3.0.4"), same, same2, roomy, idle,
                                     link: (a, b) => ++links <= 2 && File.Exists(a) && Link(a, b));
        Require(half.What == CachesKept.Kept && !half.Moved && File.ReadAllText(Moved("longshot_programs.bin")) == "newer programs"
                && File.ReadAllText(Path.Combine(games, "v3.0.4", "cache", "longshot_programs.bin")) == "newer programs"
                && !Directory.EnumerateFiles(Path.Combine(games, "v3.0.4", "cache"), "*.part").Any(), "Half a move is a copy.");

        // ---- a build that did not finish: what was taken goes back (Undo) ----
        var failed = Path.Combine(games, "v3.0.6");
        string Failed(string name) => Path.Combine(failed, "cache", name);
        var gone = ShaderCaches.Keep(moved, failed, same, same2, roomy, idle, link: Link);
        Require(gone.What == CachesKept.Kept && gone.Moved && !File.Exists(Moved("longshot_programs.bin")), "Moved before a build.");
        File.WriteAllText(Failed("longshot_programs.bin"), "programs the build's prepare added to");
        File.WriteAllText(Failed("longshot_pipelines.bin"), "v3.0.6's shipped list");   // (seed_pipelines, before the prepare)
        Require(ShaderCaches.Undo(moved, failed, gone) == 5 && !Directory.EnumerateFiles(Path.Combine(failed, "cache")).Any()
                && File.ReadAllText(Moved("longshot_programs.bin")) == "programs the build's prepare added to"
                && File.ReadAllText(Moved("longshot_pipelines.bin")) == "the shipped list"
                && File.ReadAllText(Moved("longshot_programs.idx")) == "index" && File.Exists(Moved("longshot_vk_pipelines_play_3.bin")),
                "A move put back: the old folder has them all again, as the build left them.");
        var copied = ShaderCaches.Keep(moved, failed, same, same2, roomy, idle, link: otherDrive);
        Require(copied.What == CachesKept.Kept && !copied.Moved && ShaderCaches.Undo(moved, failed, copied) == 5
                && !Directory.EnumerateFiles(Path.Combine(failed, "cache")).Any() && File.ReadAllText(Moved("longshot_programs.idx")) == "index",
                "A copy put back: the copies go, the old folder keeps its own.");
        var twice = ShaderCaches.Keep(moved, failed, same, same2, roomy, idle, link: Link);
        File.Copy(Failed("longshot_programs.idx"), Moved("longshot_programs.idx"));   // (an old name the move could not take away)
        File.WriteAllText(Failed("longshot_programs.idx"), "a newer index");
        Require(ShaderCaches.Undo(moved, failed, twice) == 5 && File.ReadAllText(Moved("longshot_programs.idx")) == "index"
                && !File.Exists(Failed("longshot_programs.idx")) && File.Exists(Moved("longshot_programs.bin")),
                "A file the old folder still has only loses its new name.");
        Require(ShaderCaches.Undo(moved, failed, new CachesResult(CachesKept.HasCaches)) == 0 && ShaderCaches.Undo(moved, failed, gone) == 0,
                "Nothing taken, or nothing left to put back: nothing done.");
        ClearCacheChecks();
    }

    /// Clearing a game folder's shader caches (Free up space, a cold build): the caches and a copy's leftovers, the pipeline notes
    /// only when asked, nothing else in cache/ or the folder, never while Longshot runs
    static void ClearCacheChecks()
    {
        var game = Folder("clear-caches");
        string At(string name) => Path.Combine(game, ShaderCaches.CacheDir, name);
        void Fill()
        {
            Touch(At("longshot_programs.bin"), new string('p', 100));
            Touch(At("longshot_programs.idx"), new string('i', 10));
            Touch(At("longshot_vk_pipelines_play_2.bin"), new string('v', 20));
            Touch(At("longshot_programs.idx.part"), new string('x', 5));
            Touch(At("longshot_pipelines.bin"), "the shipped list");
            Touch(At("longshot_log.txt"), "not a cache");
            Touch(At("notes.part"), "not a cache's copy");
            Touch(Path.Combine(game, "user", "save", "slot0.bin"), "a save");
        }
        Fill();
        Require(ShaderCaches.ClearableBytes(game) == 135 && ShaderCaches.ClearableBytes(game, notes: true) == 135 + "the shipped list".Length
                && ShaderCaches.ClearableBytes(Path.Combine(game, "none")) == 0, "What clearing the caches lets go: " + ShaderCaches.ClearableBytes(game));
        Require(ShaderCaches.Clear(game, running: () => true) == null && File.Exists(At("longshot_programs.bin")),
                "Nothing is cleared while Longshot runs.");
        Require(ShaderCaches.Clear(game, running: () => false) == 135 && !File.Exists(At("longshot_programs.bin")) && !File.Exists(At("longshot_programs.idx.part"))
                && !File.Exists(At("longshot_vk_pipelines_play_2.bin")) && File.ReadAllText(At("longshot_pipelines.bin")) == "the shipped list"
                && File.Exists(At("longshot_log.txt")) && File.Exists(At("notes.part")) && File.Exists(Path.Combine(game, "user", "save", "slot0.bin")),
                "Free up space clears the caches, keeps the shipped pipeline list, and touches nothing else.");
        Fill();
        Require(ShaderCaches.Clear(game, notes: true, running: () => false) == 135 + "the shipped list".Length && !File.Exists(At("longshot_pipelines.bin"))
                && File.Exists(At("longshot_log.txt")) && File.Exists(Path.Combine(game, "user", "save", "slot0.bin")),
                "A cold build clears the pipeline notes too, and nothing else.");
        // (a completely cold build's start: the build files and both stores, the caches with their notes; nothing while Longshot
        // runs; the player's own files in either folder stay)
        var work = Folder("cold-work");
        Touch(Path.Combine(work, "objcache", "a.o"), new string('o', 30));
        Touch(Path.Combine(work, Disk.LiftStore, "k", "c.c"), new string('c', 20));
        Touch(Path.Combine(work, "My notes.txt"), "keep");
        Fill();
        Require(ColdBuild.Clear(work, game, running: () => true) == null && File.Exists(Path.Combine(work, "objcache", "a.o")) && File.Exists(At("longshot_programs.bin")),
                "A cold build deletes nothing while Longshot runs.");
        var cleared = ColdBuild.Clear(work, game, running: () => false);
        Require(cleared == new ColdBuild.Cleared(50, 135 + "the shipped list".Length) && Disk.WorkBytes(work) == 0 && File.Exists(Path.Combine(work, "My notes.txt"))
                && !File.Exists(At("longshot_pipelines.bin")) && File.Exists(At("longshot_log.txt")) && File.Exists(Path.Combine(game, "user", "save", "slot0.bin")),
                "A cold build starts from nothing earlier builds kept, and keeps the player's files: " + cleared);
        // (a cache/ that is a link: another folder's, never cleared from here)
        var other = Folder("clear-caches-other");
        Touch(Path.Combine(other, "longshot_programs.bin"), "theirs");
        var linked = Folder("clear-caches-linked");
        if (TestLinks.Available)
        {
            Directory.CreateSymbolicLink(Path.Combine(linked, ShaderCaches.CacheDir), other);
            Require(ShaderCaches.ClearableBytes(linked) == 0 && ShaderCaches.Clear(linked, running: () => false) == 0
                    && File.Exists(Path.Combine(other, "longshot_programs.bin")), "A linked cache/ is not followed.");
        }
        ShaderStoreChecks();
    }

    /// The shader store every build shares (Longshot's SPIR-V store): where it is, what it takes, and Free up space clearing it
    /// with every version's caches, its files only, never while Longshot runs
    static void ShaderStoreChecks()
    {
        var root = Folder("shader-store");
        string Full(params string[] parts) => Path.GetFullPath(Path.Combine(parts));
        string? Env(string key, Dictionary<string, string> vars) => vars.GetValueOrDefault(key);
        var home = Full(root, "home");
        Require(new SaveEnvironment(Platform.Windows, k => Env(k, new() { ["LOCALAPPDATA"] = Full(root, "Local") })).ShaderStore == Full(root, "Local", "Longshot", "shaders")
                && new SaveEnvironment(Platform.Linux, k => Env(k, new() { ["HOME"] = home, ["XDG_CACHE_HOME"] = Full(root, "cache") })).ShaderStore == Full(root, "cache", "longshot", "shaders")
                && new SaveEnvironment(Platform.Linux, k => Env(k, new() { ["HOME"] = home })).ShaderStore == Full(home, ".cache", "longshot", "shaders"),
                "The store where the game keeps it: %LOCALAPPDATA%\\Longshot\\shaders, ${XDG_CACHE_HOME:-~/.cache}/longshot/shaders.");
        Require(new SaveEnvironment(Platform.Windows, k => Env(k, new() { ["LOCALAPPDATA"] = Full(root, "Local"), ["LONGSHOT_SHADER_STORE"] = Full(root, "mine") })).ShaderStore == Full(root, "mine")
                && new SaveEnvironment(Platform.Linux, k => Env(k, new() { ["HOME"] = home, ["LONGSHOT_SHADER_STORE"] = Full(root, "mine") })).ShaderStore == Full(root, "mine"),
                "LONGSHOT_SHADER_STORE names another folder, on both systems.");
        Require(new SaveEnvironment(Platform.Windows, k => Env(k, new() { ["LOCALAPPDATA"] = Full(root, "Local"), ["LONGSHOT_SHADER_STORE"] = "0" })).ShaderStore == null
                && new SaveEnvironment(Platform.Linux, k => Env(k, new() { ["HOME"] = home, ["LONGSHOT_SHADER_STORE"] = "0" })).ShaderStore == null
                && new SaveEnvironment(Platform.Windows, k => Env(k, new() { ["USERPROFILE"] = home })).ShaderStore == null,
                "No store, as the game has none: LONGSHOT_SHADER_STORE=0, or no LOCALAPPDATA on Windows (never a folder named 0).");
        Require(new SaveEnvironment(Platform.Linux, k => Env(k, new() { ["HOME"] = home, ["XDG_CACHE_HOME"] = "relative/cache" })).ShaderStore == Full(home, ".cache", "longshot", "shaders"),
                "An XDG_CACHE_HOME that is not a full path is not used, as the game does not use it.");
        Require(ShaderCaches.StoreBytes(null) == 0 && ShaderCaches.StoreFiles(null).Count == 0, "No store: nothing to count or clear.");

        var store = Full(root, "Local", "Longshot", "shaders");
        Touch(Path.Combine(store, "spirv.pack"), new string('p', 300));
        Touch(Path.Combine(store, "spirv.idx"), new string('i', 20));
        Touch(Path.Combine(store, "spirv.lock"), "");
        Touch(Path.Combine(store, "spirv.pack.tmp"), new string('t', 7));
        Touch(Path.Combine(store, "spirv"), "not the store's");
        Touch(Path.Combine(store, "notes.txt"), "the player's");
        Require(ShaderCaches.StoreBytes(store) == 327 && ShaderCaches.StoreBytes(Full(root, "none")) == 0,
                "What the store takes: its files and what a write left beside them: " + ShaderCaches.StoreBytes(store));
        Require(ShaderCaches.StoreNames.All(n => !ShaderCaches.IsCache(n)), "The store's files are no build's cache: a build's own clear never takes them.");
        Require(ShaderCaches.ClearStore(store, running: () => true) == null && File.Exists(Path.Combine(store, "spirv.pack")),
                "Nothing is cleared while Longshot runs.");
        Require(ShaderCaches.ClearStore(store, running: () => false) == 327 && !File.Exists(Path.Combine(store, "spirv.pack"))
                && !File.Exists(Path.Combine(store, "spirv.lock")) && !File.Exists(Path.Combine(store, "spirv.pack.tmp"))
                && File.Exists(Path.Combine(store, "spirv")) && File.Exists(Path.Combine(store, "notes.txt")),
                "Free up space clears the store's files and nothing else in its folder (LONGSHOT_SHADER_STORE may name one that holds other things).");
        File.Delete(Path.Combine(store, "spirv"));
        File.Delete(Path.Combine(store, "notes.txt"));
        Touch(Path.Combine(store, "spirv.pack"), "p");
        Require(ShaderCaches.ClearStore(store, running: () => false) == 1 && !Directory.Exists(store) && Directory.Exists(Path.GetDirectoryName(store)),
                "A folder that held only the store goes with it.");
        Require(ShaderCaches.ClearStore(store, running: () => false) == 0, "No store: nothing to clear.");
    }

    static bool Link(string from, string to)
    {
        File.Copy(from, to);
        return true;
    }
}
