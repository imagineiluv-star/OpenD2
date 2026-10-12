using System.Text.Json;
using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class CooperativeRoomContracts
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Cooperative room: " + reason); }
    private static void Reject(int status, Action action)
    {
        try { action(); } catch (Rejected e) when (e.Status == status) { return; }
        throw new Exception("Expected cooperative room rejection " + status);
    }
    internal static void Run()
    {
        for (int n = 1; n <= 4; n++)
        {
            var game = CooperativeWorld.Create(n);
            Check(game.WorldPlayerCount == n && game.Entities.ToArray().Count(e => e.Kind == EntityKind.Player) == n, "validated 1..4 distinct walkable spawns");
        }
        foreach (string mode in new[] { "realm", "open" }) Verify(mode);
    }
    private static void Verify(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-world-" + Guid.NewGuid().ToString("N"));
        const string password = "cooperative-room-contract-password";
        var content = CooperativeWorld.Content;
        var time = new TestTime(); Guid room, character; RoomView saved;
        try
        {
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("world-host", password), true);
                var guest = realm.Login(new("world-guest", password), true);
                character = mode == "open" ? realm.ImportCharacter(host.Token, new(2, GameSimulation.RulesVersion, "open", "WorldHost", 0,
                    [new(ItemDefinition.TrainingSword, ItemLocation.Inventory, 0)])).Id : realm.CreateCharacter(host.Token, "WorldHost").Id;
                var peer = realm.CreateCharacter(guest.Token, "WorldGuest").Id;
                Check(realm.Worlds(host.Token).SequenceEqual([content]), "authenticated fixed content catalog");
                Reject(401, () => realm.Worlds("invalid"));
                Reject(409, () => realm.CreateRoom(host.Token, new(character, "World", "", new("../world", content.Hash))));
                Reject(409, () => realm.CreateRoom(host.Token, new(character, "World", "", content with { Hash = new('0', 64) })));
                Check(realm.Rooms(host.Token, true).Length == 0, "failed content selection has no room mutation");
                room = realm.CreateRoom(host.Token, new(character, "World", "", content)).Id;
                Check(realm.Rooms(host.Token).Length == 0 && realm.Rooms(host.Token, true).Single().World == content, "legacy lists hide world rooms");
                Reject(403, () => realm.State(guest.Token, room));
                Reject(409, () => realm.Join(guest.Token, room, new(peer, "")));
                Reject(409, () => realm.Join(guest.Token, room, new(peer, "", World: content with { Hash = "wrong" })));
                Check(realm.State(host.Token, room).Members.Length == 1, "mismatched join preserves membership");
                realm.Join(guest.Token, room, new(peer, "", World: content));
                for (int i = 3; i <= 4; i++)
                {
                    var extra = realm.Login(new("world-peer-" + i, password), true);
                    var hero = realm.CreateCharacter(extra.Token, "WorldPeer" + i);
                    realm.Join(extra.Token, room, new(hero.Id, "", World: content));
                }
                Check(realm.State(host.Token, room).Members.Length == 4, "four participants admitted with unique actors");
                Reject(403, () => realm.Start(guest.Token, room));
                Reject(409, () => realm.StreamState(host.Token, room, inputProgress: true));
                realm.Start(host.Token, room);
                RoomView View(string? token = null, RoomView? previous = null) => realm.StreamState(token ?? host.Token, room, true, previous, true, true);
                InputRequest Input(RoomView view, CommandKind kind, int x = 0, uint target = 0) => new(view.NextSequence, kind, x, Target: target,
                    Region: view.Entities.Single(e => e.Id.Value == view.Actor).Region.Value, RegionEpoch: view.World!.RegionEpoch);
                var start = View();
                Check(start.World!.Quest.Stage == QuestStage.Available && start.Entities.All(e => e.Kind == EntityKind.Player), "town excludes field monsters");
                realm.Input(host.Token, room, Input(start, CommandKind.Interact, target: 300)); realm.Tick();
                var quest = View();
                Check(quest.World!.Quest.Stage == QuestStage.Active, "server executes the host's own NPC quest");
                Check(View(guest.Token).World!.Quest.Stage == QuestStage.Available, "a peer's quest is untouched by the host's acceptance");
                var initialFrame = RoomFrames.Create(start, null, 1);
                var questFrame = RoomFrames.Create(quest, start, 2);
                Check(questFrame.State is not null && RoomFrames.Apply(questFrame, RoomFrames.Apply(initialFrame, null, 0, room), 1, room).World == quest.World,
                    "quest transition refreshes frame baseline");
                var oldVisit = Input(quest, CommandKind.SetMove, 1);
                realm.AcknowledgeInput(host.Token, room, Input(quest, CommandKind.Interact, target: 200));
                realm.AcknowledgeInput(guest.Token, room, Input(View(guest.Token), CommandKind.SetMove, 1));
                Check(View().ProcessedSequence == quest.ProcessedSequence, "portal acceptance is not yet consumption");
                realm.Tick();
                var field = View(previous: quest); var town = View(guest.Token);
                Check(field.Entities.Single(e => e.Id.Value == 1).Region.Value == 2 && field.World!.RegionEpoch == field.Tick && field.ProcessedSequence == quest.NextSequence,
                    "portal consumes input and advances own epoch");
                Check(town.World!.RegionEpoch == 0 && town.Entities.Single(e => e.Id.Value == 2).MoveX == 1 && town.ProcessedSequence == 1,
                    "peer lease and progress survive another actor's portal");
                Check(field.Entities.Any(e => e.Kind == EntityKind.Monster) && town.Entities.All(e => e.Kind == EntityKind.Player) &&
                    field.Entities.Any(e => e.Id.Value == 2) && field.StateHash == town.StateHash, "region interest keeps party and shared authoritative hash");
                foreach (var invalid in new[] { field with { World = null }, field with { World = field.World! with { RegionEpoch = 0 } },
                    field with { World = field.World! with { RegionEpoch = field.Tick + 1 } },
                    field with { World = field.World! with { Content = content with { Hash = "wrong" } } },
                    field with { World = field.World! with { Quest = new(QuestStage.Completed, 0, 2) } } })
                {
                    bool denied = false;
                    try { RoomFrames.Apply(new(2, State: invalid), field, 1, room); } catch (InvalidDataException) { denied = true; }
                    Check(denied, "frame rejects lost content, stale/future epoch and invalid quest");
                }
                Reject(409, () => realm.Input(host.Token, room, oldVisit with { Sequence = field.NextSequence }));
                Reject(409, () => realm.Input(host.Token, room, new(field.NextSequence, CommandKind.SetMove, 1)));
                Reject(409, () => realm.BatchInput(host.Token, room, new([Input(field, CommandKind.SetMove, 1),
                    oldVisit with { Sequence = field.NextSequence + 1 }])));
                Check(View().NextSequence == field.NextSequence && View().StateHash == field.StateHash, "stale batch rejects atomically");
                realm.Input(host.Token, room, Input(field, CommandKind.Interact, target: 201)); realm.Tick();
                var returned = View();
                Check(returned.Entities.Single(e => e.Id.Value == 1).Region.Value == 1 && returned.World!.RegionEpoch > field.World!.RegionEpoch,
                    "round trip advances region visit even at same region ID");
                Reject(409, () => realm.Input(host.Token, room, oldVisit with { Sequence = returned.NextSequence }));
                realm.Input(host.Token, room, Input(returned, CommandKind.Interact, target: 200)); realm.Tick();
                var moved = View();
                realm.Input(host.Token, room, Input(moved, CommandKind.SetMove, 1)); realm.Tick();
                time.Advance(1); realm.Tick();
                Check(View().Entities.Where(e => e.Kind == EntityKind.Player).All(e => e.MoveX == 0), "new-region and peer leases expire");
                var beforeRestart = View();
                realm.AcknowledgeInput(host.Token, room, Input(beforeRestart, CommandKind.SetMove, 1));
                realm.Checkpoint(); saved = realm.State(host.Token, room) with { ProcessedSequence = View().ProcessedSequence };
                Check(saved.NextSequence - 1 > saved.ProcessedSequence, "restart fixture contains accepted unconsumed input");
                host = realm.Login(new("world-host", password), false);
                Reject(409, () => realm.State(host.Token, room));
                Reject(409, () => realm.Join(host.Token, room, new(character, "")));
                realm.Join(host.Token, room, new(character, "", World: content));
                Check(View(host.Token).World == saved.World, "new login requires matching content, preserves own world state");
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("world-host", password), false);
                Reject(409, () => realm.State(host.Token, room));
                var restored = realm.Join(host.Token, room, new(character, "", World: content));
                var progress = realm.StreamState(host.Token, room, inputProgress: true, world: true);
                Check(restored.World == saved.World && restored.Tick == saved.Tick && restored.Entities.SequenceEqual(saved.Entities.Select(e => e with { MoveX = 0, MoveY = 0 })) && restored.Items.SequenceEqual(saved.Items),
                    "restart preserves quest, actor regions and region epoch");
                Check(progress.ProcessedSequence == saved.NextSequence - 1 && progress.NextSequence == saved.NextSequence, "restart discards and retires queued input");
                realm.AcknowledgeInput(host.Token, room, new(restored.NextSequence, CommandKind.SetMove, 1, Region: 2, RegionEpoch: restored.World!.RegionEpoch));
                realm.Tick(); Check(realm.State(host.Token, room).Entities.Single(e => e.Id.Value == 1).MoveX == 1, "input continues after restart");
                realm.Checkpoint();
            }
            string file = Path.Combine(folder, "realm.json"); byte[] original = File.ReadAllBytes(file);
            foreach (string fault in new[] { "hash", "missing-world", "world-hash", "epoch", "version" })
            {
                var json = JsonNode.Parse(original)!; var stored = json["Rooms"]![0]!;
                switch (fault)
                {
                    case "hash": stored["World"]!["Hash"] = new string('0', 64); break;
                    case "missing-world": stored["World"] = null; break;
                    case "world-hash": stored["Game"]!["WorldHash"] = "wrong"; break;
                    case "epoch": stored["Members"]![0]!["RegionEpoch"] = -1; break;
                    case "version": json["Version"] = 2; break;
                }
                byte[] invalid = JsonSerializer.SerializeToUtf8Bytes(json); File.WriteAllBytes(file, invalid);
                bool denied = false;
                try { using var bad = new Realm(folder, time, mode); } catch (InvalidDataException) { denied = true; }
                Check(denied && File.ReadAllBytes(file).SequenceEqual(invalid), "reject and preserve invalid saved " + fault);
            }
            File.WriteAllBytes(file, original);
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("world-host", password), false);
                realm.Join(host.Token, room, new(character, "", World: content)); realm.Close(host.Token, room);
                Check(realm.Characters(host.Token).Single().Victories == 0, "unfinished shared quest is not a victory");
                if (mode == "open") Check(realm.ExportCharacter(host.Token, character).Inventory!.Single().Definition == ItemDefinition.TrainingSword,
                    "world close returns personal carried items");
                var arena = realm.CreateRoom(host.Token, new(character, "Arena", ""));
                Check(arena.World is null && !JsonSerializer.Serialize(arena).Contains("\"World\":"), "Arena omits world metadata");
                realm.Start(host.Token, arena.Id);
                Reject(400, () => realm.Input(host.Token, arena.Id, new(1, CommandKind.SetMove, Region: 1, RegionEpoch: 0)));
                realm.Input(host.Token, arena.Id, new(1, CommandKind.SetMove, 1)); realm.Tick();
                Check(realm.State(host.Token, arena.Id).Entities[0].Position.X == 800, "Arena input remains compatible");
            }
            Console.WriteLine($"COOPERATIVE ROOM PASS {mode} content/auth/spawns/quest/portal/epoch/atomic/interest/lease/reconnect/restart/preservation/legacy");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
