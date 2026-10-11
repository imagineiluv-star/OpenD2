using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class RoomSnapshotContracts
{
    private static void Check(bool value, string reason)
    {
        if (!value) throw new Exception("Room snapshot: " + reason);
    }
    private static void Reject(int status, Action action)
    {
        try { action(); } catch (Rejected error) when (error.Status == status) { return; }
        throw new Exception("Expected snapshot access rejection " + status);
    }
    internal static void Run()
    {
        foreach (string mode in new[] { "realm", "open" }) Verify(mode);
    }
    private static void Verify(string mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "opend2-snapshot-" + Guid.NewGuid().ToString("N"));
        const string password = "snapshot-contract-password";
        var time = new TestTime(); Guid room; RoomView saved;
        try
        {
            using (var realm = new Realm(directory, time, mode))
            {
                var users = Enumerable.Range(0, 4).Select(i => realm.Login(new("snapshot" + i, password), true)).ToArray();
                var characters = users.Select((u, i) => realm.CreateCharacter(u.Token, "Hero" + i)).ToArray();
                room = realm.CreateRoom(users[0].Token, new(characters[0].Id, "SnapshotRoom", "")).Id;
                for (int i = 1; i < 4; i++) realm.Join(users[i].Token, room, new(characters[i].Id, ""));
                realm.Start(users[0].Token, room);
                var original = realm.StreamState(users[0].Token, room);
                var filtered = realm.StreamState(users[0].Token, room, interest: true);
                Check(ReferenceEquals(original.Entities, realm.StreamState(users[0].Token, room).Entities), "full snapshot remains shared across projections");
                Check(filtered.Entities.SequenceEqual(RoomInterest.Project(original).Entities), "stream projection matches policy");
                // Monster 101 starts exactly at the exit radius, outside the entry radius.
                var retained = realm.StreamState(users[0].Token, room, interest: true, previous: original);
                Check(retained.Entities.Any(e => e.Id.Value == 101) && !filtered.Entities.Any(e => e.Id.Value == 101), "stream forwards connection history");
                Check(!realm.StreamState(users[0].Token, room, interest: true).Entities.Any(e => e.Id.Value == 101), "fresh stream does not inherit peer visibility");
                Check(realm.StreamState(users[0].Token, room, previous: filtered).Entities.SequenceEqual(original.Entities), "legacy stream ignores interest history");
                string originalJson = JsonSerializer.Serialize(original);
                long builds = realm.SnapshotBuilds;
                var reference = Arena.Create(4);
                for (int tick = 0; tick < 100; tick++)
                {
                    realm.Tick(); reference.Step();
                    RoomView? first = null;
                    for (int i = 0; i < 4; i++)
                    {
                        var view = realm.StreamState(users[i].Token, room);
                        Check(view.Actor == (uint)i + 1 && view.Host == (i == 0) && view.NextSequence == 1, "recipient metadata");
                        Check(view.StateHash == reference.ComputeStateHash() && view.Tick == reference.Tick &&
                            view.Entities.SequenceEqual(reference.Entities.ToArray()) && view.Items.SequenceEqual(reference.Items.ToArray()), "uncached reference equivalence");
                        if (first is not null) Check(ReferenceEquals(first.Entities, view.Entities) && ReferenceEquals(first.Items, view.Items), "shared arrays");
                        first = view;
                    }
                }
                Check(realm.SnapshotBuilds - builds == 100, "400 stream reads build exactly 100 snapshots");
                Check(JsonSerializer.Serialize(original) == originalJson, "old stream baseline remains immutable");
                // Use a fresh arena so movement validation is independent of combat after 100 ticks.
                realm.Close(users[0].Token, room);
                room = realm.CreateRoom(users[0].Token, new(characters[0].Id, "InputRoom", "")).Id;
                realm.Join(users[1].Token, room, new(characters[1].Id, ""));
                realm.Start(users[0].Token, room);
                var before = realm.StreamState(users[0].Token, room);
                realm.AcknowledgeInput(users[0].Token, room, new(1, CommandKind.SetMove, -1));
                var pending = realm.StreamState(users[0].Token, room);
                Check(realm.StreamState(users[0].Token, room, interest: true).StateHash == pending.StateHash, "projection same tick refresh");
                Check(pending.Tick == before.Tick && pending.StateHash != before.StateHash && pending.NextSequence == 2, "same tick acknowledgement invalidates hash");
                realm.BatchInput(users[0].Token, room, new([new(2, CommandKind.SetMove, 1), new(3, CommandKind.SetMove, -1)]));
                var batch = realm.StreamState(users[0].Token, room);
                Check(batch.Tick == pending.Tick && batch.StateHash != pending.StateHash && batch.NextSequence == 4, "same tick batch invalidates hash");
                Check(realm.StreamState(users[1].Token, room).NextSequence == 1, "peer sequence independent");
                builds = realm.SnapshotBuilds;
                Reject(409, () => realm.AcknowledgeInput(users[0].Token, room, new(3, CommandKind.SetMove)));
                Reject(400, () => realm.BatchInput(users[0].Token, room, new([new(4, CommandKind.SetMove), null!])));
                Check(realm.StreamState(users[0].Token, room).StateHash == batch.StateHash && realm.SnapshotBuilds == builds, "rejected input preserves snapshot");
                var legacy = realm.Input(users[0].Token, room, new(4, CommandKind.SetMove, 1));
                Check(legacy.StateHash != batch.StateHash && legacy.NextSequence == 5, "legacy input refreshes hash");
                var shared = realm.StreamState(users[0].Token, room);
                var owned = realm.State(users[0].Token, room);
                owned.Entities[0] = default; legacy.Entities[0] = default; owned.Members[0] = owned.Members[0] with { Name = "Corrupted" };
                Check(shared.Entities[0] != default && realm.State(users[0].Token, room).Members[0].Name == "Hero0", "public response isolation");
                realm.Tick(); time.Advance(1); realm.Tick();
                Check(realm.StreamState(users[0].Token, room).Entities.Single(e => e.Id.Value == 1).MoveX == 0, "lease stop refreshes state");
                realm.AcknowledgeInput(users[0].Token, room, new(5, CommandKind.SetMove, 1));
                var idlePending = realm.StreamState(users[0].Token, room);
                time.Advance(16); realm.Tick();
                var idle = realm.StreamState(users[0].Token, room);
                Check(idle.Tick == idlePending.Tick && idle.StateHash != idlePending.StateHash, "idle replacement invalidates without tick advance");
                Check(idle.Entities.Single(e => e.Id.Value == 1).MoveX == 0, "idle clears movement");
                Reject(401, () => realm.StreamState("invalid", room, interest: true));
                Reject(403, () => realm.StreamState(users[2].Token, room, interest: true));
                var renewed = realm.Login(new("snapshot0", password), false);
                Reject(401, () => realm.StreamState(users[0].Token, room));
                realm.Logout(users[1].Token); Reject(401, () => realm.StreamState(users[1].Token, room));
                saved = realm.StreamState(renewed.Token, room); realm.Checkpoint();
                time.Advance(8 * 60 * 60); Reject(401, () => realm.StreamState(renewed.Token, room));
            }
            using (var realm = new Realm(directory, time, mode))
            {
                var host = realm.Login(new("snapshot0", password), false);
                var restored = realm.StreamState(host.Token, room);
                Check(restored.Tick == saved.Tick && restored.StateHash == saved.StateHash && restored.Entities.SequenceEqual(saved.Entities), "checkpoint restart builds fresh state");
                realm.Close(host.Token, room); Reject(404, () => realm.StreamState(host.Token, room));
            }
            // Trusted persisted inventory exercises nonempty item-array ownership in both modes.
            string file = Path.Combine(directory, "realm.json");
            var db = JsonSerializer.Deserialize<Database>(File.ReadAllText(file))!;
            db.Characters[0] = db.Characters[0] with { Inventory = [new(new(1), ItemDefinition.ManaPotion, ItemLocation.Inventory, 0)] };
            File.WriteAllText(file, JsonSerializer.Serialize(db));
            using (var realm = new Realm(directory, time, mode))
            {
                var host = realm.Login(new("snapshot0", password), false);
                room = realm.CreateRoom(host.Token, new(db.Characters[0].Id, "ItemRoom", "")).Id;
                var started = realm.Start(host.Token, room);
                var shared = realm.StreamState(host.Token, room);
                var owned = realm.State(host.Token, room);
                Check(shared.Items.Length == 1 && !ReferenceEquals(shared.Items, owned.Items), "owned item array");
                started.Items[0] = default; owned.Items[0] = default;
                Check(realm.StreamState(host.Token, room).Items[0] != default, "item response isolation");
                var item = shared.Items[0];
                realm.AcknowledgeInput(host.Token, room, new(1, CommandKind.DropItem, Item: item.Id.Value)); realm.Tick();
                Check(realm.StreamState(host.Token, room).Items[0].Location == ItemLocation.Ground && shared.Items[0] == item, "item change leaves old baseline intact");
            }
            Console.WriteLine($"ROOM SNAPSHOT PASS mode={mode} reads=400 builds=100 reference/ownership/same-tick/lease/idle/auth/restart/items");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
