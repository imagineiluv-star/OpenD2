using System.Text.Json;
using OpenD2.Online;

internal static class ServerCatalogContracts
{
    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "servers.json"), invalid = Path.Combine(folder, "invalid.json");
        var entry = new ServerEntry("Realm preview", "North America", "https://realm.example.invalid", "realm");
        var catalog = new ServerCatalog(1, [entry, new("Open preview", "Asia", "https://open.example.invalid", "open", new string('A', 64))]);
        void Reject(string json)
        {
            File.WriteAllText(invalid, json);
            try { ServerCatalog.Load(invalid); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid catalog accepted.");
        }
        try
        {
            catalog.Save(path);
            var loaded = ServerCatalog.Load(path);
            if (loaded.Version != 1 || !loaded.Servers.SequenceEqual(catalog.Servers) || loaded.Servers.Any(s => s.ToInvite().Version != 1))
                throw new Exception("Catalog roundtrip failed.");
            var legacy = new ConnectionInvite { Version = 1, Address = entry.Address, Mode = entry.Mode };
            if (ConnectionInvite.Parse(loaded.Servers[0].ToInvite().Encode()) != legacy) throw new Exception("Catalog changed connection contract.");
            string original = File.ReadAllText(path), json = JsonSerializer.Serialize(catalog);
            Reject("null"); Reject("{}"); Reject(json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"));
            foreach (string field in new[] { "Password", "Account", "Invitation", "Room", "CaFile" })
                Reject(json.Replace("\"Name\":", "\"" + field + "\":\"secret\",\"Name\":"));
            Reject(JsonSerializer.Serialize(catalog with { Version = 2 }));
            Reject(JsonSerializer.Serialize(catalog with { Servers = [] }));
            Reject(JsonSerializer.Serialize(catalog with { Servers = null! }));
            Reject(JsonSerializer.Serialize(catalog with { Servers = [null!] }));
            foreach (var bad in new[] { entry with { Name = "" }, entry with { Region = " padded" }, entry with { Name = "a\nb" },
                entry with { Name = "a\u202eb" }, entry with { Name = new string('a', 65) }, entry with { Region = new string('r', 33) },
                entry with { Address = "http://127.0.0.1" }, entry with { Address = "https://user:secret@example.invalid" },
                entry with { Address = "https://example.invalid?token=secret" }, entry with { Mode = "bad" }, entry with { RootCaSha256 = "bad" } })
                Reject(JsonSerializer.Serialize(new ServerCatalog(1, [bad])));
            Reject(JsonSerializer.Serialize(new ServerCatalog(1, [entry, entry with { Address = "https://REALM.example.invalid:443/" }])));
            Reject(JsonSerializer.Serialize(new ServerCatalog(1, Enumerable.Range(0, 65).Select(i => entry with { Address = $"https://server{i}.invalid" }).ToArray())));
            Reject(new string(' ', ServerCatalog.MaxBytes + 1));
            File.WriteAllBytes(invalid, [0xff, 0xfe]);
            try { ServerCatalog.Load(invalid); throw new Exception("Invalid UTF8 accepted."); } catch (InvalidDataException) { }
            try { (catalog with { Version = 2 }).Save(path); throw new Exception("Invalid catalog saved."); } catch (InvalidDataException) { }
            if (File.ReadAllText(path) != original) throw new Exception("Failed save damaged catalog.");
            string blocked = Path.Combine(folder, "blocked.json"); Directory.CreateDirectory(blocked);
            try { catalog.Save(blocked); throw new Exception("Write failure ignored."); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (Directory.GetFiles(folder, "*.tmp").Length != 0) throw new Exception("Temporary catalog leaked.");
            (catalog with { Servers = [entry with { Region = "Europe" }] }).Save(path);
            if (ServerCatalog.Load(path).Servers.Single().Region != "Europe") throw new Exception("Catalog replacement failed.");
            Console.WriteLine("SERVER CATALOG PASS roundtrip/strict-schema/HTTPS/trust/duplicates/bounds/atomic-replacement/no-credentials");
        }
        finally { Directory.Delete(folder, true); }
    }
}
