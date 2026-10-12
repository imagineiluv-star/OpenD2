using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

// M5-02a: cooperative world rooms through the encrypted byte relay. The Arena probe already covers
// uncertain *movement*; a world adds region/visit state, so a lost response can hide a portal.
// Both outcomes (portal refused, portal taken) must be recoverable from server state alone,
// and replaying the lost input must never take the portal twice.
internal static class CooperativeNetworkContracts
{
    private static EntityState Actor(RoomView view, uint id) => view.Entities.Single(e => e.Id.Value == id);
    private static InputRequest Input(RoomView view, CommandKind kind, int x = 0, int y = 0, uint target = 0) =>
        new(view.NextSequence, kind, x, y, target, Region: Actor(view, view.Actor).Region.Value, RegionEpoch: view.World!.RegionEpoch);
    // A held relay stalls any new TLS handshake, so the request under test must reuse an idle pooled connection.
    private static async Task Warm(OnlineClient client, CancellationToken stop) =>
        await client.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: stop);
    private static async Task Conflict(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { return; }
        throw new Exception("Expected 409 for " + reason);
    }

    internal static async Task Run(string url, string ca, string mode, string output)
    {
        var checks = new List<string>(); string result = "FAIL";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var stop = deadline.Token;
        var proxy = new TcpFaultProxy(url);
        var content = CooperativeWorld.Content;
        long matchedStates = 0, recoveries = 0;
        void Pass(string name) { checks.Add(name); Console.WriteLine("WORLD NETWORK PASS " + mode + " " + name); }
        try
        {
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var credentials = new Credentials("wnet-guest-" + suffix, password);
            using var host = new OnlineClient(url, ca, mode);
            using var guest = new OnlineClient(proxy.Address, ca, mode);
            await host.Login(new("wnet-host-" + suffix, password), true, stop);
            await guest.Login(credentials, true, stop);
            if (!host.SupportsWorldRooms || !guest.SupportsWorldRooms) throw new Exception("Missing world capability.");
            var a = await host.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("WNetHost"), stop);
            var b = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("WNetGuest"), stop);
            var room = await host.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(a.Id, "WorldNetwork", "", content), stop);
            string path = $"v1/rooms/{room.Id}";
            await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, "", World: content), stop);
            await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
            await using var hostFeed = new NetworkResilienceContracts.Feed(host, room.Id, stop, content);
            await using var guestFeed = new NetworkResilienceContracts.Feed(guest, room.Id, stop, content);
            await hostFeed.Until(v => v.Started, stop);
            await guestFeed.Until(v => v.Started, stop);
            const uint guestActor = 2, portal = 200;
            if (Actor(guestFeed.Latest!, guestActor).Region.Value != 1) throw new Exception("Guest must start in the Camp.");
            Pass("trusted TLS through opaque relay with two v5 world observers");

            // Case 1: the guest stands outside portal range. The batch [portal, move] is accepted and consumed, but the
            // portal fails. The peer sees the move, which proves server acceptance; the lost response must then not be
            // mistaken for a transition.
            await Warm(guest, stop);
            proxy.HoldDownstream();
            var refused = Input(guestFeed.Latest!, CommandKind.Interact, target: portal);
            var drift = Input(guestFeed.Latest!, CommandKind.SetMove, 0, -1) with { Sequence = refused.Sequence + 1 };
            using var refusedStop = CancellationTokenSource.CreateLinkedTokenSource(stop); refusedStop.CancelAfter(TimeSpan.FromSeconds(8));
            Task<InputReceipt> pending = guest.SendInputs(room.Id, [refused, drift], refusedStop.Token);
            await Lose(proxy, hostFeed, guestFeed, pending, stop,
                () => hostFeed.Until(v => Actor(v, guestActor).MoveY == -1, stop));
            using (var recovered = new OnlineClient(proxy.Address, ca, mode))
            {
                await recovered.Login(credentials, false, stop);
                var view = await recovered.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, "", World: content), stop);
                if (view.NextSequence != drift.Sequence + 1 || view.World!.RegionEpoch != 0 || Actor(view, guestActor).Region.Value != 1 || view.World.Content != content)
                    throw new Exception($"Refused portal changed region/epoch or replayed the lost batch. next={view.NextSequence} expected={drift.Sequence + 1} epoch={view.World?.RegionEpoch} region={Actor(view, guestActor).Region.Value}");
                recoveries++;
            }
            Pass("lost refused-portal batch response: cursor advanced once, region and visit epoch unchanged");

            // Case 2: after the unrefreshed lease expires the guest stands within portal range. Step sideways through the
            // delayed, fragmented relay (v5 delta decoding), then lose the response of a portal that DOES transition.
            await hostFeed.Until(v => Actor(v, guestActor).MoveY == 0, stop);
            using var walker = new OnlineClient(proxy.Address, ca, mode);
            await walker.Login(credentials, false, stop);
            var current = await walker.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, "", World: content), stop);
            await using var walkerFeed = new NetworkResilienceContracts.Feed(walker, room.Id, stop, content);
            await walkerFeed.Until(v => v.NextSequence == current.NextSequence, stop);
            proxy.Impaired = true;
            var walk = await walker.SendInput(room.Id, Input(walkerFeed.Latest!, CommandKind.SetMove, 1, 0), stop);
            await walkerFeed.Until(v => Actor(v, guestActor).MoveX == 1 && v.NextSequence == walk.NextSequence, stop);
            await walkerFeed.Until(v => Actor(v, guestActor).MoveX == 0, stop);
            if (proxy.DelayedWrites == 0 || walker.RoomDeltaFrames == 0) throw new Exception("Fault path or delta stream was not exercised.");
            await CompareHashes(hostFeed, walkerFeed, walk.Tick, walk.NextSequence, stop);
            matchedStates++;
            proxy.Impaired = false;
            Pass("delayed fragmented TLS bytes keep v5 world frames decodable and hash-identical");

            await Warm(walker, stop);
            proxy.HoldDownstream();
            var taken = Input(walkerFeed.Latest!, CommandKind.Interact, target: portal);
            using var takenStop = CancellationTokenSource.CreateLinkedTokenSource(stop); takenStop.CancelAfter(TimeSpan.FromSeconds(8));
            pending = walker.SendInput(room.Id, taken, takenStop.Token);
            await Lose(proxy, hostFeed, walkerFeed, pending, stop,
                () => hostFeed.Until(v => Actor(v, guestActor).Region.Value == 2, stop));
            using var survivor = new OnlineClient(proxy.Address, ca, mode);
            await survivor.Login(credentials, false, stop);
            var after = await survivor.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, "", World: content), stop);
            long epoch = after.World!.RegionEpoch;
            if (after.NextSequence != taken.Sequence + 1 || Actor(after, guestActor).Region.Value != 2 || epoch <= 0 || epoch > hostFeed.Latest!.Tick)
                throw new Exception("Recovery after a taken portal lost the new region/epoch or replayed the lost input.");
            recoveries++;
            Pass("lost taken-portal response: recovery reads new region and visit epoch, cursor advanced once");

            // Replaying the lost portal input or reusing its stale visit must be refused without side effects.
            await using var survivorFeed = new NetworkResilienceContracts.Feed(survivor, room.Id, stop, content);
            await survivorFeed.Until(v => v.NextSequence == after.NextSequence && v.World!.RegionEpoch == epoch, stop);
            if (survivor.RoomFullFrames < 1) throw new Exception("Recovery did not receive a full baseline.");
            await Conflict(() => survivor.SendInput(room.Id, taken, stop), "same sequence and stale visit");
            await Conflict(() => survivor.SendInput(room.Id, taken with { Sequence = after.NextSequence }, stop), "fresh sequence with stale region and epoch");
            await Conflict(() => survivor.SendInputs(room.Id, [Input(survivorFeed.Latest!, CommandKind.SetMove, 1),
                taken with { Sequence = after.NextSequence + 1 }], stop), "batch containing a stale visit");
            await Task.Delay(300, stop);
            var untouched = survivorFeed.Latest!;
            if (untouched.NextSequence != after.NextSequence || Actor(untouched, guestActor).Region.Value != 2 || untouched.World!.RegionEpoch != epoch)
                throw new Exception("Rejected replays changed the room.");
            Pass("replayed lost portal input and stale visits are rejected atomically");

            // The party is still split: the guest's field has monsters, the host's camp has none, and both see the same hash.
            var step = await survivor.SendInput(room.Id, Input(survivorFeed.Latest!, CommandKind.SetMove, 1), stop);
            await hostFeed.Until(v => Actor(v, guestActor).MoveX == 1 && v.Tick > step.Tick, stop);
            await CompareHashes(hostFeed, survivorFeed, step.Tick, step.NextSequence, stop);
            matchedStates++;
            if (!survivorFeed.Latest!.Entities.Any(e => e.Kind == EntityKind.Monster) || hostFeed.Latest!.Entities.Any(e => e.Kind == EntityKind.Monster))
                throw new Exception("Region interest filtering differs from the split party.");
            Pass("fresh input after recovery reaches the split party with region-filtered, hash-identical frames");
            await host.Send<object>(HttpMethod.Post, path + "/close", cancellation: stop);
            result = "PASS";
        }
        finally
        {
            try { await proxy.DisposeAsync(); }
            catch { result = "FAIL"; throw; }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { result, mode, checks,
                    scope = "loopback-two-production-clients-byte-relay-cooperative-world-v5",
                    impairment = "5/10/15ms-per-chunk-max1024bytes-downstream-hold-and-TCP-cut",
                    recoveries, matched_states = matchedStates,
                    proxy.DelayedWrites, proxy.BlockedPumps, proxy.CutConnections, proxy.ForwardedBytes, proxy.Writes,
                    physical_multi_pc = "NOT_RUN", wan = "NOT_RUN", packet_loss = "NOT_RUN", manual_gui = "NOT_RUN",
                    capacity = "NOT_RUN", long_duration = "NOT_RUN" },
                    new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
    }

    // Hold the relay's downstream, let the input finish on the server (the unaffected peer proves it ran),
    // then cut every relayed connection so the pending response can never arrive.
    private static async Task Lose(TcpFaultProxy proxy, NetworkResilienceContracts.Feed peer, NetworkResilienceContracts.Feed own,
        Task<InputReceipt> pending, CancellationToken stop, Func<Task>? effect = null)
    {
        try
        {
            await NetworkResilienceContracts.Wait(() => proxy.BlockedPumps > 0, stop);
            long before = peer.Latest!.Tick;
            if (effect is not null) await effect();
            await peer.Until(v => v.Tick >= before + 10, stop);
            if (pending.IsCompleted) throw new Exception("Held downstream did not retain the command response.");
            int cuts = proxy.CutConnections;
            proxy.Cut(); proxy.ReleaseDownstream();
            try { await pending; throw new Exception("Cut response unexpectedly succeeded."); }
            catch (Exception e) when (e is HttpRequestException or IOException) { }
            await NetworkResilienceContracts.Wait(() => own.Finished, stop);
            if (own.Error is not (System.Net.WebSockets.WebSocketException or HttpRequestException or IOException))
                throw new Exception("Abrupt stream cut did not surface a transport failure.");
            if (proxy.CutConnections < cuts + 2) throw new Exception("HTTP and WSS connections were not both cut.");
        }
        finally
        {
            proxy.ReleaseDownstream();
            try { await pending; } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { }
        }
    }

    // Independent 25 Hz writers may skip different ticks: compare any post-input tick both observed.
    private static async Task CompareHashes(NetworkResilienceContracts.Feed first, NetworkResilienceContracts.Feed second,
        long afterTick, long sequence, CancellationToken stop) =>
        await NetworkResilienceContracts.Wait(() =>
        {
            var byTick = first.History.GroupBy(v => v.Tick).ToDictionary(g => g.Key, g => g.Last());
            foreach (var peer in second.History.Where(v => v.Tick > afterTick && v.NextSequence == sequence))
            {
                if (!byTick.TryGetValue(peer.Tick, out var other)) continue;
                if (other.StateHash != peer.StateHash) throw new Exception("Peer state hashes differ at the same tick.");
                return true;
            }
            return false;
        }, stop);
}
