using OpenD2.Core;

internal static class SimulationContracts
{
	private static readonly EntityId Actor = new(7);
	private static readonly RegionId Region = new(3);
	private static readonly EntityState Initial = new(Actor, Region, new(0, 0));
	private static void Check(bool condition) { if (!condition) throw new Exception("Simulation assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception
	{ try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}."); }
	private static GameSimulation New() => new(1, [Initial]);
	private static GameCommand Move(long tick, ulong seq, int x = 0, int y = 0) => new(tick, seq, Actor, Region, CommandKind.SetMove, x, y);
	private static GameCommand Signal(long tick, ulong seq) => new(tick, seq, Actor, Region, CommandKind.Signal);
	private static void Accept(GameSimulation game, GameCommand command) => Check(game.Submit(command) == CommandResult.Accepted);
	public static void Run(Action<string, Action> test)
	{
		test("Simulation RNG follows pinned uint32 vectors and resumes from explicit state", () =>
		{
			var random = new SimulationRandom(1);
			foreach (uint expected in new uint[] { 270369, 67634689, 2647435461, 307599695, 2398689233, 745495504 }) Check(random.NextUInt32() == expected);
			var restored = new SimulationRandom(random.State);
			for (int i = 0; i < 1000; i++) Check(restored.NextUInt32() == random.NextUInt32());
			Throws<ArgumentOutOfRangeException>(() => new SimulationRandom(0));
			Throws<InvalidOperationException>(() => { SimulationRandom empty = default; empty.NextUInt32(); });
		});
		test("Simulation RNG bounded samples reject tail values and invalid bounds preserve state", () =>
		{
			var random = new SimulationRandom(1584200935); // Next uint is 0xffffffff, rejected for bound int.MaxValue.
			var reference = random; Check(reference.NextUInt32() == uint.MaxValue); uint second = reference.NextUInt32();
			Check(random.NextInt(int.MaxValue) == (second - 1) % int.MaxValue && random.State == reference.State);
			uint state = random.State; Throws<ArgumentOutOfRangeException>(() => random.NextInt(0)); Check(random.State == state);
			for (int i = 0; i < 1000; i++) Check(random.NextInt(6) is >= 0 and < 6);
			Check(random.NextInt(1) == 0);
		});
		test("Clock exact boundaries retain sub-tick time without advancing game state", () =>
		{
			var clock = new FixedTickClock(); int ticks = 0;
			Check(clock.Advance(TimeSpan.FromMilliseconds(39), () => ticks++) == 0 && Math.Abs(clock.Alpha - 0.975) < 1e-10);
			Check(clock.Advance(TimeSpan.FromMilliseconds(1), () => ticks++) == 1 && ticks == 1 && clock.Alpha == 0);
			Check(clock.Advance(TimeSpan.Zero, () => ticks++) == 0 && clock.DroppedTime == TimeSpan.Zero);
		});
		test("Clock long stalls bound catch-up and count discarded wall time without skipping tick IDs", () =>
		{
			var clock = new FixedTickClock(); var game = New();
			Check(clock.Advance(TimeSpan.FromSeconds(1), game.Step) == 5 && game.Tick == 5);
			Check(clock.DroppedTime == TimeSpan.FromMilliseconds(790) && Math.Abs(clock.Alpha - 0.25) < 1e-10);
			clock.Advance(TimeSpan.FromMilliseconds(30), game.Step); Check(game.Tick == 6 && clock.Alpha == 0);
		});
		test("Clock extreme durations saturate metrics and invalid inputs do not change state", () =>
		{
			var clock = new FixedTickClock(); int ticks = 0;
			clock.Advance(TimeSpan.MaxValue, () => ticks++); clock.Advance(TimeSpan.MaxValue, () => ticks++);
			Check(ticks == 10 && clock.DroppedTime == TimeSpan.MaxValue);
			double before = clock.Alpha; Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromTicks(-1), () => ticks++));
			Throws<ArgumentNullException>(() => clock.Advance(TimeSpan.Zero, null!)); Check(clock.Alpha == before && !clock.IsFaulted);
			clock.Reset(); Check(clock.Alpha == 0 && clock.DroppedTime == TimeSpan.Zero);
		});
		test("Clock callback failure and reentry fail closed until reset", () =>
		{
			var clock = new FixedTickClock();
			Throws<InvalidOperationException>(() => clock.Advance(TimeSpan.FromMilliseconds(40), () => clock.Advance(TimeSpan.Zero, () => { })));
			Check(clock.IsFaulted); Throws<InvalidOperationException>(() => clock.Advance(TimeSpan.Zero, () => { }));
			clock.Reset(); int calls = 0;
			Throws<IOException>(() => clock.Advance(TimeSpan.FromMilliseconds(80), () => { calls++; throw new IOException(); }));
			Check(calls == 1 && clock.IsFaulted); clock.Reset(); Check(clock.Advance(TimeSpan.FromMilliseconds(40), () => calls++) == 1);
		});
		test("Simulation constructor owns sorted entities and rejects invalid identities and coordinates", () =>
		{
			EntityState[] original = [Initial with { Id = new(9) }, Initial]; var game = new GameSimulation(1, original); original[0] = default;
			Check(game.Entities[0].Id == Actor && game.Entities[1].Id.Value == 9);
			foreach (var bad in new[] { Initial with { Id = default }, Initial with { Region = default }, Initial with { Position = new(int.MinValue, 0) }, Initial with { MoveX = 2 } })
				Throws<ArgumentException>(() => new GameSimulation(1, [bad]));
			Throws<ArgumentException>(() => new GameSimulation(1, [])); Throws<ArgumentException>(() => new GameSimulation(1, [Initial, Initial]));
			Throws<ArgumentException>(() => new GameSimulation(1, Enumerable.Range(1, 1025).Select(i => Initial with { Id = new((uint)i) })));
		});
		test("Simulation commands apply only on their tick with persistent integer motion", () =>
		{
			var game = New(); Accept(game, Move(2, 1, 1)); Accept(game, Move(4, 2));
			game.Step(); Check(game.GetEntity(Actor).Position == new GamePosition(0, 0) && game.Events.Length == 0);
			game.Step(); Check(game.GetEntity(Actor).Position == new GamePosition(32, 0) && game.Events[0] == new SimulationEvent(2, SimulationEventKind.Moved, Actor, Region, new(0, 0), new(32, 0)));
			game.Step(); Check(game.GetEntity(Actor).Position == new GamePosition(64, 0)); game.Step();
			Check(game.GetEntity(Actor).MoveX == 0 && game.Events.Length == 0 && game.PendingCommands == 0);
		});
		test("Simulation diagonal motion and world bounds remain integer and overflow safe", () =>
		{
			var game = New(); Accept(game, Move(1, 1, 1, -1)); game.Step(); Check(game.GetEntity(Actor).Position == new GamePosition(23, -23));
			var edge = new GameSimulation(1, [Initial with { Position = new(GameSimulation.PositionLimit - 1, -GameSimulation.PositionLimit), MoveX = 1, MoveY = -1 }]);
			edge.Step(); Check(edge.GetEntity(Actor).Position == new GamePosition(GameSimulation.PositionLimit, -GameSimulation.PositionLimit));
			edge.Step(); Check(edge.Events.Length == 0);
		});
		test("Rejected commands do not mutate state, RNG, queue or sequence watermark", () =>
		{
			var game = New(); Accept(game, Move(2, 5, 1));
			(GameCommand Command, CommandResult Result)[] invalid =
			[
				(Move(3, 0), CommandResult.InvalidCommand), (Move(3, 6, 2), CommandResult.InvalidCommand),
				(Signal(3, 6) with { X = 1 }, CommandResult.InvalidCommand), (Move(3, 6) with { Kind = (CommandKind)99 }, CommandResult.InvalidCommand),
				(Move(3, 6) with { Actor = new(99) }, CommandResult.UnknownActor), (Move(3, 6) with { Region = new(99) }, CommandResult.WrongRegion),
				(Move(0, 6), CommandResult.ExpiredTick), (Move(-1, 6), CommandResult.ExpiredTick), (Move(long.MaxValue, 6), CommandResult.TooFarAhead),
				(Move(3, 5), CommandResult.StaleSequence), (Move(1, 6), CommandResult.OutOfOrderTick)
			];
			string before = game.ComputeStateHash(); foreach (var item in invalid) { Check(game.Submit(item.Command) == item.Result); Check(game.ComputeStateHash() == before); }
			Accept(game, Move(3, 6));
		});
		test("Per-tick command ceiling recovers on the next tick without consuming rejected sequence", () =>
		{
			var game = New(); for (ulong i = 1; i <= 128; i++) Accept(game, Signal(1, i));
			string before = game.ComputeStateHash(); Check(game.Submit(Signal(1, 129)) == CommandResult.TickFull && game.ComputeStateHash() == before);
			game.Step(); Check(game.Events.Length == 128 && game.PendingCommands == 0); Accept(game, Signal(2, 129)); game.Step(); Check(game.Events.Length == 1);
		});
		test("Global command ceiling and future horizon remain bounded and recover after drain", () =>
		{
			var game = New(); Check(game.Submit(Move(251, 1)) == CommandResult.TooFarAhead);
			for (ulong i = 1; i <= 4096; i++) Accept(game, Signal((long)(i - 1) / 128 + 1, i));
			string before = game.ComputeStateHash(); Check(game.Submit(Signal(33, 4097)) == CommandResult.QueueFull && game.ComputeStateHash() == before);
			game.Step(); Accept(game, Signal(33, 4097)); Check(game.PendingCommands == 3969);
		});
		test("Canonical actor and sequence order makes cross-actor arrival order irrelevant", () =>
		{
			EntityState other = Initial with { Id = new(9) }; var a = new GameSimulation(1, [other, Initial]); var b = new GameSimulation(1, [Initial, other]);
			var left = Signal(1, 1); var right = left with { Actor = other.Id };
			Accept(a, right); Accept(a, left); Accept(b, left); Accept(b, right); Check(a.ComputeStateHash() == b.ComputeStateHash());
			a.Step(); b.Step(); Check(a.ComputeStateHash() == b.ComputeStateHash() && a.Events.SequenceEqual(b.Events));
			Check(a.Events[0].Actor == Actor && a.Events[0].Value == 2 && a.Events[1].Actor == other.Id && a.Events[1].Value == 0);
		});
		test("Same-tick last movement command wins and signals precede sorted movement events", () =>
		{
			var game = New(); Accept(game, Move(1, 1, 1)); Accept(game, Signal(1, 2)); Accept(game, Move(1, 3, 0, 1)); game.Step();
			Check(game.GetEntity(Actor).Position == new GamePosition(0, 32) && game.Events.Length == 2);
			Check(game.Events[0].Kind == SimulationEventKind.Signaled && game.Events[1].Kind == SimulationEventKind.Moved);
		});
		test("Maximum entity and event load fits one bounded tick", () =>
		{
			var game = new GameSimulation(1, Enumerable.Range(1, 1024).Select(i => Initial with { Id = new((uint)i), MoveX = 1 }));
			for (ulong i = 1; i <= 128; i++) Accept(game, Signal(1, i)); game.Step();
			Check(game.Events.Length == 1152 && game.Events[128].Actor.Value == 1 && game.Events[^1].Actor.Value == 1024);
		});
		test("Snapshots remain unchanged across future ticks and reject collection mutation", () =>
		{
			var game = New(); Accept(game, Move(1, 1, 1)); var snapshot = game.CaptureSnapshot(); game.Step();
			Check(snapshot.Tick == 0 && snapshot.RandomState == 1 && snapshot.Entities[0].Position == new GamePosition(0, 0) && snapshot.PendingCommands.Count == 1);
			Check(snapshot.Inputs[0] == new CommandCursor(Actor, 1, 1));
			Throws<NotSupportedException>(() => ((IList<EntityState>)snapshot.Entities)[0] = default);
		});
		test("Canonical simulation state matches an independent little-endian golden hash", () =>
		{
			var game = New(); Accept(game, Move(1, 1, 1)); Accept(game, Signal(2, 2)); Accept(game, Move(3, 3, 0, -1));
			for (int i = 0; i < 4; i++) game.Step();
			Check(game.GetEntity(Actor).Position == new GamePosition(64, -64) && game.RandomState == 270369);
			const string expected = "9c8624135830d3503236d8472dc57b177061ac6140e3e0223781077797e321e7";
			Check(game.ComputeStateHash() == expected); Console.WriteLine("SIMULATION_V2_GOLDEN " + expected);
		});
		test("State hashes include intent, region, RNG, queued payloads and accepted input watermarks", () =>
		{
			var a = New(); var b = New(); Accept(a, Move(1, 1, 1)); Accept(b, Move(1, 2, 1));
			Check(a.ComputeStateHash() != b.ComputeStateHash()); a.Step(); b.Step(); Check(a.GetEntity(Actor) == b.GetEntity(Actor) && a.ComputeStateHash() != b.ComputeStateHash());
			var c = New(); var d = New(); Accept(c, Move(2, 1, 1)); Accept(d, Move(2, 1, -1)); Check(c.ComputeStateHash() != d.ComputeStateHash());
			Check(New().ComputeStateHash() != new GameSimulation(2, [Initial]).ComputeStateHash());
			Check(New().ComputeStateHash() != new GameSimulation(1, [Initial with { Region = new(4) }]).ComputeStateHash());
		});
		test("Recorded submission times replay past actions and still-pending commands exactly", () =>
		{
			var game = New(); var trace = new List<RecordedCommand>();
			void Record(GameCommand c) { Accept(game, c); trace.Add(new(game.Tick, c)); }
			Record(Move(2, 1, 1)); game.Step(); game.Step(); Record(Signal(4, 2)); game.Step(); Record(Move(6, 3));
			var replay = GameSimulation.Replay(1, [Initial], trace, 3);
			Check(replay.ComputeStateHash() == game.ComputeStateHash() && replay.PendingCommands == 2);
			for (int i = 0; i < 5; i++) { game.Step(); replay.Step(); Check(replay.ComputeStateHash() == game.ComputeStateHash() && replay.Events.SequenceEqual(game.Events)); }
		});
		test("Malformed or oversized replays fail without changing the live simulation", () =>
		{
			var game = New(); string before = game.ComputeStateHash();
			Throws<InvalidDataException>(() => GameSimulation.Replay(1, [Initial], [new(2, Move(3, 1)), new(1, Move(4, 2))], 3));
			Throws<InvalidDataException>(() => GameSimulation.Replay(1, [Initial], [new(0, Move(0, 1))], 3));
			Throws<InvalidDataException>(() => GameSimulation.Replay(1, [Initial], [new(4, Move(5, 1))], 3));
			Throws<ArgumentOutOfRangeException>(() => GameSimulation.Replay(1, [Initial], [], GameSimulation.MaxReplayTicks + 1));
			Throws<ArgumentOutOfRangeException>(() => GameSimulation.Replay(1, [Initial], new RecordedCommand[GameSimulation.MaxReplayCommands + 1], 0));
			Check(game.ComputeStateHash() == before);
		});
		test("Different render frame partitions produce identical simulation and event stream", () =>
		{
			var a = New(); var b = New(); var ca = new FixedTickClock(); var cb = new FixedTickClock();
			foreach (var game in new[] { a, b }) { Accept(game, Move(1, 1, 1, 1)); Accept(game, Signal(25, 2)); Accept(game, Move(40, 3, -1)); }
			var ea = new List<SimulationEvent>(); var eb = new List<SimulationEvent>();
			void StepA() { a.Step(); ea.AddRange(a.Events.ToArray()); }
			void StepB() { b.Step(); eb.AddRange(b.Events.ToArray()); }
			for (int i = 0; i < 100; i++) { ca.Advance(TimeSpan.FromMilliseconds(10), StepA); ca.Advance(TimeSpan.FromMilliseconds(10), StepA); cb.Advance(TimeSpan.FromMilliseconds(7), StepB); cb.Advance(TimeSpan.FromMilliseconds(13), StepB); }
			Check(a.Tick == 50 && b.Tick == 50 && a.ComputeStateHash() == b.ComputeStateHash() && ea.SequenceEqual(eb));
		});
		test("Long recorded run replays 5000 ticks and repeated event consumption is side-effect free", () =>
		{
			var game = New(); var trace = new List<RecordedCommand>(); ulong sequence = 0;
			for (int tick = 1; tick <= 5000; tick++)
			{
				if (tick % 37 == 1) { var command = Signal(tick, ++sequence); Accept(game, command); trace.Add(new(game.Tick, command)); }
				if (tick % 100 == 1) { var command = Move(tick, ++sequence, tick % 200 == 1 ? 1 : -1, 1); Accept(game, command); trace.Add(new(game.Tick, command)); }
				game.Step(); foreach (var item in game.Events) Check(item.Tick == tick);
			}
			string expected = game.ComputeStateHash(); game.Events.ToArray(); game.CaptureSnapshot(); Check(game.ComputeStateHash() == expected);
			Check(GameSimulation.Replay(1, [Initial], trace, 5000).ComputeStateHash() == expected);
		});
		test("Simulation Step uses no per-tick managed allocation after warmup", () =>
		{
			var game = New(); Accept(game, Move(1, 1, 1)); for (int i = 0; i < 1000; i++) game.Step();
			long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 1000; i++) game.Step();
			Check(GC.GetAllocatedBytesForCurrentThread() == before);
		});
		test("Tick p99 uses a bounded duration window and retains fractional milliseconds", () =>
		{
			var metrics = new FrameMetrics(); for (int i = 1; i <= 100; i++) metrics.Record(i / 100000.0);
			Check(Math.Abs(metrics.P99Milliseconds() - 0.99) < 1e-10);
			for (int i = 0; i < 600; i++) metrics.Record(0.0002); Check(Math.Abs(metrics.P99Milliseconds() - 0.2) < 1e-10);
		});
	}
}
