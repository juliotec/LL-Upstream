namespace LongshotLauncher.Core;

/// A version's builder tools fetched once for whoever wants them. As soon as the update check answers, the launcher starts the
/// tools of the version the player is most likely to build next (Likely: a first build's; an update's only when the player chose
/// that in Settings), so the wizard's step 1 joins a download that is already under way, or done, instead of starting it
/// (~500 MB on Windows: a minute or more of a first build). One install runs at a time: every install downloads into the tools'
/// shared .downloads folder, each part named by its SHA-256, which two versions often share (Toolchains.InstallAsync)
public sealed class ToolsPrefetch
{
    readonly Func<string, Action<ToolsDownloadProgress>, CancellationToken, Task<Toolchain>> fetch;
    readonly CancellationToken stop;
    readonly object gate = new();
    readonly Dictionary<string, Fetch> fetches = new(StringComparer.Ordinal);
    readonly SemaphoreSlim one = new(1, 1);
    int running;

    /// fetch: how a version's tools are made ready (ToolsSource.EnsureAsync); stop: ends every fetch (the launcher closing)
    public ToolsPrefetch(Func<string, Action<ToolsDownloadProgress>, CancellationToken, Task<Toolchain>> fetch, CancellationToken stop = default)
    {
        this.fetch = fetch;
        this.stop = stop;
    }

    /// The last running fetch ended (on its thread): what waited for the fetches (pruning, LauncherService.PruneTools) can run
    public event Action? Idle;

    /// One version's fetch: its task, how far it is, who watches it, and its own stop
    public sealed class Fetch
    {
        readonly object gate = new();
        readonly CancellationTokenSource cancel;
        Action<ToolsDownloadProgress>? watchers;

        internal Fetch(string tag, bool early, CancellationToken stop)
        {
            Tag = tag;
            Early = early;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        }

        public string Tag { get; }
        /// Started by the update check (PrefetchLikely), before anyone asked: the wizard says the tools came "in the background"
        public bool Early { get; }
        public Task<Toolchain> Task { get; internal set; } = null!;
        internal CancellationToken Token => cancel.Token;
        /// The last progress reported (0 of 0: still finding the version's tools, or waiting for another version's install)
        public ToolsDownloadProgress Last { get; private set; } = new(0, 0);
        /// Whether anything had to be downloaded (a total was reported), not just found on this PC
        public bool Downloaded { get; private set; }

        /// Stops the fetch (the wizard left step 1): its download ends and its staging goes; the next Get starts it again
        public void Cancel()
        {
            try { cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// on now with the last progress, then with each one after; the result stops it
        public IDisposable Watch(Action<ToolsDownloadProgress> on)
        {
            ToolsDownloadProgress now;
            lock (gate)
            {
                watchers += on;
                now = Last;
            }
            on(now);
            return new Unwatch(() => { lock (gate) watchers -= on; });
        }

        internal void Report(ToolsDownloadProgress p)
        {
            Action<ToolsDownloadProgress>? now;
            lock (gate)
            {
                Last = p;
                if (p.Total > 0) Downloaded = true;
                now = watchers;
            }
            now?.Invoke(p);
        }

        sealed class Unwatch(Action undo) : IDisposable
        {
            int done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref done, 1) == 0) undo();
            }
        }
    }

    /// The fetch of tag's tools: the one under way or finished, else a new one; one that failed or was stopped starts again.
    /// early: the update check's guess (PrefetchLikely). A Get that is not early (the wizard's) stops the early fetches of other
    /// versions still running: the player chose another version, and that guess's download would only hold this one up
    public Fetch Get(string tag, bool early = false)
    {
        List<Fetch> wrong;
        Fetch f;
        lock (gate)
        {
            wrong = early ? new List<Fetch>() : fetches.Values.Where(x => x.Early && x.Tag != tag && !x.Task.IsCompleted).ToList();
            if (fetches.TryGetValue(tag, out var have) && !have.Task.IsFaulted && !have.Task.IsCanceled) f = have;
            else
            {
                f = new Fetch(tag, early, stop);
                running++;
                f.Task = System.Threading.Tasks.Task.Run(() => RunAsync(f));
                fetches[tag] = f;
            }
        }
        foreach (var w in wrong) w.Cancel();
        return f;
    }

    async Task<Toolchain> RunAsync(Fetch f)
    {
        try
        {
            await one.WaitAsync(f.Token).ConfigureAwait(false);
            try { return await fetch(f.Tag, f.Report, f.Token).ConfigureAwait(false); }
            finally { one.Release(); }
        }
        finally
        {
            bool idle;
            lock (gate) idle = --running == 0;
            if (idle) Idle?.Invoke();
        }
    }

    /// Whether a fetch is still running: its staging folder (&lt;tag&gt;.part) and downloads must not be pruned meanwhile
    public bool Busy
    {
        get { lock (gate) return running > 0; }
    }

    /// Run staging cleanup under the same exclusion as an install.
    public bool CleanStaging(Action cleanup)
    {
        if (!one.Wait(0)) return false;
        try { cleanup(); }
        finally { one.Release(); }
        return true;
    }

    /// The versions fetched or being fetched this session (not one that failed or was stopped), which pruning keeps
    public IReadOnlyList<string> Tags
    {
        get { lock (gate) return fetches.Values.Where(f => !f.Task.IsFaulted && !f.Task.IsCanceled).Select(f => f.Tag).ToList(); }
    }

    /// The version whose tools are worth fetching before anyone asks, if any: with no builds yet, the newest of the channel new
    /// builds start on (the first build); else, only when the player chose it in Settings (updates), the default build's update.
    /// Null when it is already on this PC, or there is none
    public static string? Likely(IReadOnlyList<BuildEntry> builds, string? newestOfDefaultChannel, Func<BuildEntry, string?> updateFor,
                                 Func<string, bool> installed, bool updates)
    {
        var tag = builds.Count == 0 ? newestOfDefaultChannel
            : updates ? updateFor(builds.FirstOrDefault(b => b.Default) ?? builds[0])
            : null;
        return tag != null && !installed(tag) ? tag : null;
    }

    /// LONGSHOT_LAUNCHER_NO_PREFETCH=1: the tools download only in the wizard's step 1, as before (also a first build's)
    public static bool EnabledBy(Func<string, string?> env) => env("LONGSHOT_LAUNCHER_NO_PREFETCH") != "1";
}
