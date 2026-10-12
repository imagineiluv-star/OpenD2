using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

// A deliberately non-reading TLS peer. Unlike OnlineClient it has no receive timeout
// or keepalive of its own, so only the server can release the first reservation.
internal static class SilentStreamContracts
{
    internal sealed record Evidence(string Result, double EvictionMs, int ConflictResponses, long NextSequence);

    internal static async Task<Evidence> Run(string url, string ca, LoginResult session,
        Guid room, uint actor, Guid character, long sequence, Func<Task> healthyPeer,
        Action<string> pass, CancellationToken stop, (WorldContent Content, uint Region, long Epoch)? world = null)
    {
        string protocol = world is null ? RoomInterest.Protocol : RoomFrames.WorldProtocol;
        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(ca));
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.CertificateChainPolicy = new()
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true, CustomTrustStore = { root },
            ApplicationPolicy = { new("1.3.6.1.5.5.7.3.1") }
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        var uri = new UriBuilder(new Uri(http.BaseAddress, $"v1/rooms/{room}/stream")) { Scheme = "wss" }.Uri;
        ClientWebSocket Socket()
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(protocol);
            socket.Options.SetRequestHeader("Authorization", "Bearer " + session.Token);
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            return socket;
        }
        // A fresh login revokes the previous world session; the new token must state the world content again.
        if (world is { } joined)
        {
            using var join = await http.PostAsJsonAsync($"v1/rooms/{room}/join", new JoinRequest(character, "", World: joined.Content), stop);
            join.EnsureSuccessStatusCode();
        }
        using var silent = Socket();
        await silent.ConnectAsync(uri, http, stop);
        var watch = Stopwatch.StartNew();
        int conflicts = 0;
        using (var duplicate = Socket())
        {
            try { await duplicate.ConnectAsync(uri, http, stop); throw new Exception("Duplicate silent stream accepted."); }
            catch (WebSocketException) when (duplicate.HttpStatusCode == HttpStatusCode.Conflict) { conflicts++; }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // Do not read, close, abort, dispose or send on 'silent' before replacement.
        // No relogin occurs here: each handshake carries exactly the same token.
        ClientWebSocket? replacement = null;
        try
        {
            while (replacement is null)
            {
                await healthyPeer().WaitAsync(deadline.Token);
                var attempt = Socket();
                try { await attempt.ConnectAsync(uri, http, deadline.Token); replacement = attempt; }
                catch (WebSocketException) when (attempt.HttpStatusCode == HttpStatusCode.Conflict)
                { conflicts++; attempt.Dispose(); }
                catch { attempt.Dispose(); throw; }
            }
            double eviction = watch.Elapsed.TotalMilliseconds;
            pass("server retires a non-reading TLS stream while peer WSS ticks and HTTP remain live");
            var frame = await Read(replacement, stop);
            var state = RoomFrames.Apply(frame, null, 0, room);
            if (replacement.SubProtocol != protocol || frame.State is null ||
                state.Actor != actor || state.NextSequence != sequence || !state.Started || state.Host ||
                !state.Members.Any(m => m.Character == character && m.Actor == actor))
                throw new Exception("Same-session recovery lost its baseline, identity or input cursor.");
            if (world is { } expected && (state.World is null || state.World.Content != expected.Content || state.World.RegionEpoch != expected.Epoch ||
                state.Entities.Single(e => e.Id.Value == actor).Region.Value != expected.Region))
                throw new Exception("Same-session world recovery lost its content, region or visit epoch.");
            using var response = await http.PostAsJsonAsync($"v1/rooms/{room}/input/ack",
                new InputRequest(sequence, CommandKind.SetMove, Region: world?.Region, RegionEpoch: world?.Epoch), stop);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<InputReceipt>(stop)
                ?? throw new Exception("Missing recovered input receipt.");
            if (receipt.Room != room || receipt.Actor != actor || receipt.NextSequence != sequence + 1)
                throw new Exception("Recovered session did not accept exactly one new input.");
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(stop);
            observation.CancelAfter(TimeSpan.FromSeconds(10));
            while (state.NextSequence != receipt.NextSequence || state.Tick <= receipt.Tick)
            {
                var next = await Read(replacement, observation.Token);
                state = RoomFrames.Apply(next, state, frame.Sequence, room); frame = next;
            }
            await healthyPeer();
            pass(world is null ? "same token reopens a full v3 baseline and observes exactly one fresh input without relogin"
                : "same token reopens a full v5 world baseline (content, region, visit epoch) and observes exactly one fresh input");
            return new("PASS", eviction, conflicts, state.NextSequence);
        }
        finally { replacement?.Dispose(); }
    }

    private static async Task<RoomFrame> Read(ClientWebSocket socket, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var bytes = new byte[1024 * 1024]; int length = 0;
        while (true)
        {
            var part = await socket.ReceiveAsync(bytes.AsMemory(length), deadline.Token);
            if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Expected a room frame.");
            length += part.Count;
            if (part.EndOfMessage) break;
            if (length == bytes.Length) throw new InvalidDataException("Room frame exceeds 1 MiB.");
        }
        return JsonSerializer.Deserialize<RoomFrame>(bytes.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty room frame.");
    }
}
