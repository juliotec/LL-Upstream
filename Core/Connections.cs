using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LongshotLauncher.Core;

/// A new connection, as HttpFetcher makes it, in the launcher's log: the addresses a name resolved to, then each address tried,
/// with how long it took and how it ended. A report then shows what a request's own line cannot: whether IPv6 or IPv4 was tried,
/// which address answered (Cloudflare's, or a filter's), and where a timeout was spent. A pooled connection that is used again is
/// not logged again. .NET goes on with a connect after the request that asked for it timed out, so a connect's line can come after
/// its request's "timed out" line (by about 5 s on Windows, measured).
///
/// The addresses are tried as browsers do (Happy Eyeballs, RFC 8305): the name service's first, then the families taking turns
/// (IPv6, IPv4, IPv6...); each next one starts when the one before fails, or after Stagger with no answer while it goes on; the
/// first to connect is kept and the others stopped. .NET's own connect tries them one after another, each to its end: on a PC
/// whose IPv6 doesn't reach the internet, a connect to an IPv6 address waits ~21 s for nothing, longer than the update check's
/// 20 s, and git.magicbean.dev "did not answer" while a browser on the same PC opens it at once
public static class Connections
{
    /// How long a connect goes on alone before the next address starts beside it (RFC 8305's Connection Attempt Delay)
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    /// A stream to host:port. resolve: the name's addresses (not called for an address); usable: the addresses this PC can try
    /// (null: all); connect: one address; stagger: Stagger (the tests pass theirs). Throws as .NET's own connect does: the name
    /// service's SocketException, the last address's SocketException, or OperationCanceledException (the request's timeout)
    public static async ValueTask<Stream> OpenAsync(string host, int port, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
                                                    Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, CancellationToken cancel,
                                                    Func<IPAddress, bool>? usable = null, TimeSpan? stagger = null)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = new[] { literal };
        else
        {
            var clock = Stopwatch.StartNew();
            try { addresses = await resolve(host, cancel).ConfigureAwait(false); }
            catch (SocketException e)
            {
                LauncherLog.Add($"DNS {host}: {e.SocketErrorCode} after {clock.ElapsedMilliseconds} ms");
                throw;
            }
            catch (OperationCanceledException)
            {
                LauncherLog.Add($"DNS {host}: no answer, stopped after {clock.ElapsedMilliseconds} ms");
                throw;
            }
            LauncherLog.Add($"DNS {host}: {(addresses.Length == 0 ? "no address" : string.Join(", ", addresses.Select(a => a.ToString())))}"
                            + $" ({clock.ElapsedMilliseconds} ms)");
        }
        var tries = Interleave(addresses.Where(a => usable?.Invoke(a) ?? true).ToList());
        if (tries.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        var wait = stagger ?? Stagger;
        using var stopOthers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var running = new List<(Task<Stream> Task, IPEndPoint At, Stopwatch Clock)>();
        Exception? last = null;
        var next = 0;
        void StartNext()
        {
            var at = new IPEndPoint(tries[next++], port);
            running.Add((Attempt(connect, at, stopOthers.Token), at, Stopwatch.StartNew()));
        }
        try
        {
            StartNext();
            while (running.Count > 0)
            {
                var waits = running.Select(r => (Task)r.Task).ToList();
                var staggered = next < tries.Count ? Task.Delay(wait, stopOthers.Token) : null;
                if (staggered != null) waits.Add(staggered);
                var done = await Task.WhenAny(waits).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if (done == staggered)
                {
                    // (no answer yet: the next address starts beside the ones still going)
                    StartNext();
                    continue;
                }
                var ended = running.First(r => r.Task == done);
                running.Remove(ended);
                if (ended.Task.IsCompletedSuccessfully)
                {
                    LauncherLog.Add($"connect {host} {ended.At}: connected in {ended.Clock.ElapsedMilliseconds} ms");
                    stopOthers.Cancel();
                    foreach (var r in running)
                    {
                        LauncherLog.Add($"connect {host} {r.At}: stopped after {r.Clock.ElapsedMilliseconds} ms, {ended.At} connected first");
                        DisposeLate(r.Task);
                    }
                    return ended.Task.Result;
                }
                last = ended.Task.Exception?.GetBaseException() ?? new SocketException((int)SocketError.SocketError);
                var why = last is SocketException se ? se.SocketErrorCode.ToString() : last.GetType().Name;
                LauncherLog.Add($"connect {host} {ended.At}: {why} after {ended.Clock.ElapsedMilliseconds} ms");
                // (a failure: the next address at once, not after the stagger)
                if (next < tries.Count) StartNext();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // (the request gave up: every connect still going stops, and says where the time went)
            stopOthers.Cancel();
            foreach (var r in running)
            {
                LauncherLog.Add($"connect {host} {r.At}: no answer, stopped after {r.Clock.ElapsedMilliseconds} ms");
                DisposeLate(r.Task);
            }
            throw;
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(last!);
        throw last!;
    }

    /// One address's connect as a task that ends when it does (a connect that throws at once is a failed task, not an exception here)
    static Task<Stream> Attempt(Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, IPEndPoint at, CancellationToken cancel)
    {
        try { return connect(at, cancel).AsTask(); }
        catch (Exception e) { return Task.FromException<Stream>(e); }
    }

    /// A connect that lost: its stream, should it still connect, closed when it does
    static void DisposeLate(Task<Stream> attempt) =>
        _ = attempt.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; },
                                 CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// The addresses in the order they are tried (RFC 8305, 4): the name service's first, then the two families taking turns, each
    /// in the name service's order
    public static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0) return new List<IPAddress>();
        var first = addresses[0].AddressFamily;
        var same = addresses.Where(a => a.AddressFamily == first).ToList();
        var other = addresses.Where(a => a.AddressFamily != first).ToList();
        var order = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(same.Count, other.Count); i++)
        {
            if (i < same.Count) order.Add(same[i]);
            if (i < other.Count) order.Add(other[i]);
        }
        return order;
    }

    /// HttpFetcher's connect: OpenAsync over the name service and TCP, IPv6 only where this PC has it (as .NET's own; also
    /// DOTNET_SYSTEM_NET_DISABLEIPV6=1)
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancel) =>
        OpenAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, (h, c) => Dns.GetHostAddressesAsync(h, c), TcpAsync, cancel,
                  a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6));

    static async ValueTask<Stream> TcpAsync(IPEndPoint at, CancellationToken cancel)
    {
        var socket = new Socket(at.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(at, cancel).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
