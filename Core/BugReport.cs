using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// What a bug report says about this launcher and PC: its version and how it is installed, the system, and the way it goes to the
/// network (git-cdn first or not, the proxy the system or HTTPS_PROXY sets)
public sealed record ReportFacts(string Launcher, string System, string Network);

/// Copy for a bug report: what happened, as the screen said it, the launcher's version, this PC's system and network, what the
/// screen adds (a build's settings and log), and the launcher's log of this run (LauncherLog). The player pastes it where they
/// ask for help. It names no game file's contents and no save, and the home folder shows as ~, so no user name goes with it.
public static class BugReport
{
    /// The launcher log's lines a report carries
    public const int LogLines = 200;

    public static string Make(ReportFacts facts, string what, IEnumerable<string>? more = null, IReadOnlyList<string>? log = null, string? home = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(facts.Launcher);
        sb.AppendLine(facts.System);
        sb.AppendLine(facts.Network);
        sb.AppendLine("what happened: " + what.ReplaceLineEndings(" / "));
        var extra = more?.ToList() ?? new List<string>();
        if (extra.Count > 0)
        {
            sb.AppendLine();
            foreach (var l in extra) sb.AppendLine(l);
        }
        var tail = log ?? LauncherLog.Tail(LogLines);
        sb.AppendLine();
        sb.AppendLine(tail.Count > 0 ? $"launcher log, its last {tail.Count} lines:" : "launcher log: empty");
        foreach (var l in tail) sb.AppendLine(l);
        return Scrub(sb.ToString(), home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// The system: the OS as .NET names it, the processor's architecture and the .NET runtime
    public static string System() =>
        $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}";

    /// How the launcher goes to git.magicbean.dev: git-cdn first (CdnFetcher) or not, and the proxy on the way, if any
    public static string Network(bool cdn, Uri? proxy) =>
        $"network: {(cdn ? "git-cdn.magicbean.dev first, then git.magicbean.dev" : $"git.magicbean.dev only ({CdnFetcher.DisableVariable}=1)")}; " +
        $"proxy: {(proxy is { } p ? p.Authority : "none")}";

    /// text with the home folder as ~ (any case on Windows, whose paths ignore it), wherever it starts a path: C:\Users\Name\x is
    /// ~\x, and C:\Users\Name2 is left as it is. On Windows either separator matches, also doubled (C:/Users/Name/x,
    /// file:///C:/Users/Name/x, C:\\Users\\Name\\x as JSON writes it), and a space in the home folder also as %20 (a file:// link).
    /// The home folder must end where its last name does (HomeEnd): C:\Users\Name Smith\x is another folder, left as it is
    public static string Scrub(string text, string? home)
    {
        var h = home?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(h) || h.Length <= 3) return text;
        var windows = h.Contains('\\') || (h.Length > 1 && h[1] == ':');
        var names = h.Split(windows ? new[] { '\\', '/' } : new[] { '/' });
        // (Regex.Escape writes a space as "\ ")
        var pattern = string.Join(windows ? @"[\\/]{1,2}" : "/", names.Select(n => Regex.Escape(n).Replace(@"\ ", @"(?:\ |%20)")));
        return Regex.Replace(text, pattern + HomeEnd, "~", windows ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    /// Where a path's home folder ends: a separator, a line's or the text's end, a character a path ends at (a quote, a bracket,
    /// a comma, a colon…), a sentence's full stop, or a space or tab whose next word is no path (C:\Users\Name is full; not
    /// C:\Users\Name Smith\x). Anything else (Name2, Name.old, Name-x, Name%20Smith) continues a longer name
    const string HomeEnd = @"(?=[\\/]|[\r\n]|$|[""'`<>|)\]},;:?*]|\.(?:\s|$)|[ \t](?![^\s\\/""'<>|]*[\\/]))";
}
