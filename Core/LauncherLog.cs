namespace LongshotLauncher.Core;

/// The launcher's own log for this run, in memory only: what it asked the network and what came back (HttpFetcher, CdnFetcher),
/// and the failures it showed. A bug report (BugReport) carries its last lines, so a helper sees which link failed, how, and through
/// which proxy, where a screen has room for one sentence. Nothing is written to disk; it starts empty with each run. No line holds
/// the user's name: Add takes it out of every path first.
public static class LauncherLog
{
    /// The lines kept: the oldest go first
    public const int Keep = 500;

    static readonly object gate = new();
    static readonly Queue<string> lines = new();

    /// The time each line is stamped with (the tests set it)
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// A line, stamped with the time; one line each (a line break in it becomes a space), with no user name in its paths
    /// (Privacy.Scrub): kept that way, so whatever reads the log (a bug report, a file of it) gets no user name either
    public static void Add(string line)
    {
        var text = $"{Clock():HH:mm:ss.fff} {Privacy.Scrub(line.ReplaceLineEndings(" "))}";
        lock (gate)
        {
            lines.Enqueue(text);
            while (lines.Count > Keep) lines.Dequeue();
        }
    }

    /// The last count lines, oldest first
    public static IReadOnlyList<string> Tail(int count)
    {
        lock (gate) return lines.Skip(Math.Max(0, lines.Count - count)).ToList();
    }

    public static void Clear()
    {
        lock (gate) lines.Clear();
    }
}
