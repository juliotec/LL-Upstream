using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LongshotLauncher.Core;

/// A new connection, as HttpFetcher makes it, in the launcher's log: the addresses a name resolved to, then each address tried,
/// with how long it took and how it ended. A report then shows what a request's own line cannot: whether IPv6 or IPv4 was tried,
/// which address answered (Cloudflare's, or a filter's), and where a timeout was spent. The connection is made as .NET's own
/// is: each address in turn, in the order the name service gives, the first that connects kept. A pooled connection that is
/// used again is not logged again. .NET goes on with a connect after the request that asked for it timed out, so a connect's
/// line can come after its request's "timed out" line (by about 5 s on Windows, measured)
public static class Connections
{
    /// A stream to host:port. resolve: the name's addresses (not called for an address); usable: the addresses this PC can try
    /// (null: all); connect: one address. Throws as .NET's own connect does: the name service's SocketException, the last
    /// address's SocketException, or OperationCanceledException (the request's timeout)
    public static async ValueTask<Stream> OpenAsync(string host, int port, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
                                                    Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect, CancellationToken cancel,
                                                    Func<IPAddress, bool>? usable = null)
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
        var tries = addresses.Where(a => usable?.Invoke(a) ?? true).ToList();
        if (tries.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        SocketException? last = null;
        foreach (var address in tries)
        {
            var at = new IPEndPoint(address, port);
            var clock = Stopwatch.StartNew();
            try
            {
                var stream = await connect(at, cancel).ConfigureAwait(false);
                LauncherLog.Add($"connect {host} {at}: connected in {clock.ElapsedMilliseconds} ms");
                return stream;
            }
            catch (SocketException e)
            {
                LauncherLog.Add($"connect {host} {at}: {e.SocketErrorCode} after {clock.ElapsedMilliseconds} ms");
                last = e;
            }
            catch (OperationCanceledException)
            {
                LauncherLog.Add($"connect {host} {at}: no answer, stopped after {clock.ElapsedMilliseconds} ms");
                throw;
            }
        }
        throw last!;
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
