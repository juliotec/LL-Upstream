using System.Text;
using LongshotLauncher.Core;

/// The shader caches an update keeps (Core/ShaderCaches.cs): what a version's tools translate shaders with, and the copy from the
/// build an update replaces, over authored files (no game's caches)
static partial class Program
{
    static readonly string Llvm = new('1', 64), OtherLlvm = new('2', 64), Python = new('3', 64), Cpm = new('d', 64);

    /// A version's tools as far as the shader caches go (the Windows builder's): nx2/translator_files.py and the sources its
    /// stamp is made of, and the record of its parts with llvm-mingw's SHA-256 (compiler; null: no record) and cpm's
    static Toolchain TranslatorTools(string name, string shader = "translate();\n", string recipe = "DIRS = ()\n", string? compiler = null,
                                     bool record = true)
    {
        var chain = new Toolchain("v3.0.0", Folder(name), ListKind.Setup, ToolsLock.Run);
        var tools = chain.ToolsRoot;
        Touch(Path.Combine(tools, "nx2", "translator_files.py"), recipe);
        Touch(Path.Combine(tools, "gpu", "vk", "eden", "b.cpp"), "b\n");
        Touch(Path.Combine(tools, "gpu", "vk", "eden", "a", "z.h"), "z\n");
        Touch(Path.Combine(tools, "gpu", "vk", "eden", "a.cpp"), "a\n");
        Touch(Path.Combine(tools, "gpu", "vk", "sirit", "s.cpp"), "s\n");
        Touch(Path.Combine(tools, "gpu", "vk", "compat", "c.h"), "c\n");
        Touch(Path.Combine(tools, "gpu", "vk", "vk_shader.cpp"), shader);
        Touch(Path.Combine(tools, "gpu", "vk", "vk_bank.cpp"), "bank\n");
        Touch(Path.Combine(tools, "gpu", "vk", "vk_state.h"), "state\n");
        Touch(Path.Combine(tools, "gpu", "vk", "vk_draw.cpp"), "draw\n");   // (not the translator's)
        if (record) ToolsRecord(chain.Folder, "Setup", ("LongshotBuilder/tools", "llvm-mingw", compiler ?? Llvm), ("LongshotBuilder/tools", "cpm", Cpm),
                                ("LongshotBuilder/tools", "python", Python));
        return chain;
    }

    /// A version folder's record of its parts (Toolchains.RecordName), as an install writes it: each part's dest, the folder
    /// its prefix became (to; null: the whole archive at dest) and its SHA-256
    static void ToolsRecord(string folder, string kind, params (string Dest, string? To, string Sha)[] parts)
    {
        static string Text(string? s) => s == null ? "null" : "\"" + s + "\"";
        var list = string.Join(",", parts.Select(p => $"{{\"sha\":\"{p.Sha}\",\"size\":1,\"dest\":\"{p.Dest}\",\"from\":{Text(p.To == null ? null : p.To + "-1")}," +
                                                      $"\"to\":{Text(p.To)},\"url\":\"https://example.invalid/a.zip\",\"entries\":[]}}"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Toolchains.RecordName), $"{{\"format\":1,\"tag\":\"v3.0.0\",\"kind\":\"{kind}\",\"run\":null,\"parts\":[{list}]}}");
    }

    static void ShaderCacheChecks()
    {
        Require(ShaderCaches.IsCache("longshot_programs.bin") && ShaderCaches.IsCache("longshot_vk_pipelines.stamp")
                && ShaderCaches.IsCache("longshot_vk_pipelines_play_12.bin"), "The caches the game reads back.");
        Require(!ShaderCaches.IsCache("longshot_prepared.bin.warming") && !ShaderCaches.IsCache("longshot_vk_pipelines_play_.bin")
                && !ShaderCaches.IsCache("longshot_vk_pipelines_play_1.bin.tmp") && !ShaderCaches.IsCache("longshot_vk_pipelines_play_x.bin")
                && !ShaderCaches.IsCache("longshot_log.txt"), "Not a file being written, nor anything else in the folder.");

        // (the stamp: sha1 of each file's path below gpu/vk and its bytes with Unix line ends, the folders' files sorted by
        // path, then the three files: what nx2/translator_files.py answers for this tree)
        var tools = TranslatorTools("caches-tools").ToolsRoot;
        var key = ShaderCaches.Translator(tools);
        var order = new[] { "compat/c.h", "eden/a.cpp", "eden/a/z.h", "eden/b.cpp", "sirit/s.cpp" };
        var text = new Dictionary<string, string>
        {
            ["compat/c.h"] = "c\n", ["eden/a.cpp"] = "a\n", ["eden/a/z.h"] = "z\n", ["eden/b.cpp"] = "b\n", ["sirit/s.cpp"] = "s\n",
            ["vk_shader.cpp"] = "translate();\n", ["vk_bank.cpp"] = "bank\n", ["vk_state.h"] = "state\n",
        };
        var all = string.Concat(order.Concat(new[] { "vk_shader.cpp", "vk_bank.cpp", "vk_state.h" }).Select(n => n + text[n]));
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(all)))[..16];
        Require(key != null && key.StartsWith(expected + "-", StringComparison.Ordinal) && key.Length == 33,
                "The translator's stamp, as nx2/translator_files.py makes it, then its recipe's.");
        File.WriteAllText(Path.Combine(tools, "gpu", "vk", "vk_draw.cpp"), "another draw\n");
        Require(ShaderCaches.Translator(tools) == key, "A source that translates nothing keeps the stamp.");
        File.WriteAllText(Path.Combine(tools, "gpu", "vk", "eden", "a.cpp"), "a\r\n");
        Require(ShaderCaches.Translator(tools) == key, "Windows line ends are stamped as Unix ones.");
        Require(ShaderCaches.Translator(TranslatorTools("caches-tools-shader", shader: "translate(2);\n").ToolsRoot) != key, "Another translator.");
        var recipe = ShaderCaches.Translator(TranslatorTools("caches-tools-recipe", recipe: "DIRS = ('eden',)\n").ToolsRoot);
        Require(recipe != key && recipe != null && recipe.StartsWith(expected + "-", StringComparison.Ordinal),
                "The same sources stamped by another recipe never compare equal.");
        var none = TranslatorTools("caches-tools-none");
        File.Delete(Path.Combine(none.ToolsRoot, "nx2", "translator_files.py"));
        Require(ShaderCaches.Translator(none.ToolsRoot) == null && ShaderCaches.Stamp(none) == null && ShaderCaches.Translator(Path.Combine(scratch, "caches-no-tools")) == null,
                "Tools with no recipe say nothing of their translator.");
        File.Delete(Path.Combine(tools, "gpu", "vk", "vk_bank.cpp"));
        Require(ShaderCaches.Translator(tools) == null, "Nor do tools that lack one of its sources.");

        // (what compiles the translator: the SHA-256 of the compiler's tools.lock parts, as each version's record names them)
        var first = TranslatorTools("caches-compiler-a");
        var second = TranslatorTools("caches-compiler-b");
        ToolsRecord(second.Folder, "Setup", ("LongshotBuilder/tools", "python", new string('4', 64)), ("LongshotBuilder/tools", "cpm", Cpm),
                    ("LongshotBuilder/tools", "llvm-mingw", Llvm));
        var llvm = TranslatorTools("caches-compiler-llvm", compiler: OtherLlvm);
        Require(ShaderCaches.Compiler(first) is { Length: 16 } compiler && ShaderCaches.Compiler(second) == compiler
                && ShaderCaches.Stamp(first) == ShaderCaches.Translator(first.ToolsRoot) + "-" + compiler && ShaderCaches.Stamp(second) == ShaderCaches.Stamp(first),
                "The same llvm-mingw: the same compiler, whatever else of the tools changed.");
        Require(ShaderCaches.Translator(llvm.ToolsRoot) == ShaderCaches.Translator(first.ToolsRoot) && ShaderCaches.Compiler(llvm) != ShaderCaches.Compiler(first)
                && ShaderCaches.Stamp(llvm) != ShaderCaches.Stamp(first), "Another llvm-mingw: the same translator, compiled otherwise.");
        var headers = TranslatorTools("caches-compiler-cpm");
        ToolsRecord(headers.Folder, "Setup", ("LongshotBuilder/tools", "llvm-mingw", Llvm), ("LongshotBuilder/tools", "cpm", new string('e', 64)),
                    ("LongshotBuilder/tools", "python", Python));
        Require(ShaderCaches.Compiler(headers) is { } cpmCompiler && cpmCompiler != ShaderCaches.Compiler(first) && ShaderCaches.Stamp(headers) != ShaderCaches.Stamp(first),
                "Another cpm: the same llvm-mingw, other Vulkan and SPIR-V headers, another compiler.");
        var unread = TranslatorTools("caches-compiler-unread", record: false);
        var noLlvm = TranslatorTools("caches-compiler-none");
        ToolsRecord(noLlvm.Folder, "Setup", ("LongshotBuilder/tools", "cpm", Cpm), ("LongshotBuilder/tools", "python", Python));
        var noCpm = TranslatorTools("caches-compiler-no-cpm");
        ToolsRecord(noCpm.Folder, "Setup", ("LongshotBuilder/tools", "llvm-mingw", Llvm), ("LongshotBuilder/tools", "python", Python));
        Require(ShaderCaches.Compiler(unread) == null && ShaderCaches.Stamp(unread) == null && ShaderCaches.Compiler(noLlvm) == null
                && ShaderCaches.Stamp(noLlvm) == null && ShaderCaches.Compiler(noCpm) == null && ShaderCaches.Stamp(noCpm) == null,
                "Tools whose compiler cannot be read say nothing of it, nor do Windows tools without cpm.");
        // (the Linux builder's: its AppImage, then emsdk's clang, the sysroot and cpm. A builder that carried its tools has none of them)
        var linux = new Toolchain("v3.0.0", Folder("caches-compiler-linux"), ListKind.Linux, null);
        (string, string?, string)[] linuxParts =
        {
            (".", null, new string('5', 64)), ("usr/lib/longshot", "emsdk", new string('6', 64)), ("usr/lib/longshot", "sysroot", new string('7', 64)),
            ("usr/share/longshot", "cpm", new string('8', 64)), ("usr/lib/longshot", "ffmpeg", new string('9', 64)),
        };
        ToolsRecord(linux.Folder, "Linux", linuxParts);
        var shas = Toolchains.PartShas(linux.Folder);
        Require(shas != null && shas["."] == new string('5', 64) && shas["usr/lib/longshot/sysroot"] == new string('7', 64)
                && shas["usr/share/longshot/cpm"] == new string('8', 64) && Toolchains.PartShas(Path.Combine(scratch, "caches-no-tools")) == null,
                "Each part by the folder it became.");
        var linuxCompiler = ShaderCaches.Compiler(linux);
        linuxParts[0].Item3 = new string('a', 64);
        linuxParts[4].Item3 = new string('b', 64);
        ToolsRecord(linux.Folder, "Linux", linuxParts);
        Require(linuxCompiler is { Length: 16 } && ShaderCaches.Compiler(linux) == linuxCompiler, "Linux: another AppImage or FFmpeg, the same compiler.");
        foreach (var i in new[] { 1, 2, 3 })
        {
            var changed = (((string, string?, string)[])linuxParts.Clone());
            changed[i].Item3 = new string('c', 64);
            ToolsRecord(linux.Folder, "Linux", changed);
            Require(ShaderCaches.Compiler(linux) is { } c && c != linuxCompiler, "Linux: another " + changed[i].Item2 + ", another compiler.");
        }
        ToolsRecord(linux.Folder, "Linux", linuxParts[0], linuxParts[1], linuxParts[2]);
        Require(ShaderCaches.Compiler(linux) == null, "Linux: tools without cpm say nothing of their compiler.");

        // ---- the copy ----
        var same = TranslatorTools("caches-same");
        var same2 = TranslatorTools("caches-same-2");
        var other = TranslatorTools("caches-other", shader: "translate(3);\n");
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
        Require(ShaderCaches.Room(bytes) == bytes + Disk.BuildBytes + ShaderCaches.Margin, "A copy made before the build leaves it its room.");
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

        // (Free up space's option counts the store with the caches and its hint names it; with no caches, the option names it)
        Require(ShaderCaches.ClearText(2, 9L << 30, 0) == "Also clear the shader caches of your 2 versions (9.0 GB)" && ShaderCaches.StoreHint(2, 0) == ""
                && ShaderCaches.ClearText(2, 1L << 30, 3L << 29) == "Also clear the shader caches of your 2 versions (2.5 GB)"
                && ShaderCaches.StoreHint(2, 3L << 29) == "1.5 GB of that is the shader store your versions share, which goes with their caches."
                && ShaderCaches.ClearText(0, 0, 3L << 30) == "Also clear the shader store your versions share (3.0 GB)" && ShaderCaches.StoreHint(0, 3L << 30) == "",
                "Free up space's option says what it clears: " + ShaderCaches.ClearText(2, 1L << 30, 3L << 29) + " / " + ShaderCaches.StoreHint(2, 3L << 29));
    }

    static bool Link(string from, string to)
    {
        File.Copy(from, to);
        return true;
    }
}