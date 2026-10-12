using System.Diagnostics;
using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-03b: what the host spends per 40 ms tick when eight four-player world rooms are active (the documented
// 32-stream target). Two parts, both in process and on one thread, so this is CPU cost only:
//   * Realm.Tick(): lease handling, the simulation step of every room, event handling and snapshots' invalidation.
//   * Frame production for every recipient: StreamState (+ interest projection), RoomFrames.Create and JSON encoding.
//   * Realm.Checkpoint(): the once-a-second save (JSON of every room, fsync, atomic replace). Since M5-03d only the snapshot
//     is taken under the lock shared with Tick, input and stream reads; this measures the whole call.
// No sockets, TLS, WebSocket send or HTTP input handling are included, and wall-clock numbers depend on the machine.
// Only gross regressions are asserted (far above what was measured); the numbers are for sizing, not an SLA.
internal static class WorldHostLoadContracts
{
    private const string Password = "world-host-load-contract-password";
    private const int Rooms = 8, Players = 4, Ticks = 150;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    private static readonly WorldContent Content = CooperativeWorld.Content;
    private static void Check(bool value, string reason) { if (!value) throw new Exception("World host load: " + reason); }
    private static double Percentile(double[] sorted, double fraction) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(fraction * sorted.Length) - 1)];
    private static double Milliseconds(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;

    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-world-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var realm = new Realm(folder, new TestTime());
            var tokens = new string[Rooms * Players]; var rooms = new Guid[Rooms];
            for (int room = 0; room < Rooms; room++)
            {
                var heroes = new Guid[Players];
                for (int p = 0; p < Players; p++)
                {
                    int index = room * Players + p;
                    tokens[index] = realm.Login(new($"load-{room}-{p}", Password), true).Token;
                    heroes[p] = realm.CreateCharacter(tokens[index], "Load" + index).Id;
                }
                rooms[room] = realm.CreateRoom(tokens[room * Players], new(heroes[0], "Load" + room, "", Content)).Id;
                for (int p = 1; p < Players; p++) realm.Join(tokens[room * Players + p], rooms[room], new(heroes[p], "", World: Content));
                realm.Start(tokens[room * Players], rooms[room]);
            }

            var previous = new RoomView?[tokens.Length]; var sequence = new long[tokens.Length]; var latest = new RoomView[tokens.Length];
            var tickMs = new List<double>(); var frameMs = new List<double>(); var allocated = new List<long>(); long bytes = 0, frames = 0;
            void Produce()
            {
                for (int i = 0; i < tokens.Length; i++)
                {
                    var view = realm.StreamState(tokens[i], rooms[i / Players], interest: true, previous[i], inputProgress: true, world: true);
                    var frame = RoomFrames.Create(view, previous[i], checked(++sequence[i]));
                    bytes += JsonSerializer.SerializeToUtf8Bytes(frame, Wire).Length; frames++;
                    previous[i] = view; latest[i] = view;
                }
            }
            Produce(); // the full baselines, as a connection would receive first
            var start = latest.Select(v => v.Entities.Single(e => e.Id.Value == v.Actor).Position).ToArray();
            for (int tick = 1; tick <= Ticks; tick++)
            {
                // Everybody walks (leases refreshed every 8 ticks) so each room has real work every tick.
                if (tick % 8 == 1)
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        var self = latest[i].Entities.Single(e => e.Id.Value == latest[i].Actor);
                        realm.Input(tokens[i], rooms[i / Players], new(latest[i].NextSequence, CommandKind.SetMove, (tick / 8 + i) % 2 == 0 ? 1 : -1,
                            Region: self.Region.Value, RegionEpoch: latest[i].World!.RegionEpoch));
                    }
                var watch = Stopwatch.StartNew();
                realm.Tick();
                long afterTick = watch.ElapsedTicks;
                long memory = GC.GetAllocatedBytesForCurrentThread();
                Produce();
                tickMs.Add(Milliseconds(afterTick));
                frameMs.Add(Milliseconds(watch.ElapsedTicks - afterTick));
                allocated.Add(GC.GetAllocatedBytesForCurrentThread() - memory);
            }
            // The measurement is only meaningful if every room really advanced and every player really walked.
            Check(latest.All(v => v.Tick == Ticks), "rooms did not advance with the measured ticks");
            Check(Enumerable.Range(0, tokens.Length).Count(i => latest[i].Entities.Single(e => e.Id.Value == latest[i].Actor).Position != start[i]) == tokens.Length,
                "not every player moved, so the tick cost was measured on idle rooms");

            var saveMs = new List<double>();
            for (int save = 0; save < 12; save++) { var watch = Stopwatch.StartNew(); realm.Checkpoint(); saveMs.Add(Milliseconds(watch.ElapsedTicks)); }
            double[] saves = saveMs.Order().ToArray();
            long databaseBytes = new FileInfo(Path.Combine(folder, "realm.json")).Length;
            double[] ticks = tickMs.Order().ToArray(), produce = frameMs.Order().ToArray(), total = tickMs.Zip(frameMs, (a, b) => a + b).Order().ToArray();
            long[] bytesAllocated = allocated.Order().ToArray();
            Console.WriteLine($"WORLD HOST LOAD {Rooms} rooms x {Players} players, {Ticks} ticks, {Rooms * Players} recipients");
            Console.WriteLine($"WORLD HOST LOAD Realm.Tick ms p50 {Percentile(ticks, .5):F2} p99 {Percentile(ticks, .99):F2} max {ticks[^1]:F2}");
            Console.WriteLine($"WORLD HOST LOAD {Rooms * Players} frames ms p50 {Percentile(produce, .5):F2} p99 {Percentile(produce, .99):F2} max {produce[^1]:F2}");
            Console.WriteLine($"WORLD HOST LOAD tick+frames ms p50 {Percentile(total, .5):F2} p99 {Percentile(total, .99):F2} of the 40 ms budget; frame stage allocates p50 {bytesAllocated[bytesAllocated.Length / 2] / 1024.0:F0} KiB/tick; payload {bytes / (double)frames:F0} B/frame");

            Console.WriteLine($"WORLD HOST LOAD Checkpoint call (JSON snapshot under the lock, fsync + replace outside it) ms p50 {Percentile(saves, .5):F2} max {saves[^1]:F2} for a {databaseBytes / 1024.0:F0} KiB database with {Rooms} rooms; it runs every 25 ticks");

            // Gross-regression limits only: an order of magnitude above a typical desktop, to survive slow CI runners.
            Check(Percentile(total, .5) < 20, "median CPU cost of one tick for eight active rooms exceeds half of the 40 ms budget");
            Check(bytesAllocated[bytesAllocated.Length / 2] < 16 * 1024 * 1024, "frame production allocates more than 16 MiB per tick");
            Check(Percentile(saves, .5) < 1000, "a checkpoint of eight rooms takes more than a second, longer than the interval between checkpoints");
            Check(databaseBytes < 1024 * 1024, "eight world rooms exceed 1 MiB of saved state (budget is 16 MiB for the whole realm)");
            Console.WriteLine("WORLD HOST LOAD PASS in-process CPU and allocation cost measured for eight active four-player world rooms (no network)");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
