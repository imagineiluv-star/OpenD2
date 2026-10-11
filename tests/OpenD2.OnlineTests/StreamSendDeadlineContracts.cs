using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OpenD2.Online;
using OpenD2.Server;

// Deterministic blocked-transport contract, separate from actual TLS keepalive tests.
// Exercises the production RoomStream.Run without changing its two-second deadline.
internal static class StreamSendDeadlineContracts
{
    internal static async Task Run()
    {
        foreach (string mode in new[] { "realm", "open" })
        {
            string directory = Path.Combine(Path.GetTempPath(), "opend2-send-deadline-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var realm = new Realm(directory, new TestTime(), mode);
                var host = realm.Login(new("deadline-host", "deadline-test-password"), true);
                var guest = realm.Login(new("deadline-guest", "deadline-test-password"), true);
                var a = realm.CreateCharacter(host.Token, "Host");
                var b = realm.CreateCharacter(guest.Token, "Guest");
                var room = realm.CreateRoom(host.Token, new(a.Id, "Deadline", "")).Id;
                realm.Join(guest.Token, room, new(b.Id, "")); realm.Start(host.Token, room);
                using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var slow = new Transport(blockSend: true);
                using var healthy = new Transport(blockSend: false);
                Task blocked = RoomStream.Run(Context(slow, lifetime.Token), realm, room, guest.Token);
                Task peer = RoomStream.Run(Context(healthy, lifetime.Token), realm, room, host.Token);
                try
                {
                    await slow.Entered.Task.WaitAsync(lifetime.Token);
                    var watch = Stopwatch.StartNew();
                    using (var duplicate = new Transport(false))
                    {
                        try { await RoomStream.Run(Context(duplicate, lifetime.Token), realm, room, guest.Token); throw new Exception("Duplicate reservation accepted."); }
                        catch (Rejected error) when (error.Status == 409) { }
                    }
                    for (int tick = 0; tick < 10; tick++) realm.Tick();
                    while (healthy.Last?.Tick < 10 || healthy.Last is null)
                        await Task.Delay(10, lifetime.Token);
                    if (blocked.IsCompleted || peer.IsCompleted || slow.Sends != 1)
                        throw new Exception("Blocked send queued more snapshots or stopped its healthy peer.");
                    await blocked.WaitAsync(TimeSpan.FromSeconds(5));
                    if (!slow.Cancelled || !slow.Aborted || slow.Sends != 1 || lifetime.IsCancellationRequested ||
                        watch.Elapsed.TotalMilliseconds < 1500 || watch.Elapsed.TotalSeconds >= 5)
                        throw new Exception("Production send deadline did not cancel exactly one stalled send.");
                    // Reusing the very same token proves the finally block released its reservation.
                    using var replacement = new Transport(false);
                    using var replacementStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    Task reopened = RoomStream.Run(Context(replacement, replacementStop.Token), realm, room, guest.Token);
                    try
                    {
                        await replacement.Entered.Task.WaitAsync(lifetime.Token);
                        if (replacement.First is not { Sequence: 1, BaseSequence: 0, State: { Actor: 2, NextSequence: 1 } })
                            throw new Exception("Reopened sender did not begin at a fresh full baseline.");
                    }
                    finally { replacementStop.Cancel(); await reopened; }
                    Console.WriteLine("STREAM SEND DEADLINE PASS " + mode + " bounded sender, peer isolation and same-session reservation recovery (synthetic transport)");
                }
                finally { lifetime.Cancel(); await Task.WhenAll(blocked, peer); }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }

    private static HttpContext Context(WebSocket socket, CancellationToken stop)
    {
        var context = new DefaultHttpContext { RequestAborted = stop };
        context.Request.Headers.SecWebSocketProtocol = RoomInterest.Protocol;
        context.Features.Set<IHttpWebSocketFeature>(new Upgrade(socket));
        return context;
    }

    private sealed class Upgrade(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class Transport(bool blockSend) : WebSocket
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int sends;
        private RoomView? last;
        public RoomFrame? First { get; private set; }
        public RoomView? Last => Volatile.Read(ref last);
        public int Sends => Volatile.Read(ref sends);
        public bool Cancelled { get; private set; }
        public bool Aborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => RoomInterest.Protocol;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException("Unreachable receive."); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default)
        {
            int count = Interlocked.Increment(ref sends);
            var frame = JsonSerializer.Deserialize<RoomFrame>(buffer.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Volatile.Write(ref last, RoomFrames.Apply(frame, Last, count - 1, frame.State?.Id ?? Last!.Id));
            if (count == 1) First = frame;
            Entered.TrySetResult();
            if (!blockSend) return;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Cancelled = true; throw; }
        }
    }
}
