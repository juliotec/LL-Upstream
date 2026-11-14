using System.Text;
using System.Security.Cryptography;

namespace LongshotLauncher.Core;

/// A file written whole or not at all: into a temporary file of its own beside it (&lt;name&gt;.&lt;random&gt;.tmp, so two writers never
/// share one), flushed to the disk, then renamed over the old one. A failed write leaves the old file and no temporary one
static class AtomicFile
{
    // (the same ordinary full pathname, including different casing, in this Windows session; separate from InstanceLock)
    internal static string MutexName(string path) => @"Local\LongshotLauncher-Write-"
        + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    public static void Write(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));

    public static void Write(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                f.Write(bytes);
                f.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
            {
                // (Windows can refuse concurrent replacements with access denied. Only the rename is serialized:
                // writers still prepare and flush their own temporary files independently)
                using var mutex = new Mutex(false, MutexName(full));
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }   // (the previous writer ended; this thread owns the mutex now)
                try { File.Move(tmp, full, overwrite: true); }
                finally { mutex.ReleaseMutex(); }
            }
            else File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
