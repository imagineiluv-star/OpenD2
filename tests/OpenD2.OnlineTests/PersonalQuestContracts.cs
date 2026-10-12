using System.Text.Json;
using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-01d-1: a character keeps its own record of finished shared world quests across rooms.
internal static class PersonalQuestContracts
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Personal quest: " + reason); }
    internal static void Run() { foreach (string mode in new[] { "realm", "open" }) Verify(mode); }

    // Real server output with one controlled change: both monsters are dead and the quest awaits turn-in,
    // members 1 and 2 stand beside the Guide, member 3 is dead. The state hash is computed by Core, not forged.
    private static JsonNode ReadyGame()
    {
        var snapshot = CooperativeWorld.Create(3).CaptureSnapshot();
        var entities = snapshot.Entities.Select(e =>
            e.Kind == EntityKind.Monster ? e with { Health = 0, Mode = MonsterMode.Dead } :
            e.Id.Value == 1 ? e with { Position = new(768, 640) } :
            e.Id.Value == 2 ? e with { Position = new(896, 512) } :
            e.Id.Value == 3 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray();
        var game = GameSimulation.Restore(snapshot with { Entities = entities, QuestState = QuestStage.ReadyToTurnIn });
        return JsonSerializer.SerializeToNode(SavedGame.Capture(game))!;
    }

    private static void Verify(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-quest-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "realm.json");
        const string password = "personal-quest-contract-password";
        var content = CooperativeWorld.Content; var time = new TestTime();
        string[] names = ["quest-a", "quest-b", "quest-c"];
        var characters = new Guid[3];
        byte[] ready = [], closed = [];
        try
        {
            for (int round = 1; round <= 2; round++)
            {
                Guid room;
                using (var realm = new Realm(folder, time, mode))
                {
                    var tokens = names.Select(n => realm.Login(new(n, password), round == 1).Token).ToArray();
                    if (round == 1) for (int i = 0; i < 3; i++) characters[i] = realm.CreateCharacter(tokens[i], "Quest" + "ABC"[i]).Id;
                    room = realm.CreateRoom(tokens[0], new(characters[0], "Quest", "", content)).Id;
                    for (int i = 1; i < 3; i++) realm.Join(tokens[i], room, new(characters[i], "", World: content));
                    realm.Start(tokens[0], room); realm.Checkpoint(tokens[0], room);
                }
                var json = JsonNode.Parse(File.ReadAllBytes(file))!;
                json["Rooms"]![0]!["Game"] = ReadyGame();
                ready = JsonSerializer.SerializeToUtf8Bytes(json); File.WriteAllBytes(file, ready);

                using (var realm = new Realm(folder, time, mode))
                {
                    var tokens = names.Select(n => realm.Login(new(n, password), false).Token).ToArray();
                    for (int i = 0; i < 3; i++) realm.Join(tokens[i], room, new(characters[i], "", World: content));
                    for (int i = 0; i < 2; i++)
                    {
                        var view = realm.StreamState(tokens[i], room, inputProgress: true, world: true);
                        Check(view.World!.Quest.Stage == QuestStage.ReadyToTurnIn, "fixture awaits turn-in");
                        realm.Input(tokens[i], room, new(view.NextSequence, CommandKind.Interact, Target: 300, Region: 1, RegionEpoch: view.World.RegionEpoch));
                    }
                    realm.Tick();
                    Check(realm.StreamState(tokens[0], room, world: true).World!.Quest.Stage == QuestStage.Completed, "shared quest completes once");
                    Check((realm.Characters(tokens[0]).Single().Quests?.Sum(q => q.Completions) ?? 0) == round - 1, "records are written when the room closes, not before");
                    realm.Checkpoint(tokens[0], room);
                }
                // Credit lives in the saved room, so a restart between completion and close loses nothing.
                using (var realm = new Realm(folder, time, mode))
                {
                    var tokens = names.Select(n => realm.Login(new(n, password), false).Token).ToArray();
                    realm.Join(tokens[0], room, new(characters[0], "", World: content));
                    realm.Close(tokens[0], room);
                    var records = tokens.Select(t => realm.Characters(t).Single()).ToArray();
                    var expected = new[] { new QuestRecord(content.Id, round) };
                    Check(records[0].Quests!.SequenceEqual(expected) && records[1].Quests!.SequenceEqual(expected),
                        $"living participants keep a completion record (round {round})");
                    Check(records[2].Quests is null && !JsonSerializer.Serialize(records[2]).Contains("Quests"),
                        "member dead at completion has no record and the wire shape is unchanged");
                    Check(records.All(r => r.Victories == round), "existing victory rule is unchanged");
                    Check(realm.Rooms(tokens[0], true).Length == 0, "closed room is gone");
                }
                closed = File.ReadAllBytes(file);
            }

            // Persisted records survive restart and appear on the next login.
            using (var realm = new Realm(folder, time, mode))
            {
                var token = realm.Login(new(names[0], password), false).Token;
                Check(realm.Characters(token).Single().Quests!.Single() == new QuestRecord(content.Id, 2), "records survive restart");
            }

            bool Refused(byte[] bytes)
            {
                File.WriteAllBytes(file, bytes);
                try { using var bad = new Realm(folder, time, mode); return false; }
                catch (InvalidDataException) { return File.ReadAllBytes(file).SequenceEqual(bytes); }
            }
            byte[] Edit(byte[] source, Action<JsonNode> change)
            {
                var json = JsonNode.Parse(source)!; change(json); return JsonSerializer.SerializeToUtf8Bytes(json);
            }
            Check(Refused(Edit(ready, j => j["Rooms"]![0]!["Members"]![0]!["QuestCredit"] = true)), "credit without a completed quest is rejected and preserved");
            foreach (var (name, change) in new (string, Action<JsonNode>)[]
            {
                ("zero completions", j => j["Characters"]![0]!["Quests"]![0]!["Completions"] = 0),
                ("negative completions", j => j["Characters"]![0]!["Quests"]![0]!["Completions"] = -1),
                ("excess completions", j => j["Characters"]![0]!["Quests"]![0]!["Completions"] = 1_000_001),
                ("invalid world ID", j => j["Characters"]![0]!["Quests"]![0]!["World"] = "Bad World"),
                ("duplicate world", j => j["Characters"]![0]!["Quests"]!.AsArray().Add(JsonNode.Parse("""{"World":"cooperative-demo-v1","Completions":1}""")))
            })
                Check(Refused(Edit(closed, change)), "invalid saved quest record is rejected and preserved: " + name);
            Check(Refused(Edit(closed, j =>
            {
                var many = j["Characters"]![0]!["Quests"]!.AsArray();
                for (int i = 0; i < 8; i++) many.Add(JsonNode.Parse($$"""{"World":"extra-world-{{i}}","Completions":1}"""));
            })), "quest record budget is enforced");

            // Databases written before this change have neither field and must keep loading.
            File.WriteAllBytes(file, Edit(closed, j => { foreach (var c in j["Characters"]!.AsArray()) c!.AsObject().Remove("Quests"); }));
            using (var realm = new Realm(folder, time, mode))
                Check(realm.Characters(realm.Login(new(names[0], password), false).Token).Single().Quests is null, "legacy character without records loads");
            File.WriteAllBytes(file, Edit(ready, j => { foreach (var m in j["Rooms"]![0]!["Members"]!.AsArray()) m!.AsObject().Remove("QuestCredit"); }));
            using (var realm = new Realm(folder, time, mode))
            {
                var token = realm.Login(new(names[0], password), false).Token;
                realm.Join(token, realm.Rooms(token, true).Single().Id, new(characters[0], "", World: content));
            }
            Console.WriteLine($"PERSONAL QUEST PASS {mode} credit/dead-excluded/restart/close/repeat/validation/legacy/wire");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
