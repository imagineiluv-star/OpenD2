using System.Net;
using System.Net.Sockets;

namespace OpenD2.Client;

/// <summary>Machine-local lease shared by all normal client copies and user profiles.</summary>
internal static class ClientInstance
{
    // Fixed, not configurable: changing profiles or install paths must not bypass it.
    // A loopback socket works across OS users without privileged filesystem ACLs.
    private static Socket? lease;
    internal const int Port = 24682;

    internal static bool Acquire()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, Port));
            socket.Listen(1);
            lease = socket;
            // Keep the lease through scene/engine teardown. The OS also releases it
            // on a crash; there is no stale PID file to delete or racing recovery.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => lease?.Dispose();
            return true;
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            socket.Dispose();
            return false;
        }
        catch { socket.Dispose(); throw; }
    }

    internal static bool IsLocalValidation(string[] args)
    {
        var runs = args.Where(a => a.StartsWith("--run=", StringComparison.Ordinal)).ToArray();
        var servers = args.Where(a => a.StartsWith("--server=", StringComparison.Ordinal)).ToArray();
        if (runs.Length != 1 || servers.Length != 1) return false;
        string run = runs[0][6..];
        string server = servers[0][9..];
        return !string.IsNullOrEmpty(run) && run == Environment.GetEnvironmentVariable("OPEND2_ONLINE_VALIDATION_RUN")
            && Uri.TryCreate(server, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https")
            && uri.Host == "127.0.0.1" && uri.Port > 0 && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}
