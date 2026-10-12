using System.Text.Json;
using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-01d-1: a character keeps its own record of finished shared world quests across rooms.
internal static class PersonalQuestContracts
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Personal quest: " + reason); }
    internal static void Run()
    {
        foreach (string mode in new[] { "realm", "open" }) { Verify(mode); Independent(mode); Console.WriteLine($"PERSONAL QUEST INDEPENDENT PASS {mode} own-view/own-hand-in/restart/record-and-victory-per-member"); }
        OpenFile();
    }

    // Real server output with one controlled change: both monsters are dead and the quest awaits turn-in,
    // members 1 and 2 stand beside the Guide, member 3 is dead. The state hash is computed by Core, not forged.
    internal static JsonNode ReadyGame()
    {
        var snapshot = CooperativeWorld.Create(3).CaptureSnapshot();
        var entities = snapshot.Entities.Select(e =>
            e.Kind == EntityKind.Monster ? e with { Health = 0, Mode = MonsterMode.Dead } :
            e.Id.Value == 1 ? e with { Position = new(768, 640) } :
            e.Id.Value == 2 ? e with { Position = new(896, 512) } :
            e.Id.Value == 3 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray();
        var game = GameSimulation.Restore(snapshot with { Entities = entities, QuestState = QuestStage.ReadyToTurnIn,
            PlayerQuests = [QuestStage.ReadyToTurnIn, QuestStage.ReadyToTurnIn, QuestStage.ReadyToTurnIn] });
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
                        $"each member who completed their own quest keeps a record (round {round})");
                    Check(records[2].Quests is null && !JsonSerializer.Serialize(records[2]).Contains("Quests"),
                        "member who never completed has no record and the wire shape is unchanged");
                    // A victory is each member's own completed quest; the member who could not turn in earns none.
                    Check(records[0].Victories == round && records[1].Victories == round && records[2].Victories == 0, "victories follow each member's own completion");
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

            if (mode == "open")
            {
                // The completions earned above leave the server inside the portable file and arrive on another host.
                using var realm = new Realm(folder, time, mode);
                var token = realm.Login(new(names[0], password), false).Token;
                var exported = realm.ExportCharacter(token, characters[0]);
                Check(exported.Version == 4 && exported.Quests!.SequenceEqual([new QuestRecord(content.Id, 2)]), "export carries earned completion records");
                string path = Path.Combine(folder, "earned.json"); OpenCharacterFile.Save(path, exported);
                using var destination = new Realm(Path.Combine(folder, "destination"), time, mode);
                var guest = destination.Login(new("quest-dest", password), true).Token;
                var copy = destination.ImportCharacter(guest, OpenCharacterFile.Load(path));
                Check(copy.Quests!.SequenceEqual(exported.Quests!) && destination.ExportCharacter(guest, copy.Id).Quests!.SequenceEqual(exported.Quests!),
                    "earned records survive file export and import on another host");
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
                ("duplicate world", j => j["Characters"]![0]!["Quests"]!.AsArray().Add(JsonNode.Parse($$"""{"World":"{{content.Id}}","Completions":1}""")))
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

    // Open file format 3: quest completions travel with the character, and earlier formats cannot carry them.
    private static void OpenFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2-quest-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Reject(int status, Action action)
        {
            try { action(); } catch (Rejected e) when (e.Status == status) { return; }
            throw new Exception("Personal quest: expected rejection " + status);
        }
        void Invalid(string reason, OpenCharacter file)
        {
            try { OpenCharacterFile.Validate(file); }
            catch (InvalidDataException) { return; }
            throw new Exception("Personal quest: invalid file accepted: " + reason);
        }
        try
        {
            string world = CooperativeWorld.Content.Id;
            QuestRecord[] earned = [new(world, 3), new("future-world-v2", 1)];
            var profile = OpenItemTransferContracts.FullCharacter() with { Version = 3, Quests = earned };

            // Budget: the largest inventory plus the maximum number of records still fits the 8 KiB file limit.
            var many = profile with { Quests = [.. Enumerable.Range(0, QuestRecords.MaxRecords).Select(i => new QuestRecord("extra-world-" + i, QuestRecords.MaxCompletions))] };
            string big = Path.Combine(root, "big.json"); OpenCharacterFile.Save(big, many);
            Check(new FileInfo(big).Length <= OpenCharacterFile.MaximumBytes && OpenCharacterFile.Load(big).Quests!.Length == QuestRecords.MaxRecords, "maximum inventory and records fit the file budget");

            string path = Path.Combine(root, "hero.json"); OpenCharacterFile.Save(path, profile);
            string text = File.ReadAllText(path);
            Check(text.Contains("\"version\":4") && text.Contains("\"quests\":[{\"world\":\"" + world + "\",\"completions\":3}") &&
                !text.Contains("\"owner\"") && !text.Contains("\"account\""), "format 3 stores only world ID and count");
            OpenItemTransferContracts.Same(profile, OpenCharacterFile.Load(path));

            // Earlier formats upgrade in memory without touching the source file, and gain an empty record list.
            string v2 = Path.Combine(root, "v2.json");
            string v2Text = text.Replace("\"version\":4", "\"version\":2");
            v2Text = v2Text[..v2Text.IndexOf(",\"quests\"", StringComparison.Ordinal)] + "}";
            File.WriteAllText(v2, v2Text);
            var upgraded = OpenCharacterFile.Load(v2);
            Check(upgraded.Version == 4 && upgraded.Quests is { Length: 0 } && upgraded.Inventory!.Length == 46 && File.ReadAllText(v2) == v2Text, "format 2 file upgrades without records and stays unmodified");

            // Format 2 cannot carry records, format 3 must state them, and every record obeys the realm.json rules.
            Invalid("format 2 with records", profile with { Version = 2 });
            Invalid("format 1 with records", new(1, GameSimulation.RulesVersion, "open", "Traveler", 1, null, earned));
            Invalid("format 3 without records", profile with { Quests = null });
            Invalid("zero completions", profile with { Quests = [new(world, 0)] });
            Invalid("excess completions", profile with { Quests = [new(world, QuestRecords.MaxCompletions + 1)] });
            Invalid("bad world ID", profile with { Quests = [new("Bad World", 1)] });
            Invalid("duplicate world", profile with { Quests = [new(world, 1), new(world, 2)] });
            Invalid("null record", profile with { Quests = [null!] });
            Invalid("too many records", profile with { Quests = [.. Enumerable.Range(0, QuestRecords.MaxRecords + 1).Select(i => new QuestRecord("w" + i, 1))] });
            foreach (string malformed in new[] { text.Replace("\"completions\":3", "\"completions\":null"), text.Replace("\"world\":", "\"owner\":2,\"world\":"),
                text.Replace("\"quests\":", "\"quests\":null,\"quests\":") })
            {
                string bad = Path.Combine(root, "bad.json"); File.WriteAllText(bad, malformed);
                try { OpenCharacterFile.Load(bad); throw new Exception("Personal quest: malformed file accepted."); } catch (InvalidDataException) { }
            }

            string database = Path.Combine(root, "open", "realm.json");
            Guid id;
            using (var open = new Realm(Path.Combine(root, "open"), mode: "open"))
            {
                var token = open.Login(new("quest-file", "personal-quest-file-password"), true).Token;
                id = open.ImportCharacter(token, profile).Id;
                Check(open.Characters(token).Single().Quests!.SequenceEqual(earned), "import stores the records on the new character");
                OpenItemTransferContracts.Same(profile, open.ExportCharacter(token, id));
                var exported = open.ExportCharacter(token, id); exported.Quests![0] = new("changed", 9);
                Check(open.ExportCharacter(token, id).Quests!.SequenceEqual(earned), "export does not alias stored records");

                byte[] before = File.ReadAllBytes(database);
                foreach (var bad in new[] { profile with { Version = 2 }, profile with { Quests = null }, profile with { Quests = [new(world, 0)] },
                    profile with { Quests = [new(world, 1), new(world, 1)] }, profile with { Quests = [new("Bad World", 1)] } })
                    Reject(400, () => open.ImportCharacter(token, bad with { Name = "BadQuestHero" }));
                Check(before.SequenceEqual(File.ReadAllBytes(database)), "rejected quest imports leave storage unchanged");

                var plain = open.ImportCharacter(token, profile with { Name = "NoRecords", Quests = [] });
                Check(plain.Quests is null && JsonSerializer.Serialize(plain).IndexOf("Quests", StringComparison.Ordinal) < 0 &&
                    open.ExportCharacter(token, plain.Id).Quests is { Length: 0 }, "empty records stay absent on the wire and export as an empty list");

                // Playing a room and closing it must not erase what the file brought in.
                var room = open.CreateRoom(token, new(id, "QuestFileRoom", ""));
                open.Start(token, room.Id); open.Close(token, room.Id);
                Check(open.ExportCharacter(token, id).Quests!.SequenceEqual(earned), "room close keeps imported records");
            }
            using (var open = new Realm(Path.Combine(root, "open"), mode: "open"))
            {
                var token = open.Login(new("quest-file", "personal-quest-file-password"), false).Token;
                Check(open.Characters(token).Single(c => c.Id == id).Quests!.SequenceEqual(earned), "imported records survive restart");
            }
            using (var realm = new Realm(Path.Combine(root, "realm"), mode: "realm"))
            {
                var token = realm.Login(new("quest-realm", "personal-quest-file-password"), true).Token;
                var hero = realm.CreateCharacter(token, "RealmHero");
                Reject(403, () => realm.ImportCharacter(token, profile));
                Reject(403, () => realm.ExportCharacter(token, hero.Id));
            }
            Console.WriteLine("PERSONAL QUEST FILE PASS format-3/upgrade/budget/validation/import/export/restart/room-close/realm-boundary");
        }
        finally { Directory.Delete(root, true); }
    }

    // Quests are personal: what one member accepts or hands in is invisible to the others, survives a restart per member,
    // and only the member who completed earns the record and the victory.
    private static void Independent(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-quest-independent-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(folder, "realm.json");
        const string password = "personal-quest-independent-password";
        var content = CooperativeWorld.Content; var time = new TestTime();
        string[] names = ["indep-a", "indep-b", "indep-c"]; var heroes = new Guid[3]; Guid room;
        try
        {
            using (var realm = new Realm(folder, time, mode))
            {
                var tokens = names.Select(n => realm.Login(new(n, password), true).Token).ToArray();
                for (int i = 0; i < 3; i++) heroes[i] = realm.CreateCharacter(tokens[i], "Indep" + "ABC"[i]).Id;
                room = realm.CreateRoom(tokens[0], new(heroes[0], "Independent", "", content)).Id;
                for (int i = 1; i < 3; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: content));
                realm.Start(tokens[0], room);
                // The host walks up to the Guide and accepts; nobody else has said a word.
                var view = realm.StreamState(tokens[0], room, inputProgress: true, world: true);
                Check(view.World!.Quest.Stage == QuestStage.Available, "every member starts with an available quest");
                realm.Input(tokens[0], room, new(view.NextSequence, CommandKind.Interact, Target: 300, Region: 1, RegionEpoch: view.World.RegionEpoch)); realm.Tick();
                var stages = tokens.Select(t => realm.StreamState(t, room, inputProgress: true, world: true).World!.Quest.Stage).ToArray();
                Check(stages.SequenceEqual([QuestStage.Active, QuestStage.Available, QuestStage.Available]), "one member's acceptance is visible only in their own view");
                realm.Checkpoint(tokens[0], room);
            }
            // Same room, but both living members are ready to hand in and the third is dead.
            var json = JsonNode.Parse(File.ReadAllBytes(file))!;
            json["Rooms"]![0]!["Game"] = ReadyGame();
            File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(json));

            string[] Login(Realm realm)
            {
                var tokens = names.Select(n => realm.Login(new(n, password), false).Token).ToArray();
                for (int i = 0; i < 3; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: content));
                return tokens;
            }
            QuestStage Stage(Realm realm, string token) => realm.StreamState(token, room, inputProgress: true, world: true).World!.Quest.Stage;
            void HandIn(Realm realm, string token)
            {
                var view = realm.StreamState(token, room, inputProgress: true, world: true);
                realm.Input(token, room, new(view.NextSequence, CommandKind.Interact, Target: 300, Region: 1, RegionEpoch: view.World!.RegionEpoch)); realm.Tick();
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var tokens = Login(realm);
                HandIn(realm, tokens[0]);
                Check(Stage(realm, tokens[0]) == QuestStage.Completed && Stage(realm, tokens[1]) == QuestStage.ReadyToTurnIn && Stage(realm, tokens[2]) == QuestStage.ReadyToTurnIn,
                    "only the member who handed in has completed");
                realm.Checkpoint();
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var tokens = Login(realm);
                Check(Stage(realm, tokens[0]) == QuestStage.Completed && Stage(realm, tokens[1]) == QuestStage.ReadyToTurnIn, "each member's stage survives a restart");
                realm.Close(tokens[0], room); // b and c never completed
                var after = tokens.Select(t => realm.Characters(t).Single()).ToArray();
                Check(after[0].Quests!.SequenceEqual([new QuestRecord(content.Id, 1)]) && after[0].Victories == 1, "the member who completed has a record and a victory");
                Check(after[1].Quests is null && after[1].Victories == 0 && after[2].Quests is null && after[2].Victories == 0, "members who did not complete earn neither");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
