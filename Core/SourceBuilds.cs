using System.Text.Json;

namespace LongshotLauncher.Core;

/// Versions built from Longshot's source on this PC, not from a release: a pull request's head (pr-&lt;number&gt;-&lt;commit&gt;) and
/// main's newest commit (main-&lt;commit&gt;), the Versions page's Add version's Advanced. Every such build goes through EnsureAsync:
/// the newest nightly's builder (fetched first when it is not on this PC) runs `longshot-builder from-source --commit &lt;sha&gt; --out
/// &lt;versions/&lt;tag&gt;&gt; --name &lt;tag&gt; --output json` (Longshot's build.md "A builder from source"), which downloads the commit's
/// source, its Rust and tools, and compiles a builder from it into the version folder, with source.json beside it. What it makes is
/// a version folder like a release's: built, played and updated the same way
public static class SourceBuilds
{
    /// main's newest commit as git.magicbean.dev says it (Forgejo's branch API): its full hash
    public static string MainUrl => $"{Product.Longshot.Api}/branches/main";

    /// Why a build from source cannot be made by this PC's builders: theirs is from before from-source
    public static string NotYet(string tag) =>
        Tr.F("{0} is built from Longshot's source, which longshot-builder can't do yet: a launcher update will build it.", Versions.Shown(tag));

    /// main's newest commit (40 hex), or null when git.magicbean.dev did not answer or answered what cannot be read
    public static async Task<string?> MainHeadAsync(IFetcher fetcher, CancellationToken cancel = default)
    {
        try { return ParseHead(await fetcher.GetTextAsync(MainUrl, "application/json", cancel).ConfigureAwait(false)); }
        catch (FetchException) { return null; }
    }

    /// The branch API's commit id: {"name": "main", "commit": {"id": "&lt;40 hex&gt;", ...}}; null for anything else
    public static string? ParseHead(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("commit", out var c)
                   && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var id) && id.GetString() is { Length: 40 } h
                   && h.All(char.IsAsciiHexDigit) ? h.ToLowerInvariant() : null;
        }
        catch (JsonException) { return null; }
    }

    /// The commit a build from source is of (40 hex): a pull request's head as Beanstalk lists it now (find), which must still be
    /// open, buildable here (PullRequests.Problem) and at the push the tag names; main's, commit when known (as Add version read
    /// it), else asked of git.magicbean.dev, and refused when main has moved past the tag's commit since
    public static async Task<string> CommitAsync(Releases releases, string tag, string? commit,
                                                 Func<int, CancellationToken, Task<PullRequest?>> find, CancellationToken cancel = default)
    {
        if (Versions.PullRequestOf(tag) is { } wanted)
        {
            var pr = await find(wanted.Number, cancel).ConfigureAwait(false)
                     ?? throw new InvalidDataException(Tr.F("Pull request #{0} is not open any more, or Beanstalk did not answer.", wanted.Number));
            if (PullRequests.Problem(pr, releases.Platform) is { } problem) throw new InvalidDataException(problem);
            if (pr.Tag != tag) throw new InvalidDataException(Tr.F("#{0} has a newer push ({1}): pick it again to build that one.", pr.Number, pr.Head[..8]));
            return pr.Head.ToLowerInvariant();
        }
        if (Versions.MainOf(tag) == null) throw new ArgumentException("Not a build from source: " + tag, nameof(tag));
        if (commit != null && Versions.MainTag(commit) != tag) throw new ArgumentException($"{commit} is not {tag}'s commit", nameof(commit));
        var head = commit ?? await MainHeadAsync(releases.Fetcher, cancel).ConfigureAwait(false)
                   ?? throw new InvalidDataException(Tr.T("git.magicbean.dev did not say main's newest commit. Try again in a moment."));
        if (Versions.MainTag(head) != tag) throw new InvalidDataException(Tr.F("main has moved on since {0}: add main again to build its newest commit.", Versions.Shown(tag)));
        return head;
    }

    /// The command line: from-source --commit &lt;sha&gt; --out &lt;the version folder&gt; --name &lt;tag&gt; [--cache &lt;the launcher's builder
    /// cache&gt;] --output json
    public static IReadOnlyList<string> Arguments(string commit, string folder, string tag, string? cache) =>
        ["from-source", "--commit", commit, "--out", folder, "--name", tag, .. cache == null ? [] : new[] { "--cache", cache }, .. LongshotBuilder.OutputJson];

    /// A version built from source, made by the newest nightly's builder (fetched first when it is not on this PC, as any
    /// version's is: Toolchains.FetchAsync). cache: the launcher's builder cache (its --cache: what from-source keeps goes with the
    /// build files). progress: the build's whole bar (its events' overall), with no size; log: its steps and log lines. Throws
    /// InvalidDataException with the builder's words when it stops, NotYet when the nightly's builder has no from-source, and
    /// OperationCanceledException when cancelled (the process ended)
    public static async Task<Toolchain> EnsureAsync(Toolchains chains, Releases releases, string tag, string? commit,
                                                    Func<int, CancellationToken, Task<PullRequest?>> find, string? cache = null,
                                                    Action<ToolsDownloadProgress>? progress = null, Action<string>? log = null,
                                                    CancellationToken cancel = default, Func<string, string?>? getenv = null)
    {
        var sha = await CommitAsync(releases, tag, commit, find, cancel).ConfigureAwait(false);
        // (the newest nightly by the release list the update check read, else read now: its builder checks the release's signature
        // as it fetches it)
        var listed = releases.Listed ?? await releases.ListAsync(cancel).ConfigureAwait(false);
        var nightly = (listed == null ? null : ReleaseManifest.Newest(listed, Versions.Nightly, releases.Product, releases.Platform))
                      ?? throw new InvalidDataException(Tr.F("{0} is built by the newest nightly's builder, and git.magicbean.dev did not say which it is. Try again in a moment.",
                                                             Versions.Shown(tag)));
        var runner = chains.Find(nightly) ?? await chains.FetchAsync(releases, nightly, null, log, cancel, getenv).ConfigureAwait(false);
        log?.Invoke($"{tag}: built from {sha} by {nightly}'s builder");
        var answer = await LongshotBuilder.AskAsync(runner.BuilderExe, Arguments(sha, chains.PathOf(tag), tag, cache), e =>
        {
            switch (e)
            {
                case BuildEvent.Progress p:
                    progress?.Invoke(new ToolsDownloadProgress(p.Overall, 0));
                    break;
                case BuildEvent.Stage s:
                    log?.Invoke($"{tag}: {s.Id}: {s.Text}");
                    break;
                case BuildEvent.Log l:
                    log?.Invoke($"{tag}: {l.Text}");
                    break;
                case BuildEvent.Warning w:
                    log?.Invoke($"{tag}: warning {w.Code}: {w.Text}");
                    break;
            }
        }, cancel, getenv, lowPriority: true).ConfigureAwait(false);
        // (exit 2 first: a builder with no from-source says so as a usage error event too)
        if (answer.Exit == 2) throw new InvalidDataException(NotYet(tag));
        if (answer.Error is { } error) throw new InvalidDataException(error.Text);
        if (answer.Exit != 0 || !answer.Events.OfType<BuildEvent.FromSource>().Any())
            throw new InvalidDataException(answer.Why ?? Tr.F("The build tools stopped without saying why (exit code {0}).", answer.Exit));
        progress?.Invoke(new ToolsDownloadProgress(1, 0));
        return chains.Find(tag) ?? throw new InvalidDataException(Tr.F("{0}'s builder was fetched, but its version folder does not hold it.", Versions.Shown(tag)));
    }
}
