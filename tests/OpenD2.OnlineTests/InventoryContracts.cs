using System.Text.Json;
using OpenD2.Core;
using OpenD2.Server;

internal static class InventoryContracts
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string mode in new[] { "realm", "open" })
            {
                string folder = Path.Combine(root, mode), file = Path.Combine(folder, "realm.json");
                Guid character, room;
                using (var server = new Realm(folder, mode: mode))
                {
                    var login = server.Login(new("inventory-user", "inventory-contract-password"), true);
                    character = server.CreateCharacter(login.Token, "Carrier").Id;
                }
                // Fixed trusted storage fixture. Gameplay uses only normal room/input APIs.
                var db = JsonSerializer.Deserialize<Database>(File.ReadAllBytes(file))!;
                db = db with { Characters = [db.Characters[0] with { Inventory = [
                    new(new(1), ItemDefinition.TrainingSword, ItemLocation.Equipped, 0),
                    new(new(1), ItemDefinition.ManaPotion, ItemLocation.Inventory, 0)] }] };
                File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(db));
                using (var server = new Realm(folder, mode: mode))
                {
                    var login = server.Login(new("inventory-user", "inventory-contract-password"), false);
                    room = server.CreateRoom(login.Token, new(character, "FirstRoom", "")).Id;
                    var state = server.Start(login.Token, room);
                    if (state.Items.Length != 2 || state.Items.Any(i => i.Owner.Value != 1 || i.Id.Value >> 32 != 2)) throw new Exception("Carried admission failed.");
                    var potion = state.Items.Single(i => i.Definition == ItemDefinition.ManaPotion);
                    server.Input(login.Token, room, new(state.NextSequence, CommandKind.DropItem, Item: potion.Id.Value)); server.Tick();
                    server.Checkpoint();
                }
                using (var server = new Realm(folder, mode: mode))
                {
                    var login = server.Login(new("inventory-user", "inventory-contract-password"), false);
                    if (!server.State(login.Token, room).Items.Any(i => i.Location == ItemLocation.Ground)) throw new Exception("Restart lost dropped item.");
                    var committed = File.ReadAllBytes(file);
                    Directory.CreateDirectory(file + ".tmp");
                    try { server.Close(login.Token, room); throw new Exception("Expected close storage failure."); }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                    try { server.Characters(login.Token); throw new Exception("Failed storage accepted a request."); }
                    catch (Rejected error) when (error.Status == 503) { }
                    if (!committed.SequenceEqual(File.ReadAllBytes(file))) throw new Exception("Failed close partially persisted inventory or removed room.");
                }
                Directory.Delete(file + ".tmp");
                using (var server = new Realm(folder, mode: mode))
                {
                    var login = server.Login(new("inventory-user", "inventory-contract-password"), false);
                    if (!server.State(login.Token, room).Items.Any(i => i.Location == ItemLocation.Ground)) throw new Exception("Failed close lost recoverable room.");
                    server.Close(login.Token, room);
                    try { server.Close(login.Token, room); throw new Exception("Repeated close succeeded."); }
                    catch (Rejected error) when (error.Status == 404) { }
                }
                using (var server = new Realm(folder, mode: mode))
                {
                    var login = server.Login(new("inventory-user", "inventory-contract-password"), false);
                    for (int i = 0; i < 3; i++)
                    {
                        room = server.CreateRoom(login.Token, new(character, "NextRoom", "")).Id;
                        var state = server.Start(login.Token, room);
                        if (state.Items.Length != 1 || state.Items[0].Definition != ItemDefinition.TrainingSword || state.Items[0].Location != ItemLocation.Equipped) throw new Exception("Dropped item resurrected or equipment lost between rooms.");
                        server.Close(login.Token, room);
                    }
                }
                Console.WriteLine("INVENTORY PASS " + mode + " carried equipment, dropped-item exclusion, active restart, failed close recovery and repeated room transfer");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
