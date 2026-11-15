namespace LongshotLauncher.Core;

/// The launcher's own log for this run, in memory only: what it asked the network and what came back (HttpFetcher, CdnFetcher),
/// and the failures it showed. A bug report (BugReport) carries its last lines, so a helper sees which link failed, how, and through
/// which proxy, where a screen has room for one sentence. Nothing is written to disk; it starts empty with each run.
public static class LauncherLog
{
    /// The lines kept: the oldest go first
    public const int Keep = 500;

    static readonly object gate = new();
    static readonly Queue<string> lines = new();

    /// The time each line is stamped with (the tests set it)
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// A line, stamped with the time; one line each (a line break in it becomes a space)
    public static void Add(string line)
    {
        var text = $"{Clock():HH:mm:ss.fff} {line.ReplaceLineEndings(" ")}";
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
