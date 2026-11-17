using System.Runtime.InteropServices;
using System.Text;

namespace LongshotLauncher.Core;

/// What a bug report says about this launcher and PC: its version and how it is installed, the system, and the way it goes to the
/// network (git-cdn first or not, the proxy the system or HTTPS_PROXY sets)
public sealed record ReportFacts(string Launcher, string System, string Network);

/// Copy for a bug report: what happened, as the screen said it, the launcher's version, this PC's system and network, what the
/// screen adds (a build's settings and log), and the launcher's log of this run (LauncherLog), with where its whole log file is, for
/// a helper who asks for it (the run before is beside it). The player pastes it where they ask for help. It names no game file's
/// contents and no save, and no user name (Privacy.Scrub: the home folder as %USERPROFILE% or ~, the name elsewhere as <user>).
public static class BugReport
{
    /// The launcher log's lines a report carries
    public const int LogLines = 200;

    /// logFile: the launcher's log file (LauncherLog.FilePath when null; "" for none)
    public static string Make(ReportFacts facts, string what, IEnumerable<string>? more = null, IReadOnlyList<string>? log = null, string? home = null,
                              string? logFile = null)
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
        if ((logFile ?? LauncherLog.FilePath) is { Length: > 0 } file)
            sb.AppendLine($"launcher log file: {file} (beside it: the run before, {LauncherLog.PreviousName}, and a shortcut's Play, {LauncherLog.PlayName})");
        return Privacy.Scrub(sb.ToString(), home);
    }

    /// The system: the OS as .NET names it, the processor's architecture and the .NET runtime
    public static string System() =>
        $"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}";

    /// How the launcher goes to git.magicbean.dev: git-cdn first (CdnFetcher) or not, and the proxy on the way, if any
    public static string Network(bool cdn, Uri? proxy) =>
        $"network: {(cdn ? "git-cdn.magicbean.dev first, then git.magicbean.dev" : $"git.magicbean.dev only ({CdnFetcher.DisableVariable}=1)")}; " +
        $"proxy: {(proxy is { } p ? p.Authority : "none")}";
}
