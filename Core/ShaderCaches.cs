using System.Security.Cryptography;
using System.Text;

namespace LongshotLauncher.Core;

/// What ShaderCaches.Keep did: Kept (the old build's caches are in the new build's folder), or why not. NoCaches: the old build
/// prepared none (or is the same folder); HasCaches: the new build has its own already; OtherTranslator: the two versions translate
/// shaders differently or compile the translator with other tools (ShaderCaches.Stamp), or one of them could not be read;
/// Running: Longshot runs, and may be writing them; NoSpace: another drive, with no room for the copy and the build
public enum CachesKept { Kept, NoCaches, HasCaches, OtherTranslator, Running, NoSpace }

/// Moved: the old folder no longer has them (both folders on one drive); else they were copied. Taken: their names, for Undo
public sealed record CachesResult(CachesKept What, int Files = 0, long Bytes = 0, bool Moved = false, IReadOnlyList<string>? Taken = null);

/// The shader caches an update keeps. The game writes them into its folder's cache/ as it prepares and plays (the translated
/// programs, the pipelines, the prepare's record: 10 GB and minutes of work), and an update builds the new version into a folder
/// of its own, so every update started them again. They are good for one shader translator (Longshot's nx2/translator_files.py:
/// its stamp, compiled into the renderer); the game drops them when the translator is another. Two versions with the same
/// translator, most nightlies in a row, can use the same caches: Keep takes them from the build an update replaces, before
/// the build, so that its prepare (Prepare shaders, which every build runs) finds them; Undo puts them back when the build
/// does not finish.
/// What the game adds to the stamp at its start (the GPU, its switches, the records' layout) the launcher cannot know: when that
/// differs too the game drops them as it would have dropped the files in place, and prepares again.
public static class ShaderCaches
{
    public const string CacheDir = "cache";

    /// Free room left beside the copy, for what the game adds to the caches as it plays
    public const long Margin = 1L << 30;

    /// The free room a copy of bytes needs: the copy, then the build it is made before (Disk.BuildBytes), and the margin
    public static long Room(long bytes) => bytes + Disk.BuildBytes + Margin;

    // (Longshot's scripts/dev_packs.py CACHES, which names them all for the same copy: tests/test_launcher_core.py --longshot)
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "longshot_programs.bin", "longshot_programs.idx", "longshot_programs.zst.bin", "longshot_programs.zst.idx",
        "longshot_module_ids.bin", "longshot_pipelines.bin", "longshot_pipelines.bin.predicted", "longshot_pipelines.bin.predicted_keys",
        "longshot_pipelines.bin.built",
        "longshot_computes.bin", "longshot_compute_variants.bin", "longshot_pipeline_states.bin",
        "longshot_pipeline_states_v1.bin", "longshot_prepared.bin", "longshot_vk_pipelines.bin", "longshot_vk_pipelines.stamp",
    };

    // (what play adds to the pipeline cache: longshot_vk_pipelines_play_<n>.bin, dev_packs.py PLAY_FILES)
    const string PlayPrefix = "longshot_vk_pipelines_play_";
    const string PlaySuffix = ".bin";

    // (the translated programs themselves: without one of them there is nothing worth a copy)
    static readonly string[] Programs = { "longshot_programs.bin", "longshot_programs.zst.bin" };

    // (the pipeline notes, which a build writes over in place: Longshot's nx2/make_game.py seed_pipelines puts the shipped list
    // in a notes file whose stamp is not the list's (program_cache_stamp: the GPU's lanes, mesh shaders, alignment). Copied
    // even on one drive, so a build that does not finish never gives the old build that list for its own notes: Undo)
    const string Notes = "longshot_pipelines.bin";

    // (Longshot's nx2/translator_files.py DIRS and FILES, below gpu/vk: tests/test_launcher_core.py --longshot)
    public static readonly IReadOnlyList<string> TranslatorDirs = new[] { "eden", "sirit", "compat" };
    public static readonly IReadOnlyList<string> TranslatorFiles = new[] { "vk_shader.cpp", "vk_bank.cpp", "vk_state.h" };
    const string Recipe = "translator_files.py";

    // (what compiles the translator, by the folder tools.lock unpacks it to (its dest): the records keep Shader::Info as laid
    // out in memory, and the game's own translator_stamp only catches another C++ library on Windows (_LIBCPP_VERSION).
    // Windows: llvm-mingw's clang and libc++, and cpm's Vulkan and SPIR-V headers (nx2/build.py header_package takes them
    // from RX_CPM, which MakeGameCommand points at the tools' cpm). Linux: emsdk, the clang that builds it, the sysroot's
    // libstdc++, and cpm's headers. tests/test_launcher_core.py --longshot checks they are tools.lock dests of their system,
    // each system's cpm among them.
    // Not nx2/build.py's flags for these files (target(), CPU, -std, -O2, the include order): they are made at run time from
    // the PC, the environment and the paths, and hashing all of build.py would make nearly every two versions differ. A
    // change to them alone is not caught here, nor by the stamp the game itself checks: Longshot's to name, should one
    // matter, as it names the stamped files in translator_files.py)
    public static readonly IReadOnlyList<string> WindowsCompiler = new[] { "LongshotBuilder/tools/llvm-mingw", "LongshotBuilder/tools/cpm" };
    public static readonly IReadOnlyList<string> LinuxCompiler = new[] { "usr/lib/longshot/emsdk", "usr/lib/longshot/sysroot", "usr/share/longshot/cpm" };
    const string Part = ".part";

    public static bool IsCache(string name)
    {
        if (Names.Contains(name)) return true;
        if (!name.StartsWith(PlayPrefix, StringComparison.Ordinal) || !name.EndsWith(PlaySuffix, StringComparison.Ordinal)) return false;
        var n = name[PlayPrefix.Length..^PlaySuffix.Length];
        return n.Length > 0 && n.All(char.IsAsciiDigit);
    }

    /// What a version's tools translate shaders with: the stamp nx2/translator_files.py makes of the translator's sources below
    /// tools/gpu/vk (its 16 hex digits, as nx2/build.py compiles them into the renderer), then a hash of that file itself, so that
    /// two versions whose recipes differ never compare equal by this one. Null when the tools have no such file or sources
    public static string? Translator(string toolsRoot)
    {
        try
        {
            var vk = Path.Combine(toolsRoot, "gpu", "vk");
            var recipe = Path.Combine(toolsRoot, "nx2", Recipe);
            if (!File.Exists(recipe)) return null;
            // (os.walk's files sorted as Python sorts their paths, by code point with the system's separator, then the three
            // files in their own order)
            var found = new List<string>();
            foreach (var top in TranslatorDirs)
            {
                var dir = Path.Combine(vk, top);
                if (Directory.Exists(dir)) found.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
            }
            found.Sort(StringComparer.Ordinal);
            found.AddRange(TranslatorFiles.Select(n => Path.Combine(vk, n)));
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            foreach (var f in found)
            {
                sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(vk, f).Replace('\\', '/')));
                sha.AppendData(UnixLines(File.ReadAllBytes(f)));
            }
            var stamp = Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
            sha.AppendData(UnixLines(File.ReadAllBytes(recipe)));
            return stamp + "-" + Convert.ToHexStringLower(sha.GetHashAndReset())[..16];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// What a version's tools compile the translator with: a hash of the SHA-256 of each of its compiler's tools.lock parts
    /// (WindowsCompiler for the Windows builder's tools, LinuxCompiler for the Linux one's), as the version's record names
    /// them (Toolchains.PartShas). Null when the record cannot be read or lacks one of them (a Linux builder that carried its
    /// tools itself): such a version counts as another
    public static string? Compiler(Toolchain tools)
    {
        if (Toolchains.PartShas(tools.Folder) is not { } shas) return null;
        var text = new StringBuilder();
        foreach (var part in tools.Kind == ListKind.Setup ? WindowsCompiler : LinuxCompiler)
        {
            if (!shas.TryGetValue(part, out var sha)) return null;
            text.Append(part).Append(' ').Append(sha).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// What two versions must share for one's caches to serve the other: the translator (Translator) and what compiles it
    /// (Compiler). Null when either cannot be read
    public static string? Stamp(Toolchain tools) =>
        Translator(tools.ToolsRoot) is { } translator && Compiler(tools) is { } compiler ? translator + "-" + compiler : null;

    /// Windows line ends as Unix ones, as the stamp reads its files (bytes.replace(b"\r\n", b"\n"))
    static byte[] UnixLines(byte[] bytes)
    {
        var at = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n') continue;
            bytes[at++] = bytes[i];
        }
        return at == bytes.Length ? bytes : bytes[..at];
    }

    /// A game folder's caches: its cache/ files the game reads back, with something in them; never a link
    static List<FileInfo> Caches(string game)
    {
        var dir = new DirectoryInfo(Path.Combine(game, CacheDir));
        if (!dir.Exists || dir.LinkTarget != null) return new List<FileInfo>();
        return dir.EnumerateFiles().Where(f => IsCache(f.Name) && f.LinkTarget == null && f.Length > 0)
                  .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// The shader caches clearing a game folder lets go (Free up space, a cold build): its cache/ files the game reads back
    /// (IsCache), and what a copy into it left half made (.part); never a link, nor anything else there. Not the pipeline
    /// notes unless notes: they hold the list the build shipped (make_game.py seed_pipelines), which only a build writes
    /// again, and without it the game prepares less ahead until then. A cold build clears them too: it writes them again
    public static IReadOnlyList<FileInfo> Clearable(string game, bool notes = false)
    {
        var dir = new DirectoryInfo(Path.Combine(game, CacheDir));
        try
        {
            if (!dir.Exists || dir.LinkTarget != null) return Array.Empty<FileInfo>();
            return dir.EnumerateFiles()
                      .Where(f => f.LinkTarget == null && (IsCache(f.Name) || f.Name.EndsWith(Part, StringComparison.Ordinal) && IsCache(f.Name[..^Part.Length]))
                                  && (notes || f.Name != Notes))
                      .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return Array.Empty<FileInfo>(); }
    }

    /// The bytes Clearable would let go in game
    public static long ClearableBytes(string game, bool notes = false) => Clearable(game, notes).Sum(f => f.Length);

    /// Whether Longshot runs: it holds its caches and writes them as it plays
    static bool LongshotRunning() => Saves.Running(Platforms.Current).Any(n => n.Contains(Saves.Longshot.Display, StringComparison.Ordinal));

    /// game's shader caches deleted (Clearable): the game prepares them again when it next starts. Null, and nothing deleted,
    /// while Longshot runs (running: the tests' stand-in); else the bytes freed. A file that cannot be deleted is the caller's
    /// exception, the ones before it gone
    public static long? Clear(string game, bool notes = false, Func<bool>? running = null)
    {
        if ((running ?? LongshotRunning)()) return null;
        long freed = 0;
        foreach (var f in Clearable(game, notes))
        {
            var size = f.Length;
            f.Delete();
            freed += size;
        }
        return freed;
    }

    /// The caches of the game folder from, taken into the game folder to, when to has none and both versions' tools (fromTools,
    /// toTools; null when a version's tools are not on this PC) translate shaders the same way, with the same compiler (Stamp).
    /// Moved when both folders are on one drive: once the update is done its old folder is no longer listed, so its caches
    /// have no other user (an update that does not finish puts them back: Undo), and a move is instant and needs no room
    /// (link: FileLinks.TryHardLink, which only one drive allows; each file gets its name in the new folder, then loses its
    /// old one), all but the pipeline notes, which are copied (Notes). Copied otherwise, with room for the build beside the
    /// copy (Room), the old folder's left as they are. Either way each file is made beside its place and put there only once
    /// all of them are, and one that cannot take its place takes the others away again, so one that stops (cancel, a full
    /// disk, a rename refused: the exception is the caller's) leaves no cache half made, here or there. progress: the bytes
    /// done so far, and all of them
    public static CachesResult Keep(string from, string to, Toolchain? fromTools, Toolchain? toTools, Func<string, long?>? free = null,
                                    Func<bool>? running = null, Action<long, long>? progress = null, CancellationToken cancel = default,
                                    Func<string, string, bool>? link = null)
    {
        var same = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(to)), same))
            return new CachesResult(CachesKept.NoCaches);
        var files = Caches(from);
        if (!files.Any(f => Programs.Contains(f.Name))) return new CachesResult(CachesKept.NoCaches);
        if (Caches(to).Count > 0) return new CachesResult(CachesKept.HasCaches);
        if (fromTools == null || toTools == null || Stamp(fromTools) is not { } old || Stamp(toTools) != old)
            return new CachesResult(CachesKept.OtherTranslator);
        if ((running ?? LongshotRunning)()) return new CachesResult(CachesKept.Running);
        var bytes = files.Sum(f => f.Length);
        var target = Path.Combine(to, CacheDir);
        string PartOf(FileInfo f) => Path.Combine(target, f.Name + Part);

        Directory.CreateDirectory(target);
        var parts = new List<string>();
        try
        {
            // (moved: each file's second name made in the new folder, which only a folder on the same drive can have; all of
            // them or none, so the old build never keeps half of its caches)
            link ??= FileLinks.TryHardLink;
            var moved = true;
            foreach (var f in files)
            {
                var part = PartOf(f);
                File.Delete(part);   // (one a launcher that was closed mid-copy left)
                if (f.Name == Notes) File.Copy(f.FullName, part);   // (the old folder keeps its own: Notes)
                else if (!link(f.FullName, part)) { moved = false; break; }
                parts.Add(part);
            }
            if (!moved)
            {
                foreach (var part in parts) File.Delete(part);
                parts.Clear();
                if ((free ?? Disk.FreeBytes)(target) is long room && room < Room(bytes)) return new CachesResult(CachesKept.NoSpace, 0, bytes);
                long done = 0;
                var buffer = new byte[1 << 20];
                foreach (var f in files)
                {
                    var part = PartOf(f);
                    parts.Add(part);
                    using var input = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                    using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1);
                    for (int n; (n = input.Read(buffer, 0, buffer.Length)) > 0;)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, n);
                        progress?.Invoke(done += n, bytes);
                    }
                }
            }
            cancel.ThrowIfCancellationRequested();
            // (all of them in place or none: one that cannot take its place takes those placed before it away again, so the
            // new folder never has programs.bin without its .idx. After a move the old names are still there until all are)
            var placed = new List<string>();
            try
            {
                foreach (var part in parts)
                {
                    var name = part[..^Part.Length];
                    File.Move(part, name, overwrite: true);
                    placed.Add(name);
                }
            }
            catch
            {
                foreach (var name in placed)
                {
                    try { File.Delete(name); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
            parts.Clear();
            if (moved)
            {
                // (the new folder has them all: the old names go. One that stays, a file something holds open, is the same
                // file under two names, which two builds of one translator can both read)
                foreach (var f in files.Where(c => c.Name != Notes))
                {
                    try { f.Delete(); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                progress?.Invoke(bytes, bytes);
            }
            return new CachesResult(CachesKept.Kept, files.Count, bytes, moved, files.Select(f => f.Name).ToList());
        }
        finally
        {
            foreach (var part in parts)
            {
                try { File.Delete(part); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// What Keep took from the game folder from into to put back, for a build that did not finish (it failed, or was
    /// stopped), so the build that is still listed keeps its caches: moved ones go back under their old names (one the old
    /// folder still has, a file something held open at the move, only loses its new name), copies are deleted. A file that
    /// cannot be put back is tried again for a moment (wait, UndoWait by default, for all of them together): a cancel kills
    /// the build's whole process tree but waits only for make_game.py itself (MakeGameRun.RunAsync), so a compiler or the
    /// game it started may still hold one for a moment, and Windows refuses to move or delete a file that is open. One that
    /// still cannot stays where it is. It sleeps between tries: never on the window's thread. Returns how many were put back
    /// or deleted
    public static int Undo(string from, string to, CachesResult kept, TimeSpan? wait = null)
    {
        if (kept.What != CachesKept.Kept || kept.Taken is not { } names) return 0;
        var back = Path.Combine(from, CacheDir);
        var here = Path.Combine(to, CacheDir);
        var budget = wait ?? UndoWait;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var done = 0;
        foreach (var name in names)
        {
            var file = Path.Combine(here, name);
            for (;;)
            {
                try
                {
                    if (!File.Exists(file)) break;
                    var old = Path.Combine(back, name);
                    if (kept.Moved && !File.Exists(old))
                    {
                        Directory.CreateDirectory(back);
                        File.Move(file, old);   // (the same drive: a rename)
                    }
                    else File.Delete(file);
                    done++;
                    break;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && clock.Elapsed < budget)
                {
                    Thread.Sleep(UndoStep);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { break; }
            }
        }
        return done;
    }

    /// How long Undo tries again, in all, for the files it could not put back at once, and how long it waits between tries
    public static readonly TimeSpan UndoWait = TimeSpan.FromSeconds(2);
    static readonly TimeSpan UndoStep = TimeSpan.FromMilliseconds(100);
}
