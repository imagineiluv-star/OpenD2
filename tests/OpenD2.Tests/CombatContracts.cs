using OpenD2.Core;
using OpenD2.Assets;

internal static class CombatContracts
{
	private static readonly RegionId Region = new(1);
	private static readonly EntityState Player = new(new(1), Region, new(384, 384));
	private static readonly EntityState Monster = new(new(2), Region, new(640, 384), Kind: EntityKind.Monster);
	private static void Check(bool value) { if (!value) throw new Exception("Combat assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
	private static CollisionGrid Grid(params (int X, int Y, CollisionCell Cell)[] changed)
	{
		var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray();
		foreach (var item in changed) cells[item.Y * 10 + item.X] = item.Cell;
		return new(Region, 10, 10, cells);
	}
	private static GameSimulation Game(EntityState? monster = null) => new(1, [Player, monster ?? Monster], Grid());
	private static GameCommand Attack(long tick = 1, ulong sequence = 1) => new(tick, sequence, Player.Id, Region, CommandKind.Attack, Target: Monster.Id);
	private static GameCommand Move(int x, int y = 0, long tick = 1, ulong sequence = 1) => new(tick, sequence, Player.Id, Region, CommandKind.SetMove, x, y);
	private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);
	public static void Run(Action<string, Action> test)
	{
		test("Collision grid owns cells and rejects malformed dimensions and coordinate bounds", () =>
		{
			CollisionCell[] cells = [CollisionCell.Open]; var grid = new CollisionGrid(Region, 1, 1, cells, new(-256, -256)); cells[0] = CollisionCell.Blocked;
			Check(grid.CanOccupy(new(-128, -128)) && grid.At(0, 0) == CollisionCell.Open);
			Throws<ArgumentException>(() => new CollisionGrid(default, 1, 1, cells));
			Throws<ArgumentException>(() => new CollisionGrid(Region, int.MaxValue, 2, cells));
			Throws<ArgumentException>(() => new CollisionGrid(Region, 1, 1, [(CollisionCell)99]));
			Throws<ArgumentException>(() => new CollisionGrid(Region, 1, 1, cells, new(GameSimulation.PositionLimit, 0)));
		});
		test("Collision body extents fail closed outside map and inside unknown cells", () =>
		{
			var grid = Grid((1, 1, CollisionCell.Unknown));
			Check(!grid.CanOccupy(Player.Position) && !grid.CanOccupy(new(63, 128)) && grid.CanOccupy(new(64, 128)));
			Check(!grid.CanOccupy(new(int.MinValue, int.MaxValue)) && grid.At(-1, 0) == CollisionCell.Unknown);
		});
		test("Legacy collision adapter preserves DT1 walk flags and unknown DS1 cells", () =>
		{
			byte[] flags = new byte[25]; flags[20] = 8;
			var scene = MapScene.Build(Ds1Map.Parse(MapContracts.Ds1()), [new("floor", Dt1Tileset.Parse(MapContracts.Dt1(new MapContracts.Tile(Flags: flags))))]);
			var grid = scene.ToCollisionGrid(Region);
			Check(grid.Width == 10 && grid.Height == 5 && grid.At(0, 0) == CollisionCell.Blocked && grid.At(1, 0) == CollisionCell.Open);
			var missing = MapScene.Build(Ds1Map.Parse(MapContracts.Ds1()), []).ToCollisionGrid(Region);
			Check(missing.At(0, 0) == CollisionCell.Unknown && !missing.CanOccupy(new(128, 128)));
		});
		test("Combat spawn rejects blocked cells, overlapping bodies, wrong region and excessive actors", () =>
		{
			Throws<ArgumentException>(() => new GameSimulation(1, [Player], Grid((1, 1, CollisionCell.Blocked))));
			Throws<ArgumentException>(() => new GameSimulation(1, [Player, Monster with { Position = Player.Position }], Grid()));
			Throws<ArgumentException>(() => new GameSimulation(1, [Player with { Region = new(2) }], Grid()));
			Throws<ArgumentException>(() => new GameSimulation(1, Enumerable.Range(1, 129).Select(i => Player with { Id = new((uint)i) }), Grid()));
			Throws<ArgumentException>(() => new GameSimulation(1, [Monster]));
			foreach (var e in new[] { Player with { Health = -1 }, Player with { MaxHealth = 0 }, Player with { Health = 101 }, Player with { HitStun = 4 }, Player with { Kind = (EntityKind)99 } }) Throws<ArgumentException>(() => new GameSimulation(1, [e], Grid()));
		});
		test("Movement stops at body margin of walls and unknown cells without tunneling", () =>
		{
			foreach (var cell in new[] { CollisionCell.Blocked, CollisionCell.Unknown })
			{
				var game = new GameSimulation(1, [Player], Grid((2, 1, cell))); Accept(game, Move(1));
				for (int i = 0; i < 20; i++) game.Step();
				Check(game.GetEntity(Player.Id).Position == new GamePosition(448, 384) && game.Events[0].Kind == SimulationEventKind.Blocked);
			}
		});
		test("Diagonal sliding follows X then Y and cannot clip the blocked corner", () =>
		{
			var initial = Player with { Position = new(448, 448) }; var game = new GameSimulation(1, [initial], Grid((2, 1, CollisionCell.Blocked)));
			Accept(game, Move(1, 1)); game.Step();
			Check(game.GetEntity(Player.Id).Position == new GamePosition(448, 471) && game.Events[0].Kind == SimulationEventKind.Blocked && game.Events[1].Kind == SimulationEventKind.Moved);
		});
		test("Living bodies block movement and corpses release collision", () =>
		{
			var obstacle = Player with { Id = new(3), Position = new(512, 384) };
			var game = new GameSimulation(1, [Player, obstacle], Grid()); Accept(game, Move(1)); game.Step(); Check(game.GetEntity(Player.Id).Position == Player.Position);
			var dead = new GameSimulation(1, [Player, obstacle with { Health = 0 }], Grid()); Accept(dead, Move(1)); dead.Step(); Check(dead.GetEntity(Player.Id).Position.X == 416);
		});
		test("Melee supercover blocks both sides of a corner and handles extreme inputs", () =>
		{
			var grid = Grid((1, 0, CollisionCell.Blocked));
			Check(!grid.HasMeleeLine(new(128, 128), new(384, 384)) && !grid.HasMeleeLine(new(384, 384), new(128, 128)));
			Check(Grid().HasMeleeLine(new(128, 128), new(384, 384)));
			Check(!grid.HasMeleeLine(new(int.MinValue, int.MinValue), new(int.MaxValue, int.MaxValue)));
			Check(grid.HasMeleeLine(new(384, 384), new(384, 640)) && grid.HasMeleeLine(new(384, 640), new(384, 384)));
		});
		test("Invalid combat command ownership and target reject without mutating state", () =>
		{
			var game = Game(); string before = game.ComputeStateHash();
			Check(game.Submit(Attack() with { Target = new(99) }) == CommandResult.InvalidTarget);
			Check(game.Submit(Attack() with { Target = Player.Id }) == CommandResult.InvalidTarget);
			Check(game.Submit(Attack() with { Actor = Monster.Id, Target = Player.Id }) == CommandResult.NotPlayerControlled);
			Check(game.Submit(Move(1) with { Target = Monster.Id }) == CommandResult.InvalidCommand);
			Check(game.ComputeStateHash() == before); Accept(game, Attack());
		});
		test("One swing per actor tick, deterministic damage, hit stun and death event", () =>
		{
			var game = Game(Monster with { Health = 1 }); Accept(game, Attack()); Accept(game, Attack(sequence: 2)); game.Step();
			Check(game.GetEntity(Monster.Id).Health == 0 && game.GetEntity(Monster.Id).Mode == MonsterMode.Dead && game.GetEntity(Monster.Id).MoveX == 0);
			Check(game.GetEntity(Player.Id).Health == 100 && game.RandomState == 270369);
			// Independent Python struct.pack little-endian encoding of this v2 state.
			const string golden = "b7a3b7a905558a741514c5bd6434cb5ffd39d845bd92065866a8cbd9eb60c5b5";
			Check(game.ComputeStateHash() == golden); Console.WriteLine("COMBAT_V2_GOLDEN " + golden);
			var events = game.Events.ToArray(); Check(events.Count(e => e.Kind == SimulationEventKind.AttackStarted) == 1 && events.Count(e => e.Kind == SimulationEventKind.Died) == 1);
			Check(events.Single(e => e.Kind == SimulationEventKind.Hit).Value == 1 && events.Single(e => e.Kind == SimulationEventKind.Died).Target == Player.Id);
			game.Step(); Check(!game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.Died));
		});
		test("Cooldown rejects repeated attacks without consuming combat randomness", () =>
		{
			var game = Game(); Accept(game, Attack()); game.Step(); uint rng = game.RandomState;
			Check(game.GetEntity(Monster.Id).Health is >= 80 and <= 86);
			Accept(game, Attack(2, 2)); game.Step();
			Check(game.RandomState == rng && game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.AttackFailed && e.Value == (int)AttackFailure.Cooldown));
		});
		test("Melee cannot hit through a wall or outside range", () =>
		{
			var p = Player with { Position = new(448, 384) }; var m = Monster with { Position = new(832, 384) };
			var game = new GameSimulation(1, [p, m], Grid((2, 1, CollisionCell.Blocked))); Accept(game, Attack()); game.Step();
			Check(game.GetEntity(Monster.Id).Health == 100 && game.RandomState == 1);
			Check(game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.AttackFailed && e.Value == (int)AttackFailure.Obstructed));
			var far = Game(Monster with { Position = new(1664, 384) }); Accept(far, Attack()); far.Step();
			Check(far.GetEntity(Monster.Id).Health == 100 && far.RandomState == 1 && far.Events.ToArray().Any(e => e.Kind == SimulationEventKind.AttackFailed && e.Value == (int)AttackFailure.OutOfRange));
		});
		test("Hit stun prevents movement for exactly three following ticks then resumes intent", () =>
		{
			var game = Game(); Accept(game, Move(0, 1)); game.Step(); Check(game.GetEntity(Player.Id).HitStun == 3 && game.GetEntity(Player.Id).Position == Player.Position);
			for (int i = 0; i < 3; i++) { game.Step(); Check(game.GetEntity(Player.Id).Position == Player.Position); }
			game.Step(); Check(game.GetEntity(Player.Id).Position.Y == Player.Position.Y + 32);
		});
		test("Death cancels future queued actions and new dead actor commands are refused", () =>
		{
			var game = new GameSimulation(1, [Player with { Health = 1 }, Monster], Grid());
			Accept(game, new(2, 1, Player.Id, Region, CommandKind.Signal)); game.Step(); uint rng = game.RandomState;
			Check(!game.GetEntity(Player.Id).IsAlive && game.Submit(Move(1, tick: 3, sequence: 2)) == CommandResult.DeadActor);
			game.Step(); Check(game.PendingCommands == 0 && game.RandomState == rng && !game.Events.ToArray().Any(e => e.Kind == SimulationEventKind.Signaled));
			Check(game.GetEntity(Monster.Id).Mode == MonsterMode.Idle);
		});
		test("A target killed before command execution emits failure and cannot be hit twice", () =>
		{
			var game = Game(Monster with { Health = 1 }); Accept(game, Attack()); Accept(game, Attack(20, 2));
			for (int i = 0; i < 20; i++) game.Step();
			Check(game.RandomState == 270369 && game.Events[0].Kind == SimulationEventKind.AttackFailed && game.Events[0].Value == (int)AttackFailure.DeadTarget);
		});
		test("AI acquires nearest player with ID ties, chases and idles beyond bounded aggro", () =>
		{
			var far = Game(Monster with { Position = new(2176, 384) }); far.Step(); Check(far.GetEntity(Monster.Id).Mode == MonsterMode.Idle);
			var chase = Game(Monster with { Position = new(1408, 384) }); chase.Step(); Check(chase.GetEntity(Monster.Id).Mode == MonsterMode.Chasing && chase.GetEntity(Monster.Id).Position.X == 1376);
			var p2 = Player with { Id = new(3), Position = new(1408, 384) }; var m = Monster with { Position = new(896, 384) };
			var tie = new GameSimulation(1, [p2, m, Player], Grid()); tie.Step(); Check(tie.GetEntity(Monster.Id).Target == Player.Id);
		});
		test("AI combat is bounded by cooldown and eventually kills a passive player", () =>
		{
			var game = Game(); var attackTicks = new List<long>();
			for (int i = 0; i < 1000; i++) { game.Step(); foreach (var e in game.Events) if (e.Kind == SimulationEventKind.AttackStarted) attackTicks.Add(e.Tick); }
			Check(!game.GetEntity(Player.Id).IsAlive && game.GetEntity(Monster.Id).Mode == MonsterMode.Idle);
			Check(attackTicks.Count > 1 && attackTicks.Zip(attackTicks.Skip(1)).All(p => p.Second - p.First == GameSimulation.MonsterAttackInterval));
		});
		test("Combat snapshot owns state and hashes include immutable collision and actor timers", () =>
		{
			var game = Game(); var before = game.CaptureSnapshot(); Accept(game, Attack()); game.Step();
			Check(before.Entities[1].Health == 100 && before.Collision!.At(0, 0) == CollisionCell.Open);
			Check(new GameSimulation(1, [Player], Grid()).ComputeStateHash() != new GameSimulation(1, [Player], Grid((9, 9, CollisionCell.Blocked))).ComputeStateHash());
			Check(new GameSimulation(1, [Player], Grid()).ComputeStateHash() != new GameSimulation(1, [Player with { AttackCooldown = 1 }], Grid()).ComputeStateHash());
		});
		test("Combat replay and frame partitions preserve state and complete event ordering", () =>
		{
			var initial = new[] { Player, Monster }; var grid = Grid(); var a = new GameSimulation(91, initial, grid); var b = new GameSimulation(91, initial.Reverse(), grid);
			var trace = new List<RecordedCommand>(); var ca = new FixedTickClock(); var cb = new FixedTickClock(); ulong seq = 0;
			var ea = new List<SimulationEvent>(); var eb = new List<SimulationEvent>();
			for (int tick = 1; tick <= 1000; tick++)
			{
				if (tick % 12 == 1 && a.GetEntity(Player.Id).IsAlive) { var c = Attack(tick, ++seq); Accept(a, c); Accept(b, c); trace.Add(new(tick - 1, c)); }
				ca.Advance(TimeSpan.FromMilliseconds(40), () => { a.Step(); ea.AddRange(a.Events.ToArray()); });
				cb.Advance(TimeSpan.FromMilliseconds(7), () => b.Step()); cb.Advance(TimeSpan.FromMilliseconds(33), () => { b.Step(); eb.AddRange(b.Events.ToArray()); });
			}
			Check(a.ComputeStateHash() == b.ComputeStateHash() && ea.SequenceEqual(eb));
			Check(GameSimulation.Replay(91, initial, trace, 1000, grid).ComputeStateHash() == a.ComputeStateHash());
		});
		test("Maximum combat population stays within event budget and tick loop allocates no managed memory", () =>
		{
			var cells = Enumerable.Repeat(CollisionCell.Open, 64 * 8).ToArray(); var grid = new CollisionGrid(Region, 64, 8, cells);
			var population = Enumerable.Range(0, 128).Select(i => new EntityState(new((uint)i + 1), Region, new(128 + i % 64 * 256, 128 + i / 64 * 512), MoveX: 1)).ToArray();
			var game = new GameSimulation(1, population, grid);
			for (int i = 0; i < 100; i++) game.Step();
			long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) game.Step(); Check(GC.GetAllocatedBytesForCurrentThread() == before);
			Check(game.Events.Length <= 6 * 128 + GameSimulation.MaxCommandsPerTick);
		});
		test("Maximum active AI population continues combat with no tick allocations", () =>
		{
			var grid = new CollisionGrid(Region, 64, 32, Enumerable.Repeat(CollisionCell.Open, 64 * 32).ToArray());
			var population = Enumerable.Range(0, 128).Select(i => new EntityState(new((uint)i + 1), Region,
				new(256 + i / 2 % 8 * 2048 + i % 2 * 256, 256 + i / 16 * 768),
				Kind: i % 2 == 0 ? EntityKind.Player : EntityKind.Monster, Health: 100000, MaxHealth: 100000)).ToArray();
			var game = new GameSimulation(1, population, grid);
			for (int i = 0; i < 100; i++) game.Step();
			uint randomBefore = game.RandomState; long[] durations = new long[1000];
			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int i = 0; i < durations.Length; i++)
			{
				long start = System.Diagnostics.Stopwatch.GetTimestamp(); game.Step();
				durations[i] = System.Diagnostics.Stopwatch.GetTimestamp() - start;
			}
			Check(GC.GetAllocatedBytesForCurrentThread() == before && game.RandomState != randomBefore);
			foreach (var e in game.Entities) Check(e.IsAlive && (e.Kind == EntityKind.Player ? e.Health < e.MaxHealth : e.Mode == MonsterMode.Attacking));
			Array.Sort(durations);
			Console.WriteLine($"COMBAT_128_ACTORS_P99_MS {durations[989] * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} (diagnostic, not a timing gate)");
		});
	}
}
