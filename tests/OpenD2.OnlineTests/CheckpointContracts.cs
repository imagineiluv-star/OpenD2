using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-03d: the periodic checkpoint snapshots under the realm lock but writes (fsync, atomic replace) outside it.
// A write that stalls for longer than the 40 ms tick therefore must not stall the room, while mutations stay durable
// before they reply and an older snapshot can never overwrite newer state. The seams are internal and null in production.
internal static class CheckpointContracts
{
    private const string Password = "checkpoint-contract-password";
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Checkpoint: " + reason); }
    private static async Task Within(TimeSpan limit, string name, Action action)
    {
        var task = Task.Run(action);
        if (await Task.WhenAny(task, Task.Delay(limit)) != task) throw new Exception($"Checkpoint: {name} blocked behind a stalled checkpoint write");
        await task;
    }

    internal static async Task Run()
    {
        foreach (string mode in new[] { "realm", "open" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-checkpoint-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Stalled(Path.Combine(folder, "stalled"), mode);
                await Ordered(Path.Combine(folder, "ordered"), mode);
                Failed(Path.Combine(folder, "failed"), mode);
                Console.WriteLine($"CHECKPOINT PASS {mode} stalled-write-does-not-stall-room/snapshot-at-call-time/older-never-overwrites-newer/write-failure-closes-admission");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    private static (Realm Realm, string Token, Guid Room) Create(string folder, string mode)
    {
        var realm = new Realm(folder, new TestTime(), mode);
        var token = realm.Login(new("checkpoint-host", Password), true).Token;
        var hero = realm.CreateCharacter(token, "Checkpoint").Id;
        var room = realm.CreateRoom(token, new(hero, "Checkpoint", "")).Id;
        realm.Start(token, room);
        return (realm, token, room);
    }
    private static long StoredTick(string folder) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(folder, "realm.json")))!["Rooms"]![0]!["Game"]!["Tick"]!.GetValue<long>();

    // The write is stuck (a slow disk). Everything that does not need the disk keeps working, and the file ends up with
    // the state at the moment of the call, not a torn mixture with ticks that ran during the stall.
    private static async Task Stalled(string folder, string mode)
    {
        var (realm, token, room) = Create(folder, mode);
        using (realm)
        {
            for (int i = 0; i < 3; i++) realm.Tick();
            long snapshotTick = realm.State(token, room).Tick;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            realm.BeforePersist = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("checkpoint write was never released"); };
            var checkpoint = Task.Run(realm.Checkpoint);
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "the checkpoint never reached its write");
            var limit = TimeSpan.FromSeconds(3);
            await Within(limit, "Tick", () => { for (int i = 0; i < 5; i++) realm.Tick(); });
            await Within(limit, "State", () => realm.State(token, room));
            await Within(limit, "Input", () => realm.Input(token, room, new(realm.State(token, room).NextSequence, CommandKind.SetMove, 1)));
            await Within(limit, "Tick after input", () => realm.Tick());
            Check(!checkpoint.IsCompleted, "the checkpoint finished although its write was still held");
            long runningTick = realm.State(token, room).Tick;
            Check(runningTick == snapshotTick + 6, "ticks did not advance during the stalled write");

            release.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
            realm.BeforePersist = null;
            Check(StoredTick(folder) == snapshotTick, "the file holds the snapshot taken when Checkpoint was called");
            realm.Checkpoint();
            Check(StoredTick(folder) == runningTick, "a later checkpoint stores the newer state");
        }
    }

    // A snapshot waiting for the disk must not overwrite a newer mutation that was saved in the meantime.
    private static async Task Ordered(string folder, string mode)
    {
        var (realm, token, room) = Create(folder, mode);
        using (realm)
        {
            using var taken = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim();
            realm.BetweenSnapshotAndPersist = () => { taken.Set(); if (!proceed.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("snapshot was never released"); };
            var checkpoint = Task.Run(realm.Checkpoint);
            Check(taken.Wait(TimeSpan.FromSeconds(10)), "the checkpoint never took its snapshot");
            // The gate is free again: a mutation saves synchronously, with a newer sequence than the pending snapshot.
            await Within(TimeSpan.FromSeconds(5), "CreateCharacter", () => realm.CreateCharacter(token, "Later"));
            Check(File.ReadAllText(Path.Combine(folder, "realm.json")).Contains("Later"), "the mutation is durable before it replies");

            proceed.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
            realm.BetweenSnapshotAndPersist = null;
            Check(File.ReadAllText(Path.Combine(folder, "realm.json")).Contains("Later"), "an older snapshot overwrote a newer mutation");
            Check(realm.Characters(token).Any(c => c.Name == "Later") && realm.State(token, room).Started, "the realm kept running");
        }
        using (var reopened = new Realm(folder, new TestTime(), mode))
        {
            var again = reopened.Login(new("checkpoint-host", Password), false).Token;
            Check(reopened.Characters(again).Any(c => c.Name == "Later"), "the newer mutation survives a restart");
        }
    }

    // A failed write closes admission exactly as a failed mutation save does, and reports the failure to the caller.
    private static void Failed(string folder, string mode)
    {
        var (realm, token, _) = Create(folder, mode);
        using (realm)
        {
            realm.BeforePersist = () => throw new IOException("simulated disk failure");
            try { realm.Checkpoint(); throw new Exception("Checkpoint: write failure was swallowed"); } catch (IOException) { }
            realm.BeforePersist = null;
            try { realm.Characters(token); throw new Exception("Checkpoint: admission stayed open after a failed write"); }
            catch (Rejected error) when (error.Status == 503) { }
            try { realm.Tick(); throw new Exception("Checkpoint: Tick kept running on failed storage"); } catch (IOException) { }
        }
    }
}
