using System.Net;

namespace LongshotLauncher.Core;

/// A download that the server (or the network) failed. Status: the HTTP answer, null when the server was not reached
public sealed class FetchException : Exception
{
    public int? Status { get; }

    public FetchException(string message, int? status = null, Exception? inner = null) : base(message, inner) => Status = status;

    /// Worth trying again: no answer, a server error or the API's rate limit
    public bool Transient => Status is null or >= 500 or 429;
}

/// The launcher's only way out to the network, so that the tests need none: a text (a release list, an API answer) and a
/// file's bytes into a stream. Both throw FetchException for an answer that is not a success.
public interface IFetcher
{
    Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel);

    /// The bytes at url into to; progress: the bytes so far
    Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel);
}

/// IFetcher over HttpClient: https links only, also after a redirect. timeout: until the answer's headers (HttpClient's own, which
/// does not cover a download's body); readTimeout: the longest a download may go without a byte, after which it fails as a
/// FetchException (transient: the mirrors and Retry take it) instead of waiting until Cancel
public sealed class HttpFetcher : IFetcher, IDisposable
{
    readonly HttpClient http;
    readonly TimeSpan readTimeout;

    public HttpFetcher(TimeSpan? timeout = null, TimeSpan? readTimeout = null)
    {
        this.readTimeout = readTimeout ?? TimeSpan.FromSeconds(60);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LongshotLauncher");
    }

    static Uri Https(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri
        : throw new FetchException("Not an https link: " + url, 0);

    async Task<HttpResponseMessage> SendAsync(string url, string? accept, HttpCompletionOption option, CancellationToken cancel)
    {
        var uri = Https(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (accept != null) request.Headers.Accept.ParseAdd(accept);
        HttpResponseMessage response;
        // (each request in the launcher's log: the link, then its answer or why there was none, how long it took, the proxy)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var proxy = ProxyFor(uri);
        var via = proxy is { } p ? ", through the proxy " + p.Authority : "";
        string Took() => $"{clock.ElapsedMilliseconds} ms{via}";
        // (why, in words a player can act on: NetFailure)
        try { response = await http.SendAsync(request, option, cancel).ConfigureAwait(false); }
        catch (HttpRequestException e)
        {
            // (the log: what failed, in .NET's own terms; the player's words for it are the screen's)
            var inner = (Exception)e;
            while (inner.InnerException != null) inner = inner.InnerException;
            LauncherLog.Add($"GET {uri} failed after {Took()}: {e.HttpRequestError} ({inner.GetType().Name}: {inner.Message.Trim()})");
            throw new FetchException(NetFailure.Why(e, uri.Host, proxy), (int?)e.StatusCode, e);
        }
        catch (TaskCanceledException e) when (!cancel.IsCancellationRequested)
        {
            LauncherLog.Add($"GET {uri} timed out after {Took()}");
            throw new FetchException(NetFailure.TimedOut(uri.Host), null, e);
        }
        catch (OperationCanceledException)
        {
            LauncherLog.Add($"GET {uri} cancelled after {Took()}");
            throw;
        }
        var final = response.RequestMessage?.RequestUri;
        var moved = final != null && final != uri ? $" (redirected to {final})" : "";
        LauncherLog.Add($"GET {uri}: HTTP {(int)response.StatusCode}{moved}, {Took()}");
        if (final != null && final.Scheme != Uri.UriSchemeHttps)
        {
            response.Dispose();
            throw new FetchException("A download was redirected away from https: " + final, 0);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var host = (final ?? uri).Host;
            response.Dispose();
            throw new FetchException(NetFailure.Status(status, host), status);
        }
        return response;
    }

    /// The proxy a request to uri goes through (HttpClient's default: the system's, or HTTPS_PROXY); null for none
    public static Uri? ProxyFor(Uri uri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            return proxy.IsBypassed(uri) ? null : proxy.GetProxy(uri) is { } p && p != uri ? p : null;
        }
        catch (Exception) { return null; }
    }

    public async Task<string> GetTextAsync(string url, string? accept, CancellationToken cancel)
    {
        using var response = await SendAsync(url, accept, HttpCompletionOption.ResponseContentRead, cancel).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string url, Stream to, Action<long>? progress, CancellationToken cancel)
    {
        using var response = await SendAsync(url, null, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        await using var from = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        // (the download's end in the launcher's log too: its bytes, or where and why it stopped)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long have = 0;
        try
        {
            await CopyAsync(from, to, n =>
            {
                have = n;
                progress?.Invoke(n);
            }, readTimeout, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FetchException or OperationCanceledException)
        {
            LauncherLog.Add($"GET {url}: stopped after {have} bytes, {clock.ElapsedMilliseconds} ms: {e.Message}");
            throw;
        }
        LauncherLog.Add($"GET {url}: {have} bytes in {clock.ElapsedMilliseconds} ms");
    }

    /// from into to, failing (FetchException) when a read waits longer than readTimeout for its next bytes: the timer starts again
    /// on every read, so a slow download goes on and only a stalled one stops
    public static async Task CopyAsync(Stream from, Stream to, Action<long>? progress, TimeSpan readTimeout, CancellationToken cancel)
    {
        var buffer = new byte[1 << 20];
        long have = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        while (true)
        {
            int n;
            stall.CancelAfter(readTimeout);
            try { n = await from.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
            catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
            {
                throw new FetchException($"The download stalled: nothing came for {readTimeout.TotalSeconds:0} seconds.", null, e);
            }
            catch (IOException e) { throw new FetchException("The download connection ended unexpectedly.", null, e); }
            stall.CancelAfter(Timeout.InfiniteTimeSpan);
            if (n == 0) break;
            await to.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
            have += n;
            progress?.Invoke(have);
        }
    }

    public void Dispose() => http.Dispose();
}
