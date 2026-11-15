namespace LongshotLauncher.Core;

/// (Linux) The player's own Vulkan FSR 4 library. AMD publishes its FSR 4 runtime for Windows only and Longshot ships nothing of
/// AMD's, so on Linux the player picks the library in the build settings and the launcher copies it into each game folder it
/// builds, where the game looks for it (Longshot's gpu/vk/vk_fsr4.cpp: beside the executable, then the game folder)
public static class FsrLibrary
{
    public const string FileName = "amd_fidelityfx_upscaler_vk.so";
    /// (no FSR 4 library is anywhere near this: a bigger file is not one)
    const long MostBytes = 256L << 20;

    /// Why the file can't be the library (null: it can): it must be a 64-bit ELF shared object of a sane size
    public static string? Check(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Tr.T("The file is not there any more.");
            if (info.Length > MostBytes || info.Length < 64) return Tr.T("This is not a Vulkan FSR 4 library (.so).");
            Span<byte> head = stackalloc byte[5];
            using (var f = File.OpenRead(path))
                if (f.ReadAtLeast(head, 5, throwOnEndOfStream: false) < 5) return Tr.T("This is not a Vulkan FSR 4 library (.so).");
            // (ELF magic, then EI_CLASS 2: 64-bit, as the game is)
            if (head[0] != 0x7f || head[1] != (byte)'E' || head[2] != (byte)'L' || head[3] != (byte)'F' || head[4] != 2)
                return Tr.T("This is not a Vulkan FSR 4 library (.so).");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    /// The library copied into the game folder as amd_fidelityfx_upscaler_vk.so (replacing the one there); returns where it went.
    /// Throws IOException (or UnauthorizedAccessException) when it can't be copied, InvalidDataException when it isn't a library
    public static string Install(string library, string gameFolder)
    {
        if (Check(library) is { } why) throw new InvalidDataException(why);
        var dst = Path.Combine(gameFolder, FileName);
        if (string.Equals(Path.GetFullPath(library), Path.GetFullPath(dst), StringComparison.Ordinal)) return dst;
        var tmp = dst + ".tmp";
        File.Copy(library, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
        return dst;
    }
}
