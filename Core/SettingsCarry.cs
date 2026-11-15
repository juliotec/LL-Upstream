using System.Text;

namespace LongshotLauncher.Core;

/// What SettingsCarry.Fill did: the settings file it took (another build's, or the user folder's) and the build's it filled
public sealed record SettingsCarried(string From, string To);

/// The player's game settings, longshot_options.ini (the Insert menu's choices and the controls), brought to the build about to
/// start when its own file has none: an update into a new folder held only make_game.py's compile_shaders= and the game started
/// from the defaults. The newest file of the user folder (where a Longshot that takes --user for its settings keeps them,
/// Longshot's host/options_path.c) and of the other builds' folders fills it, the build's own compile_shaders= kept. Only the
/// build's file is written: a Longshot that keeps its settings in the user folder copies it there at its first start, and one
/// from before reads it where it is. The user folder's own file is never written here
public static class SettingsCarry
{
    public const string FileName = "longshot_options.ini";
    public const int MaxFileSize = 256 << 10;   // (the game reads it whole; the crash report's limit)
    const string Build = "compile_shaders";   // (make_game.py's, the build's choice, not the player's)

    static (string? Key, string Value) Line(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] is '#' or ';') return (null, "");
        var eq = line.IndexOf('=');
        return eq > 0 ? (line[..eq].Trim(), line[(eq + 1)..].Trim()) : (null, "");
    }

    /// Whether the text holds a setting of the player's: a key=value line other than compile_shaders= (Longshot's
    /// host/options_path.c has_settings)
    public static bool HasSettings(string text) =>
        text.Split('\n').Select(l => Line(l).Key).Any(k => k != null && k != Build);

    /// The build's file with the source's settings: the source's lines but its compile_shaders=, then the build's own
    /// compile_shaders= lines (the build's choice, kept)
    internal static string Merge(string? build, string source)
    {
        var eol = source.Contains("\r\n") ? "\r\n" : "\n";
        static List<string> Lines(string text)
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
        var merged = Lines(source).Where(l => Line(l).Key != Build).ToList();
        merged.AddRange(Lines(build ?? "").Where(l => Line(l).Key == Build));
        return string.Join(eol, merged) + eol;
    }

    /// A settings file's text: null when there is none, or it is a link, a folder, too large or not UTF-8 (then it is neither
    /// taken nor written over)
    static string? Read(string path, out bool usable)
    {
        usable = false;
        try
        {
            var info = new FileInfo(path);
            if (Directory.Exists(path) || info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return null;
            usable = true;
            if (!info.Exists) return null;
            if (info.Length > MaxFileSize)
            {
                usable = false;
                return null;
            }
            return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or DecoderFallbackException)
        {
            usable = false;
            return null;
        }
    }

    static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// The build in folder filled from the newest settings file of userFolder and of others (game folders), when its own has no
    /// setting of the player's (none, or compile_shaders= alone). Null when it has its own, or nothing has any. A file that is a
    /// link (the player's own arrangement) is left alone. IOException or UnauthorizedAccessException when the write fails (the
    /// build's file as it was)
    public static SettingsCarried? Fill(string folder, string? userFolder, IEnumerable<string> others)
    {
        var target = Path.Combine(folder, FileName);
        var existing = Read(target, out var writable);
        if (!writable || (existing != null && HasSettings(existing))) return null;
        var sources = (userFolder != null ? new[] { userFolder } : Array.Empty<string>()).Concat(others)
            .Select(f => Path.Combine(f, FileName))
            .Where(p => !Same(p, target)).ToList();
        (string Path, string Text, DateTime When)? newest = null;
        foreach (var p in sources)
        {
            if (Read(p, out _) is not { } text || !HasSettings(text)) continue;
            DateTime when;
            try { when = File.GetLastWriteTimeUtc(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (newest == null || when > newest.Value.When) newest = (p, text, when);
        }
        if (newest == null) return null;
        AtomicFile.Write(target, Merge(existing, newest.Value.Text));
        return new SettingsCarried(newest.Value.Path, target);
    }
}
