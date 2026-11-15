using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LongshotLauncher.Core;

/// Why a server could not be reached, in words a player can act on: what failed (the address, the connection, the secure
/// connection, the proxy, the server), what to check, and .NET's own words in brackets for whoever helps them. The update check's
/// line and a download's failure both say it (HttpFetcher puts it in the FetchException)
public static class NetFailure
{
    /// A request that got no answer: no address, no connection, no secure connection, no proxy, or a broken answer. proxy: the
    /// proxy the request went through (the system's or HTTPS_PROXY), null for none: then a failed address or connection is the
    /// proxy's, not the server's
    public static string Why(HttpRequestException e, string host, Uri? proxy = null)
    {
        var why = e.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError when proxy != null =>
                Tr.F("This PC is set to go through the proxy {1}, which did not answer, so {0} was not reached. Check the proxy settings, or turn the proxy off.",
                     host, proxy.Authority),
            HttpRequestError.NameResolutionError => NoAddress(host),
            HttpRequestError.ConnectionError => Connection(Find<SocketException>(e)?.SocketErrorCode, host),
            HttpRequestError.SecureConnectionError => NotSecure(host),
            HttpRequestError.ProxyTunnelError => Tr.F("The proxy this PC uses did not connect to {0}. Check the proxy settings.", host),
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded =>
                Tr.F("{0}'s answer was cut off or could not be read. A proxy, filter or antivirus may be in the way, or the server is having trouble: try again in a few minutes.", host),
            _ when e.StatusCode is { } status => Status((int)status, host),
            _ when Find<AuthenticationException>(e) != null => NotSecure(host),
            _ when Find<SocketException>(e) is { } socket => Connection(socket.SocketErrorCode, host),
            _ => NoConnection(host),
        };
        return WithDetail(why, e);
    }

    /// No answer before the timeout
    public static string TimedOut(string host) =>
        Tr.F("{0} did not answer in time. The server may be down or busy, or the network slow or blocking it: try again in a few minutes.", host);

    /// An HTTP answer that is not a success
    public static string Status(int status, string host)
    {
        var code = Enum.IsDefined((HttpStatusCode)status) ? $"HTTP {status} ({(HttpStatusCode)status})" : $"HTTP {status}";
        return status switch
        {
            429 => Tr.F("{0} has had too many requests from this network ({1}). Try again in a few minutes.", host, code),
            403 => Tr.F("{0} refused the request ({1}). It may block the VPN, proxy or network filter this PC goes through: try without it.", host, code),
            >= 500 => Tr.F("{0} is having trouble ({1}). Try again in a few minutes.", host, code),
            _ => Tr.F("{0} answered {1}.", host, code),
        };
    }

    /// A connection that failed, by what the network said
    static string Connection(SocketError? error, string host) => error switch
    {
        SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => NoAddress(host),
        SocketError.ConnectionRefused =>
            Tr.F("{0} refused the connection: the server may be down, or a firewall is blocking it. Try again in a few minutes.", host),
        SocketError.TimedOut => TimedOut(host),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
            Tr.F("This PC has no route to {0}. Check that it is online.", host),
        SocketError.ConnectionReset or SocketError.ConnectionAborted =>
            Tr.F("The connection to {0} was cut. A firewall, antivirus or VPN may be blocking the site.", host),
        _ => NoConnection(host),
    };

    static string NoAddress(string host) =>
        Tr.F("This PC could not look up {0}'s address. Check that it is online, and that no VPN, filter or DNS setting blocks the site.", host);

    static string NotSecure(string host) =>
        Tr.F("The secure connection to {0} failed. Check that this PC's date and time are right; an antivirus or a network that inspects HTTPS can also cause this.", host);

    static string NoConnection(string host) =>
        Tr.F("Could not connect to {0}. Check that this PC is online, and that no firewall, VPN or antivirus blocks the site.", host);

    /// The innermost exception's words, in brackets: the detail a helper asks for (as .NET says it, in English)
    static string WithDetail(string why, Exception e)
    {
        var inner = e;
        while (inner.InnerException != null) inner = inner.InnerException;
        var detail = inner.Message.Trim().TrimEnd('.');
        return detail.Length == 0 ? why : $"{why} ({detail})";
    }

    static T? Find<T>(Exception e) where T : Exception
    {
        for (Exception? x = e; x != null; x = x.InnerException)
            if (x is T found) return found;
        return null;
    }
}
