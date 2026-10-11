using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using OpenD2.Online;

internal static class ConnectionFailureContracts
{
    internal static void Run()
    {
        const string secret = "https://user:private-password@proxy.invalid";
        void Check(Exception error, string expected)
        {
            string message = ConnectionFailure.Describe(error);
            if (!message.Contains(expected, StringComparison.Ordinal) || message.Contains(secret, StringComparison.Ordinal))
                throw new Exception("Incorrect or sensitive connection guidance: " + expected);
        }
        Check(new HttpRequestException(HttpRequestError.SecureConnectionError, secret), "certificate SAN");
        Check(new WebSocketException(secret, new HttpRequestException(secret, new AuthenticationException(secret))), "certificate SAN");
        Check(new HttpRequestException(HttpRequestError.NameResolutionError, secret), "DNS");
        Check(new HttpRequestException(secret, new SocketException((int)SocketError.HostNotFound)), "DNS");
        Check(new HttpRequestException(secret, new SocketException((int)SocketError.ConnectionRefused)), "Connection refused");
        Check(new WebSocketException(secret, new SocketException((int)SocketError.HostUnreachable)), "route to that LAN");
        Check(new TaskCanceledException(secret, new TimeoutException(secret)), "cannot identify a firewall or NAT problem");
        Check(new HttpRequestException(secret, new SocketException((int)SocketError.TimedOut)), "timed out");
        Check(new WebSocketException(secret), "WebSocket upgrades");
        Check(new HttpRequestException(secret), "Check server");
        foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.BadRequest })
        {
            var rejected = new HttpRequestException("Command rejected: test", new WebSocketException(secret), code);
            if (ConnectionFailure.Describe(rejected) != rejected.Message) throw new Exception("Server rejection hidden.");
        }
        var mismatch = new InvalidDataException("Server mode does not match the selected character mode.");
        if (ConnectionFailure.Describe(mismatch) != mismatch.Message) throw new Exception("Mode mismatch hidden.");
        Console.WriteLine("PASS connection guidance: TLS/DNS/refusal/route/timeout/WSS, no transport secrets, server rejections preserved");
    }
}
