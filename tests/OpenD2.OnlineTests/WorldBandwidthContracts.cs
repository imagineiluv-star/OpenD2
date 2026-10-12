using System.Text.Json;
using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-03a: application payload of the v5 world stream, measured through the production path
// (Realm.StreamState -> RoomFrames.Create -> the RoomStream JSON options), one frame per 40 ms tick.
// TLS, WebSocket framing, TCP and retransmission overhead are NOT included, and the world is the
// synthetic Camp/Field. The numbers size the protocol; they are not a WAN or capacity claim.
internal static class WorldBandwidthContracts
{
    private const string Password = "world-bandwidth-contract-password";
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly WorldContent Content = CooperativeWorld.Content;
    internal sealed record Measurement(string Scenario, int Members, int Ticks, long Frames, long FullFrames, long Bytes, int MaxFrame, int FullBytes, double MeanDelta, int MaxDelta)
    {
        // Average over all recipients and frames; the server pushes one frame per 40 ms tick (25 Hz).
        public double BytesPerFrame => Bytes / (double)Frames;
        public double BytesPerSecond => BytesPerFrame * 25;
        public override string ToString() =>
            $"{Scenario}: {Members} streams x {Ticks} ticks, full {FullBytes}B x{FullFrames / Members}/stream, delta mean {MeanDelta:F0}B max {MaxDelta}B, " +
            $"{BytesPerFrame:F0}B/frame = {BytesPerSecond / 1024:F1} KiB/s per recipient";
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception("World bandwidth: " + reason); }
    private static EntityState Actor(RoomView view, uint id) => view.Entities.Single(e => e.Id.Value == id);

    // Replays what RoomStream.Push sends: the projected view is the next frame's "previous".
    private static Measurement Measure(string scenario, Realm realm, string[] tokens, Guid room, int ticks, Action<int, RoomView[]> act)
    {
        int members = tokens.Length; var previous = new RoomView?[members]; var sequence = new long[members]; var latest = new RoomView[members];
        long frames = 0, full = 0, bytes = 0, deltaBytes = 0, deltas = 0; int maxFrame = 0, maxDelta = 0, firstFull = 0;
        for (int tick = 0; tick <= ticks; tick++)
        {
            if (tick > 0) { act(tick, latest); realm.Tick(); }
            for (int m = 0; m < members; m++)
            {
                var view = realm.StreamState(tokens[m], room, interest: true, previous[m], inputProgress: true, world: true);
                var frame = RoomFrames.Create(view, previous[m], checked(++sequence[m]));
                int size = JsonSerializer.SerializeToUtf8Bytes(frame, Wire).Length;
                frames++; bytes += size; maxFrame = Math.Max(maxFrame, size);
                if (frame.State is not null) { full++; if (firstFull == 0) firstFull = size; }
                else { deltas++; deltaBytes += size; maxDelta = Math.Max(maxDelta, size); }
                previous[m] = view; latest[m] = view;
            }
        }
        return new(scenario, members, ticks + 1, frames, full, bytes, maxFrame, firstFull, deltas == 0 ? 0 : deltaBytes / (double)deltas, maxDelta);
    }

    private static (Realm Realm, string[] Tokens, Guid Room) Create(string folder, string[] names)
    {
        var realm = new Realm(folder, new TestTime());
        var tokens = names.Select(n => realm.Login(new(n, Password), true).Token).ToArray();
        var heroes = tokens.Select((t, i) => realm.CreateCharacter(t, "Band" + i).Id).ToArray();
        var room = realm.CreateRoom(tokens[0], new(heroes[0], "Bandwidth", "", Content)).Id;
        for (int i = 1; i < tokens.Length; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: Content));
        realm.Start(tokens[0], room);
        return (realm, tokens, room);
    }

    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-world-bandwidth-" + Guid.NewGuid().ToString("N"));
        string[] names = ["band-a", "band-b", "band-c", "band-d"];
        try
        {
            var results = new List<Measurement>();
            // 1. Four players in the Camp doing nothing: the cost of simply being connected.
            {
                var (realm, tokens, room) = Create(Path.Combine(folder, "idle"), names);
                using (realm) results.Add(Measure("idle camp", realm, tokens, room, 250, (_, _) => { }));
            }
            // 2. Everybody walks back and forth: every actor changes every tick.
            {
                var (realm, tokens, room) = Create(Path.Combine(folder, "moving"), names);
                using (realm)
                    results.Add(Measure("walking camp", realm, tokens, room, 250, (tick, views) =>
                    {
                        if (tick % 8 != 1) return;
                        for (int m = 0; m < 4; m++)
                            realm.Input(tokens[m], room, new(views[m].NextSequence, CommandKind.SetMove, (tick / 8 + m) % 2 == 0 ? 1 : -1,
                                Region: Actor(views[m], views[m].Actor).Region.Value, RegionEpoch: views[m].World!.RegionEpoch));
                    }));
            }
            // 3. Split party: two idle in the Camp, two fighting a monster in the Field (loot, damage, AI).
            {
                var (setup, setupTokens, room) = Create(Path.Combine(folder, "split"), names);
                setup.Checkpoint(); setup.Dispose();
                string file = Path.Combine(folder, "split", "realm.json");
                var snapshot = CooperativeWorld.Create(4).CaptureSnapshot();
                var entities = snapshot.Entities.Select(e => e.Id.Value switch
                {
                    3 => e with { Region = new(2), Position = new(3300, 2304) },
                    4 => e with { Region = new(2), Position = new(3300, 2560) },
                    _ => e
                }).ToArray();
                var json = JsonNode.Parse(File.ReadAllBytes(file))!;
                json["Rooms"]![0]!["Game"] = JsonSerializer.SerializeToNode(SavedGame.Capture(GameSimulation.Restore(snapshot with { Entities = entities })));
                File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(json));
                using var realm = new Realm(Path.Combine(folder, "split"), new TestTime());
                var tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
                var heroes = tokens.Select(t => realm.Characters(t).Single().Id).ToArray();
                for (int i = 0; i < 4; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: Content));
                results.Add(Measure("split party, 2 fighting in the Field", realm, tokens, room, 250, (tick, views) =>
                {
                    for (int m = 2; m < 4; m++)
                    {
                        var self = Actor(views[m], views[m].Actor);
                        if (self.IsAlive && self.AttackCooldown == 0 && self.HitStun == 0 && Actor(views[m], 100).IsAlive)
                            realm.Input(tokens[m], room, new(views[m].NextSequence, CommandKind.Attack, Target: 100, Region: 2, RegionEpoch: views[m].World!.RegionEpoch));
                    }
                }));
            }
            foreach (var measurement in results) Console.WriteLine("WORLD BANDWIDTH " + measurement);

            // Limits sit ~1.5x above what was measured, so they catch payload bloat (an unfiltered region, a new
            // per-frame field) without pinning exact byte counts. If a deliberate protocol change moves them, update here.
            var (idle, walking, split) = (results[0], results[1], results[2]);
            foreach (var m in results)
            {
                Check(m.FullFrames >= m.Members * ((m.Ticks + 124) / 125), $"{m.Scenario}: at least one full frame per 125-frame cycle and stream, got {m.FullFrames}");
                Check(m.MaxFrame <= 4096, $"{m.Scenario}: largest frame {m.MaxFrame}B exceeds 4 KiB for a 4-player, 2-monster world");
            }
            Check(idle.FullFrames == idle.Members * 3 && idle.MaxDelta <= 400 && idle.BytesPerFrame < idle.FullBytes / 2,
                "idle camp: deltas stay small (fixed overhead only) and full frames only on the refresh cycle");
            // When every visible actor changes each tick the protocol deliberately sends the (small) full state instead of a patch.
            Check(walking.MaxFrame <= walking.FullBytes * 5 / 4, "walking camp: a frame never exceeds a full state plus slack");
            Check(split.MaxDelta <= 2048 && split.BytesPerFrame < split.FullBytes / 2,
                "split party: Field combat deltas stay patches and the Camp pair's frames stay cheap (region interest)");
            Console.WriteLine("WORLD BANDWIDTH PASS payload sizes measured for idle, walking and split-combat world rooms (application JSON only)");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
