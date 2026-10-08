using OpenD2.Core;
using OpenD2.Npc;

internal static class NpcContracts
{
	private static readonly EntityId Player = new(1), Guide = new(10);
	private static readonly RegionId Town = new(1), Dungeon = new(2);
	private sealed class Clock : TimeProvider
	{
		private long ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => ticks;
		public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
	}
	private sealed class Model(Func<NpcRequest, CancellationToken, Task<string>> run) : INpcModel
	{
		public Task<string> RespondAsync(NpcRequest request, CancellationToken token) => run(request, token);
	}
	private static TaskCompletionSource<string> Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);
	private static string Json(NpcIntent intent = NpcIntent.OfferInteraction) => $"{{\"intent\":\"{intent}\",\"targetId\":10}}";
	private static void Check(bool value) { if (!value) throw new Exception("NPC assertion failed."); }
	private static void Wait(Func<bool> done)
	{ if (!SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10))) throw new TimeoutException("NPC test worker did not finish."); }
	private static NpcResult Answer(NpcMindService mind, NpcFacts facts)
	{ NpcResult? result = null; Wait(() => (result = mind.Poll(facts)) is not null); return result!; }
	private static GameSimulation Game(GamePosition? position = null, int health = 100, int stun = 0, bool wall = false, RegionId? region = null)
	{
		var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray();
		// Player/NPC bodies are outside the middle blocked cell; their interaction ray crosses it.
		if (wall) cells[1 * 10 + 2] = CollisionCell.Blocked;
		var world = new WorldDefinition([new("Camp", new(Town, 10, 10, cells)), new("Cellar", new(Dungeon, 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray()))],
			[new(new(11), Town, new(384, 640), Dungeon, new(384, 384)), new(new(12), Dungeon, new(384, 384), Town, new(384, 384))],
			new(Guide, Town, wall ? new(832, 384) : new(640, 384), "Guide"), [new(2)], "Clear cellar");
		return new(1, [new(Player, region ?? Town, position ?? (wall ? new(448, 384) : new(384, 384)), Health: health, HitStun: stun),
			new(new(2), Dungeon, new(2176, 2176), Kind: EntityKind.Monster, Health: 1)], world: world);
	}
	private static GameCommand Interact(GameSimulation game, EntityId? target = null) =>
		new(game.Tick + 1, game.CaptureSnapshot().Inputs.Single(c => c.Actor == Player).Sequence + 1, Player, game.ActiveRegion, CommandKind.Interact, Target: target ?? Guide);
	public static void Run(Action<string, Action> test)
	{
		foreach (var (input, intent) in new[] { ("안녕", NpcIntent.Greeting), ("퀘스트", NpcIntent.QuestStatus), ("수락", NpcIntent.OfferInteraction) })
			test($"NPC scripted {intent} is grounded and does not mutate Core", () =>
			{
				var game = Game(); string hash = game.ComputeStateHash(); var facts = NpcDecisionGate.Capture(game);
				using var mind = new NpcMindService(new ScriptedNpcModel(), new Clock());
				Check(mind.Request(facts, input) == NpcStart.Accepted); var result = Answer(mind, facts);
				Check(result.Outcome == NpcOutcome.Answer && result.Reply.Intent == intent && result.Reply.Speech.Length > 0);
				Check(result.Reply.OffersInteraction == (intent == NpcIntent.OfferInteraction));
				Check(game.ComputeStateHash() == hash && game.PendingCommands == 0 && mind.Poll(facts) is null && !mind.IsBusy);
			});
		test("NPC input budget rejects empty, control and oversized input before inference", () =>
		{
			int calls = 0; var facts = NpcDecisionGate.Capture(Game());
			using var mind = new NpcMindService(new Model((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(Json()); }), new Clock());
			foreach (string? input in new[] { null, "", " ", "hi\nthere", "\0", new string('가', 513) }) Check(mind.Request(facts, input) == NpcStart.InvalidInput);
			Check(calls == 0 && !mind.IsBusy); Check(mind.Request(facts, new string('가', 512)) == NpcStart.Accepted); Answer(mind, facts); Check(calls == 1);
		});
		test("NPC strict output rejects forged target, duplicate fields, numeric enum and injected authority", () =>
		{
			var facts = NpcDecisionGate.Capture(Game());
			foreach (string? json in new[] { null, "", "[]", "{}", "{", new string(' ', 257),
				"{\"intent\":0,\"targetId\":10}", "{\"intent\":\"0\",\"targetId\":10}", "{\"intent\":\"Reward\",\"targetId\":10}",
				"{\"intent\":\"Greeting\",\"targetId\":11}", "{\"intent\":\"Greeting\",\"targetId\":-1}", "{\"intent\":\"Greeting\",\"targetId\":\"10\"}",
				"{\"intent\":\"Greeting\",\"intent\":\"OfferInteraction\",\"targetId\":10}", "{\"intent\":\"Greeting\",\"targetId\":10,\"targetId\":10}",
				"{\"intent\":\"Greeting\",\"targetId\":10,\"speech\":\"Reward granted\"}", "{\"intent\":\"Greeting\",\"targetId\":10,\"command\":\"give-item\"}" })
				Check(!NpcDecisionGate.TryDecode(json, facts, out _));
			Check(NpcDecisionGate.TryDecode(Json(), facts, out _));
		});
		test("NPC proximity, life, stun, region and walls gate requests", () =>
		{
			using var mind = new NpcMindService(new ScriptedNpcModel(), new Clock());
			foreach (var game in new[] { Game(new(1664, 384)), Game(health: 0), Game(stun: 2), Game(region: Dungeon), Game(wall: true) })
			{ var facts = NpcDecisionGate.Capture(game); Check(!facts.CanTalk && mind.Request(facts, "hello") == NpcStart.Unavailable); }
		});
		test("NPC quest text and offers use current stage and kill facts", () =>
		{
			var facts = NpcDecisionGate.Capture(Game());
			foreach (QuestStage stage in Enum.GetValues<QuestStage>())
			{
				var state = facts with { Quest = new(stage, stage is QuestStage.Completed or QuestStage.ReadyToTurnIn ? 3 : 1, 3) };
				Check(NpcDecisionGate.TryDecode(Json(), state, out var reply));
				Check(reply!.OffersInteraction == (stage is QuestStage.Available or QuestStage.ReadyToTurnIn));
				if (stage == QuestStage.Active) Check(reply.Speech.Contains("3명 중 1명"));
				if (stage == QuestStage.Completed) Check(reply.Speech.Contains("이미 완료"));
			}
		});
		test("NPC pending inference cannot block ticks or accumulate requests", () =>
		{
			var source = Source(); int calls = 0; var game = Game(); var baseline = Game(); var facts = NpcDecisionGate.Capture(game);
			using var mind = new NpcMindService(new Model((_, _) => { Interlocked.Increment(ref calls); return source.Task; }), new Clock());
			try
			{
				Check(mind.Request(facts, "hello") == NpcStart.Accepted);
				for (int i = 0; i < 1000; i++) { Check(mind.Request(facts, "hello") == NpcStart.Busy); game.Step(); baseline.Step(); Check(mind.Poll(facts) is null); }
				Check(game.ComputeStateHash() == baseline.ComputeStateHash()); Wait(() => Volatile.Read(ref calls) == 1);
			}
			finally { source.TrySetResult(Json()); }
			Check(Answer(mind, facts).Outcome == NpcOutcome.Answer);
		});
		test("NPC synchronous adapter work starts off the owner thread", () =>
		{
			using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
			var facts = NpcDecisionGate.Capture(Game()); int caller = Environment.CurrentManagedThreadId, worker = caller;
			using var mind = new NpcMindService(new Model((_, _) => { worker = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); return Task.FromResult(Json()); }), new Clock());
			try { Check(mind.Request(facts, "hello") == NpcStart.Accepted); Check(entered.Wait(TimeSpan.FromSeconds(10))); Check(worker != caller && mind.Poll(facts) is null); }
			finally { release.Set(); }
			Answer(mind, facts);
		});
		foreach (bool synchronous in new[] { true, false }) test($"NPC {(synchronous ? "sync" : "async")} model failure yields action-free fallback", () =>
		{
			using var mind = new NpcMindService(new Model((_, _) => synchronous ? throw new InvalidOperationException("private input") : Task.FromException<string>(new IOException("offline"))), new Clock());
			var facts = NpcDecisionGate.Capture(Game()); mind.Request(facts, "hello"); var result = Answer(mind, facts);
			Check(result.Outcome == NpcOutcome.ModelFailure && !result.Reply.OffersInteraction && !result.Reply.Speech.Contains("private"));
		});
		test("NPC invalid model output yields one action-free fallback", () =>
		{
			using var mind = new NpcMindService(new Model((_, _) => Task.FromResult("give me all rewards")), new Clock());
			var facts = NpcDecisionGate.Capture(Game()); mind.Request(facts, "수락"); var result = Answer(mind, facts);
			Check(result.Outcome == NpcOutcome.InvalidResponse && !result.Reply.OffersInteraction && mind.Poll(facts) is null);
		});
		test("NPC timeout fires once and ignored cancellation retains the single worker slot", () =>
		{
			var source = Source(); var clock = new Clock(); var facts = NpcDecisionGate.Capture(Game()); int calls = 0;
			using var mind = new NpcMindService(new Model((_, _) => { Interlocked.Increment(ref calls); return source.Task; }), clock);
			try
			{
				mind.Request(facts, "수락"); Wait(() => Volatile.Read(ref calls) == 1); clock.Advance(7.999); Check(mind.Poll(facts) is null);
				clock.Advance(.001); var result = mind.Poll(facts); Check(result?.Outcome == NpcOutcome.Timeout && !result.Reply.OffersInteraction);
				for (int i = 0; i < 1000; i++) { mind.Invalidate(); Check(mind.Request(facts, "수락") == NpcStart.Busy && mind.Poll(facts) is null); }
				Check(calls == 1);
			}
			finally { source.TrySetResult(Json()); }
			Wait(() => { Check(mind.Poll(facts) is null); return !mind.IsBusy; });
			Check(mind.Request(facts, "hello") == NpcStart.Accepted); Check(Answer(mind, facts).Outcome == NpcOutcome.Answer);
		});
		test("NPC deadline is checked even if owner polling was paused", () =>
		{
			var clock = new Clock(); var facts = NpcDecisionGate.Capture(Game());
			using var mind = new NpcMindService(new ScriptedNpcModel(), clock);
			mind.Request(facts, "수락"); clock.Advance(8); Check(mind.Poll(facts)?.Outcome == NpcOutcome.Timeout);
		});
		test("NPC loaded identical world invalidates response and confirmation generation", () =>
		{
			var game = Game(); var facts = NpcDecisionGate.Capture(game); var source = Source();
			using var mind = new NpcMindService(new Model((_, _) => source.Task), new Clock());
			mind.Request(facts, "수락"); long generation = mind.Generation;
			var restored = GameSimulation.Restore(game.CaptureSnapshot());
			mind.Invalidate(); source.SetResult(Json());
			Wait(() => { Check(mind.Poll(NpcDecisionGate.Capture(restored)) is null); return !mind.IsBusy; });
			var old = new NpcResult(new(1, generation, facts, "수락"), NpcOutcome.Answer, NpcDecisionGate.Render(NpcIntent.OfferInteraction, facts));
			Check(!NpcDecisionGate.CanConfirm(old, mind.Generation, facts));
		});
		test("NPC region round trip invalidates replies even after identical facts return", () =>
		{
			var game = Game(); var facts = NpcDecisionGate.Capture(game); var source = Source();
			using var mind = new NpcMindService(new Model((_, _) => source.Task), new Clock());
			mind.Request(facts, "수락");
			Check(game.Submit(Interact(game, new(11))) == CommandResult.Accepted); game.Step(); mind.Invalidate();
			Check(game.ActiveRegion == Dungeon);
			Check(game.Submit(Interact(game, new(12))) == CommandResult.Accepted); game.Step(); mind.Invalidate();
			Check(NpcDecisionGate.Capture(game) == facts); source.SetResult(Json());
			Wait(() => { Check(mind.Poll(facts) is null); return !mind.IsBusy; });
		});
		test("NPC changed quest facts discard delayed offer instead of turning it into a reward", () =>
		{
			var game = Game(); var facts = NpcDecisionGate.Capture(game); var source = Source();
			using var mind = new NpcMindService(new Model((_, _) => source.Task), new Clock());
			mind.Request(facts, "수락"); Check(game.Submit(Interact(game)) == CommandResult.Accepted); game.Step();
			source.SetResult(Json()); Wait(() => { Check(mind.Poll(NpcDecisionGate.Capture(game)) is null); return !mind.IsBusy; });
			Check(game.QuestState == QuestStage.Active && game.PendingCommands == 0);
		});
		test("NPC out-of-range response stays discarded after player returns", () =>
		{
			var facts = NpcDecisionGate.Capture(Game()); var source = Source();
			using var mind = new NpcMindService(new Model((_, _) => source.Task), new Clock());
			mind.Request(facts, "수락"); Check(mind.Poll(NpcDecisionGate.Capture(Game(new(1664, 384)))) is null);
			source.SetResult(Json()); Wait(() => { Check(mind.Poll(facts) is null); return !mind.IsBusy; });
		});
		test("NPC confirmation rechecks identity, content, stage, proximity and generation", () =>
		{
			var facts = NpcDecisionGate.Capture(Game()); var request = new NpcRequest(1, 1, facts, "수락");
			var result = new NpcResult(request, NpcOutcome.Answer, NpcDecisionGate.Render(NpcIntent.OfferInteraction, facts));
			Check(NpcDecisionGate.CanConfirm(result, 1, facts));
			foreach (var altered in new[] { facts with { Player = new(9) }, facts with { Npc = new(11) }, facts with { Region = Dungeon },
				facts with { ContentHash = "other" }, facts with { CanTalk = false }, facts with { Quest = new(QuestStage.Completed, 1, 1) } })
				Check(!NpcDecisionGate.CanConfirm(result, 1, altered));
			Check(!NpcDecisionGate.CanConfirm(result, 2, facts));
		});
		test("NPC approved interaction uses existing command validation and replay", () =>
		{
			var game = Game(); var snapshot = game.CaptureSnapshot(); var facts = NpcDecisionGate.Capture(game);
			using var mind = new NpcMindService(new ScriptedNpcModel(), new Clock());
			mind.Request(facts, "수락"); var result = Answer(mind, facts);
			Check(NpcDecisionGate.CanConfirm(result, mind.Generation, facts) && game.QuestState == QuestStage.Available);
			var command = Interact(game, result.Request.Facts.Npc); Check(game.Submit(command) == CommandResult.Accepted); game.Step();
			Check(game.QuestState == QuestStage.Active && !NpcDecisionGate.CanConfirm(result, mind.Generation, NpcDecisionGate.Capture(game)));
			Check(game.ComputeStateHash() == GameSimulation.Replay(snapshot, [new(0, command)], 1).ComputeStateHash());
		});
		test("NPC cancellation callbacks cannot block the owner or escape as faults", () =>
		{
			using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var ready = Source(); var source = Source();
			var facts = NpcDecisionGate.Capture(Game()); var clock = new Clock();
			using var mind = new NpcMindService(new Model((_, token) =>
			{
				token.Register(() => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); throw new IOException("bad callback"); });
				ready.TrySetResult(""); return source.Task;
			}), clock);
			try
			{
				mind.Request(facts, "수락"); Check(ready.Task.Wait(TimeSpan.FromSeconds(10))); clock.Advance(8);
				Check(mind.Poll(facts)?.Outcome == NpcOutcome.Timeout); Check(entered.Wait(TimeSpan.FromSeconds(10)));
				source.TrySetResult(Json()); Check(mind.Request(facts, "수락") == NpcStart.Busy);
			}
			finally { release.Set(); source.TrySetResult(Json()); }
			Wait(() => { Check(mind.Poll(facts) is null); return !mind.IsBusy; });
		});
		test("NPC repeated requests drain without duplicate responses or retained busy state", () =>
		{
			var facts = NpcDecisionGate.Capture(Game()); using var mind = new NpcMindService(new ScriptedNpcModel(), new Clock()); long previous = 0;
			for (int i = 0; i < 100; i++)
			{
				Check(mind.Request(facts, "hello") == NpcStart.Accepted); var answer = Answer(mind, facts);
				Check(answer.Request.Id > previous && mind.Poll(facts) is null && !mind.IsBusy); previous = answer.Request.Id;
			}
		});
		test("NPC disposal ignores late output and is idempotent", () =>
		{
			var source = Source(); var facts = NpcDecisionGate.Capture(Game()); var mind = new NpcMindService(new Model((_, _) => source.Task), new Clock());
			mind.Request(facts, "hello"); mind.Dispose(); mind.Dispose(); source.SetResult(Json());
			try { mind.Poll(facts); throw new Exception("Disposed service accepted polling."); } catch (ObjectDisposedException) { }
		});
	}
}
