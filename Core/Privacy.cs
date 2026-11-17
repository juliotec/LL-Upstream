using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// What the launcher writes down or sends about this PC carries no user name: the launcher's log (LauncherLog.Add scrubs every
/// line it keeps), a bug report (BugReport) and a build's report (BuildReports). A path keeps everything else, the drive, its
/// folders and its file, so a helper still sees where something failed: only the OS user's name goes. Its placeholders are the
/// game's own (Longshot's gpu/vk/vk_bug_report.cpp and host/hos_crash_dialog.h, hos_crash_redact_line), so a launcher's and a game's
/// report read the same: %USERPROFILE% and ~ for the home folder, <user> for the name elsewhere in a path.
public static class Privacy
{
    /// What Scrub writes in place of a user's name where it isn't the home folder (D:\Users\<user>\x), as the game does
    public const string User = "<user>";

    /// What Scrub writes in place of the home folder: Windows' own name for it, and Linux's (C:\Users\Ana\x as
    /// %USERPROFILE%\x, /home/ana/x as ~/x), as the game does
    public const string WindowsHome = "%USERPROFILE%", LinuxHome = "~";

    /// The shortest name Scrub takes out: a one-letter folder (\a\) is no one's name, and too common to replace
    public const int MinName = 2;

    /// text with no user name in its paths. Home: the home folder (this PC's, unless a test gives one); users: the user's names
    /// beside the home folder's last name (Environment.UserName by default, which can differ from it: a renamed account, a
    /// Microsoft account's 5-letter folder):
    /// - the home folder as %USERPROFILE% on Windows, ~ on Linux, wherever a path starts with it (HomeFolder);
    /// - then each name wherever it is a whole folder's name in a path, as <user>: on another drive (D:\Users\Ana\x), under /home,
    ///   /media, /run/media or /var/home, in a backup folder (E:\Backup\Ana\saves); on Windows in any case, and its 8.3 short
    ///   form too (C:\Users\ANASMI~1). Only where a separator is before it and the path's folder ends after it (FolderEnd): Ana
    ///   in a sentence, Ana2 or Ana.old stay as they are. Text scrubbed already is left as it is: neither placeholder follows a
    ///   separator directly as a name would (%USERPROFILE%, <user>)
    public static string Scrub(string text, string? home = null, IEnumerable<string>? users = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var mine = home == null;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var h = home.TrimEnd('/', '\\');
        var windows = h.Contains('\\') || (h.Length > 1 && h[1] == ':') || (h.Length == 0 && OperatingSystem.IsWindows());
        text = HomeFolder(text, h, windows);
        var names = (users ?? (mine ? new[] { Environment.UserName } : Array.Empty<string>()))
            .Append(h.Length > 3 ? h.Split('\\', '/')[^1] : "")
            .Where(n => n.Trim().Length >= MinName).Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderByDescending(n => n.Length);
        foreach (var n in names)
        {
            var pattern = Spaced(n);
            if (windows && ShortName(n) is { } shortName) pattern = $"(?:{pattern}|{Regex.Escape(shortName)}~[0-9]{{1,6}})";
            text = Regex.Replace(text, @"(?<=[\\/])" + pattern + FolderEnd, User, windows ? RegexOptions.IgnoreCase : RegexOptions.None);
        }
        return text;
    }

    /// text with the home folder as %USERPROFILE% (Windows, in any case: its paths ignore it) or ~ (Linux), wherever a path starts with it: C:\Users\Name\x is
    /// %USERPROFILE%\x, and C:\Users\Name2 is left as it is. On Windows either separator matches, also doubled (C:/Users/Name/x,
    /// file:///C:/Users/Name/x, C:\\Users\\Name\\x as JSON writes it), and a space in the home folder also as %20 (a file:// link).
    /// The home folder must end where its last name does (FolderEnd): C:\Users\Name Smith\x is another folder, left as it is
    static string HomeFolder(string text, string h, bool windows)
    {
        if (h.Length <= 3) return text;
        var names = h.Split(windows ? new[] { '\\', '/' } : new[] { '/' });
        var pattern = string.Join(windows ? @"[\\/]{1,2}" : "/", names.Select(Spaced));
        // (a Linux home where a path starts, not inside a longer one: /var/home/ana/x is no home folder, its name goes by Scrub's
        // own rule. A drive starts a Windows one wherever it is)
        var start = windows ? "" : @"(?<![\w.-])";
        return Regex.Replace(text, start + pattern + FolderEnd, windows ? WindowsHome : LinuxHome, windows ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    /// A name as a pattern, a space in it also as %20 (Regex.Escape writes a space as "\ ")
    static string Spaced(string name) => Regex.Escape(name).Replace(@"\ ", @"(?:\ |%20)");

    /// The 6 characters Windows starts a long name's 8.3 short form with (John Smith: JOHNSM, as JOHNSM~1), or null for a name
    /// that is its own short form already: upper case, without spaces and dots, and _ for a character a short name can't hold
    static string? ShortName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToUpperInvariant())
            if (c is not (' ' or '.')) sb.Append(c < 128 && (char.IsLetterOrDigit(c) || "!#$%&'()-@^_`{}~".Contains(c)) ? c : '_');
        var fits = name.Length <= 8 && sb.ToString() == name.ToUpperInvariant();
        return fits || sb.Length == 0 ? null : sb.ToString()[..Math.Min(6, sb.Length)];
    }

    /// Where a folder's name ends: a separator, a line's or the text's end, a character a path ends at (a quote, a bracket, a comma,
    /// a colon…), a sentence's full stop, or a space or tab whose next word is no path (C:\Users\Name is whole; not
    /// C:\Users\Name Smith\x). Anything else (Name2, Name.old, Name-x, Name%20Smith) continues a longer name
    const string FolderEnd = @"(?=[\\/]|[\r\n]|$|[""'`<>|)\]},;:?*]|\.(?:\s|$)|[ \t](?![^\s\\/""'<>|]*[\\/]))";
}
