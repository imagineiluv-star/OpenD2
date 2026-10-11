using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class OpenItemTransferContracts
{
    internal static OpenCharacter FullCharacter() => new(2, GameSimulation.RulesVersion, "open", "PackedHero", 9,
        [.. Enumerable.Range(0, 40).Select(i => new OpenItem(ItemDefinition.HealthPotion, ItemLocation.Inventory, i)),
         new(ItemDefinition.TrainingSword, ItemLocation.Equipped, 0), new(ItemDefinition.TrainingVest, ItemLocation.Equipped, 1),
         .. Enumerable.Range(0, 4).Select(i => new OpenItem(ItemDefinition.ManaPotion, ItemLocation.Belt, i))]);
    internal static void Same(OpenCharacter expected, OpenCharacter actual)
    {
        if (JsonSerializer.Serialize(OpenCharacterFile.Upgrade(expected)) != JsonSerializer.Serialize(OpenCharacterFile.Upgrade(actual)))
            throw new Exception("Transferred character content changed.");
    }
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2-item-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profile = FullCharacter();
            string file = Path.Combine(root, "full.json");
            OpenCharacterFile.Save(file, profile);
            Same(profile, OpenCharacterFile.Load(file));
            if (new FileInfo(file).Length > OpenCharacterFile.MaximumBytes) throw new Exception("Full character exceeds file budget.");
            var json = File.ReadAllText(file);
            foreach (string field in new[] { "owner", "account", "token", "position", "id" })
                if (json.Contains('"' + field + '"')) throw new Exception("File leaked server identity: " + field);
            // Legacy files have no inventory property. Read/upgrade must not rewrite them.
            string legacy = Path.Combine(root, "legacy.json");
            foreach (int rules in new[] { 7, GameSimulation.RulesVersion })
            {
                string old = $$"""{"version":1,"rules":{{rules}},"mode":"open","name":"OldHero","victories":3}""";
                File.WriteAllText(legacy, old);
                var upgraded = OpenCharacterFile.Load(legacy);
                if (upgraded.Version != 2 || upgraded.Inventory is not { Length: 0 } || upgraded.Victories != 3 || File.ReadAllText(legacy) != old)
                    throw new Exception("Legacy upgrade lost data or modified source.");
            }
            foreach (string invalid in new[] {
                json.Replace("\"slot\":0", "\"slot\":null"), json.Replace(",\"slot\":0", ""),
                json.Replace("\"slot\":0}", "\"definition\":2}"), // duplicate property / missing slot
                json.Replace("\"slot\":0", "\"owner\":2,\"slot\":0"),
                json.Replace("\"version\":2", "\"version\":1"), new string(' ', OpenCharacterFile.MaximumBytes + 1) })
            {
                string bad = Path.Combine(root, "bad.json"); File.WriteAllText(bad, invalid);
                try { OpenCharacterFile.Load(bad); throw new Exception("Malformed item file accepted."); }
                catch (InvalidDataException) { }
            }
            Guid sourceId;
            using (var source = new Realm(Path.Combine(root, "source"), mode: "open"))
            {
                var login = source.Login(new("item-owner", "item-transfer-password"), true);
                sourceId = source.ImportCharacter(login.Token, profile).Id;
                var exported = source.ExportCharacter(login.Token, sourceId);
                Same(profile, exported);
                exported.Inventory![0] = exported.Inventory[0] with { Slot = 39 };
                Same(profile, source.ExportCharacter(login.Token, sourceId)); // no mutable array alias
                byte[] before = File.ReadAllBytes(Path.Combine(root, "source", "realm.json"));
                foreach (var bad in new[] {
                    profile with { Inventory = null }, profile with { Rules = 7 },
                    profile with { Inventory = [null!] }, profile with { Inventory = [.. profile.Inventory!, profile.Inventory![0]] },
                    profile with { Inventory = [new((ItemDefinition)999, ItemLocation.Inventory, 0)] },
                    profile with { Inventory = [new(ItemDefinition.TrainingSword, ItemLocation.Inventory, 0), new(ItemDefinition.HealthPotion, ItemLocation.Inventory, 10)] },
                    profile with { Inventory = [new(ItemDefinition.TrainingVest, ItemLocation.Inventory, 39)] },
                    profile with { Inventory = [new(ItemDefinition.HealthPotion, ItemLocation.Equipped, 0)] },
                    profile with { Inventory = [new(ItemDefinition.TrainingSword, ItemLocation.Belt, 0)] },
                    profile with { Inventory = [new(ItemDefinition.ManaPotion, ItemLocation.Belt, 4)] },
                    profile with { Inventory = [new(ItemDefinition.HealthPotion, ItemLocation.Ground, -1)] },
                    profile with { Inventory = [new(ItemDefinition.HealthPotion, ItemLocation.Consumed, -1)] } })
                {
                    try { source.ImportCharacter(login.Token, bad with { Name = "InvalidHero" }); throw new Exception("Invalid item import accepted."); }
                    catch (Rejected error) when (error.Status == 400) { }
                    if (!before.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "source", "realm.json")))) throw new Exception("Rejected import changed storage.");
                }
                OpenCharacterFile.Save(Path.Combine(root, "export.json"), source.ExportCharacter(login.Token, sourceId));
            }
            Guid importedId;
            using (var destination = new Realm(Path.Combine(root, "destination"), mode: "open"))
            {
                var login = destination.Login(new("new-owner", "item-transfer-password"), true);
                importedId = destination.ImportCharacter(login.Token, OpenCharacterFile.Load(Path.Combine(root, "export.json"))).Id;
                if (importedId == sourceId) throw new Exception("Source identity was reused.");
            }
            using (var destination = new Realm(Path.Combine(root, "destination"), mode: "open"))
            {
                var login = destination.Login(new("new-owner", "item-transfer-password"), false);
                Same(profile, destination.ExportCharacter(login.Token, importedId));
                var room = destination.CreateRoom(login.Token, new(importedId, "ImportedRoom", ""));
                var state = destination.Start(login.Token, room.Id);
                if (state.Items.Length != 46 || state.Items.Any(i => i.Owner.Value != state.Actor || i.Id.Value >> 32 != 2))
                    throw new Exception("Full inventory was not admitted into a new room.");
                var belt = state.Items.Single(i => i.Location == ItemLocation.Belt && i.Slot == 0);
                destination.Input(login.Token, room.Id, new(state.NextSequence, CommandKind.DropItem, Item: belt.Id.Value)); destination.Tick();
                destination.Close(login.Token, room.Id);
                var after = destination.ExportCharacter(login.Token, importedId);
                if (after.Inventory!.Length != 45 || after.Inventory.Any(i => i.Location == ItemLocation.Belt && i.Slot == 0))
                    throw new Exception("Dropped imported item was resurrected in exported file.");
            }
            Console.WriteLine("OPEN ITEMS PASS full 46-item file, legacy upgrade, strict validation, atomic rejection, independent server restart and playable inventory");
        }
        finally { Directory.Delete(root, true); }
    }
}
