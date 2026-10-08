using OpenD2.Core;
using System.Text.Json.Nodes;

internal static class SaveContracts
{
	private static readonly RegionId Town = new(1), Dungeon = new(2);
	private static readonly EntityId Player = new(1);
	private static void Check(bool value) { if (!value) throw new Exception("Save assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
	private static WorldDefinition World()
	{
		var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray();
		return new([new("Town", new(Town, 10, 10, cells)), new("Dungeon", new(Dungeon, 10, 10, cells))],
			[new(new(11), Town, new(384, 640), Dungeon, new(384, 384)), new(new(12), Dungeon, new(384, 640), Town, new(384, 384))],
			new(new(10), Town, new(640, 384), "Guide"), [new(2)], "Clear");
	}
	private static GameSimulation Game()
	{
		var game = new GameSimulation(1, [new(Player, Town, new(384, 384), Health: 50), new(new(2), Dungeon, new(640, 384), Kind: EntityKind.Monster, Health: 1)], world: World());
		CommandKind[] kinds = [CommandKind.Interact, CommandKind.Interact, CommandKind.Attack, CommandKind.Pickup, CommandKind.Equip, CommandKind.Interact];
		uint[] targets = [10, 11, 2, 0, 0, 12];
		for (int i = 0; i < kinds.Length; i++)
		{
			Check(game.Submit(new(i + 1, (ulong)i + 1, Player, game.ActiveRegion, kinds[i], Target: new(targets[i]), Item: i is 3 or 4 ? new(2) : default)) == CommandResult.Accepted); game.Step();
		}
		Check(game.Submit(new(8, 7, Player, Town, CommandKind.Signal)) == CommandResult.Accepted);
		Check(game.Submit(new(9, 8, Player, Town, CommandKind.SetMove, 1)) == CommandResult.Accepted);
		return game;
	}
	private static void Edit(string path, Action<JsonObject> edit)
	{ var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); edit(json); File.WriteAllText(path, json.ToJsonString()); }
	public static void Run(string root, Action<string, Action> test)
	{
		string Slot(string name) => Path.Combine(root, "saves", name + ".json");
		test("Checkpoint restores inactive deaths, quest, equipment, RNG and pending commands exactly", () =>
		{
			var game = Game(); var copy = GameSimulation.Restore(game.CaptureSnapshot());
			Check(copy.ComputeStateHash() == game.ComputeStateHash() && copy.Events.Length == 0);
			for (int i = 0; i < 100; i++) { game.Step(); copy.Step(); Check(game.ComputeStateHash() == copy.ComputeStateHash() && game.Events.SequenceEqual(copy.Events)); }
		});
		test("Inactive chasing AI survives restore even when its target has moved to another region", () =>
		{
			var game = new GameSimulation(1, [new(Player, Town, new(384, 384)), new(new(2), Dungeon, new(1408, 384), Kind: EntityKind.Monster)], world: World());
			Check(game.Submit(new(1, 1, Player, Town, CommandKind.Interact, Target: new(11))) == CommandResult.Accepted); game.Step();
			Check(game.Submit(new(2, 2, Player, Dungeon, CommandKind.Interact, Target: new(12))) == CommandResult.Accepted); game.Step();
			Check(game.GetEntity(new(2)).Mode == MonsterMode.Chasing && game.GetEntity(new(2)).Target == Player && game.ActiveRegion == Town);
			var copy = GameSimulation.Restore(game.CaptureSnapshot());
			for (int i = 0; i < 20; i++) { game.Step(); copy.Step(); Check(game.ComputeStateHash() == copy.ComputeStateHash()); }
		});
		test("Checkpoint replay starts from saved tick and includes already queued input", () =>
		{
			var game = Game(); var baseline = game.CaptureSnapshot();
			var command = new GameCommand(10, 9, Player, Town, CommandKind.SetMove);
			Check(game.Submit(command) == CommandResult.Accepted); while (game.Tick < 20) game.Step();
			Check(game.ComputeStateHash() == GameSimulation.Replay(baseline, [new(6, command)], 20).ComputeStateHash());
			Throws<InvalidDataException>(() => GameSimulation.Replay(baseline, [new(5, command)], 20));
			Throws<ArgumentOutOfRangeException>(() => GameSimulation.Replay(baseline, [], 5));
		});
		test("File save round trip is independent of source arrays and current live progress", () =>
		{
			var game = Game(); var checkpoint = game.CaptureSnapshot(); string hash = game.ComputeStateHash(); var path = Slot("roundtrip");
			GameSave.Save(path, checkpoint); game.Step(); var loaded = GameSave.Load(path, world: World());
			Check(!loaded.RecoveredFromBackup && loaded.Simulation.ComputeStateHash() == hash && checkpoint.Tick == 6);
		});
		test("Save rotates last valid file and recovers malformed primary without changing files", () =>
		{
			var game = Game(); string first = game.ComputeStateHash(); var path = Slot("backup"); GameSave.Save(path, game.CaptureSnapshot());
			game.Step(); GameSave.Save(path, game.CaptureSnapshot()); Check(!GameSave.Load(path, world: World()).RecoveredFromBackup);
			File.WriteAllText(path, "{"); byte[] backup = File.ReadAllBytes(path + ".bak");
			var loaded = GameSave.Load(path, world: World()); Check(loaded.RecoveredFromBackup && loaded.Simulation.ComputeStateHash() == first);
			Check(File.ReadAllText(path) == "{" && File.ReadAllBytes(path + ".bak").SequenceEqual(backup));
			GameSave.Save(path, loaded.Simulation.CaptureSnapshot()); Check(File.ReadAllBytes(path + ".bak").SequenceEqual(backup));
		});
		test("Missing primary falls back to backup; missing or corrupt both never creates a game", () =>
		{
			var path = Slot("missing"); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
			GameSave.Save(path, Game().CaptureSnapshot()); File.Move(path, path + ".bak"); Check(GameSave.Load(path, world: World()).RecoveredFromBackup);
			File.WriteAllText(path, "broken"); File.WriteAllText(path + ".bak", "broken"); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
		});
		test("Checksum corruption recovers backup and does not silently accept edited progress", () =>
		{
			var path = Slot("checksum"); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); GameSave.Save(path, game.CaptureSnapshot());
			Edit(path, j => j["RandomState"] = 42); Check(GameSave.Load(path, world: World()).RecoveredFromBackup);
			File.Delete(path + ".bak"); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
		});
		test("Future schema and rules refuse fallback and refuse overwrite, including new fields", () =>
		{
			foreach (string field in new[] { "SchemaVersion", "RulesVersion" })
			{
				var path = Slot(field); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); GameSave.Save(path, game.CaptureSnapshot());
				Edit(path, j => { j[field] = 999; j["FutureField"] = true; }); byte[] before = File.ReadAllBytes(path);
				Throws<SaveCompatibilityException>(() => GameSave.Load(path, world: World()));
				Throws<SaveCompatibilityException>(() => GameSave.Save(path, game.CaptureSnapshot())); Check(File.ReadAllBytes(path).SequenceEqual(before));
			}
		});
		test("Incompatible backup also prevents overwrite of corrupt primary", () =>
		{
			var path = Slot("futurebackup"); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); GameSave.Save(path, game.CaptureSnapshot());
			Edit(path + ".bak", j => j["SchemaVersion"] = 999); File.WriteAllText(path, "{");
			Throws<SaveCompatibilityException>(() => GameSave.Save(path, game.CaptureSnapshot())); Check(File.ReadAllText(path) == "{");
		});
		test("Content identity mismatch fails closed rather than falling back to another world", () =>
		{
			var path = Slot("content"); GameSave.Save(path, Game().CaptureSnapshot());
			Throws<SaveCompatibilityException>(() => GameSave.Load(path));
			var world = World(); var changed = new WorldDefinition(world.Regions.ToArray(), world.Portals.ToArray(), world.QuestGiver, world.QuestTargets.ToArray(), "Changed quest");
			Throws<SaveCompatibilityException>(() => GameSave.Load(path, world: changed));
		});
		test("Malformed header types, null fields and oversized input fail within file budget", () =>
		{
			var path = Slot("malformed"); GameSave.Save(path, Game().CaptureSnapshot());
			Edit(path, j => j["SchemaVersion"] = "bad"); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
			GameSave.Save(path, Game().CaptureSnapshot()); Edit(path, j => j["Entities"] = null); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
			File.WriteAllBytes(path, new byte[GameSave.MaxFileBytes + 1]); Throws<InvalidDataException>(() => GameSave.Load(path, world: World()));
		});
		test("Restore rejects invalid entity identities, health, AI targets and dead movement", () =>
		{
			var game = Game(); var state = game.CaptureSnapshot(); string hash = game.ComputeStateHash();
			foreach (var bad in new[] { state.Entities[0] with { MaxHealth = 0 }, state.Entities[0] with { Target = new(999) }, state.Entities[0] with { Mode = MonsterMode.Attacking }, state.Entities[0] with { Id = new(2) } })
				Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Entities = [bad, state.Entities[1]] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Entities = [state.Entities[0], state.Entities[1] with { MoveX = 1 }] }));
			Check(game.ComputeStateHash() == hash);
		});
		test("Restore rejects duplicate items, invalid sources, ownership and slot collisions", () =>
		{
			var state = Game().CaptureSnapshot(); var item = state.Items[0];
			foreach (var bad in new[] { item with { Id = new(1) }, item with { Definition = ItemDefinition.TrainingVest }, item with { Owner = new(999) }, item with { Slot = 1 }, item with { Region = Town } })
				Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [bad] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Items = [item, item] }));
		});
		test("Restore rejects cursor violations, duplicate commands and overdue scheduling", () =>
		{
			var state = Game().CaptureSnapshot(); var first = state.PendingCommands[0];
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Inputs = [new(Player, 0, 0), state.Inputs[1]] }));
			foreach (var bad in new[] { first with { Tick = 6 }, first with { Sequence = 99 }, first with { Actor = new(999) }, first with { Region = Dungeon }, first with { X = 1 } })
				Throws<InvalidDataException>(() => GameSimulation.Restore(state with { PendingCommands = [bad] }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { PendingCommands = [first, first] }));
		});
		test("Restore validates quest completion against actual targets and world ownership", () =>
		{
			var state = Game().CaptureSnapshot();
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { QuestState = QuestStage.Active }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { WorldPlayer = new(999) }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { Tick = long.MaxValue }));
			Throws<InvalidDataException>(() => GameSimulation.Restore(state with { RandomState = 0 }));
		});
		test("Dead player retains valid queued commands through restore and discards them on their tick", () =>
		{
			var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray(); var grid = new CollisionGrid(Town, 10, 10, cells);
			var game = new GameSimulation(1, [new(Player, Town, new(384, 384), Health: 1), new(new(2), Town, new(640, 384), Kind: EntityKind.Monster)], grid);
			Check(game.Submit(new(2, 1, Player, Town, CommandKind.Signal)) == CommandResult.Accepted); game.Step();
			var copy = GameSimulation.Restore(game.CaptureSnapshot()); Check(copy.ComputeStateHash() == game.ComputeStateHash()); game.Step(); copy.Step();
			Check(copy.ComputeStateHash() == game.ComputeStateHash() && copy.Events.SequenceEqual(game.Events) && copy.PendingCommands == 0 && !copy.Events.ToArray().Any(e => e.Kind == SimulationEventKind.Signaled));
		});
		test("Atomic backup failure leaves primary bytes intact and cleans temporary output", () =>
		{
			var path = Slot("failure"); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); var before = File.ReadAllBytes(path);
			Directory.CreateDirectory(path + ".bak"); game.Step(); Throws<IOException>(() => GameSave.Save(path, game.CaptureSnapshot()));
			Check(File.ReadAllBytes(path).SequenceEqual(before) && !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any());
		});
		test("A busy save slot refuses a second writer without touching primary or backup", () =>
		{
			var path = Slot("locked"); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); var before = File.ReadAllBytes(path);
			using var busy = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			Throws<IOException>(() => GameSave.Save(path, game.CaptureSnapshot())); Check(File.ReadAllBytes(path).SequenceEqual(before));
		});
		test("Temporary crash remnants are ignored and invalid snapshots cannot replace a valid save", () =>
		{
			var path = Slot("remnant"); var game = Game(); GameSave.Save(path, game.CaptureSnapshot()); var before = File.ReadAllBytes(path);
			File.WriteAllText(path + ".orphan.tmp", "future partial output");
			Check(GameSave.Load(path, world: World()).Simulation.ComputeStateHash() == game.ComputeStateHash());
			Throws<InvalidDataException>(() => GameSave.Save(path, game.CaptureSnapshot() with { RandomState = 0 })); Check(File.ReadAllBytes(path).SequenceEqual(before));
		});
		test("Free movement saves without installed map content and resumes independently", () =>
		{
			var path = Slot("free"); var game = new GameSimulation(1, [new(Player, Town, new(0, 0), MoveX: 1)]); game.Step();
			GameSave.Save(path, game.CaptureSnapshot()); var copy = GameSave.Load(path).Simulation; game.Step(); copy.Step(); Check(copy.ComputeStateHash() == game.ComputeStateHash());
		});
	}
}
