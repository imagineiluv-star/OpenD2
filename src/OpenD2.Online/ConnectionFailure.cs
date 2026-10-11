using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace OpenD2.Online;

// Guidance from observed failures, not a diagnosis of firewall/NAT configuration.
// Never append inner exception text: it can contain addresses or proxy credentials.
public static class ConnectionFailure
{
    public static string Describe(Exception error)
    {
        // HTTP responses and game rejections prove the server was reached. Preserve
        // those messages rather than misdiagnosing their transport exception wrappers.
        if (error is HttpRequestException { StatusCode: not null }) return error.Message;
        var causes = new List<Exception>();
        for (Exception? cause = error; cause is not null && causes.Count < 16; cause = cause.InnerException)
            causes.Add(cause);
        if (causes.Any(e => e is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }))
            return "TLS connection failed. Check the server address against the certificate SAN, certificate dates, and the selected public root CA. Compare its SHA-256 with the host. Certificate verification remains required.";
        if (causes.Any(e => e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } or SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }))
            return "Server name could not be resolved. Check the hostname and DNS connection. For LAN hosting, use the host's displayed HTTPS address.";
        if (causes.Any(e => e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }))
            return "Connection refused. Check that the host is running and the address and TCP port match its displayed address. Check the host firewall rule for that port.";
        if (causes.Any(e => e is SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable }))
            return "Server network could not be reached. Check both PCs' network connection and the destination address. A private LAN address requires a route to that LAN; it is not an Internet invitation.";
        if (causes.Any(e => e is TimeoutException or OperationCanceledException or SocketException { SocketErrorCode: SocketError.TimedOut }))
            return "Connection timed out or was cancelled. Check the host, address and TCP port, then the network and host firewall. This alone cannot identify a firewall or NAT problem. Retry after checking; router settings are not changed automatically.";
        if (causes.Any(e => e is WebSocketException))
            return "Room connection interrupted. Check the server and network; any proxy must support WebSocket upgrades. Join/reconnect, or log in again after a server restart.";
        if (causes.Any(e => e is HttpRequestException { StatusCode: null }))
            return "Server connection failed. Check the HTTPS address, host status and network, then try Check server again.";
        return error.Message;
    }
}
