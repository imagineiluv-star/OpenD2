using OpenD2.Core;

internal static class ItemContracts
{
	private static readonly EntityId Player = new(1);
	private static readonly RegionId Region = new(1);
	private static void Check(bool value) { if (!value) throw new Exception("Item assertion failed."); }
	private static CollisionGrid Grid(RegionId? region = null) => new(region ?? Region, 20, 20, Enumerable.Repeat(CollisionCell.Open, 400).ToArray());
	private static EntityState[] Initial(uint monster = 2) => [new(Player, Region, new(384, 384)), new(new(monster), Region, new(640, 384), Kind: EntityKind.Monster, Health: 1)];
	private static void Act(GameSimulation game, CommandKind kind, ItemId item = default, EntityId target = default, int x = 0, int y = 0)
	{
		var cursor = game.CaptureSnapshot().Inputs.First(c => c.Actor == Player);
		Check(game.Submit(new(game.Tick + 1, cursor.Sequence + 1, Player, game.GetEntity(Player).Region, kind, x, y, target, item)) == CommandResult.Accepted); game.Step();
	}
	private static GameSimulation Loot(uint monster = 2)
	{
		var game = new GameSimulation(1, Initial(monster), Grid()); Act(game, CommandKind.Attack, target: new(monster)); return game;
	}
	private static GameSimulation Farm()
	{
		var initial = new List<EntityState> { new(Player, Region, new(384, 384), Health: 100000, MaxHealth: 100000) };
		for (uint i = 0; i < 12; i++) initial.Add(new(new(i + 2), Region, new(640 + (int)(i % 4) * 256, 384 + (int)(i / 4) * 256), Kind: EntityKind.Monster, Health: 1));
		var game = new GameSimulation(1, initial, Grid());
		for (int i = 0; i < 1000; i++)
		{
			var enemies = game.Entities.ToArray().Where(e => e.Kind == EntityKind.Monster && e.IsAlive).ToArray();
			if (enemies.Length == 0) { for (int j = 0; j < 4; j++) game.Step(); return game; }
			var p = game.GetEntity(Player).Position;
			var target = enemies.OrderBy(e => Math.Pow(e.Position.X - p.X, 2) + Math.Pow(e.Position.Y - p.Y, 2)).First();
			Act(game, CommandKind.Attack, target: target.Id);
		}
		throw new Exception("Farm did not complete.");
	}
	private static void Unique(GameSimulation game)
	{
		var items = game.Items.ToArray(); Check(items.Select(i => i.Id).Distinct().Count() == items.Length);
		Check(items.Where(i => i.Location != ItemLocation.Ground).GroupBy(i => (i.Owner, i.Location, i.Slot)).All(g => g.Count() == 1));
		foreach (var item in items)
			Check(item.Location == ItemLocation.Ground ? item.Owner == default && item.Slot == -1 && item.Region != default :
				item.Owner != default && item.Region == default && item.Position == default && item.Slot >= 0 && item.Slot < (item.Location == ItemLocation.Inventory ? 8 : 2));
	}
	public static void Run(Action<string, Action> test)
	{
		test("Items drop once on monster death without consuming the combat RNG stream", () =>
		{
			var game = Loot(); var item = game.GetItem(new(2));
			Check(item == new ItemState(new(2), ItemDefinition.TrainingSword, ItemLocation.Ground, default, -1, Region, new(640, 384)));
			Check(game.RandomState == 270369 && game.Events.ToArray().Count(e => e.Kind == SimulationEventKind.ItemDropped) == 1);
			Act(game, CommandKind.Attack, target: new(2)); for (int i = 0; i < 30; i++) game.Step();
			Check(game.Items.Length == 1 && game.RandomState == 270369); Unique(game);
		});
		test("Items do not appear for initially dead entities", () =>
		{
			var initial = Initial(); initial[1] = initial[1] with { Health = 0 };
			var game = new GameSimulation(1, initial, Grid()); game.Step(); Check(game.Items.Length == 0);
		});
		test("Pickup duplicates in the same tick are revalidated and cannot duplicate ownership", () =>
		{
			var game = Loot();
			for (ulong seq = 2; seq <= 3; seq++) Check(game.Submit(new(2, seq, Player, Region, CommandKind.Pickup, Item: new(2))) == CommandResult.Accepted);
			game.Step(); Check(game.Items.Length == 1 && game.GetItem(new(2)).Location == ItemLocation.Inventory);
			Check(game.Events[^1].Kind == SimulationEventKind.ItemFailed && game.Events[^1].Value == (int)ItemFailure.InvalidLocation); Unique(game);
		});
		test("Unknown items and malformed item payloads are rejected without consuming sequence or RNG", () =>
		{
			var game = Loot(); var valid = new GameCommand(2, 2, Player, Region, CommandKind.Pickup, Item: new(2)); string before = game.ComputeStateHash();
			foreach (var bad in new[] { valid with { Item = default }, valid with { X = 1 }, valid with { Target = new(2) }, valid with { Kind = CommandKind.Signal } })
				Check(game.Submit(bad) == CommandResult.InvalidCommand && game.ComputeStateHash() == before);
			Check(game.Submit(valid with { Item = new(999) }) == CommandResult.InvalidTarget && game.ComputeStateHash() == before);
			Check(game.Submit(valid) == CommandResult.Accepted);
		});
		test("Equipment changes derived damage and unequip restores it without changing health", () =>
		{
			var game = Loot(); Act(game, CommandKind.Pickup, new(2)); Act(game, CommandKind.Equip, new(2));
			Check(game.GetStats(Player) == new CombatStats(20, 26, 0) && game.GetEntity(Player).Health == 100);
			Act(game, CommandKind.Unequip, new(2)); Check(game.GetStats(Player) == new CombatStats(14, 20, 0)); Unique(game);
		});
		test("Inventory overflow and full-bag unequip preserve items; swap reuses the incoming slot", () =>
		{
			var game = Farm(); for (uint id = 2; id <= 9; id++) Act(game, CommandKind.Pickup, new(id));
			var before = game.Items.ToArray(); Act(game, CommandKind.Pickup, new(10));
			Check(game.Events[^1].Value == (int)ItemFailure.InventoryFull && game.Items.SequenceEqual(before));
			Act(game, CommandKind.Equip, new(2)); Act(game, CommandKind.Pickup, new(10));
			int slot = game.GetItem(new(4)).Slot; Act(game, CommandKind.Equip, new(4));
			Check(game.GetItem(new(4)).Location == ItemLocation.Equipped && game.GetItem(new(2)).Slot == slot && game.GetItem(new(2)).Location == ItemLocation.Inventory);
			before = game.Items.ToArray(); Act(game, CommandKind.Unequip, new(4));
			Check(game.Events[^1].Value == (int)ItemFailure.InventoryFull && game.Items.SequenceEqual(before));
			Check(game.GetStats(Player).MinimumDamage == 20); Unique(game);
		});
		test("Weapon and body slots coexist and their bonuses do not stack through repeated equip", () =>
		{
			var game = Farm(); Act(game, CommandKind.Pickup, new(2)); Act(game, CommandKind.Pickup, new(3));
			Act(game, CommandKind.Equip, new(2)); Act(game, CommandKind.Equip, new(3));
			Act(game, CommandKind.Equip, new(3));
			Check(game.Events[^1].Value == (int)ItemFailure.InvalidLocation && game.GetStats(Player) == new CombatStats(20, 26, 2)); Unique(game);
		});
		test("Dropping and repicking preserves identity and releases the inventory slot", () =>
		{
			var game = Loot(); Act(game, CommandKind.Pickup, new(2)); Act(game, CommandKind.DropItem, new(2));
			Check(game.GetItem(new(2)).Position == game.GetEntity(Player).Position && game.GetItem(new(2)).Owner == default);
			Act(game, CommandKind.Pickup, new(2)); Check(game.Items.Length == 1 && game.GetItem(new(2)).Slot == 0); Unique(game);
		});
		test("Out-of-range pickup leaves ground item and RNG unchanged", () =>
		{
			var game = Loot(); Act(game, CommandKind.SetMove, x: -1); for (int i = 0; i < 5; i++) game.Step();
			Act(game, CommandKind.SetMove); var before = game.GetItem(new(2)); uint rng = game.RandomState;
			Act(game, CommandKind.Pickup, new(2));
			Check(game.Events[^1].Value == (int)ItemFailure.OutOfRange && game.GetItem(new(2)) == before && game.RandomState == rng);
		});
		test("Items cannot be equipped by another actor", () =>
		{
			EntityState[] initial = [..Initial(), new(new(3), Region, new(384, 640))];
			var game = new GameSimulation(1, initial, Grid()); Act(game, CommandKind.Attack, target: new(2)); Act(game, CommandKind.Pickup, new(2));
			Check(game.Submit(new(3, 1, new(3), Region, CommandKind.Equip, Item: new(2))) == CommandResult.Accepted); game.Step();
			Check(game.Events[^1].Value == (int)ItemFailure.WrongOwner && game.GetItem(new(2)).Owner == Player); Unique(game);
		});
		test("Gear changes actual weapon damage and incoming damage using the same combat RNG", () =>
		{
			foreach (uint loot in new uint[] { 2, 3 })
			{
				EntityState[] initial = [..Initial(loot), new(new(4), Region, new(384, 640), Kind: EntityKind.Monster, HitStun: 3)];
				var equipped = new GameSimulation(1, initial, Grid()); var bare = new GameSimulation(1, initial, Grid());
				foreach (var game in new[] { equipped, bare }) { Act(game, CommandKind.Attack, target: new(loot)); Act(game, CommandKind.Pickup, new(loot)); }
				Act(equipped, CommandKind.Equip, new(loot)); bare.Step(); equipped.Step(); bare.Step();
				Check(equipped.GetEntity(Player).Health - bare.GetEntity(Player).Health == (loot == 3 ? 2 : 0));
				for (int i = 0; i < 8; i++) { equipped.Step(); bare.Step(); }
				Act(equipped, CommandKind.Attack, target: new(4)); Act(bare, CommandKind.Attack, target: new(4));
				Check(bare.GetEntity(new(4)).Health - equipped.GetEntity(new(4)).Health == (loot == 2 ? 6 : 0));
				Check(equipped.RandomState == bare.RandomState);
			}
		});
		test("Stun interrupts item actions and death cancels queued item transfers", () =>
		{
			foreach (int health in new[] { 100, 1 })
			{
				var initial = Initial(3); initial[0] = initial[0] with { Health = health };
				var game = new GameSimulation(1, [..initial, new(new(4), Region, new(384, 640), Kind: EntityKind.Monster, HitStun: 3)], Grid());
				Act(game, CommandKind.Attack, target: new(3)); Act(game, CommandKind.Pickup, new(3)); Act(game, CommandKind.Equip, new(3));
				Check(game.Submit(new(5, 4, Player, Region, CommandKind.Unequip, Item: new(3))) == CommandResult.Accepted);
				game.Step(); uint rng = game.RandomState; game.Step();
				Check(game.GetItem(new(3)).Location == ItemLocation.Equipped && game.RandomState == rng);
				if (health == 100) Check(game.Events[^1].Kind == SimulationEventKind.ItemFailed && game.Events[^1].Value == (int)ItemFailure.Interrupted);
				else Check(game.Items.Length == 1 && game.Submit(new(6, 5, Player, Region, CommandKind.Unequip, Item: new(3))) == CommandResult.DeadActor);
			}
		});
		test("Pickup cannot cross a collision wall even when the item is in range", () =>
		{
			var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray(); cells[12] = CollisionCell.Blocked;
			EntityState[] initial = [new(Player, Region, new(448, 384)), new(new(2), Region, new(832, 384), Kind: EntityKind.Monster, Health: 1, HitStun: 3), new(new(3), Region, new(1088, 384))];
			var game = new GameSimulation(1, initial, new(Region, 10, 10, cells));
			Check(game.Submit(new(1, 1, new(3), Region, CommandKind.Attack, Target: new(2))) == CommandResult.Accepted); game.Step();
			Act(game, CommandKind.Pickup, new(2));
			Check(game.Events[^1].Value == (int)ItemFailure.Obstructed && game.GetItem(new(2)).Location == ItemLocation.Ground);
		});
		test("Region round trips retain ground loot and equipment without respawn or remote pickup", () =>
		{
			var dungeon = new RegionId(2);
			var world = new WorldDefinition([new("Town", Grid()), new("Dungeon", Grid(dungeon))],
				[new(new(11), Region, new(384, 640), dungeon, new(384, 384)), new(new(12), dungeon, new(384, 640), Region, new(384, 384))],
				new(new(10), Region, new(640, 640), "Guide"), [new(2)], "Clear");
			var initial = Initial(); initial[1] = initial[1] with { Region = dungeon };
			var game = new GameSimulation(1, initial, world: world);
			Act(game, CommandKind.Interact, target: new(11)); Act(game, CommandKind.Attack, target: new(2)); Act(game, CommandKind.Interact, target: new(12));
			var ground = game.GetItem(new(2)); Act(game, CommandKind.Pickup, new(2));
			Check(game.Events[^1].Value == (int)ItemFailure.WrongRegion && game.GetItem(new(2)) == ground);
			Act(game, CommandKind.Interact, target: new(11)); Act(game, CommandKind.Pickup, new(2)); Act(game, CommandKind.Equip, new(2));
			Act(game, CommandKind.Interact, target: new(12)); Act(game, CommandKind.Interact, target: new(11));
			Check(game.Items.Length == 1 && game.GetStats(Player).MinimumDamage == 20 && game.GetItem(new(2)).Location == ItemLocation.Equipped); Unique(game);
		});
		test("Item snapshots own their storage and survive later equipment changes", () =>
		{
			var game = Loot(); var snapshot = game.CaptureSnapshot(); Act(game, CommandKind.Pickup, new(2));
			Check(snapshot.Items[0].Location == ItemLocation.Ground);
			try { ((IList<ItemState>)snapshot.Items)[0] = default; throw new Exception("Mutable snapshot"); } catch (NotSupportedException) { }
		});
		test("Item replay includes pending payloads, ownership, equipment and dropped positions", () =>
		{
			var game = new GameSimulation(1, Initial(), Grid()); var trace = new List<RecordedCommand>();
			CommandKind[] kinds = [CommandKind.Attack, CommandKind.Pickup, CommandKind.Equip, CommandKind.Unequip, CommandKind.DropItem, CommandKind.Pickup];
			foreach (var kind in kinds)
			{
				var command = new GameCommand(game.Tick + 1, (ulong)game.Tick + 1, Player, Region, kind, Target: kind == CommandKind.Attack ? new(2) : default, Item: kind == CommandKind.Attack ? default : new(2));
				trace.Add(new(game.Tick, command)); Check(game.Submit(command) == CommandResult.Accepted); game.Step();
				Check(game.ComputeStateHash() == GameSimulation.Replay(1, Initial(), trace, game.Tick, Grid()).ComputeStateHash());
			}
			var pending = new GameCommand(game.Tick + 2, 7, Player, Region, CommandKind.Equip, Item: new(2));
			trace.Add(new(game.Tick, pending)); Check(game.Submit(pending) == CommandResult.Accepted);
			Check(game.ComputeStateHash() == GameSimulation.Replay(1, Initial(), trace, game.Tick, Grid()).ComputeStateHash()); Unique(game);
		});
		test("Item transfers stay allocation-free inside a warmed tick", () =>
		{
			var game = Loot(); Act(game, CommandKind.Pickup, new(2)); Act(game, CommandKind.Equip, new(2)); Act(game, CommandKind.Unequip, new(2));
			ulong sequence = 5;
			for (int i = 0; i < 100; i++)
			{
				Check(game.Submit(new(game.Tick + 1, sequence++, Player, Region, i % 2 == 0 ? CommandKind.Equip : CommandKind.Unequip, Item: new(2))) == CommandResult.Accepted);
				long before = GC.GetAllocatedBytesForCurrentThread(); game.Step(); Check(GC.GetAllocatedBytesForCurrentThread() == before);
			}
		});
	}
}
