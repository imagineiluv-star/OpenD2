using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

// Test clients only. The normal Godot single-instance policy is not involved.
internal static class StreamCapacityContracts
{
    internal static async Task Run(string url, string ca, string mode, string output, int seconds)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 240));
        var stop = deadline.Token;
        var clients = new List<OnlineClient>();
        var rooms = new List<Guid>();
        var characters = new List<Guid>();
        var feeds = new List<Feed>();
        var checks = new List<string>();
        var responses = new Queue<double>(); // Bounded percentile window, not a capacity/SLA claim.
        string result = "FAIL";
        long inputs = 0, matches = 0, frames = 0, rounds = 0;
        int throttles = 0;
        double sampled = 0, recovery = 0;
        var elapsed = Stopwatch.StartNew();
        void Pass(string name) { checks.Add(name); Console.WriteLine("CAPACITY PASS " + mode + " " + name); }
        // The production password budget covers registration AND room admission (60/min).
        // Retry only explicit 429 rejections before mutation. Never retry uncertain inputs.
        async Task<T> Admit<T>(Func<Task<T>> action)
        {
            try { return await action(); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttles++;
                Console.WriteLine("CAPACITY WAIT " + mode + " production admission budget; waiting 61 seconds");
                await Task.Delay(TimeSpan.FromSeconds(61), stop);
                return await action();
            }
        }
        Feed Open(int index)
        {
            int group = index / 4;
            var feed = new Feed(clients[index], rooms[group], (uint)(index % 4 + 1),
                characters.Skip(group * 4).Take(Math.Min(4, characters.Count - group * 4)).ToArray(), stop);
            feeds.Add(feed); return feed;
        }
        try
        {
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            for (int i = 0; i < 33; i++)
            {
                var client = new OnlineClient(url, ca, mode); clients.Add(client);
                await Admit(async () => { await client.Login(new("load-" + suffix + "-" + i, password), true, stop); return true; });
                var character = await client.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("Load" + i), stop);
                characters.Add(character.Id);
                if (i % 4 == 0)
                    rooms.Add((await Admit(() => client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, "Load" + i, ""), stop))).Id);
                else
                    await Admit(() => client.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{rooms[^1]}/join", new JoinRequest(character.Id, ""), stop));
            }
            if (clients.Any(c => !c.SupportsRoomInterest)) throw new Exception("Capacity server must negotiate the current v3 interest protocol.");
            // Exercise ordinary gameplay to remove the nearby attacker before transport sampling.
            // Starting each room only when ready avoids a passive survival race during setup.
            for (int group = 0; group < 8; group++)
            {
                var host = clients[group * 4]; string path = $"v1/rooms/{rooms[group]}";
                var view = await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
                while (view.Entities.Single(e => e.Id.Value == 100).IsAlive)
                {
                    var actor = view.Entities.Single(e => e.Id.Value == view.Actor);
                    if (!actor.IsAlive) throw new Exception("Capacity setup actor died.");
                    if (actor.AttackCooldown == 0 && actor.HitStun == 0)
                        await host.SendInput(view.Id, new(view.NextSequence,
                            actor.SkillCooldown == 0 && actor.Mana >= GameSimulation.PowerStrikeManaCost
                                ? CommandKind.CastSkill : CommandKind.Attack, Target: 100), stop);
                    await Task.Delay(100, stop);
                    view = await host.Send<RoomView>(HttpMethod.Get, path, cancellation: stop);
                }
            }
            for (int i = 0; i < 32; i++) await Open(i).Until(v => v.Started, stop);
            Pass("32 authenticated WSS baselines retain eight rooms and four member identities per room");
            await ExpectRejection(clients[32], rooms[8], HttpStatusCode.TooManyRequests, stop);
            Pass("33rd stream rejected with 429 while all 32 slots are occupied");

            var sequence = feeds.Select(f => f.Latest!.NextSequence).ToArray();
            var sample = Stopwatch.StartNew();
            do
            {
                var receipts = new InputReceipt[32];
                // Four concurrent requests stay within the unchanged eight-slot HTTP budget.
                for (int start = 0; start < 32; start += 4)
                    await Task.WhenAll(Enumerable.Range(start, 4).Select(async i =>
                    {
                        var watch = Stopwatch.StartNew();
                        var ack = await clients[i].SendInput(rooms[i / 4], new(sequence[i], CommandKind.SetMove), stop);
                        if (ack.NextSequence != sequence[i] + 1) throw new Exception("Input cursor advanced incorrectly.");
                        receipts[i] = ack; sequence[i] = ack.NextSequence;
                        lock (responses) { responses.Enqueue(watch.Elapsed.TotalMilliseconds); if (responses.Count > 4096) responses.Dequeue(); }
                    }));
                inputs += 32;
                for (int group = 0; group < 8; group++)
                {
                    int offset = group * 4;
                    long after = receipts.Skip(offset).Take(4).Max(a => a.Tick);
                    await Wait(() =>
                    {
                        var histories = Enumerable.Range(offset, 4).Select(i =>
                        {
                            feeds[i].Check();
                            return feeds[i].History.Where(v => v.Tick > after && v.NextSequence == sequence[i])
                                .GroupBy(v => v.Tick).ToDictionary(g => g.Key, g => g.Last());
                        }).ToArray();
                        var ticks = histories[0].Keys.Where(t => histories.All(h => h.ContainsKey(t))).ToArray();
                        foreach (long tick in ticks)
                        {
                            var baseline = histories[0][tick];
                            var party = baseline.Members.Select(m => m.Actor).ToHashSet();
                            var actors = baseline.Entities.Where(e => party.Contains(e.Id.Value)).ToArray();
                            var items = baseline.Items.Where(i => party.Contains(i.Owner.Value)).ToArray();
                            if (actors.Length != 4 || histories.Any(h => h[tick].StateHash != baseline.StateHash ||
                                !h[tick].Entities.Where(e => party.Contains(e.Id.Value)).SequenceEqual(actors) ||
                                !h[tick].Items.Where(i => party.Contains(i.Owner.Value)).SequenceEqual(items)))
                                throw new Exception("Room peers disagree on the global hash or party state at the same post-input tick.");
                        }
                        return ticks.Length > 0;
                    }, stop);
                    matches++;
                }
                await clients[32].Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: stop);
                rounds++;
                await Task.Delay(100, stop);
            } while (sample.Elapsed.TotalSeconds < seconds);
            sampled = sample.Elapsed.TotalSeconds;
            if (clients.Take(32).Any(c => c.RoomDeltaFrames == 0)) throw new Exception("Not every stream received deltas.");
            Pass("eight active rooms keep matching peer hashes and exact input cursors while HTTP stays live");

            var reopen = Stopwatch.StartNew();
            await feeds[0].DisposeAsync();
            // Server-side cancellation can lag client disposal briefly; only explicit admission
            // rejections are retried. Any other protocol/transport error remains a failure.
            Feed? replacement = null;
            await Wait(async () =>
            {
                replacement = Open(32);
                try { await replacement.Until(_ => true, stop); return true; }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
                { await replacement.DisposeAsync(); return false; }
            }, stop);
            recovery = reopen.Elapsed.TotalMilliseconds;
            if (clients[32].RoomFullFrames < 1) throw new Exception("Replacement has no fresh full baseline.");
            await ExpectRejection(clients[0], rooms[0], HttpStatusCode.TooManyRequests, stop);
            Pass("released slot admits a fresh session and the 32-stream limit still applies");

            frames = feeds.Sum(f => f.Frames);
            foreach (var feed in feeds) await feed.DisposeAsync();
            feeds.Clear();
            // A second full wave catches leaked global permits and per-session reservations.
            for (int i = 0; i < 32; i++)
            {
                int index = i;
                long fullBefore = clients[i].RoomFullFrames;
                await Wait(async () =>
                {
                    var feed = Open(index);
                    try { await feed.Until(v => v.Started && v.NextSequence == sequence[index], stop); return true; }
                    catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict)
                    { await feed.DisposeAsync(); return false; }
                }, stop);
                if (clients[i].RoomFullFrames <= fullBefore) throw new Exception("Reopened stream did not start with a new full baseline.");
            }
            Pass("all 32 sessions reopen with fresh baselines and preserved input cursors");
            // Denied streams are checked with a free slot so admission cannot mask authorization.
            await feeds.Last(f => f.Latest?.Actor == 4).DisposeAsync();
            await Wait(async () =>
            {
                try { await ExpectRejection(clients[32], rooms[0], HttpStatusCode.Forbidden, stop); return true; }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests) { return false; }
            }, stop);
            for (int group = 0; group < 9; group++)
                await clients[group * 4].Send<object>(HttpMethod.Post, $"v1/rooms/{rooms[group]}/close", cancellation: stop);
            foreach (var feed in feeds.Where(f => !f.Disposed))
            {
                await Wait(() => feed.Finished, stop);
                if (feed.Error is not HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                    throw new Exception("Room closure did not terminate a stream with 404.");
            }
            frames += feeds.Sum(f => f.Frames);
            Pass("cross-room access is forbidden and room closure ends every active observer");
            result = "PASS";
        }
        finally
        {
            try { foreach (var feed in feeds) await feed.DisposeAsync(); }
            catch { result = "FAIL"; throw; }
            finally
            {
                foreach (var client in clients) client.Dispose();
                var sorted = responses.Order().ToArray();
                double? Percentile(double fraction) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { result, mode, checks,
                    scope = "loopback-32-production-dotnet-clients-eight-four-player-rooms-plus-admission-probe",
                    active_rooms = 8, simultaneous_streams = 32, admission_probe_clients = 1,
                    room_interest = clients.Count == 33 && clients.All(c => c.SupportsRoomInterest) ? "v3" : "NOT_VERIFIED",
                    requested_seconds = seconds, sampled_seconds = sampled, elapsed_seconds = elapsed.Elapsed.TotalSeconds,
                    rounds, inputs, matched_room_rounds = matches, frames, slot_recovery_ms = recovery, setup_throttle_waits = throttles,
                    http_response_ms = new { measured = inputs, retained = sorted.Length, window = "last-4096-requests", p50 = Percentile(.5), p95 = Percentile(.95), max = Percentile(1) },
                    setup_http_state_polling = true, measurement_http_state_polling = false,
                    physical_multi_pc = "NOT_RUN", wan = "NOT_RUN", packet_loss = "NOT_RUN", manual_gui = "NOT_RUN",
                    sustained_combat_load = "NOT_RUN", maximum_server_capacity = "NOT_RUN", long_duration = "NOT_RUN",
                    cpu_gc_tick_profiling = "NOT_RUN", server_backpressure_saturation = "NOT_RUN" },
                    new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
    }

    private static async Task ExpectRejection(OnlineClient client, Guid room, HttpStatusCode expected, CancellationToken stop)
    {
        await using var feed = client.WatchRoom(room, stop).GetAsyncEnumerator();
        try { await feed.MoveNextAsync(); }
        catch (HttpRequestException error) when (error.StatusCode == expected) { return; }
        throw new Exception("Expected stream rejection " + (int)expected);
    }
    private static Task Wait(Func<bool> predicate, CancellationToken stop) => Wait(() => Task.FromResult(predicate()), stop);
    private static async Task Wait(Func<Task<bool>> predicate, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!await predicate()) await Task.Delay(50, timeout.Token);
    }
    private sealed class Feed : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop;
        private readonly Task read;
        private RoomView? latest;
        private long frames;
        public readonly ConcurrentQueue<RoomView> History = new();
        public RoomView? Latest => Volatile.Read(ref latest);
        public long Frames => Interlocked.Read(ref frames);
        public Exception? Error { get; private set; }
        public bool Finished => read.IsCompleted;
        public bool Disposed { get; private set; }
        public Feed(OnlineClient client, Guid room, uint actor, Guid[] members, CancellationToken cancellation)
        {
            long fullBefore = client.RoomFullFrames;
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            read = Read();
            async Task Read()
            {
                try
                {
                    await foreach (var view in client.WatchRoom(room, stop.Token))
                    {
                        if (Latest is null && client.RoomFullFrames != fullBefore + 1)
                            throw new InvalidDataException("First stream frame is not a fresh full baseline.");
                        if (view.Id != room || view.Actor != actor || view.Host != (actor == 1) ||
                            !view.Members.Select(m => m.Character).Order().SequenceEqual(members.Order()) ||
                            Latest is { } previous && (view.Tick < previous.Tick || view.NextSequence < previous.NextSequence))
                            throw new InvalidDataException("Stream room, recipient, membership or cursor changed.");
                        History.Enqueue(view); while (History.Count > 256) History.TryDequeue(out _);
                        Volatile.Write(ref latest, view); Interlocked.Increment(ref frames);
                    }
                    if (!stop.IsCancellationRequested) throw new IOException("Stream ended without a room error.");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { Error = e; }
            }
        }
        public void Check() { if (Error is { } error) throw error; }
        public Task Until(Func<RoomView, bool> predicate, CancellationToken cancellation) => Wait(() =>
        { Check(); return Latest is { } view && predicate(view); }, cancellation);
        public async ValueTask DisposeAsync()
        { if (Disposed) return; Disposed = true; stop.Cancel(); await read; stop.Dispose(); }
    }
}
