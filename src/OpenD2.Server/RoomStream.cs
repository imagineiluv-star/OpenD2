using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using OpenD2.Online;

namespace OpenD2.Server;

// One sender and receiver per connection; no unbounded snapshot queue.
internal static class RoomStream
{
    private static readonly ConcurrentDictionary<string, byte> active = new();
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    internal static async Task Run(HttpContext context, Realm realm, Guid room, string token)
    {
        realm.State(token, room); // Authorize before upgrading the HTTP response.
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains("opend2.room.v1"))
            throw new Rejected(400, "Room stream protocol required.");
        // Native clients use bearer headers. Do not expose a cookie/browser endpoint.
        if (context.Request.Headers.ContainsKey("Origin")) throw new Rejected(403, "Native client required.");
        if (!active.TryAdd(token, 0)) throw new Rejected(409, "One room stream per session.");
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync("opend2.room.v1");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            Task receive = Receive(socket, stop.Token), send = Push(socket, realm, room, token, stop.Token);
            try { await Task.WhenAny(receive, send); }
            finally
            {
                stop.Cancel(); socket.Abort();
                try { await Task.WhenAll(receive, send); }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
        }
        finally { active.TryRemove(token, out _); }
    }

    private static async Task Receive(WebSocket socket, CancellationToken stop)
    {
        // Application messages are not accepted here; commands retain HTTP validation.
        // This receive also processes protocol ping/pong and detects client close.
        await socket.ReceiveAsync(new byte[1].AsMemory(), stop);
    }

    private static async Task Push(WebSocket socket, Realm realm, Guid room, string token, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40));
        do
        {
            RoomUpdate update;
            try { update = new(realm.State(token, room)); }
            catch (Rejected error) { update = new(null, error.Status, error.Message); }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(update, json);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("Room update exceeds 1 MiB.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
            if (update.Status != 200)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Room access ended.", deadline.Token);
                // Let the receiver observe the error/close before disposing the socket.
                // An unresponsive peer still cannot hold this connection indefinitely.
                await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
                return;
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }
}
