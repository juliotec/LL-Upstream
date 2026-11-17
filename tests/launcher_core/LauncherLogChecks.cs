using LongshotLauncher.Core;

/// The launcher's log (LauncherLog) of what it does to the player's data, for a helper reading a report: builds.json set aside, a
/// build's folder deleted or refused, the rows ForgetGone drops, the launcher's own update put in place
static partial class Program
{
    static void DataLogChecks()
    {
        var home = Folder("data-log");
        string Lines() => string.Join(" | ", LauncherLog.Tail(10));
        // (a path as the log keeps it: no user name, Privacy.Scrub; the tests' folder may be under the home folder)
        static string P(string path) => Privacy.Scrub(path);
        try
        {
            LauncherLog.Clear();
            var list = Path.Combine(home, "builds.json");
            File.WriteAllText(list, "{ not json");
            BuildRegistry.Load(list);
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" builds.json could not be read") && l.Contains(P(list + ".broken"))),
                    "A builds.json set aside is logged, with where it was kept: " + Lines());

            var game = Path.Combine(home, "stable");
            Game(game, "v3.3.0");
            var r = BuildRegistry.Load(Path.Combine(home, "list.json"));
            r.Add(game, "v3.3.0", null);
            BuildRegistry.DeleteFolder(game, Platforms.Current);
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" build folder deleted: " + P(game))), "A build's folder deleted is logged: " + Lines());
            var other = Path.Combine(home, "other");
            Directory.CreateDirectory(other);
            Throws<IOException>(() => BuildRegistry.DeleteFolder(other, Platforms.Current), "A folder that is no game was deleted.");
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" build folder not deleted: " + P(other) + ": ")), "A refused delete is logged, with why: " + Lines());

            r.ForgetGone();
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" build v3.3.0 dropped from the list: its folder is gone: " + P(game))),
                    "A row ForgetGone drops is logged, with its folder: " + Lines());

            var settings = Path.Combine(home, "settings.json");
            File.WriteAllText(settings, "{ not json");
            LauncherSettings.Load(settings);
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" settings.json could not be read") && l.Contains(P(settings + ".broken"))),
                    "A settings.json set aside is logged, with where it was kept: " + Lines());
            var work = Path.Combine(home, "work");
            Directory.CreateDirectory(Path.Combine(work, "objcache"));
            Disk.FreeWork(work);
            Require(LauncherLog.Tail(5).Any(l => l.Contains($" free up space: deleted in {P(work)}: objcache")), "Free up space is logged, with what it deleted: " + Lines());

            var current = Path.Combine(home, "LongshotLauncher.exe");
            File.WriteAllText(current, "old");
            File.WriteAllText(SelfUpdate.Staged(current), "new");
            SelfUpdate.Swap(current, SelfUpdate.Staged(current), Platforms.Current);
            Require(LauncherLog.Tail(5).Any(l => l.Contains(" launcher update: the new launcher put in place at " + P(current) + ", the one before kept as "
                                                              + P(SelfUpdate.Backup(current)))), "The launcher's own update put in place is logged: " + Lines());
        }
        finally { LauncherLog.Clear(); }
    }

    /// A stream that takes some bytes, then fails as a full or unplugged disk does
    sealed class FailingStream(int after) : MemoryStream
    {
        public bool Refused { get; private set; }

        void Take(int count)
        {
            if (Length + count <= after) return;
            Refused = true;
            throw new IOException("There is not enough space on the disk.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Take(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Take(buffer.Length);
            base.Write(buffer);
        }
    }

    /// The log file (LauncherLog.OpenFile): its first line the launcher's, then the lines before it, each dated, no user name
    /// and no link's query; the run before kept as launcher.1.log, one run only; past the cap, a new file with the run's start in
    /// launcher.1.log; a crash written whole and once, also before the file was opened; a disk that refuses it throws nothing; the
    /// bug report names it
    static void LogFileChecks()
    {
        var root = Folder("log-file");
        var logs = Path.Combine(root, "logs");
        // (a home folder that is not this PC's: Add takes this PC's own out first (Privacy.Scrub), so the file's must be another)
        var me = OperatingSystem.IsWindows() ? @"Q:\Users\pat" : "/home/pat";
        var mark = OperatingSystem.IsWindows() ? Privacy.WindowsHome : Privacy.LinuxHome;
        var log = Path.Combine(logs, LauncherLog.FileName);
        var previous = Path.Combine(logs, LauncherLog.PreviousName);
        var sep = Path.DirectorySeparatorChar;
        // (read as another program would while the launcher writes it)
        static string Read(string path)
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            return r.ReadToEnd();
        }
        static string[] Lines(string path) => Read(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        static Exception Thrown(string message)
        {
            try { throw new InvalidOperationException(message); }
            catch (Exception e) { return e; }
        }
        var clock = LauncherLog.Clock;
        try
        {
            Require(new LauncherHome(root).Logs == Path.Combine(Path.GetFullPath(root), "logs"), "The log files are in the home's logs folder.");
            LauncherLog.CloseFile();
            LauncherLog.Clear();
            LauncherLog.Clock = () => new DateTimeOffset(2026, 11, 17, 9, 30, 0, 250, TimeSpan.Zero);
            LauncherLog.Add("before the file: run 1");
            LauncherLog.OpenFile(logs, "Longshot Launcher v1.2.0 (installed) started", me);
            LauncherLog.Add($"GET https://cdn.example/x.zip?X-Amz-Signature=secret&a=b: HTTP 200 (redirected to https://s3.example/y?token=secret), saved in {Path.Combine(me, "Games", "Longshot")}");
            var first = Lines(log);
            Require(LauncherLog.FilePath == log && first.Length == 3
                    && first[0] == "2026-11-17 09:30:00.250 +00:00 Longshot Launcher v1.2.0 (installed) started"
                    && first[1] == "2026-11-17 09:30:00.250 before the file: run 1",
                    "The file starts with the launcher, then the lines from before it was opened: " + string.Join(" | ", first));
            Require(first[2] == $"2026-11-17 09:30:00.250 GET https://cdn.example/x.zip?…: HTTP 200 (redirected to https://s3.example/y?…), saved in {mark}{sep}Games{sep}Longshot"
                    && !Read(log).Contains("secret") && !Read(log).Contains(me), "Each line dated, no user name, no link's query: " + first[2]);
            Require(!LauncherLog.Tail(1)[0].Contains("secret") && LauncherLog.Tail(1)[0].Contains("https://cdn.example/x.zip?…"),
                    "No link's query in memory either, so none in a bug report: " + LauncherLog.Tail(1)[0]);
            // (a link as .NET writes a Uri, decoded: a query with a space or a quote in it is cut whole where it is logged, Link)
            var signed = new Uri("https://s3.example/b/x.zip?response-content-disposition=attachment%3B%20filename%3D%22x.zip%22&X-Amz-Signature=secret#part");
            Require(LauncherLog.Link(signed) == "https://s3.example/b/x.zip?…" && LauncherLog.Link(signed.ToString()) == "https://s3.example/b/x.zip?…"
                    && LauncherLog.Link(new Uri("https://git.example/a/b")) == "https://git.example/a/b" && LauncherLog.Link((string?)null) == ""
                    && LauncherLog.Link(new Uri("https://u:pw@git.example:8443/a?x")) == "https://git.example:8443/a?…"
                    && LauncherLog.Link(new Uri("x.zip?sig=s", UriKind.Relative)) == "x.zip?…" && LauncherLog.Link("cdn.example/x#sig=s") == "cdn.example/x?…",
                    "A link is logged without its query or fragment: " + LauncherLog.Link(signed));
            LauncherLog.Add("GET https://git.example/a#token=secret, then https://git.example/b?x=secret#y: done");
            Require(LauncherLog.Tail(1)[0].EndsWith(" GET https://git.example/a?…, then https://git.example/b?…: done"),
                    "A fragment is cut too, with or without a query: " + LauncherLog.Tail(1)[0]);

            // (the next runs: the one before kept, one only)
            LauncherLog.Clear();
            LauncherLog.OpenFile(logs, "run 2", me);
            LauncherLog.Add("run 2's line");
            Require(Read(previous).Contains("before the file: run 1") && Lines(log)[0].EndsWith(" run 2") && !Read(log).Contains("run 1"),
                    "The run before is kept as launcher.1.log, and this run's file starts afresh.");
            LauncherLog.Clear();
            LauncherLog.OpenFile(logs, "run 3", me);
            Require(Read(previous).Contains("run 2's line") && !Read(previous).Contains("run 1") && Directory.GetFiles(logs).Length == 2,
                    "One run before is kept, no more.");

            // (past the cap: a new file, this run's start in launcher.1.log, neither past the cap)
            var filler = new string('x', 200);
            var count = (int)(LauncherLog.FileCap / filler.Length) + 10;
            for (var i = 0; i < count; i++) LauncherLog.Add($"line {i} {filler}");
            var start = Lines(previous);
            var rest = Lines(log);
            Require(start[0].EndsWith(" run 3") && rest[0].EndsWith($" (this run, continued: its start is in {LauncherLog.PreviousName})")
                    && rest[^1].Contains($" line {count - 1} ") && start.Length + rest.Length == count + 2,
                    $"Past the cap, the run carries on in a new file, every line in one or the other ({start.Length} + {rest.Length}).");
            Require(System.Text.Encoding.UTF8.GetByteCount(Read(previous)) <= LauncherLog.FileCap && System.Text.Encoding.UTF8.GetByteCount(Read(log)) <= LauncherLog.FileCap,
                    "Neither file is past the cap.");

            // (a crash: its line, then the whole exception, written once though two handlers see it)
            var crash = Thrown("the window broke in " + Path.Combine(me, "x"));
            LauncherLog.Crash("unhandled exception, the launcher ends", crash, logs);
            LauncherLog.Crash("unhandled exception on the window's thread", crash, logs);
            var crashed = Read(log);
            Require(crashed.Contains($" unhandled exception, the launcher ends: System.InvalidOperationException: the window broke in {mark}{sep}x")
                    && crashed.Contains($"    System.InvalidOperationException: the window broke in {mark}{sep}x") && crashed.Contains(" at Program.")
                    && !crashed.Contains("the window's thread"), "A crash is written whole, with its stack, once: " + crashed[^Math.Min(600, crashed.Length)..]);

            // (a crash with no file open, before the launcher held its home: appended to play.log there, never to the window
            // launcher's launcher.log, which it may still be writing)
            LauncherLog.CloseFile();
            var early = Path.Combine(root, "early");
            Directory.CreateDirectory(early);
            File.WriteAllText(Path.Combine(early, LauncherLog.FileName), "the running launcher's line" + Environment.NewLine);
            File.WriteAllText(Path.Combine(early, LauncherLog.PlayName), "a Play before" + Environment.NewLine);
            LauncherLog.Crash("unhandled exception, the launcher ends", Thrown("after the file was let go"), early);
            Require(!Read(Path.Combine(early, LauncherLog.PlayName)).Contains("after the file was let go"),
                    "Nothing is written after CloseFile (uninstall let go of the home before deleting it).");
            // (a file that stopped, its disk refusing it: no file open, none let go, as at a launcher's start)
            LauncherLog.OpenFile(Path.Combine(root, "stopped"), "x", me, _ => new FailingStream(0));
            Require(LauncherLog.FilePath == null, "A file the disk refuses at once is no file.");
            LauncherLog.Crash("unhandled exception, the launcher ends", Thrown("at start"), early);
            var played = Read(Path.Combine(early, LauncherLog.PlayName));
            Require(played.StartsWith("a Play before") && played.Contains("InvalidOperationException: at start") && played.Contains(" a launcher with no log file of its own (process ")
                    && Read(Path.Combine(early, LauncherLog.FileName)) == "the running launcher's line" + Environment.NewLine
                    && !File.Exists(Path.Combine(early, LauncherLog.PreviousName)) && !File.Exists(Path.Combine(early, LauncherLog.PlayPreviousName)),
                    "A crash with no file open is appended to play.log there; launcher.log is left alone.");

            // (--play from a shortcut: play.log, appended to run after run, launcher.log left alone)
            LauncherLog.Clear();
            var played2 = Path.Combine(root, "play");
            Directory.CreateDirectory(played2);
            File.WriteAllText(Path.Combine(played2, LauncherLog.FileName), "the window launcher's line" + Environment.NewLine);
            LauncherLog.OpenFile(played2, "play 1", me, file: LauncherLog.PlayName);
            LauncherLog.Add("play 1's gather");
            LauncherLog.OpenFile(played2, "play 2", me, file: LauncherLog.PlayName);
            LauncherLog.Add("play 2's gather");
            var plays = Read(Path.Combine(played2, LauncherLog.PlayName));
            Require(LauncherLog.FilePath == Path.Combine(played2, LauncherLog.PlayName) && plays.Contains(" play 1's gather") && plays.Contains(" play 2's gather")
                    && Read(Path.Combine(played2, LauncherLog.FileName)) == "the window launcher's line" + Environment.NewLine,
                    "--play appends to play.log, run after run, and leaves the window launcher's log alone.");

            // (a line another launcher appended meanwhile is never written over: each line goes at the file's end)
            LauncherLog.OpenFile(played2, "window", me);
            LauncherLog.Add("window line 1");
            using (var other = new FileStream(Path.Combine(played2, LauncherLog.FileName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var w = new StreamWriter(other)) w.WriteLine("a crash from another launcher");
            LauncherLog.Add("window line 2");
            var both = Read(Path.Combine(played2, LauncherLog.FileName));
            Require(both.Contains("a crash from another launcher" + Environment.NewLine) && both.Contains(" window line 2"),
                    "A line another launcher appended stays whole: " + both);

            // (past the cap, a file that cannot be moved aside: the file stops there, and is no longer named for a report)
            LauncherLog.Clear();
            var capped = Path.Combine(root, "capped");
            Directory.CreateDirectory(Path.Combine(capped, LauncherLog.PreviousName));   // (a folder in launcher.1.log's place: no move)
            LauncherLog.OpenFile(capped, "capped", me);
            for (var i = 0; i < count; i++) LauncherLog.Add($"line {i} {filler}");
            Require(LauncherLog.FilePath == null && System.Text.Encoding.UTF8.GetByteCount(Read(Path.Combine(capped, LauncherLog.FileName))) <= LauncherLog.FileCap,
                    "A file that cannot be moved aside past the cap stops, never past the cap, and is named no more.");

            // (a file trimmed while the launcher writes it, in an editor: the next lines still go at its end)
            LauncherLog.OpenFile(Path.Combine(root, "trimmed"), "trimmed", me);
            LauncherLog.Add("a line before the trim");
            using (new FileStream(Path.Combine(root, "trimmed", LauncherLog.FileName), FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            LauncherLog.Add("a line after the trim");
            Require(LauncherLog.FilePath != null && Read(Path.Combine(root, "trimmed", LauncherLog.FileName)).StartsWith("2026-11-17 09:30:00.250 a line after the trim") && Read(Path.Combine(root, "trimmed", LauncherLog.FileName)).Contains(" a line after the trim"),
                    "A file trimmed meanwhile still takes the next lines.");

            // (a disk that refuses the file: nothing thrown, the lines kept in memory)
            LauncherLog.CloseFile();
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "a file where the logs folder would be");
            LauncherLog.OpenFile(blocked, "x", me);
            LauncherLog.Add("still in memory");
            LauncherLog.Crash("unhandled exception", Thrown("nowhere to write"), blocked);
            Require(LauncherLog.FilePath == null && LauncherLog.Tail(2)[0].EndsWith(" still in memory"), "A logs folder that cannot be made: no file, nothing thrown.");
            var failing = new FailingStream(300);
            LauncherLog.OpenFile(Path.Combine(root, "failing"), "a disk that fills up", me, _ => failing);
            for (var i = 0; i < 20; i++) LauncherLog.Add("a line the disk refuses " + i);
            Require(failing.Refused && LauncherLog.FilePath == null && LauncherLog.Tail(1)[0].EndsWith(" a line the disk refuses 19"),
                    "A disk that fails while the launcher writes: nothing thrown, the lines kept in memory, no file named for a report.");
            LauncherLog.Crash("unhandled exception", Thrown("disk full"), logs);
            Require(Read(Path.Combine(logs, LauncherLog.PlayName)).Contains("InvalidOperationException: disk full"),
                    "A crash after the file stopped goes to play.log beside it.");

            // (the bug report names the log file, with no user name)
            var facts = new ReportFacts("Longshot Launcher v1.2.0 (installed)", "Windows 11, X64, .NET 10.0.0", "network: x");
            var file = Path.Combine(me, "AppData", "Local", "LongshotLauncher", "logs", LauncherLog.FileName);
            Require(BugReport.Make(facts, "x", log: Array.Empty<string>(), home: me, logFile: file).Split(Environment.NewLine)
                        .Contains($"launcher log file: {mark}{sep}AppData{sep}Local{sep}LongshotLauncher{sep}logs{sep}launcher.log (beside it: the run before, launcher.1.log, and a shortcut's Play, play.log)"),
                    "The report says where the whole log is.");
            Require(!BugReport.Make(facts, "x", log: Array.Empty<string>(), home: me, logFile: "").Contains("launcher log file"), "No log file: no line for it.");
            LauncherLog.OpenFile(logs, "run 4", me);
            Require(BugReport.Make(facts, "x", log: Array.Empty<string>(), home: me).Contains("launcher log file: " + log), "The report names this run's file.");
        }
        finally
        {
            LauncherLog.CloseFile();
            LauncherLog.Clock = clock;
            LauncherLog.Clear();
        }
    }
}
