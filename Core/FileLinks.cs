using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LongshotLauncher.Core;

/// Hard links, which .NET has no call for: a part kept from another installed version takes no second copy on the same disk
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
}
