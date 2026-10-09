using OpenD2.Core;

internal static class WorldContracts
{
	private static readonly RegionId Town = new(1), Dungeon = new(2);
	private static readonly EntityState Player = new(new(1), Town, new(384, 384), Health: 50);
	private static readonly EntityState Monster = new(new(2), Dungeon, new(640, 384), Kind: EntityKind.Monster, Health: 1);
	private static readonly WorldNpc Npc = new(new(10), Town, new(384, 640), "Guide");
	private static readonly WorldPortal Entrance = new(new(11), Town, new(640, 384), Dungeon, new(384, 384));
	private static readonly WorldPortal Exit = new(new(12), Dungeon, new(384, 640), Town, new(384, 384));
	private static void Check(bool value) { if (!value) throw new Exception("World assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
	private static CollisionGrid Grid(RegionId region, params (int X, int Y, CollisionCell Cell)[] changes)
	{
		var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray();
		foreach (var item in changes) cells[item.Y * 10 + item.X] = item.Cell;
		return new(region, 10, 10, cells);
	}
	private static WorldDefinition World(CollisionGrid? town = null, CollisionGrid? dungeon = null, WorldPortal[]? portals = null,
		WorldNpc? npc = null, EntityId[]? targets = null) => new([new("Town", town ?? Grid(Town)), new("Dungeon", dungeon ?? Grid(Dungeon))],
			portals ?? [Entrance, Exit], npc ?? Npc, targets ?? [Monster.Id], "Clear cellar");
	private static GameSimulation Game(EntityState[]? entities = null, WorldDefinition? world = null) => new(1, entities ?? [Player, Monster], world: world ?? World());
	private static GameCommand Command(GameSimulation game, CommandKind kind, EntityId target = default, int x = 0, int y = 0)
		=> new(game.Tick + 1, game.CaptureSnapshot().Inputs[0].Sequence + 1, Player.Id, game.ActiveRegion, kind, x, y, target);
	private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);
	private static void Act(GameSimulation game, CommandKind kind, EntityId target = default, int x = 0, int y = 0)
	{ Accept(game, Command(game, kind, target, x, y)); game.Step(); }
	private static void Complete(GameSimulation game)
	{
		Act(game, CommandKind.Interact, Npc.Id); Act(game, CommandKind.Interact, Entrance.Id);
		Act(game, CommandKind.Attack, Monster.Id); Act(game, CommandKind.Interact, Exit.Id); Act(game, CommandKind.Interact, Npc.Id);
	}
	public static void Run(Action<string, Action> test)
	{
		test("World content owns ordered definitions and has canonical content identity", () =>
		{
			WorldRegion[] regions = [new("Dungeon", Grid(Dungeon)), new("Town", Grid(Town))];
			WorldPortal[] portals = [Exit, Entrance]; EntityId[] targets = [Monster.Id];
			var world = new WorldDefinition(regions, portals, Npc, targets, "Clear cellar"); string hash = world.ContentHash;
			regions[0] = default; portals[0] = default; targets[0] = default;
			Check(world.Regions[0].Id == Town && world.Portals[0] == Entrance && world.QuestTargets[0] == Monster.Id && world.ContentHash == World().ContentHash && world.ContentHash == hash);
		});
		test("World content rejects duplicates, unknown regions, bad landings and invalid quest references", () =>
		{
			Throws<ArgumentException>(() => World(portals: [Entrance, Entrance]));
			Throws<ArgumentException>(() => World(portals: [Entrance with { Destination = new(9) }]));
			Throws<ArgumentException>(() => World(portals: [Entrance with { Destination = Town }]));
			Throws<ArgumentException>(() => World(portals: [Entrance with { Arrival = new(-1, 0) }]));
			Throws<ArgumentException>(() => World(npc: Npc with { Id = Entrance.Id }));
			Throws<ArgumentException>(() => World(npc: Npc with { Name = new string('x', 81) }));
			Throws<ArgumentException>(() => World(targets: []));
			Throws<ArgumentException>(() => World(targets: [Monster.Id, Monster.Id]));
			Throws<ArgumentException>(() => World(targets: [Npc.Id]));
			Throws<ArgumentException>(() => new WorldDefinition([new("Town", Grid(Town)), new("Again", Grid(Town))], [], Npc, [Monster.Id], "Quest"));
		});
		test("World regions, total cells, portals and objectives have finite budgets", () =>
		{
			Throws<ArgumentException>(() => new WorldDefinition(Enumerable.Range(1, 9).Select(i => new WorldRegion("Region", Grid(new((uint)i)))), [], Npc, [Monster.Id], "Quest"));
			var large = Enumerable.Repeat(CollisionCell.Open, 1024 * 1024).ToArray();
			Throws<ArgumentException>(() => World(town: new(Town, 1024, 1024, large)));
			Throws<ArgumentException>(() => World(portals: Enumerable.Range(0, 33).Select(i => Entrance with { Id = new((uint)i + 100) }).ToArray()));
			Throws<ArgumentException>(() => World(targets: Enumerable.Range(0, 33).Select(i => new EntityId((uint)i + 100)).ToArray()));
		});
		test("World session validates player ownership, global IDs, targets and per-region capacity", () =>
		{
			Throws<ArgumentException>(() => Game([Monster]));
			Throws<ArgumentException>(() => Game([Player, Player with { Id = new(3) }, Monster]));
			Throws<ArgumentException>(() => new GameSimulation(1, [Player, Monster], Grid(Town), World()));
			Throws<ArgumentException>(() => Game([Player, Monster with { Id = Npc.Id }]));
			Throws<ArgumentException>(() => Game([Player, Monster with { Id = Entrance.Id }]));
			Throws<ArgumentException>(() => Game([Player, Monster with { Region = new(9) }]));
			Throws<ArgumentException>(() => Game(world: World(targets: [new(99)])));
			Throws<ArgumentException>(() => Game(world: World(targets: [Player.Id])));
			var residents = Enumerable.Range(0, 128).Select(i => Monster with { Id = new((uint)i + 100) }).Prepend(Player).ToArray();
			Throws<ArgumentException>(() => Game(residents, World(targets: [new(100)])));
		});
		test("Inactive region actors neither move, attack, tick timers nor consume randomness", () =>
		{
			var monster = Monster with { Health = 100, MoveX = 1, HitStun = 2, AttackCooldown = 13 };
			var game = Game([Player, monster]); for (int i = 0; i < 100; i++) game.Step();
			Check(game.GetEntity(monster.Id) == monster && game.GetEntity(Player.Id) == Player && game.RandomState == 1);
		});
		test("An actor at the same coordinates in an inactive region does not block movement", () =>
		{
			var game = Game([Player, Monster with { Position = new(512, 384) }]); Act(game, CommandKind.SetMove, x: 1);
			Check(game.GetEntity(Player.Id).Position == new GamePosition(416, 384));
		});
		test("Cross-region attack, NPC and portal commands reject without state mutation", () =>
		{
			var game = Game(); string hash = game.ComputeStateHash();
			Check(game.Submit(Command(game, CommandKind.Attack, Monster.Id)) == CommandResult.InvalidTarget);
			Check(game.Submit(Command(game, CommandKind.Interact, Exit.Id)) == CommandResult.InvalidTarget);
			Check(game.Submit(Command(game, CommandKind.Interact, new(99))) == CommandResult.InvalidTarget);
			Check(game.Submit(Command(game, CommandKind.Interact, Npc.Id) with { X = 1 }) == CommandResult.InvalidCommand);
			Check(game.ComputeStateHash() == hash);
			var legacy = new GameSimulation(1, [Player], Grid(Town)); Check(legacy.Submit(Command(legacy, CommandKind.Interact, Npc.Id)) == CommandResult.InvalidTarget);
		});
		test("NPC interaction checks distance and walls at execution time", () =>
		{
			var far = Game([Player with { Position = new(1664, 1664) }, Monster]); Act(far, CommandKind.Interact, Npc.Id);
			Check(far.QuestState == QuestStage.Available && far.Events[^1].Value == (int)InteractionFailure.OutOfRange && far.RandomState == 1);
			var npc = Npc with { Position = new(832, 384) };
			var wall = Game([Player with { Position = new(448, 384) }, Monster], World(town: Grid(Town, (2, 1, CollisionCell.Blocked)), portals: [], npc: npc));
			Act(wall, CommandKind.Interact, Npc.Id);
			Check(wall.QuestState == QuestStage.Available && wall.Events[^1].Value == (int)InteractionFailure.Obstructed);
		});
		test("Town acceptance, dungeon kill and return completes quest with one health reward", () =>
		{
			var game = Game(); Complete(game);
			Check(game.ActiveRegion == Town && game.Quest == new QuestProgress(QuestStage.Completed, 1, 1));
			Check(game.GetEntity(Player.Id).Health == 100 && !game.GetEntity(Monster.Id).IsAlive && game.RandomState == 270369);
			// Independent Python struct.pack encoding of the definitions and completed v4 state.
			Check(game.World!.ContentHash == "85e4863a4455df1930320c2618a7fba6b2271f61dbcd1b8a81d640086a4e0f3b");
			const string golden = "b83a628c5c3b1fb3da80c5cf86c178c72f3135d8c5b1d6d1e1b8a363703dae1e";
			Check(game.ComputeStateHash() == golden); Console.WriteLine("WORLD_V5_GOLDEN " + golden);
			Act(game, CommandKind.Interact, Npc.Id);
			Check(game.Events.Length == 1 && game.Events[0].Kind == SimulationEventKind.NpcTalked && game.QuestState == QuestStage.Completed);
		});
		test("Objectives killed before acceptance remain claimable without respawning", () =>
		{
			var game = Game([Player with { Region = Dungeon }, Monster]); Act(game, CommandKind.Attack, Monster.Id);
			Check(game.QuestState == QuestStage.Available); Act(game, CommandKind.Interact, Exit.Id);
			Act(game, CommandKind.Interact, Npc.Id); Check(game.QuestState == QuestStage.ReadyToTurnIn && game.GetEntity(Player.Id).Health == 50);
			Act(game, CommandKind.Interact, Npc.Id); Check(game.QuestState == QuestStage.Completed && game.GetEntity(Player.Id).Health == 100);
		});
		test("Incomplete quest dialogue neither completes nor grants a reward", () =>
		{
			var game = Game(); Act(game, CommandKind.Interact, Npc.Id); Act(game, CommandKind.Interact, Npc.Id);
			Check(game.QuestState == QuestStage.Active && game.GetEntity(Player.Id).Health == 50 && game.Events.Length == 1);
		});
		test("All required monsters must die and unrelated deaths do not finish the quest", () =>
		{
			var extra = Monster with { Id = new(3), Position = new(384, 640) };
			var game = Game([Player, Monster, extra], World(targets: [Monster.Id, extra.Id]));
			Act(game, CommandKind.Interact, Npc.Id); Act(game, CommandKind.Interact, Entrance.Id);
			Act(game, CommandKind.Attack, Monster.Id); Check(game.Quest == new QuestProgress(QuestStage.Active, 1, 2));
			for (int i = 0; i < GameSimulation.PlayerAttackInterval; i++) game.Step();
			Act(game, CommandKind.Attack, extra.Id); Check(game.Quest == new QuestProgress(QuestStage.ReadyToTurnIn, 2, 2));
			var other = Game([Player, Monster, extra], World(targets: [extra.Id]));
			Act(other, CommandKind.Interact, Npc.Id); Act(other, CommandKind.Interact, Entrance.Id);
			Act(other, CommandKind.Attack, Monster.Id); Check(other.Quest == new QuestProgress(QuestStage.Active, 0, 1));
		});
		test("Completed quest cannot heal again after subsequent combat damage", () =>
		{
			var guard = Monster with { Id = new(3), Position = new(1408, 384), Health = 100 };
			var game = Game([Player, Monster, guard]); Complete(game); Act(game, CommandKind.Interact, Entrance.Id);
			for (int i = 0; i < 60 && game.GetEntity(Player.Id).Health == 100; i++) game.Step();
			int damaged = game.GetEntity(Player.Id).Health; Check(damaged < 100 && damaged > 0);
			for (int i = 0; i < 3; i++) game.Step();
			Act(game, CommandKind.Interact, Exit.Id); Check(game.ActiveRegion == Town);
			Act(game, CommandKind.Interact, Npc.Id); Check(game.GetEntity(Player.Id).Health == damaged && game.Events.Length == 1);
		});
		test("Blocked arrival preserves active region, actors, RNG and future commands", () =>
		{
			var game = Game([Player, Monster with { Position = Entrance.Arrival }]);
			Accept(game, Command(game, CommandKind.Interact, Entrance.Id));
			Accept(game, new(100, 2, Player.Id, Town, CommandKind.Signal)); var before = game.CaptureSnapshot(); game.Step();
			Check(game.ActiveRegion == Town && ReferenceEquals(game.Collision, before.Collision) && game.Entities.SequenceEqual(before.Entities.ToArray()) && game.RandomState == before.RandomState);
			Check(game.PendingCommands == 1 && game.Events[^1].Kind == SimulationEventKind.InteractionFailed && game.Events[^1].Value == (int)InteractionFailure.BlockedArrival);
			var corpse = Game([Player, Monster with { Position = Entrance.Arrival, Health = 0 }]); Act(corpse, CommandKind.Interact, Entrance.Id); Check(corpse.ActiveRegion == Dungeon);
		});
		test("Transfer cancels old-region commands and preserves sequence while opening the new tick window", () =>
		{
			var game = Game(); Accept(game, Command(game, CommandKind.Interact, Entrance.Id));
			Accept(game, new(100, 2, Player.Id, Town, CommandKind.Signal)); game.Step();
			Check(game.PendingCommands == 0 && game.Events[^1].Kind == SimulationEventKind.RegionChanged && game.Events[^1].Value == 1 && game.Events[^1].Destination == Dungeon);
			Check(game.Submit(new(2, 2, Player.Id, Dungeon, CommandKind.Signal)) == CommandResult.StaleSequence);
			Check(game.Submit(new(2, 3, Player.Id, Town, CommandKind.Signal)) == CommandResult.WrongRegion);
			Act(game, CommandKind.Attack, Monster.Id); Act(game, CommandKind.Interact, Exit.Id);
			for (int i = 0; i < 100; i++) game.Step();
			Check(game.ActiveRegion == Town && game.RandomState == 270369 && game.PendingCommands == 0);
		});
		test("Only the final interaction of a tick executes and cannot chain acceptance into completion", () =>
		{
			var game = Game([Player, Monster with { Health = 0 }]);
			for (ulong i = 1; i <= 128; i++) Accept(game, new(1, i, Player.Id, Town, CommandKind.Interact, Target: Npc.Id));
			game.Step(); Check(game.QuestState == QuestStage.ReadyToTurnIn && game.GetEntity(Player.Id).Health == 50 && game.Events.Length == 2);
		});
		test("Hit stun, successful attack and lethal damage prevent same-tick portal escape", () =>
		{
			var stunned = Game([Player with { HitStun = 1 }, Monster]); Act(stunned, CommandKind.Interact, Entrance.Id);
			Check(stunned.ActiveRegion == Town && stunned.Events[^1].Value == (int)InteractionFailure.Interrupted);
			var attacking = Game([Player with { Region = Dungeon }, Monster]); Accept(attacking, Command(attacking, CommandKind.Attack, Monster.Id));
			Accept(attacking, new(1, 2, Player.Id, Dungeon, CommandKind.Interact, Target: Exit.Id)); attacking.Step();
			Check(attacking.ActiveRegion == Dungeon && attacking.Events[^1].Value == (int)InteractionFailure.Interrupted);
			var dying = Game([Player with { Region = Dungeon, Health = 1 }, Monster with { Health = 100 }]); Act(dying, CommandKind.Interact, Exit.Id);
			Check(!dying.GetEntity(Player.Id).IsAlive && dying.ActiveRegion == Dungeon && dying.QuestState == QuestStage.Available);
		});
		test("Revisiting a region resumes its exact combat state and does not respawn corpses", () =>
		{
			var game = Game([Player, Monster with { Health = 100, AttackCooldown = 9 }]); Act(game, CommandKind.Interact, Entrance.Id);
			Act(game, CommandKind.Attack, Monster.Id); Act(game, CommandKind.Interact, Exit.Id); var frozen = game.GetEntity(Monster.Id);
			for (int i = 0; i < 100; i++) game.Step(); Check(game.GetEntity(Monster.Id) == frozen);
			Act(game, CommandKind.Interact, Entrance.Id); Check(game.GetEntity(Monster.Id) == frozen); game.Step();
			Check(game.GetEntity(Monster.Id).HitStun == frozen.HitStun - 1);
			var dead = Game(); Complete(dead); Act(dead, CommandKind.Interact, Entrance.Id); for (int i = 0; i < 100; i++) dead.Step(); Check(!dead.GetEntity(Monster.Id).IsAlive);
		});
		test("Owned snapshots preserve world, quest and inactive actor values across transfer", () =>
		{
			var game = Game(); var before = game.CaptureSnapshot(); Complete(game);
			Check(before.World == game.World && before.WorldPlayer == Player.Id && before.QuestState == QuestStage.Available && before.Entities[1].Health == 1 && before.Entities[0].Region == Town);
			Check(before.Collision!.Region == Town && game.CaptureSnapshot().QuestState == QuestStage.Completed);
		});
		test("World hashes cover inactive collision, portal arrival and quest content", () =>
		{
			string before = Game().ComputeStateHash();
			Check(Game(world: World(dungeon: Grid(Dungeon, (9, 9, CollisionCell.Blocked)))).ComputeStateHash() != before);
			Check(Game(world: World(portals: [Entrance with { Arrival = new(384, 640) }, Exit])).ComputeStateHash() != before);
			Check(Game(world: World(npc: Npc with { Name = "Another guide" })).ComputeStateHash() != before);
		});
		test("World input replay preserves transitions, cancellations, quest and event order across frame partitions", () =>
		{
			var a = Game(); var b = Game([Monster, Player]); var trace = new List<RecordedCommand>();
			var ca = new FixedTickClock(); var cb = new FixedTickClock(); var ea = new List<SimulationEvent>(); var eb = new List<SimulationEvent>();
			for (int tick = 1; tick <= 200; tick++)
			{
				GameCommand? command = tick switch
				{
					1 => Command(a, CommandKind.Interact, Npc.Id), 2 => Command(a, CommandKind.Interact, Entrance.Id),
					3 => Command(a, CommandKind.Attack, Monster.Id), 4 => Command(a, CommandKind.Interact, Exit.Id),
					5 => Command(a, CommandKind.Interact, Npc.Id), _ => null
				};
				if (command is { } c) { Accept(a, c); Accept(b, c); trace.Add(new(tick - 1, c)); }
				if (tick == 2) { var future = new GameCommand(100, 3, Player.Id, Town, CommandKind.Signal); Accept(a, future); Accept(b, future); trace.Add(new(1, future)); }
				ca.Advance(TimeSpan.FromMilliseconds(40), () => { a.Step(); ea.AddRange(a.Events.ToArray()); });
				cb.Advance(TimeSpan.FromMilliseconds(13), () => b.Step()); cb.Advance(TimeSpan.FromMilliseconds(27), () => { b.Step(); eb.AddRange(b.Events.ToArray()); });
			}
			Check(a.QuestState == QuestStage.Completed && a.ComputeStateHash() == b.ComputeStateHash() && ea.SequenceEqual(eb));
			Check(GameSimulation.Replay(1, [Player, Monster], trace, 200, world: World()).ComputeStateHash() == a.ComputeStateHash());
			Check(ea.Count(e => e.Kind == SimulationEventKind.RegionChanged) == 2 && !ea.Any(e => e.Kind == SimulationEventKind.Signaled));
		});
		test("Eight bounded regions advance only the active population with no tick allocations", () =>
		{
			var regions = Enumerable.Range(1, 8).Select(r => new WorldRegion("Region " + r, new(new((uint)r), 32, 16, Enumerable.Repeat(CollisionCell.Open, 32 * 16).ToArray()))).ToArray();
			var residents = Enumerable.Range(0, 8 * 127).Select(i => new EntityState(new((uint)i + 2), new((uint)(i / 127 + 1)),
				new(384 + i % 127 % 16 * 256, 128 + i % 127 / 16 * 256), Kind: EntityKind.Monster, Health: 100000, MaxHealth: 100000)).ToArray();
			var player = Player with { Position = new(128, 128), Health = 100000, MaxHealth = 100000 };
			var world = new WorldDefinition(regions, [], Npc with { Id = new(5000) }, [new(2)], "Bounded world");
			var game = Game(residents.Prepend(player).ToArray(), world);
			for (int i = 0; i < 100; i++) game.Step();
			long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) game.Step(); Check(GC.GetAllocatedBytesForCurrentThread() == before);
			for (int i = 127; i < residents.Length; i++) Check(game.GetEntity(residents[i].Id) == residents[i]);
			Check(game.GetEntity(Player.Id).Health < player.Health && game.Entities.Length == 1017);
		});
	}
}
