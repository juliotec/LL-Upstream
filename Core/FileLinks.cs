using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LongshotLauncher.Core;

/// A file's identity on its disk: the device and the inode on Linux, the volume's serial and the file's id on Windows. Two
/// paths with one identity are hard links of one file (an older launcher's builds' game files linked from one another's)
readonly record struct FileId(ulong Volume, ulong File);

/// Hard links and junctions, which .NET has no call for: a part kept from another installed version takes no second copy on the
/// same disk, and a game folder's user\save can be the shared save (Saves.LinkSaves)
static class FileLinks
{
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    static extern int UnixLink([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsLink(string to, string from, IntPtr security);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int UnixStatx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] statx);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsFileInformation(SafeFileHandle file, out FileInformation information);

    /// BY_HANDLE_FILE_INFORMATION (its FILETIMEs as two DWORDs each, as Windows lays them out)
    [StructLayout(LayoutKind.Sequential)] struct FileInformation
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    /// A hard link at to of the file from; false when the disk or the system does not make one (the caller copies)
    public static bool TryHardLink(string from, string to)
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsLink(to, from, IntPtr.Zero) : UnixLink(from, to) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// A copy of the file from at to: a hard link when it can be, else its bytes (and its mode, on Linux)
    public static void LinkOrCopy(string from, string to)
    {
        if (TryHardLink(from, to)) return;
        File.Copy(from, to, overwrite: false);
    }

    /// Whether path and other are one file, hard-linked: more than one link, and the same file on the same disk (Linux: the
    /// device and the inode, statx; Windows: the volume's serial and the file index). A copy of the same bytes is not, nor is what
    /// cannot be told: false then, so a file is counted twice rather than not at all
    public static bool SameFile(string path, string other)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var a = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var b = File.OpenHandle(other, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return WindowsFileInformation(a, out var x) && WindowsFileInformation(b, out var y) && x.Links > 1
                       && x.VolumeSerial == y.VolumeSerial && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
            }
            return UnixIdentity(path) is { } i && UnixIdentity(other) is { } j && i.Links > 1 && i.Device == j.Device && i.Inode == j.Inode;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// statx(2)'s link count, device and inode (struct statx is laid out the same on every architecture: stx_nlink at 16,
    /// stx_ino at 32, stx_dev_major and stx_dev_minor at 136 and 140); null when it fails or does not give them
    static (uint Links, ulong Device, ulong Inode)? UnixIdentity(string path)
    {
        const int AtFdCwd = -100;
        const uint StatxNlink = 0x4, StatxIno = 0x100;
        var buffer = new byte[256];
        if (UnixStatx(AtFdCwd, path, 0, StatxNlink | StatxIno, buffer) != 0) return null;
        if ((BitConverter.ToUInt32(buffer, 0) & (StatxNlink | StatxIno)) != (StatxNlink | StatxIno)) return null;
        var device = ((ulong)BitConverter.ToUInt32(buffer, 136) << 32) | BitConverter.ToUInt32(buffer, 140);
        return (BitConverter.ToUInt32(buffer, 16), device, BitConverter.ToUInt64(buffer, 32));
    }

    // ---- a file's identity and its links (Disk.Measure) ----

    // (statx(2), glibc 2.28, UnixStatx above: its struct statx is laid out the same on every architecture, unlike struct stat)
    const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, AtStatxDontSync = 0x4000;
    const uint StatxNlink = 0x4, StatxIno = 0x100, StatxSize = 0x200;
    static volatile bool noStatx;
    [ThreadStatic] static byte[]? statxBuffer;

    /// On Linux, the file at path (not followed when it is a link) by one statx, the call FileInfo.Length makes too: its bytes,
    /// its identity and its hard links. False elsewhere, or when the system or the disk does not say (the caller counts the file
    /// as its own)
    public static bool TryUnixFile(string path, out long size, out FileId id, out uint links)
    {
        size = 0;
        id = default;
        links = 0;
        if (noStatx || !OperatingSystem.IsLinux()) return false;
        var b = statxBuffer ??= new byte[256];
        try
        {
            if (UnixStatx(AtFdCwd, path, AtSymlinkNoFollow | AtStatxDontSync, StatxNlink | StatxIno | StatxSize, b) != 0) return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            noStatx = true;
            return false;
        }
        var got = Read<uint>(b, 0);
        if ((got & (StatxNlink | StatxIno | StatxSize)) != (StatxNlink | StatxIno | StatxSize)) return false;
        // (struct statx: stx_nlink at 16, stx_ino at 32, stx_size at 40, stx_dev_major and stx_dev_minor at 136 and 140)
        links = Read<uint>(b, 16);
        size = (long)Read<ulong>(b, 40);
        id = new FileId(((ulong)Read<uint>(b, 136) << 32) | Read<uint>(b, 140), Read<ulong>(b, 32));
        return true;
    }

    /// An entry of a folder's listing on Windows: its name, its attributes, its bytes, its file id (0: the disk has none) and, for a
    /// reparse point, its tag
    public readonly record struct Listed(string Name, uint Attributes, long Size, ulong Id, uint Tag);

    public const uint AttributeDirectory = 0x10, AttributeReparsePoint = 0x400;
    /// The reparse tags of a symbolic link and a junction: .NET's LinkTarget, never followed
    public const uint TagSymlink = 0xA000000C, TagMountPoint = 0xA0000003;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle WindowsOpen(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsFileInfo(SafeFileHandle file, [Out] byte[] info);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsFileInfoEx(SafeFileHandle file, int infoClass, [Out] byte[] info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize,
                                      out uint returned, IntPtr overlapped);

    const uint FileListDirectory = 0x1, FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;
    const int IdBothDirectoryInfo = 10, IdBothDirectoryRestartInfo = 11, ErrorNoMoreFiles = 18;
    [ThreadStatic] static byte[]? listBuffer;

    /// On Windows, folder's entries with each file's id, from the folder's own listing (FILE_ID_BOTH_DIR_INFO: one open for the
    /// folder, none for each file, where an open of each of a romfs's ~186,000 files takes tens of seconds), and the serial of
    /// the volume they are on. The listing says no link count. False elsewhere, or when the folder cannot be listed so (into is
    /// then empty, and the caller lists it as .NET does)
    public static bool TryWindowsListing(string folder, List<Listed> into, out ulong volume)
    {
        volume = 0;
        into.Clear();
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var handle = WindowsOpen(LongPath(Path.GetFullPath(folder)), FileListDirectory | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                                           BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            // (BY_HANDLE_FILE_INFORMATION, which FILE_READ_ATTRIBUTES lets the handle read: dwVolumeSerialNumber at 28)
            var info = new byte[52];
            if (!WindowsFileInfo(handle, info)) return false;
            volume = Read<uint>(info, 28);
            var b = listBuffer ??= new byte[64 * 1024];
            for (var kind = IdBothDirectoryRestartInfo; WindowsFileInfoEx(handle, kind, b, (uint)b.Length); kind = IdBothDirectoryInfo)
            {
                // (FILE_ID_BOTH_DIR_INFO: NextEntryOffset at 0, EndOfFile at 40, FileAttributes at 56, FileNameLength at 60, EaSize,
                // a reparse point's tag, at 64, FileId at 96, FileName at 104)
                for (var at = 0; ; )
                {
                    var next = Read<int>(b, at);
                    var name = Encoding.Unicode.GetString(b, at + 104, Read<int>(b, at + 60));
                    if (name is not "." and not "..")
                        into.Add(new Listed(name, Read<uint>(b, at + 56), Read<long>(b, at + 40), Read<ulong>(b, at + 96), Read<uint>(b, at + 64)));
                    if (next <= 0) break;
                    at += next;
                }
            }
            if (Marshal.GetLastPInvokeError() == ErrorNoMoreFiles) return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or ArgumentException
                                      or NotSupportedException or IndexOutOfRangeException) { }
        into.Clear();
        return false;
    }

    const uint OpenReparsePoint = 0x00200000;

    /// On Windows, the hard links of the file at path that the listing knew as id (Disk.Measure): one open for its attributes,
    /// never its data. 0 when it cannot be told, or the file there is no longer that one (the caller goes by the links it saw)
    public static uint WindowsLinks(string path, FileId id)
    {
        try
        {
            using var handle = WindowsOpen(LongPath(Path.GetFullPath(path)), FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                                           BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !WindowsFileInformation(handle, out var info)) return 0;
            return info.VolumeSerial == id.Volume && (((ulong)info.IndexHigh << 32) | info.IndexLow) == id.File ? info.Links : 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or ArgumentException) { return 0; }
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr WindowsFirstName(string file, uint flags, ref uint length, [Out] char[] name);

    [DllImport("kernel32.dll", EntryPoint = "FindNextFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsNextName(IntPtr find, ref uint length, [Out] char[] name);

    [DllImport("kernel32.dll", EntryPoint = "FindClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsFindClose(IntPtr find);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WindowsVolumePath(string file, [Out] char[] volume, uint length);

    /// Every name of the file at path (its hard links), in full, on Windows; none elsewhere (Linux has no call that lists them)
    /// or when they cannot be told. A few calls, never the file's data
    public static IReadOnlyList<string> Names(string path)
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        try
        {
            var full = Path.GetFullPath(path);
            var volume = new char[32768];
            if (!WindowsVolumePath(LongPath(full), volume, (uint)volume.Length)) return names;
            var root = new string(volume, 0, Array.IndexOf(volume, '\0') is var end && end >= 0 ? end : volume.Length);
            if (root.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) root = @"\\" + root[8..];
            else if (root.StartsWith(@"\\?\", StringComparison.Ordinal)) root = root[4..];
            root = Path.TrimEndingDirectorySeparator(root);
            var name = new char[32768];
            var length = (uint)name.Length;
            var find = WindowsFirstName(LongPath(full), 0, ref length, name);
            if (find == new IntPtr(-1)) return names;
            try
            {
                do
                {
                    names.Add(root + new string(name, 0, Math.Max(0, (int)length - 1)).TrimEnd('\0'));
                    length = (uint)name.Length;
                } while (WindowsNextName(find, ref length, name));
            }
            finally { WindowsFindClose(find); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or ArgumentException) { names.Clear(); }
        return names;
    }

    const uint GenericWrite = 0x40000000, ControlSetReparsePoint = 0x000900A4;

    /// The data FSCTL_SET_REPARSE_POINT takes to make a junction to target (REPARSE_DATA_BUFFER, a mount point): its tag, the length
    /// of what follows, the offset and length of the two names (the target as the system names it, \??\C:\..., then as it is shown,
    /// C:\...), and the names, each ended by a zero. A junction names a folder of one of this PC's disks, in full
    internal static byte[] JunctionData(string target)
    {
        var shown = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var system = Encoding.Unicode.GetBytes(@"\??\" + shown);
        var print = Encoding.Unicode.GetBytes(shown);
        var data = new byte[16 + system.Length + 2 + print.Length + 2];
        BitConverter.TryWriteBytes(data.AsSpan(0), TagMountPoint);
        BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)(data.Length - 8));
        BitConverter.TryWriteBytes(data.AsSpan(10), (ushort)system.Length);
        BitConverter.TryWriteBytes(data.AsSpan(12), (ushort)(system.Length + 2));
        BitConverter.TryWriteBytes(data.AsSpan(14), (ushort)print.Length);
        system.CopyTo(data, 16);
        print.CopyTo(data, 16 + system.Length + 2);
        return data;
    }

    /// A link at link to the folder target, which is there: on Windows a junction (made by anyone, where a symbolic link needs a
    /// privilege or Developer Mode), and a symbolic link where the disk gives none or elsewhere. False when none could be made;
    /// nothing is left at link then
    public static bool TryDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows() && TryJunction(link, target)) return true;
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// A junction at link: an empty folder there is given the reparse point. False when the folder is on a share, link is not
    /// empty or on a disk with no reparse points, or Windows says no; the folder made is gone again then
    static bool TryJunction(string link, string target)
    {
        var full = Path.GetFullPath(target);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // (a junction cannot name a share)
        Directory.CreateDirectory(link);
        var made = false;
        try
        {
            using var handle = WindowsOpen(LongPath(Path.GetFullPath(link)), GenericWrite, ShareAll, IntPtr.Zero, OpenExisting,
                                           BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            var data = JunctionData(full);
            made = !handle.IsInvalid && WindowsControl(handle, ControlSetReparsePoint, data, (uint)data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            if (!made)
            {
                try { Directory.Delete(link); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return made;
    }

    /// A Windows path past MAX_PATH as CreateFileW takes it (a romfs is deep): \\?\C:\..., \\?\UNC\server\share\...
    static string LongPath(string full) =>
        full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
        : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
        : @"\\?\" + full;

    static T Read<T>(byte[] b, int at) where T : struct => MemoryMarshal.Read<T>(b.AsSpan(at));
}
