using System.Net;
using System.Net.Sockets;
using LongshotLauncher.Core;

/// The launcher log's connection lines (Connections): the addresses a name resolved to, each address tried in order with how it
/// ended, the request's timeout where it was spent, and HttpFetcher's failures still read as before (NetFailure's words)
static partial class Program
{
    static async Task ConnectionChecks()
    {
        static IPAddress[] Both() => new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("192.0.2.1") };
        static Task<IPAddress[]> Resolve(string host, CancellationToken cancel) => Task.FromResult(Both());

        // (IPv6 first, as the name service ordered it, refused; then IPv4, connected)
        LauncherLog.Clear();
        var asked = new List<string>();
        var stream = await Connections.OpenAsync("git.example", 443, Resolve, (at, _) =>
        {
            asked.Add(at.ToString());
            return at.AddressFamily == AddressFamily.InterNetworkV6
                ? ValueTask.FromException<Stream>(new SocketException((int)SocketError.NetworkUnreachable))
                : ValueTask.FromResult<Stream>(new MemoryStream());
        }, CancellationToken.None);
        var lines = LauncherLog.Tail(10);
        Require(stream is MemoryStream && asked.SequenceEqual(new[] { "[2001:db8::1]:443", "192.0.2.1:443" })
                && lines.Count == 3
                && lines[0].Contains(" DNS git.example: 2001:db8::1, 192.0.2.1 (")
                && lines[1].Contains(" connect git.example [2001:db8::1]:443: NetworkUnreachable after ")
                && lines[2].Contains(" connect git.example 192.0.2.1:443: connected in "),
                "The addresses, each tried in order, and how each ended: " + string.Join(" | ", lines));

        // (the request's timeout: where it was spent)
        LauncherLog.Clear();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await ThrowsAsync<OperationCanceledException>(async () => await Connections.OpenAsync("git.example", 443, Resolve,
                async (at, c) => { await Task.Delay(Timeout.Infinite, c); return Stream.Null; }, timeout.Token),
                "A connect with no answer ends with the request's timeout.");
        lines = LauncherLog.Tail(10);
        Require(lines.Count == 2 && lines[1].Contains(" connect git.example [2001:db8::1]:443: no answer, stopped after "),
                "The address the timeout was spent on: " + string.Join(" | ", lines));

        // (every address failed: the last one's SocketException, as .NET's own connect throws; usable leaves IPv6 out)
        LauncherLog.Clear();
        asked.Clear();
        var last = new SocketException((int)SocketError.ConnectionRefused);
        try
        {
            await Connections.OpenAsync("git.example", 443, Resolve, (at, _) =>
            {
                asked.Add(at.ToString());
                return ValueTask.FromException<Stream>(last);
            }, CancellationToken.None, a => a.AddressFamily == AddressFamily.InterNetwork);
            Require(false, "Every address refused: no stream.");
        }
        catch (SocketException e) { Require(e == last && asked.SequenceEqual(new[] { "192.0.2.1:443" }), "The last address's error, IPv4 only: " + string.Join(", ", asked)); }

        // (Happy Eyeballs: an IPv6 address that never answers doesn't hold up an IPv4 one: IPv4 starts after the stagger, wins,
        // and the IPv6 connect is stopped)
        LauncherLog.Clear();
        var v6Stopped = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        stream = await Connections.OpenAsync("git.example", 443, Resolve, async (at, c) =>
        {
            if (at.AddressFamily == AddressFamily.InterNetworkV6)
            {
                try { await Task.Delay(Timeout.Infinite, c); }
                catch (OperationCanceledException) { v6Stopped = true; throw; }
            }
            return new MemoryStream();
        }, CancellationToken.None, stagger: TimeSpan.FromMilliseconds(50));
        var took = clock.ElapsedMilliseconds;
        for (var i = 0; i < 300 && !v6Stopped; i++) await Task.Delay(10);   // (its stop ends on its own thread)
        lines = LauncherLog.Tail(10);
        Require(stream is MemoryStream && took < 5000 && v6Stopped
                && lines.Count == 3 && lines[1].Contains(" connect git.example 192.0.2.1:443: connected in ")
                && lines[2].Contains(" connect git.example [2001:db8::1]:443: stopped after ") && lines[2].EndsWith(", 192.0.2.1:443 connected first"),
                "A silent IPv6 address is overtaken by IPv4 after the stagger, and stopped: " + string.Join(" | ", lines));

        // (the order: the name service's first, then the families taking turns)
        var order = Connections.Interleave(new[] { IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::2"), IPAddress.Parse("2001:db8::3"),
                                                   IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2") });
        Require(string.Join(" ", order) == "2001:db8::1 192.0.2.1 2001:db8::2 192.0.2.2 2001:db8::3", "Interleaved: " + string.Join(" ", order));
        Require(string.Join(" ", Connections.Interleave(new[] { IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2") })) == "192.0.2.1 192.0.2.2",
                "One family: its own order.");

        // (a connect that loses but connects later anyway: its stream is closed, not left open)
        LauncherLog.Clear();
        var late = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateStream = new TrackedStream();
        stream = await Connections.OpenAsync("git.example", 443, Resolve, (at, _) => at.AddressFamily == AddressFamily.InterNetworkV6
            ? new ValueTask<Stream>(late.Task) : ValueTask.FromResult<Stream>(new MemoryStream()), CancellationToken.None, stagger: TimeSpan.FromMilliseconds(20));
        late.SetResult(lateStream);
        for (var i = 0; i < 300 && !lateStream.Disposed; i++) await Task.Delay(10);
        Require(stream is MemoryStream, "IPv4 kept; the late IPv6 stream closed.");

        // (the request gives up with two connects going: both stop, each with where its time went)
        LauncherLog.Clear();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            await ThrowsAsync<OperationCanceledException>(async () => await Connections.OpenAsync("git.example", 443, Resolve,
                async (at, c) => { await Task.Delay(Timeout.Infinite, c); return Stream.Null; }, timeout.Token, stagger: TimeSpan.FromMilliseconds(50)),
                "No address answers: the request's timeout.");
        lines = LauncherLog.Tail(10);
        Require(lines.Count == 3 && lines.Skip(1).All(l => l.Contains(": no answer, stopped after "))
                && lines.Any(l => l.Contains("[2001:db8::1]:443")) && lines.Any(l => l.Contains("192.0.2.1:443")),
                "Both connects stopped at the timeout, each logged: " + string.Join(" | ", lines));

        // (every address refused at once: no stagger waited for; the last one's error, as .NET's own connect throws)
        LauncherLog.Clear();
        clock.Restart();
        await ThrowsAsync<SocketException>(async () => await Connections.OpenAsync("git.example", 443, Resolve,
            (_, _) => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)), CancellationToken.None,
            stagger: TimeSpan.FromSeconds(30)), "Every address refused.");
        Require(clock.ElapsedMilliseconds < 5000 && LauncherLog.Tail(10).Count(l => l.Contains(": ConnectionRefused after ")) == 2,
                "A failure starts the next address at once: " + string.Join(" | ", LauncherLog.Tail(10)));

        // (a name that does not resolve: logged, and still the name service's error)
        LauncherLog.Clear();
        await ThrowsAsync<SocketException>(async () => await Connections.OpenAsync("git.example", 443,
            (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
            (_, _) => ValueTask.FromResult(Stream.Null), CancellationToken.None), "No address.");
        Require(LauncherLog.Tail(1).Single().Contains(" DNS git.example: HostNotFound after "), "A failed lookup is logged: " + LauncherLog.Tail(1).Single());

        // (through HttpFetcher: a name that does not resolve still reads as one, and a server that is not TLS as a secure connection
        // that failed, its address connected first)
        LauncherLog.Clear();
        using var http = new HttpFetcher(TimeSpan.FromSeconds(10));
        try
        {
            await http.GetTextAsync("https://longshot-test.invalid/x", null, CancellationToken.None);
            Require(false, "longshot-test.invalid does not resolve.");
        }
        catch (FetchException e)
        {
            Require(e.Message.StartsWith("This PC could not look up longshot-test.invalid's address.", StringComparison.Ordinal),
                    "A failed lookup reads as before: " + e.Message);
        }
        Require(LauncherLog.Tail(5).Any(l => l.Contains(" DNS longshot-test.invalid: ")), "The lookup is logged: " + string.Join(" | ", LauncherLog.Tail(5)));

        LauncherLog.Clear();
        var plain = new TcpListener(IPAddress.Loopback, 0);
        plain.Start();
        try
        {
            var port = ((IPEndPoint)plain.LocalEndpoint).Port;
            var answer = Task.Run(async () =>
            {
                using var c = await plain.AcceptTcpClientAsync();
                var s = c.GetStream();
                _ = await s.ReadAsync(new byte[4096]);
                await s.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"u8.ToArray());
                await Task.Delay(500);
            });
            try
            {
                await http.GetTextAsync($"https://127.0.0.1:{port}/x", null, CancellationToken.None);
                Require(false, "A plain HTTP server is no TLS one.");
            }
            catch (FetchException e)
            {
                Require(e.Message.StartsWith("The secure connection to 127.0.0.1 failed.", StringComparison.Ordinal), "Not TLS reads as before: " + e.Message);
            }
            lines = LauncherLog.Tail(5);
            Require(lines.Any(l => l.Contains($" connect 127.0.0.1 127.0.0.1:{port}: connected in "))
                    && lines.Any(l => l.Contains(": SecureConnectionError (")), "Connected, then TLS failed: " + string.Join(" | ", lines));
            await answer;
        }
        finally
        {
            plain.Stop();
        }
        LauncherLog.Clear();
    }

    sealed class TrackedStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
