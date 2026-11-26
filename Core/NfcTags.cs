using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LongshotLauncher.Core;

/// A file of the NFC folder: its name as the game shows it (the file name without its ending), its ending (.bin or .nfc), and,
/// when it is an NFC tag dump, the figure's model (16 hex digits) and the tag's UID (14); else why it is not one
public sealed record NfcTagFile(string Path, string Name, string Format, string? Model, string? Uid, string? Problem)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public bool Valid => Problem == null;
}

/// Added: copied in under its own name; Numbered: a different file of that name was there, so under "Name (2).bin"; Same: the same
/// file is there already, nothing copied; Refused: not an NFC tag dump (Reason says why), nothing copied
public enum NfcTagOutcome { Added, Numbered, Same, Refused }

/// What became of one file handed to NfcTags.Add: Source, the file; FileName, its name in the NFC folder (null when refused)
public sealed record NfcTagAdded(string Source, NfcTagOutcome Outcome, string? FileName, string? Reason)
{
    public bool Copied => Outcome is NfcTagOutcome.Added or NfcTagOutcome.Numbered;
}

/// The NFC folder the game reads its NFC tab from, shared by every build of the game and by the launcher: LONGSHOT_NFC_DIR,
/// else %LOCALAPPDATA%\Longshot\nfc on Windows and ${XDG_DATA_HOME:-~/.local/share}/longshot/nfc on Linux. Each NFC tag is one
/// file in it (no subfolders), .bin (a raw NTAG215 dump) or .nfc (a Flipper Zero NFC file); the game ignores key_* files and so
/// does this. Listed, checked, added (copied, never moved; never over another file) and deleted here. Nothing of a dump leaves the
/// PC or goes to a log: a message names the file and why, never its bytes.
public static class NfcTags
{
    public const string Variable = "LONGSHOT_NFC_DIR";
    /// The game lists this many of the folder's .bin and .nfc files at most (key_ files aside), dumps or not. As Longshot's
    /// host/nn_nfp.c reads the folder today, it stops at this many while the folder is still being read and only then sorts them
    /// by name, so past it the ones it keeps are whichever the folder gave first, not the first by name
    public const int Most = 256;
    /// An NTAG215's 135 pages of 4 bytes: what a dump is read into, whatever its format
    public const int TagBytes = 540;
    /// A .nfc file is text, 135 short lines and a header: one larger than this is not read
    const int LargestNfc = 1 << 20;
    const int Pages = TagBytes / 4, FewestPages = 130;

    /// gameFolder: the build's folder, where the game is started (GamePlay.StartInfo). The game takes LONGSHOT_NFC_DIR as it is
    /// given, so a relative one is read from there: it is resolved against gameFolder here too, and, with no build to take it from,
    /// left aside for the usual folder
    public static string Folder(Platform platform, Func<string, string?>? getenv = null, string? gameFolder = null)
    {
        getenv ??= Environment.GetEnvironmentVariable;
        if (getenv(Variable) is { Length: > 0 } set)
        {
            if (Path.IsPathFullyQualified(set)) return Path.GetFullPath(set);
            if (gameFolder is { Length: > 0 }) return Path.GetFullPath(set, Path.GetFullPath(gameFolder));
        }
        if (platform == Platform.Windows) return Path.GetFullPath(Path.Combine(LauncherLayout.LocalAppData(getenv), "Longshot", "nfc"));
        var data = getenv("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(LauncherHome.Home(getenv), ".local", "share");
        return Path.GetFullPath(Path.Combine(data, "longshot", "nfc"));
    }

    /// ".bin" or ".nfc" (whatever the case of the file's), or null: not an NFC tag's file name
    public static string? Format(string fileName)
    {
        var ending = Path.GetExtension(fileName).ToLowerInvariant();
        return ending is ".bin" or ".nfc" ? ending : null;
    }

    /// key_* (the keys some tools keep beside the dumps): never an NFC tag
    public static bool IsKeyFile(string fileName) => Path.GetFileName(fileName).StartsWith("key_", StringComparison.OrdinalIgnoreCase);

    /// A file the folder's list takes: a .bin or .nfc that is not a key file
    public static bool IsCandidate(string fileName) => Format(fileName) != null && !IsKeyFile(fileName);

    /// The 540 bytes of the tag in a .bin's bytes: the file is 532, 540 or 572 bytes, the tag its first 540
    public static byte[]? TagOfBin(byte[] file, out string? problem)
    {
        problem = null;
        if (file.Length is not (532 or 540 or 572))
        {
            problem = Tr.F("it is {0} bytes, and a .bin dump is 532, 540 or 572", file.Length);
            return null;
        }
        var tag = new byte[TagBytes];
        file.AsSpan(0, Math.Min(file.Length, TagBytes)).CopyTo(tag);
        return tag;
    }

    /// The 540 bytes of the tag in a Flipper Zero NFC file: it starts with "Filetype:", and its "Page N: xx xx xx xx" lines (N in
    /// decimal, four bytes in hex) give page N, at N*4; at least 130 of the 135 pages are there
    public static byte[]? TagOfNfc(byte[] file, out string? problem)
    {
        problem = null;
        var text = Encoding.Latin1.GetString(file);
        if (!text.StartsWith("Filetype:", StringComparison.Ordinal))
        {
            problem = Tr.T("it is not a Flipper NFC file (it does not start with Filetype:)");
            return null;
        }
        var tag = new byte[TagBytes];
        var have = new bool[Pages];
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Page ", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var number = line[5..colon];
            if (number.Length is 0 or > 4 || !number.All(char.IsAsciiDigit)) continue;
            var page = int.Parse(number, CultureInfo.InvariantCulture);
            var bytes = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (page >= Pages || bytes.Length != 4 || !bytes.All(b => b.Length == 2 && b.All(char.IsAsciiHexDigit))) continue;
            for (var i = 0; i < 4; i++) tag[page * 4 + i] = byte.Parse(bytes[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            have[page] = true;
        }
        var pages = have.Count(h => h);
        if (pages >= FewestPages) return tag;
        problem = pages == 0 ? Tr.T("it has no pages") : Tr.F("it has {0} of the tag's {1} pages, and a dump needs {2}", pages, Pages, FewestPages);
        return null;
    }

    /// Why a tag is not a figure's, or null: an NTAG215's capability container (F1 10 FF EE at 0x0C) and the figure's 0x02 at 0x5B
    public static string? Check(byte[] tag)
    {
        if (tag.Length != TagBytes) return Tr.T("it is not an NTAG215 dump");
        if (!tag.AsSpan(0x0C, 4).SequenceEqual(new byte[] { 0xF1, 0x10, 0xFF, 0xEE })) return Tr.T("it is not an NTAG215 tag");
        return tag[0x5B] != 0x02 ? Tr.T("the tag has no figure on it") : null;
    }

    /// The figure's model: the 8 bytes at 0x54, as 16 hex digits
    public static string Model(byte[] tag) => Convert.ToHexString(tag, 0x54, 8);

    /// The tag's UID: its bytes 0, 1, 2 and 4 to 7 (3 is a check byte), as 14 hex digits
    public static string Uid(byte[] tag) => Convert.ToHexString(tag, 0, 3) + Convert.ToHexString(tag, 4, 4);

    /// A file's bytes read as an NFC tag dump: its tag, or why it is not one (by its ending, .bin or .nfc)
    public static byte[]? Read(string fileName, byte[] file, out string? problem)
    {
        var format = Format(fileName);
        if (format == null)
        {
            problem = Tr.T("it is not a .bin or .nfc file");
            return null;
        }
        if (IsKeyFile(fileName))
        {
            problem = Tr.T("it is a key file (key_…), not an NFC tag");
            return null;
        }
        var tag = format == ".bin" ? TagOfBin(file, out problem) : TagOfNfc(file, out problem);
        if (tag == null) return null;
        problem = Check(tag);
        return problem == null ? tag : null;
    }

    /// A file's bytes, unless it is larger than any dump is: never more than one byte past the largest is read. The size is that of
    /// what is read, not FileInfo.Length, which for a link to a file is the link's own (a symlink's on Linux, 0 on Windows)
    static byte[]? Bytes(string path, out string? problem)
    {
        problem = null;
        var bin = Format(path) == ".bin";
        var file = ReadAtMost(path, bin ? 572 : LargestNfc);
        if (bin && file.Length is not (532 or 540 or 572))
            problem = file.Length > 572 ? Tr.T("it is more than 572 bytes, and a .bin dump is 532, 540 or 572")
                : Tr.F("it is {0} bytes, and a .bin dump is 532, 540 or 572", file.Length);
        else if (file.Length > LargestNfc) problem = Tr.T("it is too large to be a Flipper NFC file");
        return problem == null ? file : null;
    }

    /// The file's first most + 1 bytes at most (through a link, to the file it leads to): more than most when it is larger.
    /// Nothing is read from what has no length where the link ends: opening a FIFO blocks until something writes to it, and a
    /// device or an empty file is no dump either, so those give no bytes without being opened
    static byte[] ReadAtMost(string path, int most)
    {
        var info = new FileInfo(path);
        var target = info.LinkTarget == null ? info : info.ResolveLinkTarget(true) as FileInfo;
        if (target is not { Exists: true }) throw new FileNotFoundException("the file it leads to is not there", path);
        if (target.Length == 0) return Array.Empty<byte>();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while (read.Length <= most && (n = f.Read(chunk, 0, (int)Math.Min(chunk.Length, most + 1 - read.Length))) > 0) read.Write(chunk, 0, n);
        return read.ToArray();
    }

    /// A file on the disk read as an NFC tag dump: its bytes and its tag, or why it is not one
    static byte[]? Load(string path, out byte[]? tag, out string? problem)
    {
        var fileName = Path.GetFileName(path);
        tag = null;
        if (!IsCandidate(fileName))
        {
            Read(fileName, Array.Empty<byte>(), out problem);
            return null;
        }
        if (Bytes(path, out problem) is not { } file) return null;
        tag = Read(fileName, file, out problem);
        return tag == null ? null : file;
    }

    /// One file as the list shows it
    public static NfcTagFile Describe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var format = Format(path) ?? Path.GetExtension(path).ToLowerInvariant();
        string? problem;
        try
        {
            if (Load(path, out var tag, out problem) != null) return new NfcTagFile(path, name, format, Model(tag!), Uid(tag!), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problem = Tr.F("it could not be read: {0}", e.Message); }
        return new NfcTagFile(path, name, format, null, null, problem);
    }

    /// The .bin and .nfc files directly in a folder (no subfolders, no key_ files), in the order of their names
    public static IReadOnlyList<string> FilesIn(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        return Directory.EnumerateFiles(folder).Where(IsCandidate)
            .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Path.GetFileName(p), StringComparer.Ordinal).ToList();
    }

    /// A folder's .bin and .nfc files and those of every folder under it (a collection sorted into folders: adding it takes them
    /// all), in the order of their paths; what cannot be read is passed over. A link to a folder (a symlink, or a junction on
    /// Windows) is not gone into: one can loop back up (a Wine or Proton prefix's dosdevices/z: is /), and the walk would never
    /// end. A link to a file is taken, as the file. (Not RecurseSubdirectories, which follows links to folders on Linux; and not
    /// every reparse point either: OneDrive's folders that are not downloaded yet are reparse points that are no link)
    public static IReadOnlyList<string> FilesUnder(string folder)
    {
        if (!Directory.Exists(folder)) return Array.Empty<string>();
        var here = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var found = new List<string>();
        var left = new Stack<DirectoryInfo>();
        left.Push(new DirectoryInfo(folder));
        while (left.Count > 0)
        {
            var dir = left.Pop();
            try
            {
                found.AddRange(dir.EnumerateFiles("*", here).Select(f => f.FullName).Where(IsCandidate));
                foreach (var sub in dir.EnumerateDirectories("*", here))
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint) || sub.LinkTarget == null) left.Push(sub);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// The NFC folder's files, by name: the dumps, and the .bin and .nfc files that are not dumps (so that they can be deleted)
    public static IReadOnlyList<NfcTagFile> List(string folder) => FilesIn(folder).Select(Describe).ToList();

    /// Copies files into the NFC folder (made when it is not there), each checked first and under its own file name. A file
    /// that is not an NFC tag dump is refused; one that is there already, byte for byte, is left; a different one of the same name
    /// is never replaced: the new one goes in as "Name (2).bin", "Name (3).bin"… Each is written whole or not at all (AtomicFile)
    public static IReadOnlyList<NfcTagAdded> Add(string folder, IEnumerable<string> sources)
    {
        var done = new List<NfcTagAdded>();
        foreach (var source in sources)
        {
            try
            {
                var fileName = Path.GetFileName(source);
                if (Load(source, out _, out var problem) is not { } file)
                {
                    done.Add(new NfcTagAdded(source, NfcTagOutcome.Refused, null, problem));
                    continue;
                }
                var (outcome, name) = Place(folder, fileName, file);
                done.Add(new NfcTagAdded(source, outcome, name, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                done.Add(new NfcTagAdded(source, NfcTagOutcome.Refused, null, Tr.F("it could not be copied: {0}", e.Message)));
            }
        }
        return done;
    }

    /// A dump that is no file yet (one just scanned: NfcReader), added as a file of that name is: checked first, never over another
    public static NfcTagAdded AddBytes(string folder, string fileName, byte[] file)
    {
        if (fileName != Path.GetFileName(fileName)) return new NfcTagAdded(fileName, NfcTagOutcome.Refused, null, Tr.T("its name is not a file name"));
        if (Read(fileName, file, out var problem) == null) return new NfcTagAdded(fileName, NfcTagOutcome.Refused, null, problem);
        try
        {
            var (outcome, name) = Place(folder, fileName, file);
            return new NfcTagAdded(fileName, outcome, name, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new NfcTagAdded(fileName, NfcTagOutcome.Refused, null, Tr.F("it could not be saved: {0}", e.Message));
        }
    }

    /// A checked dump into the folder under its name, or the next free numbered one; left when the same file is there
    static (NfcTagOutcome Outcome, string Name) Place(string folder, string fileName, byte[] file)
    {
        var (stem, ending) = (Path.GetFileNameWithoutExtension(fileName), Path.GetExtension(fileName));
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? fileName : $"{stem} ({n}){ending}";
            var target = Path.Combine(folder, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (Same(target, file)) return (NfcTagOutcome.Same, name);
                continue;
            }
            // (the move never replaces a file: one that took this name since the check above, a scan's saved while an add runs,
            // is kept, and this one goes on to the next number)
            if (AtomicFile.WriteNew(target, file)) return (n == 1 ? NfcTagOutcome.Added : NfcTagOutcome.Numbered, name);
            if (Same(target, file)) return (NfcTagOutcome.Same, name);
        }
    }

    static bool Same(string path, byte[] bytes) =>
        File.Exists(path) && ReadAtMost(path, bytes.Length).AsSpan().SequenceEqual(bytes);

    /// Keep the original file open so its identity cannot be reused while the question is open.
    public sealed class DeletionSnapshot(FileStream stream, byte[] bytes) : IDisposable
    {
        internal FileStream Stream { get; } = stream;
        internal byte[] Bytes { get; } = bytes;
        public void Dispose() => Stream.Dispose();
    }

    /// Capture a listed file for a later delete. Limit even invalid files, and leave unreadable or oversized files alone.
    public static DeletionSnapshot? DeleteSnapshot(string folder, string name)
    {
        if (name != Path.GetFileName(name) || !IsCandidate(name)) return null;
        try
        {
            var stream = OpenSnapshotFile(Path.Combine(folder, name));
            if (stream == null) return null;
            try
            {
                if (stream.Length > LargestNfc) { stream.Dispose(); return null; }
                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                return new DeletionSnapshot(stream, bytes);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle OpenForDelete(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct FileAttributeTagInfo { public uint Attributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdInfo info, uint size);

    // statx has a fixed layout on Linux, including on 32-bit and non-x86 systems.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int statx(int fd, string path, int flags, uint mask, out Statx stat);

    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags);

    static bool LinuxIdentity(SafeFileHandle handle, out Statx stat) =>
        statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x103, out stat) == 0 &&
        (stat.Mask & 0x103) == 0x103 && (stat.Mode & 0xf000) == 0x8000;

    static FileStream? OpenSnapshotFile(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            // O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC: do not follow links or block on a FIFO.
            var fd = open(path, 0x800 | 0x20000 | 0x80000);
            if (fd < 0) return null;
            handle = new SafeFileHandle((IntPtr)fd, true);
            try { if (!LinuxIdentity(handle, out _)) { handle.Dispose(); return null; } }
            catch { handle.Dispose(); throw; }
        }
        else if (OperatingSystem.IsWindows())
        {
            // Allow replacement while the question is open, but do not follow reparse points.
            handle = OpenForDelete(path, 0x80000000, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo info, 8) ||
                (info.Attributes & (0x400 | 0x10)) != 0) { handle.Dispose(); return null; }
        }
        else return null;
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    static bool SameIdentity(SafeFileHandle first, SafeFileHandle second)
    {
        if (OperatingSystem.IsLinux())
            return LinuxIdentity(first, out var a) && LinuxIdentity(second, out var b) &&
                a.Inode == b.Inode && a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
        return GetFileInformationByHandleEx(first, 18, out FileIdInfo x, 24) &&
            GetFileInformationByHandleEx(second, 18, out FileIdInfo y, 24) &&
            x.Volume == y.Volume && x.Low == y.Low && x.High == y.High;
    }

    static bool SameContents(FileStream stream, byte[] bytes)
    {
        if (stream.Length != bytes.Length) return false;
        stream.Position = 0;
        var current = new byte[bytes.Length];
        stream.ReadExactly(current);
        return current.AsSpan().SequenceEqual(bytes);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref int info, uint size);

    [DllImport("libc", SetLastError = true)]
    static extern int mkdir(string path, uint mode);

    // Isolate the entry before checking it, so a replacement at the original name cannot be deleted. A writer with an
    // already open handle can still change it after the check; the confirmation warns about that on Linux.
    static bool DeleteSnapshotLinux(string folder, string name, DeletionSnapshot snapshot, out string? recovery)
    {
        recovery = null;
        var path = Path.Combine(folder, name);
        var quarantine = Path.Combine(folder, ".nfc-delete-" + Guid.NewGuid().ToString("N"));
        if (mkdir(quarantine, 448) != 0) return false; // 0700
        var held = Path.Combine(quarantine, name);
        var moved = false;
        try
        {
            File.Move(path, held);
            moved = true;
            using var stream = OpenSnapshotFile(held);
            if (stream == null || !SameIdentity(snapshot.Stream.SafeFileHandle, stream.SafeFileHandle) || !SameContents(stream, snapshot.Bytes)) return false;
            File.Delete(held);
            moved = false;
            return true;
        }
        finally
        {
            if (moved)
            {
                try { File.Move(held, path); moved = false; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                for (var n = 2; moved && n <= 1000; n++)
                {
                    var alternative = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} (recovered {n}){Path.GetExtension(name)}");
                    try { File.Move(held, alternative); moved = false; recovery = alternative; }
                    catch (IOException) { if (!File.Exists(alternative) && !Directory.Exists(alternative)) break; }
                    catch (UnauthorizedAccessException) { break; }
                }
                if (moved) recovery = held;
            }
            if (!moved)
                try { Directory.Delete(quarantine); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// On Windows, denying write and delete sharing prevents changes and replacement until the handle is closed.
    /// On Linux, isolate the entry before checking it; the player confirms the remaining concurrent-write risk.
    public static bool DeleteSnapshot(string folder, string name, DeletionSnapshot? snapshot, out string? recovery)
    {
        recovery = null;
        if (snapshot == null || name != Path.GetFileName(name) || !IsCandidate(name)) return false;
        try
        {
            if (OperatingSystem.IsLinux()) return DeleteSnapshotLinux(folder, name, snapshot, out recovery);
            if (!OperatingSystem.IsWindows()) return false;
            var path = Path.Combine(folder, name);
            // GENERIC_READ | DELETE, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT.
            using var handle = OpenForDelete(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInfo info, 8) || (info.Attributes & 0x400) != 0 ||
                !SameIdentity(snapshot.Stream.SafeFileHandle, handle)) return false;
            using var stream = new FileStream(handle, FileAccess.Read);
            if (!SameContents(stream, snapshot.Bytes)) return false;
            var delete = 1; // FILE_DISPOSITION_INFO.DeleteFile
            return SetFileInformationByHandle(handle, 4, ref delete, 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// Deletes files of the NFC folder by their file names: only .bin and .nfc files directly in it, never a key file, a folder
    /// or anything outside it. The names of the ones deleted
    public static IReadOnlyList<string> Delete(string folder, IEnumerable<string> fileNames)
    {
        var gone = new List<string>();
        foreach (var name in fileNames)
        {
            if (name != Path.GetFileName(name) || !IsCandidate(name)) continue;
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            gone.Add(name);
        }
        return gone;
    }
}
