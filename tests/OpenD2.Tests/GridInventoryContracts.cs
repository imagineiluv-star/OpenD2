using System.Text.Json.Nodes;
using System.Text;
using OpenD2.Core;
using OpenD2.Assets;

internal static class GridInventoryContracts
{
	private static readonly EntityId Player = new(1);
	private static CollisionGrid Grid() => new(new(1), 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray());
	private static void Check(bool value) { if (!value) throw new Exception("Grid inventory assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
	private static ItemState Bag(uint id, int slot) => new(new(id), id % 2 == 0 ? ItemDefinition.TrainingSword : ItemDefinition.TrainingVest, ItemLocation.Inventory, Player, slot, default, default);
	private static InventoryLayout Layout(int swordWidth = 1, int swordHeight = 3, int vestWidth = 2, int vestHeight = 3, string code = "") => new([new(ItemDefinition.TrainingSword, code, swordWidth, swordHeight), new(ItemDefinition.TrainingVest, "", vestWidth, vestHeight)]);
	private static GameSimulation Game(ItemState[] items, InventoryLayout? layout = null)
	{
		var actors = new[] { new EntityState(Player, new(1), new(384, 384)) }.Concat(items.Select(i => new EntityState(new(i.Id.Value), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 0)));
		var game = new GameSimulation(1, actors, Grid(), inventory: layout);
		return GameSimulation.Restore(game.CaptureSnapshot() with { Items = items });
	}
	private static GameCommand Command(GameSimulation game, ItemId id, int slot, CommandKind kind = CommandKind.MoveItem)
		=> new(game.Tick + 1, game.CaptureSnapshot().Inputs[0].Sequence + 1, Player, new(1), kind, kind == CommandKind.MoveItem ? slot % 10 : 0, kind == CommandKind.MoveItem ? slot / 10 : 0, Item: id);
	private static void Act(GameSimulation game, ItemId id, int slot, CommandKind kind = CommandKind.MoveItem)
	{ Check(game.Submit(Command(game, id, slot, kind)) == CommandResult.Accepted); game.Step(); }
	private static string CopyFixture(string root, string name, string target)
	{ string path = Path.Combine(root, target + ".json"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), path); return path; }
	private static void Edit(string path, Action<JsonObject> edit)
	{ var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); edit(json); File.WriteAllText(path, json.ToJsonString()); }
	public static void Run(string root, Action<string, Action> test)
	{
		test("grid layout owns immutable bounded definitions and hashes dimensions and codes", () =>
		{
			var entries = InventoryLayout.Default.Entries.ToArray(); var layout = new InventoryLayout(entries); entries[0] = entries[0] with { Width = 10 };
			Check(layout.Get(ItemDefinition.TrainingSword).Width == 1 && layout.ContentHash == InventoryLayout.Default.ContentHash);
			Check(Layout(code: "fws").ContentHash != layout.ContentHash && Layout(swordWidth: 2).ContentHash != layout.ContentHash);
			Throws<InvalidDataException>(() => new InventoryLayout([InventoryLayout.Default.Entries[0], InventoryLayout.Default.Entries[0]]));
			Throws<InvalidDataException>(() => Layout(vestHeight: 5)); Throws<InvalidDataException>(() => Layout(swordWidth: 0)); Throws<InvalidDataException>(() => Layout(code: "../a"));
		});
		test("grid rectangles cannot wrap rows or overflow the 40-cell mask", () =>
		{
			var layout = InventoryLayout.Default;
			Check(layout.Mask(ItemDefinition.TrainingSword, 19) == ((1UL << 19) | (1UL << 29) | (1UL << 39)));
			foreach (int slot in new[] { -1, 20, 39, 40, int.MaxValue }) Check(layout.Mask(ItemDefinition.TrainingSword, slot) == 0);
			Check(layout.Mask(ItemDefinition.TrainingVest, 9) == 0 && layout.Mask(ItemDefinition.TrainingVest, 18) != 0);
			Check(layout.FirstFit(ItemDefinition.TrainingSword, (1UL << 30) - 1) == -1); // ten free cells, no 1×3 rectangle
		});
		test("grid moves allow overlap with the same item and do not consume random values", () =>
		{
			var game = Game([Bag(2, 0)]); uint rng = game.RandomState;
			Check(game.ItemAt(Player, 20) == new ItemId(2) && game.CanMoveItem(Player, new(2), 10));
			Act(game, new(2), 10); Check(game.GetItem(new(2)).Slot == 10 && game.ItemAt(Player, 0) == default && game.ItemAt(Player, 30) == new ItemId(2) && game.RandomState == rng);
		});
		test("grid swaps commit both different-size rectangles together", () =>
		{
			var game = Game([Bag(2, 0), Bag(3, 3)]); Check(game.CanMoveItem(Player, new(2), 3)); Act(game, new(2), 3);
			Check(game.GetItem(new(2)).Slot == 3 && game.GetItem(new(3)).Slot == 0 && game.Items.Length == 2);
			Check(game.ItemAt(Player, 21) == new ItemId(3) && game.ItemAt(Player, 23) == new ItemId(2));
		});
		test("grid rejects multi-item overlap, failed reverse fit and out-of-bounds placement atomically", () =>
		{
			var game = Game([Bag(2, 0), Bag(3, 3), Bag(4, 1)]); var before = game.Items.ToArray();
			Act(game, new(3), 0); Check(game.Items.SequenceEqual(before) && game.Events[^1].Value == (int)ItemFailure.InvalidPlacement);
			Act(game, new(3), 9); Check(game.Items.SequenceEqual(before));
			var reverse = Game([Bag(2, 9), Bag(3, 0)]); before = reverse.Items.ToArray();
			Check(!reverse.CanMoveItem(Player, new(2), 0)); Act(reverse, new(2), 0); Check(reverse.Items.SequenceEqual(before));
		});
		test("grid move payloads and foreign ownership cannot bypass authoritative checks", () =>
		{
			var game = Game([Bag(2, 0)]); string hash = game.ComputeStateHash(); var command = Command(game, new(2), 0);
			foreach (var bad in new[] { command with { X = -1 }, command with { X = 10 }, command with { Y = 4 }, command with { Item = default }, command with { Target = Player } })
				Check(game.Submit(bad) == CommandResult.InvalidCommand && game.ComputeStateHash() == hash);
			Check(!game.CanMoveItem(new(99), new(2), 1));
			var state = game.CaptureSnapshot();
			game = GameSimulation.Restore(state with { Entities = state.Entities.Append(new EntityState(new(99), new(1), new(384, 640))).ToArray(), Inputs = state.Inputs.Append(new CommandCursor(new(99), 0, 0)).ToArray() });
			Check(game.Submit(command with { Actor = new(99), X = 1 }) == CommandResult.Accepted); game.Step();
			Check(game.GetItem(new(2)).Slot == 0 && game.Events[^1].Value == (int)ItemFailure.WrongOwner);

		});
		test("grid same-tick moves recheck occupancy after earlier commands", () =>
		{
			var game = Game([Bag(2, 0), Bag(3, 3), Bag(4, 6)]); var first = Command(game, new(2), 7);
			Check(game.Submit(first) == CommandResult.Accepted && game.Submit(first with { Sequence = 2, Item = new(3), X = 6 }) == CommandResult.Accepted);
			game.Step(); Check(game.GetItem(new(2)).Slot == 7 && game.GetItem(new(3)).Slot == 3 && game.GetItem(new(4)).Slot == 6 && game.Events[^1].Kind == SimulationEventKind.ItemFailed);
		});
		test("equipped items can move into an exact empty rectangle but cannot replace bag items", () =>
		{
			var game = Game([Bag(2, 0) with { Location = ItemLocation.Equipped, Slot = 0 }, Bag(3, 3)]);
			Check(!game.CanMoveItem(Player, new(2), 3)); Act(game, new(2), 10);
			Check(game.GetItem(new(2)).Location == ItemLocation.Inventory && game.GetItem(new(2)).Slot == 10 && game.GetStats(Player).MinimumDamage == 14);
		});
		test("grid snapshot validation rejects hidden overlap, row wrap and missing layout", () =>
		{
			var state = Game([Bag(2, 0), Bag(3, 3)]).CaptureSnapshot();
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [Bag(2, 0), Bag(3, 10)] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [Bag(2, 0), Bag(3, 9)] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Inventory = null }));
		});
		test("grid replay and save round trip retain pending placement and item content identity", () =>
		{
			var game = Game([Bag(2, 0), Bag(3, 3)], Layout(swordHeight: 2, code: "fws")); var baseline = game.CaptureSnapshot(); var command = Command(game, new(2), 10);
			Check(game.Submit(command) == CommandResult.Accepted); string file = Path.Combine(root, "grid-roundtrip.json"); GameSave.Save(file, game.CaptureSnapshot());
			var loaded = GameSave.Load(file, Grid(), inventory: game.Inventory); Check(!loaded.Migrated && loaded.Simulation.ComputeStateHash() == game.ComputeStateHash());
			game.Step(); loaded.Simulation.Step(); Check(loaded.Simulation.ComputeStateHash() == game.ComputeStateHash());
			Check(GameSimulation.Replay(baseline, [new(0, command)], 1).ComputeStateHash() == game.ComputeStateHash());
			Throws<SaveCompatibilityException>(() => GameSave.Load(file, Grid()));
		});
		test("v1 rules-v4 save is verified before grid conversion without writing source or dropping queued commands", () =>
		{
			string file = CopyFixture(root, "save-v1-items.json", "migrate"); byte[] source = File.ReadAllBytes(file);
			var result = GameSave.Load(file, Grid()); var game = result.Simulation;
			Check(result.Migrated && !result.RecoveredFromBackup && File.ReadAllBytes(file).SequenceEqual(source) && !File.Exists(file + ".bak"));
			Check(game.GetItem(new(2)).Slot == 0 && game.GetItem(new(3)).Location == ItemLocation.Equipped && game.PendingCommands == 1 && game.RandomState == 1 && game.GetEntity(Player).Mana == 60);
			game.Step(); game.Step(); Check(game.GetItem(new(4)).Location == ItemLocation.Inventory && game.GetItem(new(4)).Slot == 1);
			GameSave.Save(file, game.CaptureSnapshot()); Check(File.ReadAllBytes(file + ".bak").SequenceEqual(source));
			Check(!GameSave.Load(file, Grid()).Migrated && JsonNode.Parse(File.ReadAllText(file))!["SchemaVersion"]!.GetValue<int>() == GameSave.SchemaVersion);
		});
		test("legacy conversion overflow preserves both files and never silently falls back to an older checkpoint", () =>
		{
			string file = CopyFixture(root, "save-v1-full.json", "overflow"); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v1-items.json"), file + ".bak");
			byte[] source = File.ReadAllBytes(file), backup = File.ReadAllBytes(file + ".bak");
			Throws<SaveCompatibilityException>(() => GameSave.Load(file, Grid()));
			Throws<SaveCompatibilityException>(() => GameSave.Save(file, Game([Bag(2, 0)]).CaptureSnapshot()));
			Check(File.ReadAllBytes(file).SequenceEqual(source) && File.ReadAllBytes(file + ".bak").SequenceEqual(backup));
		});
		test("corrupt v4 checksum or old-slot state is rejected before conversion with backup recovery retained", () =>
		{
			string file = CopyFixture(root, "save-v1-items.json", "old-corrupt"); File.Copy(file, file + ".bak");
			Edit(file, j => j["RandomState"] = 99); var recovered = GameSave.Load(file, Grid()); Check(recovered.Migrated && recovered.RecoveredFromBackup);
			File.Delete(file + ".bak"); Throws<InvalidDataException>(() => GameSave.Load(file, Grid()));
			File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "save-v1-items.json"), file, true);
			Edit(file, j => j["Items"]![0]!["Slot"] = 8); Throws<InvalidDataException>(() => GameSave.Load(file, Grid()));
		});
		test("grid layout corruption cannot be accepted under a valid state hash", () =>
		{
			string file = Path.Combine(root, "grid-corrupt.json"); var game = Game([Bag(2, 0)]); GameSave.Save(file, game.CaptureSnapshot());
			Edit(file, j => j["Inventory"]![0]!["Width"] = 2); Throws<SaveCompatibilityException>(() => GameSave.Load(file, Grid()));
			Throws<InvalidDataException>(() => GameSave.Load(file, Grid(), inventory: Layout(swordWidth: 2)));
		});
		test("scene item dimensions are explicit, hashed and copied into the core layout without importing damage", () =>
		{
			byte[] Read(string path) => path.EndsWith("weapons.txt") ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ItemTableContracts.Read(path)).Replace("\t1\t3\tfixtureweapon", "\t2\t2\tfixtureweapon")) : path.EndsWith(".txt") ? ItemTableContracts.Read(path) : PlayAssetContracts.Read(path);
			var request = PlaySceneContracts.Request() with { ItemDefinitions = ItemTableContracts.Request() };
			var reference = LegacyPlayScene.Load(request, Read); var applied = LegacyPlayScene.Load(request with { UseItemDimensions = true }, Read);
			Check(reference.Inventory.ContentHash == InventoryLayout.Default.ContentHash && applied.Inventory.Get(ItemDefinition.TrainingSword).Code == "fws" && applied.ContentId != reference.ContentId);
			Check(applied.Inventory.Get(ItemDefinition.TrainingSword).Width == 2 && applied.Inventory.Get(ItemDefinition.TrainingSword).Height == 2);
			Check(applied.Create(1).Inventory == applied.Inventory && applied.Create(1).GetStats(Player).MinimumDamage == 14);
			Throws<InvalidDataException>(() => LegacyPlayScene.Load(request with { UseItemDimensions = true, ItemDefinitions = null }, _ => throw new Exception("Reached I/O")));
			Throws<InvalidDataException>(() => LegacyPlayScene.Load(request with { UseItemDimensions = true }, p => p.EndsWith("weapons.txt") ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(Read(p)).Replace("\t2\t2\tfixtureweapon", "\t2\t5\tfixtureweapon")) : Read(p)));
		});
		test("stunned and dead actors cannot use placement to bypass item action gates", () =>
		{
			var state = Game([Bag(2, 0)]).CaptureSnapshot();
			var stunned = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { HitStun = 1 } : e).ToArray() });
			Act(stunned, new(2), 1); Check(stunned.GetItem(new(2)).Slot == 0 && stunned.Events[^1].Value == (int)ItemFailure.Interrupted);
			var dead = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray() });
			Check(dead.Submit(Command(dead, new(2), 1)) == CommandResult.DeadActor);
		});
		test("warmed grid moves allocate nothing inside the authoritative tick", () =>
		{
			var game = Game([Bag(2, 0)]);
			for (int i = 0; i < 100; i++)
			{
				Check(game.Submit(Command(game, new(2), i % 2 == 0 ? 10 : 0)) == CommandResult.Accepted);
				long before = GC.GetAllocatedBytesForCurrentThread(); game.Step(); if (i > 4) Check(GC.GetAllocatedBytesForCurrentThread() == before);
			}
		});
	}
}
