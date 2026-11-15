namespace LongshotLauncher.Core;

/// Release files and release lists through Cloudflare's edge: a download from git.magicbean.dev of a release file
/// (/&lt;owner&gt;/&lt;repo&gt;/releases/download/...) or a package (/api/packages/...), and the API's release list and a release by its tag
/// (/api/v1/repos/&lt;owner&gt;/&lt;repo&gt;/releases, .../releases/tags/&lt;tag&gt;), are tried first at git-cdn.magicbean.dev, the same path
/// (served from the same files, cached), then at git.magicbean.dev when the CDN fails: no answer, a TLS error, a timeout or a
/// stall, or HTTP 403, 404 or 5xx. Anything else goes as it is: the rest of the API, other hosts, a link already on git-cdn.
/// The CDN keeps a release list for a minute, and longer while git.magicbean.dev is down (magicbean-infra's git-cdn), so a
/// list may be a minute old: an update is only ever to a newer version and each release's files are checked by its signed
/// SHA256SUMS, so an old list only offers the new version a minute later. A file that is not the one its release names (size,
/// SHA-256) is not a failure of the CDN: it fails as before, the callers' checks unchanged. A download the CDN cut short
/// starts again from the stream's start, so no half of the CDN's file stays in front of the server's; a stream that cannot go
/// back (not seekable) keeps the CDN's failure. LONGSHOT_LAUNCHER_NO_CDN=1: no CDN, everything from git.magicbean.dev
public sealed class CdnFetcher(IFetcher inner, bool enabled = true) : IFetcher
{
    public const string Origin = "https://git.magicbean.dev", Cdn = "https://git-cdn.magicbean.dev";
    public const string DisableVariable = "LONGSHOT_LAUNCHER_NO_CDN";

    public bool Enabled { get; } = enabled;

    /// Whether the CDN is on: LONGSHOT_LAUNCHER_NO_CDN=1 turns it off
    public static bool EnabledByEnvironment(Func<string, string?>? getenv = null) =>
        (getenv ?? Environment.GetEnvironmentVariable)(DisableVariable) != "1";

    /// The CDN's link for a release file or package on git.magicbean.dev; null for any other link
    public static string? CdnOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort
            || !string.Equals(u.Host, "git.magicbean.dev", StringComparison.OrdinalIgnoreCase) || u.UserInfo.Length > 0)
            return null;
        var path = u.AbsolutePath;
        var release = path.Split('/') is [_, { Length: > 0 }, { Length: > 0 }, "releases", "download", ..];
        return release || path.StartsWith("/api/packages/", StringComparison.Ordinal) ? Cdn + u.PathAndQuery : null;
    }

    /// The CDN's link for a release list (any page) or a release by its tag on git.magicbean.dev's API; null for any other link
    public static string? ListCdnOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort
            || !string.Equals(u.Host, "git.magicbean.dev", StringComparison.OrdinalIgnoreCase) || u.UserInfo.Length > 0)
            return null;
        var list = u.AbsolutePath.Split('/') is
            ["", "api", "v1", "repos", { Length: > 0 }, { Length: > 0 }, "releases"]
            or ["", "api", "v1", "repos", { Length: > 0 }, { Length: > 0 }, "releases", "tags", { Length: > 0 }];
        return list ? Cdn + u.PathAndQuery : null;
    }

    /// A tool's own links (tools.lock: its git-cdn link, the same file on git.magicbean.dev, where it came from), which Toolchains
    /// tries in order through Inner, moving on also when a link gives other bytes: each git.magicbean.dev link with its CDN link in
    /// front when the list lacks it; with the CDN off, a git-cdn link whose git.magicbean.dev twin is listed is left out
    public IReadOnlyList<string> Expand(IReadOnlyList<string> urls)
    {
        if (!Enabled)
            return urls.Where(u => !(u.StartsWith(Cdn + "/", StringComparison.Ordinal) && urls.Contains(Origin + u[Cdn.Length..]))).ToList();
        var links = new List<string>();
        foreach (var u in urls)
        {
            if (CdnOf(u) is { } cdn && !links.Contains(cdn) && !urls.Contains(cdn)) links.Add(cdn);
            if (!links.Contains(u)) links.Add(u);
        }
        return links;
    }

    /// The fetcher this one wraps: a download as it is, with no CDN
    public IFetcher Inner => inner;

    /// Whether the CDN's failure is worth the server's try: no answer (Status null: connection, TLS, timeout, stall), or 403, 404, 5xx
    public static bool FallsBack(FetchException e) => e.Status is null or 403 or 404 or >= 500;

    /// The fall back in the launcher's log: the CDN's own request is there already, with its answer
    static void Fell(FetchException e) =>
        LauncherLog.Add($"git-cdn.magicbean.dev failed ({(e.Status is int s ? "HTTP " + s : "no answer")}): trying git.magicbean.dev");

    public async Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel)
    {
        if (Enabled && ListCdnOf(url) is { } cdn)
        {
            try { return await inner.GetTextAsync(cdn, accept, cancel).ConfigureAwait(false); }
            catch (FetchException e) when (FallsBack(e)) { Fell(e); }
        }
        return await inner.GetTextAsync(url, accept, cancel).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
    {
        if (!Enabled || CdnOf(url) is not { } cdn)
        {
            await inner.DownloadAsync(url, to, progress, cancel).ConfigureAwait(false);
            return;
        }
        var start = to.CanSeek ? to.Position : 0;
        long wrote = 0;
        try
        {
            await inner.DownloadAsync(cdn, to, have =>
            {
                wrote = have;
                progress?.Invoke(have);
            }, cancel).ConfigureAwait(false);
            return;
        }
        catch (FetchException e) when (FallsBack(e))
        {
            Fell(e);
            if (wrote > 0 || (to.CanSeek && to.Position != start))
            {
                if (!to.CanSeek) throw;
                to.SetLength(start);
                to.Position = start;
            }
        }
        await inner.DownloadAsync(url, to, progress, cancel).ConfigureAwait(false);
    }
}
