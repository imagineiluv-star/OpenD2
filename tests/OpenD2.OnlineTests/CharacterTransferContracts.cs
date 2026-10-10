using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class CharacterTransferContracts
{
    static void Reject(int status, Action action)
    {
        try { action(); throw new Exception("Expected transfer rejection " + status); }
        catch (Rejected error) when (error.Status == status) { }
    }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = new OpenCharacter(1, GameSimulation.RulesVersion, "open", "Traveler", 7);
            string path = Path.Combine(root, "hero.json");
            OpenCharacterFile.Save(path, profile);
            if (OpenCharacterFile.Load(path) != profile) throw new Exception("Local profile roundtrip failed.");
            byte[] original = File.ReadAllBytes(path);
            try { OpenCharacterFile.Save(path, profile with { Victories = 8 }); throw new Exception("Existing save overwritten."); }
            catch (IOException) { }
            if (!original.SequenceEqual(File.ReadAllBytes(path))) throw new Exception("Existing save changed.");
            foreach (string invalid in new[] { "null", "{}", "[]", "{", new string(' ', 8193),
                System.Text.Encoding.UTF8.GetString(original).Replace("\"name\":", "\"inventory\":[],\"name\":"),
                System.Text.Encoding.UTF8.GetString(original).Replace("\"victories\":7", "\"victories\":null") })
            {
                File.WriteAllText(path, invalid);
                try { OpenCharacterFile.Load(path); throw new Exception("Malformed profile accepted."); }
                catch (InvalidDataException) { }
            }
            using (var realm = new Realm(Path.Combine(root, "realm")))
            {
                var login = realm.Login(new("realm-user", "transfer-test-password"), true);
                var c = realm.CreateCharacter(login.Token, "RealmHero");
                Reject(403, () => realm.ImportCharacter(login.Token, profile));
                Reject(403, () => realm.ExportCharacter(login.Token, c.Id));
            }
            Guid id;
            using (var open = new Realm(Path.Combine(root, "open"), mode: "open"))
            {
                var login = open.Login(new("open-user", "transfer-test-password"), true);
                var other = open.Login(new("other-user", "transfer-test-password"), true);
                Reject(401, () => open.ImportCharacter("invalid", profile));
                var c = open.ImportCharacter(login.Token, profile); id = c.Id;
                if (open.ExportCharacter(login.Token, id) != profile) throw new Exception("Imported profile changed.");
                Reject(404, () => open.ExportCharacter(other.Token, id));
                string database = Path.Combine(root, "open", "realm.json");
                byte[] before = File.ReadAllBytes(database);
                foreach (var invalid in new[] { profile with { Mode = "realm" }, profile with { Version = 2 },
                    profile with { Rules = -1 }, profile with { Name = "../bad" }, profile with { Victories = -1 },
                    profile with { Victories = int.MaxValue }, profile with { Name = null! } })
                    Reject(400, () => open.ImportCharacter(login.Token, invalid));
                Reject(409, () => open.ImportCharacter(login.Token, profile with { Name = "TRAVELER" }));
                if (!before.SequenceEqual(File.ReadAllBytes(database))) throw new Exception("Rejected import mutated storage.");
                var room = open.CreateRoom(login.Token, new(id, "TransferRoom", ""));
                Reject(409, () => open.ExportCharacter(login.Token, id));
                open.Close(login.Token, room.Id);
                if (open.ExportCharacter(login.Token, id) != profile) throw new Exception("Closed room profile changed.");
                for (int i = 0; i < 3; i++) open.ImportCharacter(login.Token, profile with { Name = "Hero" + i });
                Reject(409, () => open.ImportCharacter(login.Token, profile with { Name = "TooMany" }));
            }
            using (var open = new Realm(Path.Combine(root, "open"), mode: "open"))
            {
                var login = open.Login(new("open-user", "transfer-test-password"), false);
                if (open.ExportCharacter(login.Token, id) != profile) throw new Exception("Imported profile lost on restart.");
            }
            using (var destination = new Realm(Path.Combine(root, "destination"), mode: "open"))
            {
                var login = destination.Login(new("destination-user", "transfer-test-password"), true);
                var imported = destination.ImportCharacter(login.Token, profile);
                if (imported.Id == id || destination.ExportCharacter(login.Token, imported.Id) != profile)
                    throw new Exception("Independent host transfer failed.");
                var room = destination.CreateRoom(login.Token, new(imported.Id, "NewHostRoom", ""));
                if (!destination.Start(login.Token, room.Id).Started) throw new Exception("Imported character cannot play.");
                Reject(409, () => destination.ExportCharacter(login.Token, imported.Id));
                destination.Close(login.Token, room.Id);
            }
            Console.WriteLine("TRANSFER PASS local snapshots, malformed files, mode/owner/room boundaries, duplicate/capacity rejection and restart");
        }
        finally { Directory.Delete(root, true); }
    }
}
