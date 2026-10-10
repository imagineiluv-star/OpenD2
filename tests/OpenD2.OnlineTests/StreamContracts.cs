using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class StreamContracts
{
    internal static async Task Run(string url, string ca, string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stop = deadline.Token;
        var checks = new List<string>();
        void Pass(string name) { checks.Add(name); Console.WriteLine("STREAM PASS " + name); }
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var credentials = new Credentials("stream-host", password);
        using var host = new OnlineClient(url, ca);
        using var guest = new OnlineClient(url, ca);
        using var outsider = new OnlineClient(url, ca);
        await host.Login(credentials, true, stop);
        await guest.Login(new("stream-guest", password), true, stop);
        await outsider.Login(new("stream-outsider", password), true, stop);
        var a = await host.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("StreamHost"), stop);
        var b = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("StreamGuest"), stop);
        var room = await host.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(a.Id, "StreamRoom", ""), stop);
        string path = $"v1/rooms/{room.Id}";
        await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, ""), stop);
        await RawContracts(url, ca, path, password, stop);
        Pass("origin and subprotocol rejected; application messages disconnect the stream");
        await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
        await using var first = host.WatchRoom(room.Id, stop).GetAsyncEnumerator();
        await using var second = guest.WatchRoom(room.Id, stop).GetAsyncEnumerator();
        if (!await first.MoveNextAsync() || !await second.MoveNextAsync() || first.Current.Actor != 1 || second.Current.Actor != 2)
            throw new Exception("Stream members differ.");
        Pass("two authenticated WSS streams receive their own actor and room");
        long tick = second.Current.Tick;
        do { if (!await second.MoveNextAsync()) throw new Exception("Missing pushed tick."); } while (second.Current.Tick <= tick);
        Pass("server ticks arrive without HTTP state polling");
        var before = second.Current.Entities.Single(e => e.Id.Value == 1).Position.X;
        await host.Send<RoomView>(HttpMethod.Post, path + "/input", new InputRequest(first.Current.NextSequence, CommandKind.SetMove, -1, 0), stop);
        do { if (!await second.MoveNextAsync()) throw new Exception("Missing peer movement."); } while (second.Current.Entities.Single(e => e.Id.Value == 1).Position.X >= before);
        Pass("HTTP-owned movement is pushed to the peer over WSS");
        await using (var duplicate = host.WatchRoom(room.Id, stop).GetAsyncEnumerator()) await Rejected(duplicate, 409);
        Pass("duplicate stream for the same session rejected");
        await using (var denied = outsider.WatchRoom(room.Id, stop).GetAsyncEnumerator()) await Rejected(denied, 403);
        Pass("non-member stream rejected before upgrade");
        using (var anonymous = new OnlineClient(url, ca))
        {
            try { await anonymous.Send<object>(HttpMethod.Get, path + "/stream", cancellation: stop); throw new Exception("Anonymous stream accepted."); }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { }
        }
        Pass("unauthenticated endpoint rejected");

        using var renewed = new OnlineClient(url, ca);
        await renewed.Login(credentials, false, stop);
        await Rejected(first, 401);
        Pass("relogin revokes an already connected stream");
        await renewed.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(a.Id, ""), stop);
        await using var replacement = renewed.WatchRoom(room.Id, stop).GetAsyncEnumerator();
        if (!await replacement.MoveNextAsync() || replacement.Current.Actor != 1) throw new Exception("Reconnect identity lost.");
        Pass("reconnect starts from a full authoritative state");
        await renewed.Send<object>(HttpMethod.Post, path + "/close", cancellation: stop);
        await Rejected(second, 404); await Rejected(replacement, 404);
        Pass("room closure terminates both streams with not-found");

        var clients = new List<OnlineClient>(); var feeds = new List<IAsyncEnumerator<RoomView>>();
        try
        {
            Guid group = default;
            for (int i = 0; i < 9; i++)
            {
                var client = new OnlineClient(url, ca); clients.Add(client);
                await client.Login(new("stream-many-" + i, password), true, stop);
                var character = await client.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("Many" + i), stop);
                if (i % 3 == 0) group = (await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, "Group" + i, ""), stop)).Id;
                else await client.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{group}/join", new JoinRequest(character.Id, ""), stop);
                var feed = client.WatchRoom(group, stop).GetAsyncEnumerator(); feeds.Add(feed);
                if (!await feed.MoveNextAsync()) throw new Exception("Missing concurrent stream.");
            }
            await renewed.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: stop);
            Pass("nine streams do not exhaust the eight HTTP request slots");
            clients[0].Dispose();
            bool cancelled = false;
            try { while (await feeds[0].MoveNextAsync()) { } }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { cancelled = true; }
            if (!cancelled) throw new Exception("Disposing the client did not cancel its stream.");
            Pass("client disposal cancels its active stream");
        }
        finally { foreach (var feed in feeds) await feed.DisposeAsync(); foreach (var client in clients) client.Dispose(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { result = "PASS", checks, transport = "WSS",
            http_state_polling = false, manual_gui = "NOT_RUN", physical_multi_pc = "NOT_RUN" }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static async Task Rejected(IAsyncEnumerator<RoomView> feed, int status)
    {
        try { while (await feed.MoveNextAsync()) { } }
        catch (HttpRequestException e) when ((int?)e.StatusCode == status) { return; }
        throw new Exception("Expected stream rejection " + status);
    }

    private static async Task RawContracts(string url, string ca, string path, string password, CancellationToken stop)
    {
        using var member = new OnlineClient(url, ca);
        var credentials = new Credentials("stream-raw", password);
        await member.Login(credentials, true, stop);
        var character = await member.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("RawMember"), stop);
        await member.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(character.Id, ""), stop);
        // This fresh token is used only by the raw protocol-boundary probes below.
        var session = await member.Send<LoginResult>(HttpMethod.Post, "v1/login", credentials, stop);
        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(ca));
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        handler.SslOptions.CertificateChainPolicy = new() { TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck, CustomTrustStore = { root } };
        using var http = new HttpClient(handler);
        var uri = new UriBuilder(new Uri(new Uri(url), path + "/stream")) { Scheme = "wss" }.Uri;
        foreach (int expected in new[] { 400, 403, 0 })
        {
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("Authorization", "Bearer " + session.Token);
            if (expected != 400) socket.Options.AddSubProtocol("opend2.room.v1");
            if (expected == 403) socket.Options.SetRequestHeader("Origin", "https://untrusted.invalid");
            try
            {
                await socket.ConnectAsync(uri, http, stop);
                if (expected != 0) throw new Exception("Invalid handshake accepted.");
                await socket.SendAsync("{}"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, stop);
                using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop); closed.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var bytes = new byte[16384];
                    while ((await socket.ReceiveAsync(bytes.AsMemory(), closed.Token)).MessageType != WebSocketMessageType.Close) { }
                }
                catch (WebSocketException) { }
            }
            catch (WebSocketException) when (expected != 0 && (int)socket.HttpStatusCode == expected) { }
        }
    }
}
