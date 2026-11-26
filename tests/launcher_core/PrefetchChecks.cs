using LongshotLauncher.Core;

static partial class Program
{
    /// ToolsPrefetch: one fetch per version shared by the early start and the wizard, its progress for late watchers, a failed one
    /// started again, one install at a time, a stopped one gone, an early guess stopped when the wizard wants another version,
    /// Idle when the last ends, and which version is worth fetching early (an update's only when the player chose it)
    static async Task PrefetchChecks()
    {
        var calls = new Dictionary<string, int>();
        var gates = new Dictionary<string, TaskCompletionSource>();
        var failNext = false;
        var chain = new Toolchain("v3.0.0", Path.GetTempPath(), null);
        var prefetch = new ToolsPrefetch(async (tag, progress, cancel) =>
        {
            lock (calls) calls[tag] = calls.GetValueOrDefault(tag) + 1;
            progress(new ToolsDownloadProgress(0, 1000));
            Task wait;
            lock (gates) wait = (gates.TryGetValue(tag, out var g) ? g : gates[tag] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            await wait.WaitAsync(cancel);
            if (failNext)
            {
                failNext = false;
                throw new IOException("the download stopped");
            }
            progress(new ToolsDownloadProgress(1, 1000));
            return chain with { Tag = tag };
        });
        TaskCompletionSource Gate(string tag)
        {
            lock (gates) return gates.TryGetValue(tag, out var g) ? g : gates[tag] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var idles = 0;
        prefetch.Idle += () => Interlocked.Increment(ref idles);
        var early = prefetch.Get("v3.0.0", early: true);
        var joined = prefetch.Get("v3.0.0");
        Require(ReferenceEquals(early, joined) && early.Early, "The wizard joins the fetch the launcher started early, not a second download.");
        await WaitUntil(() => early.Last.Total == 1000);
        Require(prefetch.Busy && prefetch.Tags.SequenceEqual(new[] { "v3.0.0" }), "A running fetch keeps pruning off, and its version is kept.");
        var cleaned = false;
        Require(!prefetch.CleanStaging(() => cleaned = true) && !cleaned, "Staging cleanup refuses immediately while a fetch holds it.");
        var seen = new List<ToolsDownloadProgress>();
        using (early.Watch(p => { lock (seen) seen.Add(p); }))
        {
            Require(seen.Count == 1 && seen[0].Total == 1000 && early.Downloaded, "A late watcher is told how far the fetch is at once.");
            Gate("v3.0.0").SetResult();
            var t = await early.Task;
            Require(t.Tag == "v3.0.0" && seen[^1].Fraction == 1, "The watcher sees the fetch finish, and gets the version's tools.");
        }
        await WaitUntil(() => !prefetch.Busy);
        Require(prefetch.CleanStaging(() => cleaned = true) && cleaned, "Staging cleanup runs after the fetch ends.");
        Require(calls["v3.0.0"] == 1 && idles == 1, "One download for both, and nothing runs after it: Idle once.");
        Require(ReferenceEquals(prefetch.Get("v3.0.0"), early), "A finished fetch is kept: a later build finds its tools at once.");

        // (a failed fetch is started again by the next Get: the wizard's Retry)
        failNext = true;
        var bad = prefetch.Get("v3.1.0");
        Gate("v3.1.0").SetResult();
        await ThrowsAsync<IOException>(() => bad.Task, "A failed fetch fails for whoever waits on it.");
        var again = prefetch.Get("v3.1.0");
        Require(!ReferenceEquals(bad, again) && (await again.Task).Tag == "v3.1.0" && calls["v3.1.0"] == 2,
                "A fetch that failed starts again when it is asked for (Retry).");

        // (one install at a time: they share the tools' .downloads folder, parts named by their SHA-256)
        // (which of the two starts first is the thread pool's choice: either, but only one at a time)
        bool Called(string tag) { lock (calls) return calls.ContainsKey(tag); }
        var fetches = new Dictionary<string, ToolsPrefetch.Fetch> { ["v3.5.0"] = prefetch.Get("v3.5.0", early: true), ["v3.6.0"] = prefetch.Get("v3.6.0", early: true) };
        await WaitUntil(() => Called("v3.5.0") || Called("v3.6.0"));
        await Task.Delay(50);
        var (began, waiting) = Called("v3.5.0") ? ("v3.5.0", "v3.6.0") : ("v3.6.0", "v3.5.0");
        Require(!Called(waiting) && fetches[waiting].Last.Total == 0 && prefetch.Busy, "A second version waits for the first install to end.");
        Gate(began).SetResult();
        await fetches[began].Task;
        await WaitUntil(() => Called(waiting));
        Gate(waiting).SetResult();
        await fetches[waiting].Task;
        await WaitUntil(() => !prefetch.Busy);

        // (the wizard wants another version: the early guess still running stops, and leaves the next one free to start)
        var guess = prefetch.Get("v3.7.0", early: true);
        await WaitUntil(() => calls.GetValueOrDefault("v3.7.0") == 1);
        var chosen = prefetch.Get("v3.8.0");
        await ThrowsAsync<OperationCanceledException>(() => guess.Task, "The early guess stops when the wizard asks for another version.");
        await WaitUntil(() => calls.GetValueOrDefault("v3.8.0") == 1);
        Require(!chosen.Early && !prefetch.Tags.Contains("v3.7.0"), "A stopped fetch is not kept from pruning.");

        // (the wizard left step 1: its fetch stops, nothing goes on unseen; asked again, it starts again)
        chosen.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => chosen.Task, "Cancel stops the fetch.");
        await WaitUntil(() => !prefetch.Busy);
        var restarted = prefetch.Get("v3.8.0");
        Require(!ReferenceEquals(restarted, chosen), "A stopped fetch starts again when it is asked for.");
        Gate("v3.8.0").SetResult();
        Require((await restarted.Task).Tag == "v3.8.0" && calls["v3.8.0"] == 2, "The second try fetches the version.");
        await WaitUntil(() => !prefetch.Busy);

        // (the setting, off unless chosen, kept with the others)
        var settingsPath = Path.Combine(Path.GetTempPath(), $"prefetch-settings-{Guid.NewGuid():N}.json");
        try
        {
            Require(!new LauncherSettings().DownloadUpdatesEarly, "An update's tools are not downloaded early unless the player chose it.");
            new LauncherSettings { DownloadUpdatesEarly = true }.Save(settingsPath);
            Require(LauncherSettings.Load(settingsPath).DownloadUpdatesEarly, "The choice is saved.");
        }
        finally { File.Delete(settingsPath); }

        // (the version worth fetching early)
        var none = new List<BuildEntry>();
        Require(ToolsPrefetch.Likely(none, "v3.2.0", _ => null, _ => false, false) == "v3.2.0",
                "No builds yet: the newest of the channel new builds start on (the first build).");
        Require(ToolsPrefetch.Likely(none, "v3.2.0", _ => null, tag => tag == "v3.2.0", false) == null, "Tools already on this PC are not fetched.");
        Require(ToolsPrefetch.Likely(none, null, _ => null, _ => false, false) == null, "No answer from the update check: nothing is fetched.");
        var a = new BuildEntry { Id = "a", Version = "v3.0.0" };
        var b = new BuildEntry { Id = "b", Version = "v3.1.0", Default = true };
        var builds = new List<BuildEntry> { a, b };
        Require(ToolsPrefetch.Likely(builds, "v3.2.0", e => e.Id == "b" ? "v3.1.1" : "v3.0.9", _ => false, false) == null,
                "With builds, an update's tools are not fetched early unless the player chose it in Settings.");
        Require(ToolsPrefetch.Likely(builds, "v3.2.0", e => e.Id == "b" ? "v3.1.1" : "v3.0.9", _ => false, true) == "v3.1.1",
                "Chosen in Settings: the default build's update.");
        Require(ToolsPrefetch.Likely(builds, "v3.2.0", _ => null, _ => false, true) == null, "With builds and no update: nothing is fetched.");
        Require(ToolsPrefetch.Likely(new List<BuildEntry> { a }, "v3.2.0", e => "v3.0.9", _ => false, true) == "v3.0.9",
                "With no default marked: the first build's update.");
        Require(ToolsPrefetch.EnabledBy(_ => null) && !ToolsPrefetch.EnabledBy(n => n == "LONGSHOT_LAUNCHER_NO_PREFETCH" ? "1" : null),
                "LONGSHOT_LAUNCHER_NO_PREFETCH=1 turns the early download off.");
    }

    static async Task WaitUntil(Func<bool> ok)
    {
        for (var i = 0; i < 500 && !ok(); i++) await Task.Delay(10);
        Require(ok(), "Timed out waiting.");
    }
}
