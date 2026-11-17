using System.Text;
using System.Text.RegularExpressions;

namespace LongshotLauncher.Core;

/// The launcher's own log for this run: what it asked the network and what came back (HttpFetcher, CdnFetcher), what it did to the
/// player's data (saves gathered, build folders deleted, rows dropped from the list, builds.json set aside, its own update, install
/// and uninstall), and the failures it showed. A bug report (BugReport) carries its last lines, so a helper sees which link failed,
/// how, and through which proxy, where a screen has room for one sentence.
///
/// In memory (its last Keep lines), and once the launcher holds its home (OpenFile) also in &lt;home&gt;/logs/launcher.log, with the run
/// before kept as launcher.1.log: a report written after the launcher closed, or crashed (Crash), still has it. A launcher that
/// does not hold the home (--play from a shortcut, a crash before the home was held) appends to play.log beside it instead, never
/// to the window launcher's file. The files stay on the PC; each line has its date, no user name (Privacy.Scrub) and no link's
/// query (Link). A run that writes more than FileCap carries on in a new file, the one before moved to its .1.log. Writing never
/// throws: a disk that refuses it leaves the log in memory only.
public static class LauncherLog
{
    /// The lines kept: the oldest go first
    public const int Keep = 500;
    /// The most a log file holds: past it, the run carries on in a new launcher.log and the old one becomes launcher.1.log
    public const long FileCap = 1 << 20;
    public const string FileName = "launcher.log";
    public const string PreviousName = "launcher.1.log";
    /// The file of a launcher that does not hold the home (--play, a crash before the home was held): appended to, run after run,
    /// moved to play.1.log once past FileCap
    public const string PlayName = "play.log";
    public const string PlayPreviousName = "play.1.log";

    static readonly object gate = new();
    static readonly Queue<string> lines = new();
    static StreamWriter? writer;
    static long written;
    static string? folder;
    static string? home;
    static string name = FileName, previous = PreviousName;
    static bool letGo;
    static Func<string, Stream>? opener;
    static object? crashed;

    /// The time each line is stamped with (the tests set it)
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// The log file this run writes, for a bug report to name; null when there is none (not opened, the disk refused it, or it
    /// stopped)
    public static string? FilePath { get; private set; }

    /// A line, stamped with the time; one line each (a line break in it becomes a space), with no user name in its paths
    /// (Privacy.Scrub): kept that way, so whatever reads the log (a bug report, its file) gets no user name either
    public static void Add(string line)
    {
        var at = Clock();
        var text = $"{at:HH:mm:ss.fff} {NoQuery(Privacy.Scrub(line.ReplaceLineEndings(" ")))}";
        lock (gate)
        {
            lines.Enqueue(text);
            while (lines.Count > Keep) lines.Dequeue();
            Write($"{at:yyyy-MM-dd} {text}");
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

    /// A link as the log writes it: its scheme, host, port and path, as https://host/path?… when it had a query or fragment (a
    /// signed download link's token); never a user:password@. A relative one is cut at its first ? or #
    public static string Link(Uri? uri) =>
        uri == null ? "" : !uri.IsAbsoluteUri ? CutQuery(uri.OriginalString)
        : uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
          + (uri.Query.Length > 1 || uri.Fragment.Length > 1 ? "?…" : "");

    /// A link given as text, as the log writes it (Link)
    public static string Link(string? url) =>
        url == null ? "" : Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Link(uri) : CutQuery(url);

    static string CutQuery(string link) => link.IndexOfAny(['?', '#']) is var i and >= 0 ? link[..i] + "?…" : link;

    /// From now on the lines go to logs/launcher.log too: the launcher.log of the run before becomes launcher.1.log (one run is
    /// kept), header is its first line (the launcher's version and system), then the lines this run had before it. Only the
    /// launcher that holds the home opens launcher.log (InstanceLock): another would move the first one's log aside, and opens
    /// play.log (file: PlayName), appended to. roll: false for a start that hands over to another launcher on the same home (it
    /// rolls itself). userHome: the home folder Privacy.Scrub takes out (the tests' own; this PC's when null); open: the tests'
    /// stand-in for the file
    public static void OpenFile(string logs, string header, string? userHome = null, Func<string, Stream>? open = null,
                                string file = FileName, bool roll = true)
    {
        lock (gate)
        {
            CloseFile();
            letGo = false;
            folder = logs;
            home = userHome;
            opener = open;
            (name, previous) = file == PlayName ? (PlayName, PlayPreviousName) : (FileName, PreviousName);
            if (!Start(roll && file != PlayName)) return;
            Write($"{Clock():yyyy-MM-dd HH:mm:ss.fff zzz} {header.ReplaceLineEndings(" ")}");
            var date = $"{Clock():yyyy-MM-dd}";
            foreach (var l in lines) Write($"{date} {l}");
        }
    }

    /// The file let go (before the home is deleted: Installer.Uninstall); the lines stay in memory, and a crash after it writes no
    /// file (it would make the deleted home again)
    public static void CloseFile()
    {
        lock (gate)
        {
            Drop();
            FilePath = null;
            letGo = true;
        }
    }

    /// An exception that ends the launcher (or that nobody saw: what): its line, then its whole text (the stack), on the disk before
    /// the process goes. The same exception reported twice (the window's thread, then the process) is written once. logs: the
    /// folder to write in when no file is open (a crash before the launcher held its home, or after its file stopped): its
    /// play.log, appended to, never the window launcher's launcher.log. Nothing is written after CloseFile
    public static void Crash(string what, object? error, string? logs = null)
    {
        try
        {
            lock (gate)
            {
                if (error != null && ReferenceEquals(error, crashed)) return;
                crashed = error;
                if (writer == null && !letGo && logs != null)
                {
                    folder = logs;
                    opener = null;   // (another file than the one that stopped)
                    (name, previous) = (PlayName, PlayPreviousName);
                    // (the file is shared: which launcher this is, as OpenFile's header says)
                    if (Start(roll: false))
                        Write($"{Clock():yyyy-MM-dd HH:mm:ss.fff zzz} a launcher with no log file of its own (process {Environment.ProcessId}, "
                              + $"{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "version unknown"}):");
                }
                var e = error as Exception;
                Add($"{what}: {(e != null ? $"{e.GetType().FullName}: {e.Message}" : error?.ToString() ?? "(nothing said)")}");
                if (e != null)
                    foreach (var l in e.ToString().Split('\n'))
                        Write("    " + l.TrimEnd('\r'));
                writer?.Flush();
                (writer?.BaseStream as FileStream)?.Flush(flushToDisk: true);
            }
        }
        catch (Exception) { }
    }

    /// The file opened (roll, or a file there past FileCap: that one moved to its .1.log first); false when the disk refused it
    static bool Start(bool roll)
    {
        try
        {
            Directory.CreateDirectory(folder!);
            var path = Path.Combine(folder!, name);
            if (File.Exists(path) && (roll || new FileInfo(path).Length > FileCap))
            {
                // (still open in a launcher that is closing, after its update: Windows lets it be renamed, it shares delete; if not, the
                // file is appended to)
                try { File.Move(path, Path.Combine(folder!, previous), overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            // (not FileMode.Append: each line seeks to the end itself (Write), and a file trimmed meanwhile, in an editor, must not stop it)
            var stream = opener?.Invoke(path) ?? new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            written = stream.CanSeek ? stream.Length : 0;
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            return true;
        }
        catch (Exception)
        {
            Drop();
            FilePath = null;
            return false;
        }
    }

    static void Drop()
    {
        try { writer?.Dispose(); }
        catch (Exception) { }
        writer = null;
    }

    /// One line into the file, at its end (another launcher may append to it too), past the cap into a new one; a failure stops
    /// the file (FilePath null; the line stays in memory)
    static void Write(string text)
    {
        if (writer == null) return;
        try
        {
            var line = Clean(text);
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (writer.BaseStream.CanSeek) written = writer.BaseStream.Seek(0, SeekOrigin.End);
            if (written > 0 && written + bytes > FileCap)
            {
                Drop();
                // (a file that cannot be moved aside would grow past the cap: it is left as it is)
                if (!Start(roll: true) || written > 0)
                {
                    Drop();
                    FilePath = null;
                    return;
                }
                var more = $"{Clock():yyyy-MM-dd HH:mm:ss.fff zzz} (this run, continued: its start is in {previous})";
                writer!.WriteLine(more);
                written += Encoding.UTF8.GetByteCount(more) + Environment.NewLine.Length;
            }
            writer!.WriteLine(line);
            written += bytes;
        }
        catch (Exception)
        {
            Drop();
            FilePath = null;
        }
    }

    /// A line as the file keeps it: no user name (Privacy.Scrub) and no link's query; a line from Add has neither already, the
    /// header and a crash's stack are cleaned here
    static string Clean(string text) => NoQuery(Privacy.Scrub(text, home));

    /// text with each link's query and fragment cut (https://host/path?…): what is left of one where the line was not written with
    /// Link; a link cut already stays as it is. A query ends at a space, a quote or bracket, or a colon, comma or full stop that
    /// ends a sentence
    static string NoQuery(string text) => Query.Replace(text, "$1?…");

    static readonly Regex Query = new(@"(https?://[^\s?#""'<>]+)(?:\?(?!…)|#)[^\s""'<>)]*?(?=[:,;.]?(?:[\s""'<>)]|$))", RegexOptions.Compiled);
}
