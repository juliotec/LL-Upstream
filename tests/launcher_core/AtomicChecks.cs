using System.Diagnostics;
using LongshotLauncher.Core;

static partial class Program
{
    static int AtomicChild(string[] args)
    {
        if (args[0] == "--atomic-acquire" && args.Length == 2)
        {
            using var mutex = new Mutex(false, args[1]);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new Exception("The previous writer did not release its mutex.");
            mutex.ReleaseMutex();
            return 0;
        }
        if (args[0] == "--atomic-write" && args.Length == 5)
        {
            File.WriteAllText(args[3], "ready");
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(args[2]) && DateTime.UtcNow < until) Thread.Sleep(5);
            if (!File.Exists(args[2])) throw new Exception("The atomic writer's start gate did not open.");
            for (var i = 0; i < 64; i++) AtomicFile.Write(args[1], new string(args[4][0], 4096));
            return 0;
        }
        if (args[0] == "--atomic-abandon" && args.Length == 3)
        {
            using var mutex = new Mutex(false, args[1]);
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) throw new Exception("The abandonment fixture could not acquire its mutex.");
            File.WriteAllText(args[2], "owned");
            Environment.Exit(0);   // (leave while owning it; the parent's second handle keeps this named object alive)
        }
        throw new ArgumentException("Unknown atomic test child.");
    }

    sealed record AtomicRun(Process Process, Task<string> Output, Task<string> Error);

    static AtomicRun StartAtomicChild(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        return new AtomicRun(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    static void FinishAtomicChild(AtomicRun run)
    {
        Require(run.Process.WaitForExit(30000), "An atomic writer did not finish within 30 seconds.");
        Require(run.Process.ExitCode == 0,
                "An atomic writer failed: " + run.Error.GetAwaiter().GetResult() + run.Output.GetAwaiter().GetResult());
    }

    static void CloseAtomicChild(AtomicRun run)
    {
        if (!run.Process.HasExited) { run.Process.Kill(entireProcessTree: true); run.Process.WaitForExit(10000); }
        run.Process.Dispose();
    }

    static void CheckAtomicReleased(string name)
    {
        // (a wait on this thread would be reentrant and would not prove the production write released ownership)
        var child = StartAtomicChild("--atomic-acquire", name);
        try { FinishAtomicChild(child); }
        finally { CloseAtomicChild(child); }
    }

    static void AtomicChecks()
    {
        foreach (var existing in new[] { false, true })
        {
            var root = Folder("atomic-processes-" + existing);
            var target = Path.Combine(root, "both.json");
            var gate = Path.Combine(root, "go");
            if (existing) File.WriteAllText(target, new string('z', 4096));
            var runs = new List<AtomicRun>();
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    // (Windows callers may spell the same file differently; Linux paths remain case-sensitive)
                    var path = OperatingSystem.IsWindows() && i == 3 ? target.ToUpperInvariant() : target;
                    runs.Add(StartAtomicChild("--atomic-write", path, gate, Path.Combine(root, "ready-" + i), ((char)('a' + i)).ToString()));
                }
                var until = DateTime.UtcNow.AddSeconds(30);
                bool Ready() => Enumerable.Range(0, runs.Count).All(i => File.Exists(Path.Combine(root, "ready-" + i)));
                while (!Ready() && DateTime.UtcNow < until) Thread.Sleep(5);
                Require(Ready(), "All four writers must be waiting before the shared start gate opens.");
                File.WriteAllText(gate, "go");
                foreach (var run in runs) FinishAtomicChild(run);
                var result = File.ReadAllText(target);
                Require(result.Length == 4096 && result[0] >= 'a' && result[0] <= 'd' && result.All(c => c == result[0]),
                        "Concurrent processes leave one complete write, for a new or an existing file.");
                Require(Directory.GetFiles(root, "*.tmp").Length == 0, "Concurrent writers leave no temporary files.");
            }
            finally { foreach (var run in runs) CloseAtomicChild(run); }
        }

        if (OperatingSystem.IsWindows())
        {
            var root = Folder("atomic-abandoned");
            var target = Path.Combine(root, "settings.json");
            var ready = Path.Combine(root, "owned");
            var name = AtomicFile.MutexName(target);
            using var keeper = new Mutex(false, name);
            var child = StartAtomicChild("--atomic-abandon", name, ready);
            try { FinishAtomicChild(child); }
            finally { CloseAtomicChild(child); }
            Require(File.Exists(ready), "The exiting child really owned the mutex.");
            AtomicFile.Write(target, "complete after abandoned owner");
            Require(File.ReadAllText(target) == "complete after abandoned owner", "A writer recovers ownership after a process exits.");
            // (without the production wait this would still throw AbandonedMutexException, not pass)
            CheckAtomicReleased(name);

            var locked = Path.Combine(root, "locked.json");
            var lockedName = AtomicFile.MutexName(locked);
            using var lockedKeeper = new Mutex(false, lockedName);
            File.WriteAllText(locked, "keep the old bytes");
            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var refused = false;
                try { AtomicFile.Write(locked, "replacement"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { refused = true; }
                Require(refused && File.ReadAllText(locked) == "keep the old bytes", "A genuinely locked destination fails with its old bytes intact.");
                Require(Directory.GetFiles(root, "*.tmp").Length == 0, "A refused write removes its own temporary file.");
                CheckAtomicReleased(lockedName);
            }
            AtomicFile.Write(locked, "written after unlock");
            Require(File.ReadAllText(locked) == "written after unlock", "A failed replacement released its mutex for the next write.");
        }
    }
}
