namespace LongshotLauncher.Core;

/// What a game folder says about itself: the stamp make_game.py writes (longshot_version.txt: version=, builder=, builder_local=),
/// the game's language (language.txt) and whether it is built (bin/longshot[.exe], or longshot[.exe] beside its renderer: ExePath)
public static class GameFolder
{
    public const string StampName = "longshot_version.txt";
    public const string LanguageName = "language.txt";

    /// The stamp's key=value lines, in order (the first of a key wins, as linux_update.game_version reads version=); null: none
    public static IReadOnlyDictionary<string, string>? ReadStamp(string folder)
    {
        string[] lines;
        try { lines = File.ReadAllLines(Path.Combine(folder, StampName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        var stamp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0) stamp.TryAdd(line[..eq], line[(eq + 1)..].Trim());
        }
        return stamp;
    }

    /// The release the folder was built with (version=), or null
    public static string? Version(string folder) =>
        ReadStamp(folder) is { } s && s.TryGetValue("version", out var v) && v.Length > 0 ? v : null;

    /// A portable install's folder (builder_local=1)
    public static bool Portable(string folder) => ReadStamp(folder) is { } s && s.TryGetValue("builder_local", out var v) && v == "1";

    /// The game a folder plays. make_game.py puts it in bin/ when it installs the setup as the folder's longshot[.exe], and
    /// then removes the renderer from the folder itself; without the setup (a development build, or a setup an antivirus took)
    /// the game and its renderer stay in the folder. So the renderer in the folder means its last build left the game there:
    /// that one is played, even when bin/ still holds an older game from a packaged build before. Else bin/'s, which is also
    /// the path a folder with no game at all reports (a setup left without bin/'s game must not enable Play)
    public static string ExePath(string folder, Platform platform)
    {
        var exe = Platforms.GameExe(platform);
        var atRoot = Path.Combine(folder, exe);
        var renderer = platform == Platform.Windows ? "longshot_vk.dll" : "longshot_vk.so";
        return File.Exists(atRoot) && File.Exists(Path.Combine(folder, renderer)) ? atRoot : Path.Combine(folder, "bin", exe);
    }

    public static bool IsBuilt(string folder, Platform platform) => File.Exists(ExePath(folder, platform));

    /// The game's language: the first line of language.txt that is not blank or a # comment, as written (make_game.folder_language
    /// also wants it to be one of its codes: Languages.Normalize); null when it has none
    public static string? Language(string folder)
    {
        try
        {
            return File.ReadLines(Path.Combine(folder, LanguageName)).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// Built with NVIDIA DLSS: its runtime in ngx/ (longshot_builder.folder_has_dlss: libnvidia-ngx-dlss.so*; on Windows nvngx_dlss.dll)
    public static bool HasDlss(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(Path.Combine(folder, "ngx")).Select(Path.GetFileName)
                .Any(n => n != null && (n.StartsWith("libnvidia-ngx-dlss.so", StringComparison.Ordinal)
                                        || n.Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// A folder make_game.py made: its stamp, or the game executable. Only such a folder may be deleted from the launcher
    public static bool IsGameFolder(string folder, Platform platform) =>
        Directory.Exists(folder) && (File.Exists(Path.Combine(folder, StampName)) || IsBuilt(folder, platform));

    /// As IsGameFolder, but report probes that could not be made during a storage scan.
    public static bool IsGameFolder(string folder, Platform platform, Action partial)
    {
        bool Exists(string path, bool directory = false)
        {
            try { return ((File.GetAttributes(path) & FileAttributes.Directory) != 0) == directory; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { partial(); return false; }
        }
        if (!Exists(folder, directory: true)) return false;
        var exe = Platforms.GameExe(platform);
        return Exists(Path.Combine(folder, StampName))
            || (Exists(Path.Combine(folder, exe)) && Exists(Path.Combine(folder, platform == Platform.Windows ? "longshot_vk.dll" : "longshot_vk.so")))
            || Exists(Path.Combine(folder, "bin", exe));
    }
}
