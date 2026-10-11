using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class NetworkResilienceContracts
{
    internal static async Task Run(string url, string ca, string mode, string output, Process process, int seconds = 5)
    {
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var checks = new List<string>();
        string result = "FAIL";
        var watch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 90));
        var stop = deadline.Token;
        var proxy = new TcpFaultProxy(url);
        long hostFrames = 0, guestFrames = 0, matchedStates = 0, inputs = 0;
        double recoveryMs = 0, sampledSeconds = 0;
        double? serverCpuMs = null;
        long? peakWorkingSet = null;
        bool metricsAvailable = false;
        SilentStreamContracts.Evidence? silentStream = null;
        var responseTimes = new List<double>();
        void Pass(string name) { checks.Add(name); Console.WriteLine("NETWORK PASS " + mode + " " + name); }
        try
        {
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var credentials = new Credentials("net-guest-" + suffix, password);
            using var host = new OnlineClient(url, ca, mode);
            using var guest = new OnlineClient(proxy.Address, ca, mode);
            await host.Login(new("net-host-" + suffix, password), true, stop);
            await guest.Login(credentials, true, stop);
            if (guest.TrustedRootSha256 != host.TrustedRootSha256) throw new Exception("Relay changed certificate trust.");
            var a = await host.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("NetHost"), stop);
            var b = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("NetGuest"), stop);
            var room = await host.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(a.Id, "Network", ""), stop);
            string path = $"v1/rooms/{room.Id}";
            var joined = await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, ""), stop);
            var state = await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
            // Set up a safe transport probe through ordinary combat, never a test server mutation.
            while (state.Entities.Single(e => e.Id.Value == 100).IsAlive)
            {
                var actor = state.Entities.Single(e => e.Id.Value == state.Actor);
                if (!actor.IsAlive) throw new Exception("Network setup actor died.");
                if (actor.AttackCooldown == 0 && actor.HitStun == 0)
                    await host.SendInput(room.Id, new(state.NextSequence,
                        actor.SkillCooldown == 0 && actor.Mana >= GameSimulation.PowerStrikeManaCost
                            ? CommandKind.CastSkill : CommandKind.Attack, Target: 100), stop);
                await Task.Delay(100, stop);
                state = await host.Send<RoomView>(HttpMethod.Get, path, cancellation: stop);
            }
            await using var hostFeed = new Feed(host, room.Id, stop);
            await using var guestFeed = new Feed(guest, room.Id, stop);
            await hostFeed.Until(v => v.Started, stop);
            await guestFeed.Until(v => v.Started, stop);
            Pass("trusted TLS through opaque relay and independent WSS observers");

            proxy.Impaired = true;
            long guestTick = guestFeed.Latest!.Tick;
            var receipt = await guest.SendInput(room.Id, new(joined.NextSequence, CommandKind.SetMove, 0, 1), stop);
            await hostFeed.Until(v => v.Tick > receipt.Tick && Actor(v, joined.Actor).MoveY == 1, stop);
            await guestFeed.Until(v => v.Tick > guestTick && v.NextSequence == receipt.NextSequence, stop);
            if (proxy.DelayedWrites == 0 || guest.RoomDeltaFrames == 0) throw new Exception("Fault path or delta stream was not exercised.");
            Pass("delayed fragmented TLS bytes preserve input receipt and delta decoding");

            // Lose an accepted command's response. The unaffected peer proves server acceptance.
            // No retry is allowed; reconnect must recover exactly the advanced server cursor.
            proxy.HoldDownstream();
            using var pendingStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            pendingStop.CancelAfter(TimeSpan.FromSeconds(8));
            Task<InputReceipt> pending = guest.SendInputs(room.Id,
                [new(receipt.NextSequence, CommandKind.SetMove), new(receipt.NextSequence + 1, CommandKind.SetMove, 0, -1)], pendingStop.Token);
            try
            {
                await hostFeed.Until(v => Actor(v, joined.Actor).MoveY == -1, stop);
                await Wait(() => proxy.BlockedPumps > 0, stop);
                long before = hostFeed.Latest!.Tick;
                await hostFeed.Until(v => v.Tick >= before + 20 && Actor(v, joined.Actor).MoveY == 0, stop);
                if (pending.IsCompleted) throw new Exception("Held downstream did not retain the command response.");
                await host.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: stop);
                Pass("stalled peer leaves other WSS and HTTP live; unrefreshed movement lease expires");

                proxy.Cut(); proxy.ReleaseDownstream(); proxy.Impaired = false;
                try { await pending; throw new Exception("Cut response unexpectedly succeeded."); }
                catch (Exception e) when (e is HttpRequestException or IOException) { }
                await Wait(() => guestFeed.Finished, stop);
                if (guestFeed.Error is not (WebSocketException or HttpRequestException or IOException))
                    throw new Exception("Abrupt stream cut did not surface a transport failure.");
                if (proxy.CutConnections < 2) throw new Exception("HTTP and WSS connections were not both cut.");
                Pass("connection cut exposes uncertain command outcome without treating it as success");
            }
            finally
            {
                proxy.ReleaseDownstream(); pendingStop.Cancel();
                try { await pending; } catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { }
            }

            var recovery = Stopwatch.StartNew();
            using var recovered = new OnlineClient(proxy.Address, ca, mode);
            await recovered.Login(credentials, false, stop);
            var restored = await recovered.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, ""), stop);
            if (restored.Id != room.Id || restored.Actor != joined.Actor || restored.NextSequence != receipt.NextSequence + 2 ||
                !restored.Members.Any(m => m.Character == b.Id))
                throw new Exception("Recovery lost membership or replayed an uncertain batch.");
            await using var recoveredFeed = new Feed(recovered, room.Id, stop);
            await recoveredFeed.Until(v => v.NextSequence == restored.NextSequence, stop);
            if (recovered.RoomFullFrames < 1) throw new Exception("Recovery did not receive a new full baseline.");
            recoveryMs = recovery.Elapsed.TotalMilliseconds;
            var resumed = await recovered.SendInput(room.Id, new(restored.NextSequence, CommandKind.SetMove, 0, 1), stop);
            await hostFeed.Until(v => v.Tick > resumed.Tick && Actor(v, joined.Actor).MoveY == 1, stop);
            await recoveredFeed.Until(v => v.NextSequence == resumed.NextSequence, stop);
            Pass("relogin preserves character actor and exact accepted cursor; fresh input reaches peer");

            // Bounded duration probe. Neutral inputs exercise receipts/cursors without inventing
            // a combat benchmark. This is two subscribers, not a server capacity claim.
            proxy.Impaired = true;
            long sequence = resumed.NextSequence;
            var cpuStart = ProcessSample(process);
            metricsAvailable = cpuStart.HasValue;
            var sample = Stopwatch.StartNew();
            while (sample.Elapsed.TotalSeconds < seconds)
            {
                var response = Stopwatch.StartNew();
                var ack = await recovered.SendInput(room.Id, new(sequence, CommandKind.SetMove), stop);
                responseTimes.Add(response.Elapsed.TotalMilliseconds);
                var resources = metricsAvailable ? ProcessSample(process) : null;
                metricsAvailable &= resources.HasValue;
                if (resources.HasValue) peakWorkingSet = Math.Max(peakWorkingSet ?? 0, resources.Value.Memory);
                sequence = ack.NextSequence; inputs++;
                await recoveredFeed.Until(v => v.NextSequence == sequence && v.Tick > ack.Tick, stop);
                // Independent 25 Hz writers may skip different ticks. Compare a common
                // post-input tick, rather than waiting forever for one already skipped tick.
                await Wait(() =>
                {
                    var byTick = hostFeed.History.GroupBy(v => v.Tick).ToDictionary(g => g.Key, g => g.Last());
                    bool matched = false;
                    foreach (var peer in recoveredFeed.History.Where(v => v.Tick > ack.Tick && v.NextSequence == sequence))
                    {
                        if (!byTick.TryGetValue(peer.Tick, out var other)) continue;
                        if (other.StateHash != peer.StateHash) throw new Exception("Peer state hashes differ at the same tick.");
                        matched = true;
                    }
                    return matched;
                }, stop);
                matchedStates++;
                if (hostFeed.Error is { } error) throw new Exception("Unaffected stream failed.", error);
                await Task.Delay(100, stop);
            }
            sampledSeconds = sample.Elapsed.TotalSeconds;
            var cpuEnd = metricsAvailable ? ProcessSample(process) : null;
            metricsAvailable &= cpuEnd.HasValue;
            if (metricsAvailable) serverCpuMs = (cpuEnd!.Value.Cpu - cpuStart!.Value.Cpu).TotalMilliseconds;
            else { peakWorkingSet = null; Console.WriteLine("NETWORK METRICS UNAVAILABLE process resource inspection is restricted"); }
            hostFrames = hostFeed.Frames; guestFrames = recoveredFeed.Frames + guestFeed.Frames;
            if (matchedStates == 0 || recovered.RoomDeltaFrames == 0) throw new Exception("Duration probe lacks authoritative matches.");
            Pass("bounded impaired-stream run matches peer hashes at identical ticks");
            proxy.Impaired = false;
            // Retire the previous observer before the deliberate non-reading peer.
            // This single login establishes the token reused throughout that probe.
            var session = await recovered.Send<LoginResult>(HttpMethod.Post, "v1/login", credentials, stop);
            await Wait(() => recoveredFeed.Finished, stop);
            silentStream = await SilentStreamContracts.Run(url, ca, session, room.Id, restored.Actor, b.Id, sequence,
                async () =>
                {
                    long tick = hostFeed.Latest!.Tick;
                    await hostFeed.Until(v => v.Tick >= tick + 10, stop);
                    await host.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters", cancellation: stop);
                }, Pass, stop);
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
                    scope = "loopback-two-production-clients-byte-relay-and-raw-silent-WSS-peer",
                    impairment = "5/10/15ms-per-chunk-max1024bytes-downstream-hold-TCP-cut-and-nonreading-WSS",
                    requested_seconds = seconds, sampled_seconds = sampledSeconds, elapsed_seconds = watch.Elapsed.TotalSeconds,
                    host_frames = hostFrames, guest_frames = guestFrames, inputs, matched_states = matchedStates, recovery_ms = recoveryMs,
                    server_process_metrics = metricsAvailable ? "PASS" : "UNAVAILABLE", server_cpu_ms = serverCpuMs, server_peak_sampled_working_set_bytes = peakWorkingSet,
                    http_response_ms = new { count = responseTimes.Count, p50 = Percentile(responseTimes, 0.50), p95 = Percentile(responseTimes, 0.95), max = Percentile(responseTimes, 1) },
                    gc_and_tick_execution_time = "NOT_RUN",
                    silent_stream = silentStream,
                    proxy.DelayedWrites, proxy.BlockedPumps, proxy.CutConnections, proxy.ForwardedBytes, proxy.Writes,
                    setup_http_state_polling = true, measurement_http_state_polling = false,
                    physical_multi_pc = "NOT_RUN", wan = "NOT_RUN", packet_loss = "NOT_RUN", manual_gui = "NOT_RUN",
                    capacity = "NOT_RUN", server_backpressure_saturation = "NOT_RUN", long_duration = "NOT_RUN" },
                    new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }
    }

    private static (TimeSpan Cpu, long Memory)? ProcessSample(Process process)
    {
        if (process.HasExited) throw new Exception("Server exited during resource sampling.");
        try { process.Refresh(); return (process.TotalProcessorTime, process.WorkingSet64); }
        catch (Exception e) when (e is Win32Exception or PlatformNotSupportedException)
        {
            if (process.HasExited) throw new Exception("Server exited during resource sampling.", e);
            return null; // Restricted containers may prohibit /proc inspection; never invent zeros.
        }
    }

    private static double Percentile(List<double> samples, double fraction)
    {
        if (samples.Count == 0) return 0;
        var ordered = samples.Order().ToArray();
        return ordered[(int)Math.Ceiling(fraction * ordered.Length) - 1];
    }

    private static EntityState Actor(RoomView view, uint id) => view.Entities.Single(e => e.Id.Value == id);
    private static async Task Wait(Func<bool> predicate, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
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
        public Feed(OnlineClient client, Guid room, CancellationToken cancellation)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            read = Read(client, room);
        }
        private async Task Read(OnlineClient client, Guid room)
        {
            try
            {
                await foreach (var view in client.WatchRoom(room, stop.Token))
                {
                    if (Latest is { } previous && (view.Actor != previous.Actor || view.Tick < previous.Tick))
                        throw new InvalidDataException("Stream identity or tick regressed.");
                    History.Enqueue(view); while (History.Count > 512) History.TryDequeue(out _);
                    Volatile.Write(ref latest, view); Interlocked.Increment(ref frames);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { Error = e; }
        }
        public Task Until(Func<RoomView, bool> predicate, CancellationToken cancellation) => Wait(() =>
        {
            if (Error is { } error) throw new Exception("WSS observation failed.", error);
            return Latest is { } view && predicate(view);
        }, cancellation);
        public async ValueTask DisposeAsync() { stop.Cancel(); await read; stop.Dispose(); }
    }
}
