using System.Text.Json.Nodes;
using OpenD2.Core;

static class CarriedItemContracts
{
	static void Check(bool yes) { if (!yes) throw new Exception("Carried item assertion failed."); }
	static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Malformed carried items accepted."); }
	static CollisionGrid Grid() => new(new(1), 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray());
	static GameSimulation Fresh(uint player = 1) => new(1, [new(new(player), new(1), new(384, 384), Health: 50, Mana: 30), new(new(2), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 1)], Grid());
	internal static void Run(string root, Action<string, Action> test)
	{
		test("carried IDs remain disjoint from new room equipment and potion drops", () =>
		{
			var empty = Fresh();
			var game = empty.WithCarriedItems([new(new(1), ItemDefinition.TrainingSword, ItemLocation.Equipped, 0), new(new(1), ItemDefinition.HealthPotion, ItemLocation.Belt, 0)]);
			Check(empty.Items.Length == 0 && game.GetStats(new(1)).MinimumDamage == 20);
			Check(game.Submit(new(1, 1, new(1), new(1), CommandKind.Attack, Target: new(2))) == CommandResult.Accepted); game.Step();
			Check(game.Items.Length == 4 && game.Items.ToArray().Select(i => i.Id).Distinct().Count() == 4);
			Check(game.GetItem(new(2)).Location == ItemLocation.Ground && game.BeltAt(new(1), 0).Value >> 32 == 2);
			var next = Fresh(9).WithCarriedItems(game.CaptureCarriedItems(new(1)).Select(i => i with { Owner = new(9) }).ToArray());
			Check(next.Items.Length == 2 && next.Items.ToArray().All(i => i.Owner == new EntityId(9)) && next.GetStats(new(9)).MinimumDamage == 20);
		});
		test("carried placement ownership identity and admission budgets fail without mutation", () =>
		{
			var game = Fresh(); string before = game.ComputeStateHash();
			CarriedItem valid = new(new(1), ItemDefinition.HealthPotion, ItemLocation.Inventory, 0);
			foreach (var invalid in new[] { valid with { Owner = new(2) }, valid with { Definition = (ItemDefinition)999 }, valid with { Location = ItemLocation.Ground }, valid with { Slot = 40 }, valid with { Location = ItemLocation.Equipped } })
				Reject(() => game.WithCarriedItems([invalid]));
			Reject(() => game.WithCarriedItems([valid, valid]));
			Reject(() => game.WithCarriedItems(Enumerable.Repeat(valid, GameSimulation.MaxCarriedItems + 1).ToArray()));
			Check(game.ComputeStateHash() == before);
			var full = game.WithCarriedItems([valid]); Reject(() => full.WithCarriedItems([]));
			var state = full.CaptureSnapshot();
			foreach (ulong id in new[] { 2UL << 32, (2UL << 32) | 257, (3UL << 32) | 1 })
				Reject(() => GameSimulation.Restore(state with { Items = [state.Items[0] with { Id = new(id) }] }));
			game.Step(); Reject(() => game.WithCarriedItems([]));
		});
		test("carried drops consumption saves and deterministic replay preserve room IDs", () =>
		{
			var idle = new GameSimulation(1, [new(new(1), new(1), new(384, 384), Health: 50, Mana: 30)], Grid());
			var game = idle.WithCarriedItems([new(new(1), ItemDefinition.HealthPotion, ItemLocation.Belt, 0), new(new(1), ItemDefinition.ManaPotion, ItemLocation.Inventory, 1)]);
			var baseline = game.CaptureSnapshot(); var command = new GameCommand(1, 1, new(1), new(1), CommandKind.UseBelt, 0);
			Check(game.Submit(command) == CommandResult.Accepted); game.Step();
			Check(game.Items[0].Location == ItemLocation.Consumed && game.CaptureCarriedItems(new(1)).Length == 1);
			Check(GameSimulation.Replay(baseline, [new(0, command)], 1).ComputeStateHash() == game.ComputeStateHash());
			Check(game.Submit(new(2, 2, new(1), new(1), CommandKind.DropItem, Item: game.Items[1].Id)) == CommandResult.Accepted); game.Step();
			Check(game.CaptureCarriedItems(new(1)).Length == 0 && game.Items[1].Location == ItemLocation.Ground);
			string file = Path.Combine(root, "carried-roundtrip.json"); GameSave.Save(file, game.CaptureSnapshot());
			Check(GameSave.Load(file, Grid()).Simulation.ComputeStateHash() == game.ComputeStateHash());
		});
		test("v4 rules7 checked save migrates belt consumed items queue and original bytes", () =>
		{
			string file = Path.Combine(root, "carried-v4.json"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures/save-v4-items.json"), file);
			byte[] before = File.ReadAllBytes(file); var loaded = GameSave.Load(file, Grid());
			Check(loaded.Migrated && loaded.Simulation.PendingCommands == 1 && loaded.Simulation.Items.Length == 3 && loaded.Simulation.Items[1].Location == ItemLocation.Consumed);
			Check(before.SequenceEqual(File.ReadAllBytes(file)));
			GameSave.Save(file, loaded.Simulation.CaptureSnapshot());
			Check(before.SequenceEqual(File.ReadAllBytes(file + ".bak")) && !GameSave.Load(file, Grid()).Migrated);
			var old = JsonNode.Parse(before)!; old["Items"]![0]!["Id"]!["Value"] = (2UL << 32) | 1;
			string bad = Path.Combine(root, "carried-v4-bad.json"); File.WriteAllText(bad, old.ToJsonString());
			Reject(() => GameSave.Load(bad, Grid()));
		});
		test("imported capacity reserves room loot space independently of actor count", () =>
		{
			var items = Enumerable.Range(0, 40).Select(i => new CarriedItem(new(1), ItemDefinition.HealthPotion, ItemLocation.Inventory, i)).ToArray();
			var game = Fresh().WithCarriedItems(items);
			Check(game.Submit(new(1, 1, new(1), new(1), CommandKind.Attack, Target: new(2))) == CommandResult.Accepted); game.Step();
			Check(game.Items.Length == 42 && GameSimulation.Restore(game.CaptureSnapshot()).Items.Length == 42);
		});
	}
}
