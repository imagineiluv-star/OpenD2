using System.Text.Json.Nodes;
using OpenD2.Assets;
using OpenD2.Core;

internal static class PotionContracts
{
	private static readonly EntityId Player = new(1);
	private static ItemId Id(uint source) => new((1UL << 32) | source);
	private static ItemState Potion(uint source, ItemLocation location = ItemLocation.Inventory, int slot = 0) => new(Id(source), source % 2 == 0 ? ItemDefinition.HealthPotion : ItemDefinition.ManaPotion, location, Player, slot, default, default);
	private static CollisionGrid Grid() => new(new(1), 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray());
	private static GameSimulation Game(ItemState[]? items = null, int hp = 20, int mana = 10)
	{
		var game = new GameSimulation(1, [new(Player, new(1), new(384, 384), Health: hp, Mana: mana),
			new(new(2), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 0), new(new(3), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 0), new(new(4), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 0)], Grid());
		return GameSimulation.Restore(game.CaptureSnapshot() with { Items = items ?? [Potion(2), Potion(3, slot: 1), Potion(4, slot: 2)] });
	}
	private static GameCommand Command(GameSimulation game, CommandKind kind, ItemId item = default, int slot = 0, ulong? sequence = null) =>
		new(game.Tick + 1, sequence ?? game.CaptureSnapshot().Inputs[0].Sequence + 1, Player, new(1), kind, X: slot, Item: item);
	private static void Check(bool value) { if (!value) throw new Exception("Potion assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
	private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);
	private static void Act(GameSimulation game, CommandKind kind, ItemId item = default, int slot = 0) { Accept(game, Command(game, kind, item, slot)); game.Step(); }
	private static void Edit(string path, Action<JsonNode> edit) { var json = JsonNode.Parse(File.ReadAllText(path))!; edit(json); File.WriteAllText(path, json.ToJsonString()); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("potions have fixed 1x1 footprints and cannot alter equipment content identity", () =>
		{
			var layout = InventoryLayout.Default;
			Check(layout.Entries.Count == 2 && layout.Get(ItemDefinition.HealthPotion).Width == 1 && layout.Get(ItemDefinition.ManaPotion).Height == 1);
			Check(layout.Mask(ItemDefinition.HealthPotion, 39) == 1UL << 39);
			Throws<InvalidDataException>(() => new InventoryLayout([..layout.Entries, new(ItemDefinition.HealthPotion, "hp1", 1, 1)]));
			Throws<InvalidDataException>(() => LegacyItemDefinitions.Snapshot(new(ItemTables.Profile, [new("HealthPotion", "hp1")])));
		});
		test("each new monster death drops gear and one potion with distinct full-width IDs without extra RNG", () =>
		{
			foreach (uint source in new uint[] { 2, 3, uint.MaxValue })
			{
				var game = new GameSimulation(1, [new(Player, new(1), new(384, 384)), new(new(source), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 1)], Grid());
				Accept(game, Command(game, CommandKind.Attack) with { Target = new(source) }); game.Step();
				Check(game.Items.Length == 2 && game.GetItem(new(source)).Id != game.GetItem(Id(source)).Id && game.RandomState == 270369);
				Check(game.GetItem(Id(source)).Definition == (source % 2 == 0 ? ItemDefinition.HealthPotion : ItemDefinition.ManaPotion));
				Check(GameSimulation.Restore(game.CaptureSnapshot()).ComputeStateHash() == game.ComputeStateHash());
			}
		});
		test("health potion restores forty once and retains an inert consumption record", () =>
		{
			var game = Game(); Act(game, CommandKind.UseItem, Id(2));
			Check(game.GetEntity(Player).Health == 60 && game.GetItem(Id(2)).Location == ItemLocation.Consumed && game.GetItem(Id(2)).Owner == default && game.GetItem(Id(2)).Slot == -1);
			Check(game.Events[0].Kind == SimulationEventKind.ItemConsumed && game.Events[0].Value == 40 && game.RandomState == 1 && game.ItemAt(Player, 0) == default);
			Act(game, CommandKind.UseItem, Id(2)); Check(game.GetEntity(Player).Health == 60 && game.Events[0].Kind == SimulationEventKind.ItemFailed);
		});
		test("mana potion clamps recovery and clears fractional recovery only when full", () =>
		{
			var game = Game(mana: 10); Act(game, CommandKind.UseItem, Id(3)); Check(game.GetEntity(Player).Mana == 40 && game.GetEntity(Player).ManaRecoveryTicks == 1);
			var full = Game(hp: 90, mana: 50); Act(full, CommandKind.UseItem, Id(3)); Check(full.GetEntity(Player).Mana == 60 && full.GetEntity(Player).ManaRecoveryTicks == 0 && full.Events[0].Value == 10);
			Act(full, CommandKind.UseItem, Id(2)); Check(full.GetEntity(Player).Health == 100 && full.Events[0].Value == 10);
		});
		test("full or zero-capacity resources reject consumption while retaining the item", () =>
		{
			var game = Game(hp: 100, mana: 60); var before = game.Items.ToArray(); Act(game, CommandKind.UseItem, Id(2)); Act(game, CommandKind.UseItem, Id(3));
			Check(game.Items.SequenceEqual(before) && game.Events[0].Value == (int)ItemFailure.ResourceFull && game.RandomState == 1);
			var state = game.CaptureSnapshot(); var zero = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { MaxMana = 0, Mana = 0 } : e).ToArray() });
			Act(zero, CommandKind.UseItem, Id(3)); Check(zero.GetItem(Id(3)).Location == ItemLocation.Inventory && zero.GetEntity(Player).Mana == 0);
		});
		test("same-tick duplicate IDs and different potion IDs cannot heal twice", () =>
		{
			foreach (var second in new[] { Id(2), Id(4) })
			{
				var game = Game(); Accept(game, Command(game, CommandKind.UseItem, Id(2), sequence: 1)); Accept(game, Command(game, CommandKind.UseItem, second, sequence: 2)); game.Step();
				Check(game.GetEntity(Player).Health == 60 && game.GetItem(Id(4)).Location == ItemLocation.Inventory && game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.ItemConsumed) == 1);
				Act(game, CommandKind.UseItem, Id(4)); Check(game.GetEntity(Player).Health == 100);
			}
		});
		test("failed full-resource use does not reserve the tick for another useful potion", () =>
		{
			var game = Game(hp: 100); Accept(game, Command(game, CommandKind.UseItem, Id(2), sequence: 1)); Accept(game, Command(game, CommandKind.UseItem, Id(3), sequence: 2)); game.Step();
			Check(game.GetItem(Id(2)).Location == ItemLocation.Inventory && game.GetEntity(Player).Mana == 40);
		});
		test("bag-to-belt and belt-to-belt swaps return occupants to the exact source position", () =>
		{
			var game = Game(); Act(game, CommandKind.BeltItem, Id(2), 0); Act(game, CommandKind.BeltItem, Id(3), 0);
			Check(game.BeltAt(Player, 0) == Id(3) && game.GetItem(Id(2)).Location == ItemLocation.Inventory && game.GetItem(Id(2)).Slot == 1);
			Act(game, CommandKind.BeltItem, Id(4), 3); Act(game, CommandKind.BeltItem, Id(3), 3);
			Check(game.BeltAt(Player, 0) == Id(4) && game.BeltAt(Player, 3) == Id(3));
			Act(game, CommandKind.BeltItem, Id(3), 3); Check(game.BeltAt(Player, 3) == Id(3) && game.GetStats(Player) == new CombatStats(14, 20, 0));
		});
		test("belt use resolves the slot at execution time and reports an empty belt", () =>
		{
			var game = Game(); Accept(game, Command(game, CommandKind.BeltItem, Id(3), 2, 1)); Accept(game, Command(game, CommandKind.UseBelt, slot: 2, sequence: 2)); game.Step();
			Check(game.GetEntity(Player).Mana == 40 && game.BeltAt(Player, 2) == default);
			Act(game, CommandKind.UseBelt, slot: 2); Check(game.Events[0].Value == (int)ItemFailure.EmptyBelt && game.RandomState == 1);
		});
		test("belt potions can return to an exact empty bag cell, unequip, drop and repick", () =>
		{
			var game = Game(); Act(game, CommandKind.BeltItem, Id(2), 0); Act(game, CommandKind.MoveItem, Id(2), 3);
			Check(game.GetItem(Id(2)).Location == ItemLocation.Inventory && game.GetItem(Id(2)).Slot == 3);
			Act(game, CommandKind.BeltItem, Id(2), 0); Act(game, CommandKind.Unequip, Id(2)); Check(game.GetItem(Id(2)).Slot == 0);
			Act(game, CommandKind.BeltItem, Id(2), 0); Act(game, CommandKind.DropItem, Id(2)); Act(game, CommandKind.Pickup, Id(2)); Check(game.GetItem(Id(2)).Location == ItemLocation.Inventory);
		});
		test("belt-to-occupied-bag and full-bag unequip preserve both positions", () =>
		{
			var game = Game(); Act(game, CommandKind.BeltItem, Id(2), 0); var before = game.Items.ToArray(); Act(game, CommandKind.MoveItem, Id(2), 1);
			Check(game.Items.SequenceEqual(before) && game.Events[0].Value == (int)ItemFailure.InvalidPlacement);
			var layout = new InventoryLayout([new(ItemDefinition.TrainingSword, "", 10, 4), new(ItemDefinition.TrainingVest, "", 2, 3)]);
			var state = Game([Potion(2, ItemLocation.Belt), new(new(2), ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 0, default, default)]).CaptureSnapshot();
			game = GameSimulation.Restore(state with { Inventory = layout }); before = game.Items.ToArray(); Act(game, CommandKind.Unequip, Id(2));
			Check(game.Items.SequenceEqual(before) && game.Events[0].Value == (int)ItemFailure.InventoryFull);
		});
		test("gear cannot enter a belt or be drunk and potions cannot equip as weapons", () =>
		{
			var game = Game([Potion(2), new(new(2), ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 3, default, default)]);
			var before = game.Items.ToArray(); Act(game, CommandKind.BeltItem, new(2), 0); Act(game, CommandKind.UseItem, new(2)); Act(game, CommandKind.Equip, Id(2));
			Check(game.Items.SequenceEqual(before) && !game.CanBeltItem(Player, new(2), 0) && game.GetStats(Player).MinimumDamage == 14);
		});
		test("malformed belt/use commands do not advance cursor or RNG", () =>
		{
			var game = Game(); string before = game.ComputeStateHash(); var valid = Command(game, CommandKind.BeltItem, Id(2));
			foreach (var command in new[] { valid with { X = -1 }, valid with { X = 4 }, valid with { Y = 1 }, valid with { Target = new(2) }, valid with { Item = default }, valid with { Kind = CommandKind.UseBelt }, valid with { Kind = CommandKind.UseItem, X = 1 } })
				Check(game.Submit(command) == CommandResult.InvalidCommand);
			Check(game.ComputeStateHash() == before && game.Submit(valid with { Item = new(ulong.MaxValue) }) == CommandResult.InvalidTarget);
		});
		test("stun, death and foreign ownership cannot consume or transfer potions", () =>
		{
			var state = Game().CaptureSnapshot();
			var stunned = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { HitStun = 1 } : e).ToArray() });
			Act(stunned, CommandKind.UseItem, Id(2)); Check(stunned.GetEntity(Player).Health == 20 && stunned.GetItem(Id(2)).Location == ItemLocation.Inventory);
			var dead = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray() }); Check(dead.Submit(Command(dead, CommandKind.UseItem, Id(2))) == CommandResult.DeadActor);
			var foreign = GameSimulation.Restore(state with { Entities = [..state.Entities, new(new(99), new(1), new(384, 640))], Inputs = [..state.Inputs, new(new(99), 0, 0)] });
			Accept(foreign, Command(foreign, CommandKind.UseItem, Id(2)) with { Actor = new(99) }); foreign.Step(); Check(foreign.GetItem(Id(2)).Owner == Player && foreign.Events[0].Value == (int)ItemFailure.WrongOwner);
		});
		test("death before a queued potion use retains the potion across save/restore", () =>
		{
			var state = Game(hp: 1).CaptureSnapshot();
			var game = GameSimulation.Restore(state with { Entities = [..state.Entities, new(new(99), new(1), new(640, 384), Kind: EntityKind.Monster)], Inputs = [..state.Inputs, new(new(99), 0, 0)] });
			Accept(game, Command(game, CommandKind.UseItem, Id(2)) with { Tick = 3 }); game.Step(); Check(!game.GetEntity(Player).IsAlive);
			string path = Path.Combine(root, "dead-potion-queue.json"); GameSave.Save(path, game.CaptureSnapshot()); var loaded = GameSave.Load(path, Grid()).Simulation;
			for (int i = 0; i < 2; i++) { game.Step(); loaded.Step(); }
			Check(game.GetItem(Id(2)).Location == ItemLocation.Inventory && game.ComputeStateHash() == loaded.ComputeStateHash());
		});
		test("restore rejects forged potion namespaces, slots, ownership and consumption records", () =>
		{
			var state = Game().CaptureSnapshot(); var p = state.Items[0];
			foreach (var bad in new[] { p with { Id = new(ulong.MaxValue) }, p with { Id = new(2) }, p with { Definition = ItemDefinition.ManaPotion }, p with { Location = ItemLocation.Equipped }, p with { Location = ItemLocation.Belt, Slot = 4 }, p with { Location = ItemLocation.Consumed }, p with { Owner = new(2) } })
				Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [bad] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [Potion(2, ItemLocation.Belt), Potion(3, ItemLocation.Belt)] }));
		});
		test("v4 saves retain consumed IDs and queued full-width item commands through replay", () =>
		{
			var game = Game(); Act(game, CommandKind.UseItem, Id(2)); var baseline = game.CaptureSnapshot(); var command = Command(game, CommandKind.BeltItem, Id(3), 2); Accept(game, command);
			string path = Path.Combine(root, "potion-roundtrip.json"); GameSave.Save(path, game.CaptureSnapshot()); var loaded = GameSave.Load(path, Grid()).Simulation;
			Check(loaded.ComputeStateHash() == game.ComputeStateHash() && loaded.GetItem(Id(2)).Location == ItemLocation.Consumed);
			game.Step(); loaded.Step(); Check(game.ComputeStateHash() == loaded.ComputeStateHash() && game.BeltAt(Player, 2) == Id(3));
			Check(GameSimulation.Replay(baseline, [new(baseline.Tick, command)], game.Tick).ComputeStateHash() == game.ComputeStateHash());
			Edit(path, j => j["Items"]![0]!["Location"] = (int)ItemLocation.Inventory); Throws<InvalidDataException>(() => GameSave.Load(path, Grid()));
		});
		test("verified v3 upgrade preserves mana, selection, bag positions, queue and original bytes", () =>
		{
			string path = Path.Combine(root, "potion-v3-upgrade.json"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v3-items.json"), path); byte[] bytes = File.ReadAllBytes(path);
			var result = GameSave.Load(path, Grid()); var game = result.Simulation; var p = game.GetEntity(Player);
			Check(result.Migrated && p.Mana == 23 && p.ManaRecoveryTicks == 9 && p.SkillCooldown == 5 && p.SelectedSkill == SkillId.None && game.PendingCommands == 2 && game.Items.Length == 3);
			Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !File.Exists(path + ".bak"));
			for (int i = 0; i < 3; i++) game.Step(); Check(game.GetItem(new(2)).Slot == 2 && game.GetItem(new(4)).Slot == 1);
			GameSave.Save(path, game.CaptureSnapshot()); Check(File.ReadAllBytes(path + ".bak").SequenceEqual(bytes) && !GameSave.Load(path, Grid()).Migrated);
		});
		test("old saves reject new potion IDs/locations and checksum damage before migration", () =>
		{
			string path = Path.Combine(root, "potion-old-corrupt.json"), fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v3-items.json");
			foreach (Action<JsonNode> change in new Action<JsonNode>[] { j => j["Items"]![0]!["Id"]!["Value"] = Id(2).Value, j => j["Items"]![0]!["Location"] = (int)ItemLocation.Belt, j => j["RandomState"] = 99 })
			{ File.Copy(fixture, path, true); Edit(path, change); Throws<InvalidDataException>(() => GameSave.Load(path, Grid())); }
			File.Copy(fixture, path + ".bak"); var loaded = GameSave.Load(path, Grid()); Check(loaded.Migrated && loaded.RecoveredFromBackup);
		});
		test("warmed potion use and belt swaps allocate nothing inside Step", () =>
		{
			for (int n = 0; n < 40; n++)
			{
				var game = Game(); Accept(game, Command(game, CommandKind.UseItem, Id(2))); long before = GC.GetAllocatedBytesForCurrentThread(); game.Step(); if (n > 4) Check(GC.GetAllocatedBytesForCurrentThread() == before);
				Accept(game, Command(game, CommandKind.BeltItem, Id(3), 0)); before = GC.GetAllocatedBytesForCurrentThread(); game.Step(); if (n > 4) Check(GC.GetAllocatedBytesForCurrentThread() == before);
			}
		});
	}
}
