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
            if (transferred != OpenCharacterFile.Load(file)) throw new Exception("TLS imported profile mismatch.");
            try { await peer.ImportCharacter(file); throw new Exception("Duplicate TLS import accepted."); }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict) { }
        }
        finally { if (File.Exists(file)) File.Delete(file); }

    }
}
