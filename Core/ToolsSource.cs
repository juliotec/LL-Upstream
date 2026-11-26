namespace LongshotLauncher.Core;

/// How far the step 1 download is: Fraction 0..1 of Total bytes
public sealed record ToolsDownloadProgress(double Fraction, long Total);

/// A version's builder for the wizard's first step: the installed one, else its release's longshot-builder fetched into its version
/// folder by a builder's own fetch (Toolchains.FetchAsync). A release without one was made for make_game.py, which this launcher
/// does not run: refused with OldVersions' words. A pull request's (pr-&lt;number&gt;-&lt;commit&gt;) or main's (main-&lt;commit&gt;): built
/// from source, SourceBuilds
public static class ToolsSource
{
    /// pullRequest: a pull request as Beanstalk lists it now, for a pull request's build. log: what was done, for the launcher's log.
    /// cache: the launcher's builder cache, which a build from source keeps what it downloads and compiles in
    public static async Task<Toolchain> EnsureAsync(Toolchains chains, Releases releases, string tag,
                                                    Action<ToolsDownloadProgress>? progress = null, CancellationToken cancel = default,
                                                    Func<int, CancellationToken, Task<PullRequest?>>? pullRequest = null,
                                                    Action<string>? log = null, Func<string, string?>? getenv = null, string? cache = null)
    {
        if (chains.Find(tag) is { } have)
        {
            progress?.Invoke(new ToolsDownloadProgress(1, 0));
            return have;
        }
        // (a pull request's or main's: built from Longshot's source, SourceBuilds)
        if (Versions.IsSourceBuild(tag))
            return await SourceBuilds.EnsureAsync(chains, releases, tag, null, pullRequest ?? ((_, _) => Task.FromResult<PullRequest?>(null)), cache,
                                                  progress, log, cancel, getenv).ConfigureAwait(false);
        return await chains.FetchAsync(releases, tag, progress, log, cancel, getenv).ConfigureAwait(false);
    }
}
