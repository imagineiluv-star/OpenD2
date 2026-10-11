using System.Text.Json.Nodes;
using OpenD2.Online;
using OpenD2.Server;

internal static class ModeContracts
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2-modes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string mode in new[] { "realm", "open" })
            {
                string directory = Path.Combine(root, mode), file = Path.Combine(directory, "realm.json");
                using (var realm = new Realm(directory, mode: mode))
                {
                    var login = realm.Login(new("mode-player", "mode-contract-password"), true);
                    realm.CreateCharacter(login.Token, "ModeHero");
                }
                byte[] original = File.ReadAllBytes(file);
                try { using var wrong = new Realm(directory, mode: mode == "realm" ? "open" : "realm"); throw new Exception("Cross-mode database accepted."); }
                catch (InvalidDataException) { }
                if (!original.SequenceEqual(File.ReadAllBytes(file))) throw new Exception("Rejected database was modified.");
                using (var reopened = new Realm(directory, mode: mode))
                {
                    var login = reopened.Login(new("mode-player", "mode-contract-password"), false);
                    if (reopened.Characters(login.Token).Single().Name != "ModeHero") throw new Exception("Mode character lost.");
                }
                // V2 always requires an explicit valid marker, including Realm.
                foreach (string? marker in new string?[] { null, "unknown" })
                {
                    var json = JsonNode.Parse(original)!.AsObject();
                    if (marker is null) json.Remove("Mode"); else json["Mode"] = marker;
                    File.WriteAllText(file, json.ToJsonString());
                    try { using var bad = new Realm(directory, mode: mode); throw new Exception("Invalid mode marker accepted."); }
                    catch (InvalidDataException) { }
                }
                File.WriteAllBytes(file, original);
                Console.WriteLine("MODE PASS " + mode + " reopen, cross-mode rejection, unchanged data and required marker");
            }
            string legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
            string legacyFile = Path.Combine(legacy, "realm.json");
            var old = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "realm", "realm.json")))!.AsObject();
            old["Version"] = 1; old.Remove("Mode");
            File.WriteAllText(legacyFile, old.ToJsonString());
            try { using var bad = new Realm(legacy, mode: "open"); throw new Exception("Legacy Realm opened as Open."); }
            catch (InvalidDataException) { }
            using (var upgraded = new Realm(legacy))
            {
                var login = upgraded.Login(new("mode-player", "mode-contract-password"), false);
                if (upgraded.Characters(login.Token).Single().Name != "ModeHero") throw new Exception("Legacy character lost.");
                upgraded.Checkpoint();
            }
            var saved = JsonNode.Parse(File.ReadAllText(legacyFile))!;
            if ((int)saved["Version"]! != 2 || (string)saved["Mode"]! != "realm") throw new Exception("Legacy mode migration failed.");
            using (var reopened = new Realm(legacy)) { }
            try { using var bad = new Realm(Path.Combine(root, "invalid"), mode: "unknown"); throw new Exception("Unknown mode accepted."); }
            catch (ArgumentException) { }
            if (Directory.Exists(Path.Combine(root, "invalid"))) throw new Exception("Invalid mode created storage.");
            string oldRules = Path.Combine(root, "old-rules"); Directory.CreateDirectory(oldRules);
            byte[] fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/realm-rules7.json"));
            File.WriteAllBytes(Path.Combine(oldRules, "realm.json"), fixture);
            using (var upgraded = new Realm(oldRules))
            {
                var login = upgraded.Login(new("migration-user", "migration-fixture-password"), false);
                var room = upgraded.Rooms(login.Token).Single();
                if (!upgraded.State(login.Token, room.Id).Started || upgraded.Characters(login.Token).Single().Name != "MigrationHero") throw new Exception("Rules7 room migration lost identity/state.");
                upgraded.Checkpoint();
            }
            using (var reopened = new Realm(oldRules)) { }
            var oldHeader = JsonNode.Parse(fixture)!;
            oldHeader["Rooms"]![0]!["Game"]!["StateHash"] = new string('0', 64);
            File.WriteAllText(Path.Combine(oldRules, "realm.json"), oldHeader.ToJsonString());
            try { using var corrupted = new Realm(oldRules); throw new Exception("Corrupt rules7 room accepted."); }
            catch (InvalidDataException) { }
            Console.WriteLine("MODE PASS rules7 active room migration, reopen and original checksum rejection");
            Console.WriteLine("MODE PASS legacy Realm migration and unknown mode rejection");
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task VerifyOpenServer(string url, string ca)
    {
        var credentials = new Credentials("open-mode-player", Guid.NewGuid().ToString("N"));
        using (var wrong = new OnlineClient(url, ca))
        {
            try { await wrong.Login(credentials, true); throw new Exception("Realm client accepted Open server."); }
            catch (InvalidDataException) { }
        }
        using var client = new OnlineClient(url, ca, "open");
        // Same name must still be available: the mismatched client never registered.
        await client.Login(credentials, true);
        var character = await client.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("OpenHero"));
        var room = await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, "OpenRoom", ""));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var feed = client.WatchRoom(room.Id, deadline.Token).GetAsyncEnumerator();
        if (!await feed.MoveNextAsync() || feed.Current.Actor != 1) throw new Exception("Open stream identity failed.");
        await client.Send<object>(HttpMethod.Post, $"v1/rooms/{room.Id}/close");
        string file = Path.Combine(Path.GetTempPath(), "opend2-tls-character-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await client.ExportCharacter(character.Id, file);
            using var peer = new OnlineClient(url, ca, "open");
            await peer.Login(new("open-transfer-peer", Guid.NewGuid().ToString("N")), true);
            var imported = await peer.ImportCharacter(file);
            if (imported.Id == character.Id || imported.Name != character.Name || imported.Victories != character.Victories)
                throw new Exception("TLS profile transfer did not assign a new owned identity.");
            var transferred = await peer.Send<OpenCharacter>(HttpMethod.Get, $"v1/characters/{imported.Id}/export");
            OpenItemTransferContracts.Same(OpenCharacterFile.Load(file), transferred);
            try { await peer.ImportCharacter(file); throw new Exception("Duplicate TLS import accepted."); }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict) { }
            string fullFile = file + ".full.json";
            try
            {
                var full = OpenItemTransferContracts.FullCharacter();
                OpenCharacterFile.Save(fullFile, full);
                var packed = await peer.ImportCharacter(fullFile);
                OpenItemTransferContracts.Same(full, await peer.Send<OpenCharacter>(HttpMethod.Get, $"v1/characters/{packed.Id}/export"));
                var packedRoom = await peer.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(packed.Id, "PackedRoom", ""));
                var playing = await peer.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{packedRoom.Id}/start");
                if (playing.Items.Length != 46) throw new Exception("TLS request lost full inventory.");
                await peer.Send<object>(HttpMethod.Post, $"v1/rooms/{packedRoom.Id}/close");
                // Direct HTTP requests must enforce required item fields, not just the file loader.
                try
                {
                    await peer.Send<CharacterInfo>(HttpMethod.Post, "v1/characters/import", new { version = 2,
                        rules = OpenD2.Core.GameSimulation.RulesVersion, mode = "open", name = "MissingSlot", victories = 0,
                        inventory = new[] { new { definition = 0, location = 1 } } });
                    throw new Exception("HTTP import accepted missing item slot.");
                }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest) { }
                try
                {
                    await peer.Send<CharacterInfo>(HttpMethod.Post, "v1/characters/import", full with { Name = "BadPlacement",
                        Inventory = [new(OpenD2.Core.ItemDefinition.HealthPotion, OpenD2.Core.ItemLocation.Equipped, 0)] });
                    throw new Exception("HTTP import accepted invalid equipment.");
                }
                catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest) { }
                Console.WriteLine("TLS OPEN ITEMS PASS full 46-item request, room admission and invalid HTTP rejection");
            }
            finally { if (File.Exists(fullFile)) File.Delete(fullFile); }
        }
        finally { if (File.Exists(file)) File.Delete(file); }

    }
}
