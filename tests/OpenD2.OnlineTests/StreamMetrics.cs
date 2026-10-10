using System.Diagnostics;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;

internal static class StreamMetrics
{
    // One monotonic clock; reconstructed JSON is not wire traffic.
    internal static async Task Measure(OnlineClient host, IAsyncEnumerator<RoomView> peer,
        string path, long sequence, string output, CancellationToken stop)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var gaps = new List<double>();
        var latencies = new List<double>();
        var http = new List<double>();
        var applied = new List<double>();
        var samples = new List<object>();
        long bytes = 0, previousTick = peer.Current.Tick;
        var duration = Stopwatch.StartNew();
        double previous = duration.Elapsed.TotalMilliseconds;
        for (int i = 0; i < 100; i++)
        {
            if (!await peer.MoveNextAsync()) throw new Exception("Metrics stream ended.");
            double now = duration.Elapsed.TotalMilliseconds;
            gaps.Add(now - previous); previous = now;
            if (peer.Current.Tick < previousTick) throw new Exception("Stream tick moved backwards.");
            previousTick = peer.Current.Tick;
            bytes += JsonSerializer.SerializeToUtf8Bytes(new RoomUpdate(peer.Current), json).Length;
        }
        double seconds = duration.Elapsed.TotalSeconds;
        for (int i = 0; i < 50; i++)
        {
            int before = peer.Current.Entities.Single(e => e.Id.Value == 1).Position.X;
            int direction = i % 2 == 0 ? -1 : 1;
            var elapsed = Stopwatch.StartNew();
            var reply = await host.Send<RoomView>(HttpMethod.Post, path + "/input",
                new InputRequest(sequence, CommandKind.SetMove, direction, 0), stop);
            double httpMs = elapsed.Elapsed.TotalMilliseconds;
            double? appliedMs = null;
            int maxHitStun = reply.Entities.Single(e => e.Id.Value == 1).HitStun;
            int observedFrames = 0;
            sequence = reply.NextSequence;
            do
            {
                if (!await peer.MoveNextAsync()) throw new Exception("Missing measured movement.");
                observedFrames++;
                var actor = peer.Current.Entities.Single(e => e.Id.Value == 1);
                maxHitStun = Math.Max(maxHitStun, actor.HitStun);
                if (peer.Current.Tick > reply.Tick && actor.MoveX == direction)
                    appliedMs ??= elapsed.Elapsed.TotalMilliseconds;
            } while (peer.Current.Tick < reply.Tick ||
                (peer.Current.Entities.Single(e => e.Id.Value == 1).Position.X - before) * direction <= 0);
            double movementMs = elapsed.Elapsed.TotalMilliseconds;
            if (appliedMs is null) throw new Exception("Movement observed without the applied direction.");
            http.Add(httpMs); applied.Add(appliedMs.Value); latencies.Add(movementMs);
            samples.Add(new { index = i, http_response_ms = httpMs, direction_observed_ms = appliedMs.Value,
                movement_observed_ms = movementMs, max_hit_stun = maxHitStun, observed_frames = observedFrames,
                accepted_tick = reply.Tick, movement_tick = peer.Current.Tick });
            var stopped = await host.Send<RoomView>(HttpMethod.Post, path + "/input",
                new InputRequest(sequence, CommandKind.SetMove, 0, 0), stop);
            sequence = stopped.NextSequence;
            do
            {
                if (!await peer.MoveNextAsync()) throw new Exception("Missing measured stop.");
            } while (peer.Current.Tick < stopped.Tick ||
                peer.Current.Entities.Single(e => e.Id.Value == 1).MoveX != 0);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            result = "PASS", scope = "loopback-https-input-wss-peer-observation",
            timing = "single-client-monotonic-clock", input_to_peer_ms = Summary(latencies),
            http_response_ms = Summary(http), direction_observed_ms = Summary(applied), samples,
            observation_note = "Direction and movement observations follow HTTP response completion; buffered frames and game hit-stun are included. Not transport RTT.",
            receive_gap_ms = Summary(gaps), receive_seconds = seconds,
            observed_messages_per_second = gaps.Count / seconds,
            reconstructed_json_bytes = bytes, reconstructed_json_bytes_per_second = bytes / seconds,
            wire_bytes = "NOT_MEASURED", display_latency = "NOT_MEASURED", wan = "NOT_RUN",
            performance_threshold = "NONE: diagnostic samples, not a performance SLA",
            manual_gui = "NOT_RUN", physical_multi_pc = "NOT_RUN"
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine("STREAM METRICS PASS " + output);
    }

    private static object Summary(List<double> values)
    {
        values.Sort();
        double At(double p) => values[(int)Math.Ceiling(values.Count * p) - 1];
        return new { samples = values.Count, p50 = At(.5), p95 = At(.95), p99 = At(.99), max = values[^1] };
    }
}
