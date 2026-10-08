using Godot;
using OpenD2.Core;
using System.Diagnostics;

namespace OpenD2.Client;

// Synthetic town/dungeon slice; the authoritative simulation remains engine independent.
public partial class SimulationPreview : VBoxContainer
{
	private const int MaxRecordingTicks = 15000, MaxRecordingCommands = 4096;
	private static readonly EntityId Player = new(1);
	private static readonly RegionId Region = new(1);
	private static readonly RegionId Dungeon = new(2);
	private readonly FixedTickClock clock = new();
	private FrameMetrics tickMetrics = new();
	private readonly List<RecordedCommand> trace = new(MaxRecordingCommands);
	private readonly SpinBox seedInput = new() { MinValue = 1, MaxValue = uint.MaxValue, Value = 1, Step = 1 };
	private readonly Button restart = new() { Text = "New run" };
	private readonly Button pause = new() { Text = "Pause" };
	private readonly Button singleStep = new() { Text = "Step one tick" };
	private readonly Button signal = new() { Text = "Signal" };
	private readonly Button attack = new() { Text = "Attack nearest (Space)" };
	private readonly Button interact = new() { Text = "Interact (E)" };
	private readonly Button replay = new() { Text = "Verify replay" };
	private readonly Label questInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly SimulationCanvas view = new() { CustomMinimumSize = new Vector2(460, 300), SizeFlagsVertical = SizeFlags.ExpandFill };
	private readonly Action<string, string> log;
	private readonly Action advanceTick;
	private GameSimulation simulation = null!;
	private EntityState previous, current;
	private uint seed;
	private ulong sequence;
	private long lastAttackTick = -GameSimulation.PlayerAttackInterval;
	private int requestedX, requestedY;
	private bool paused, verifying, interactDown;
	private bool smokeTest;
	private double elapsed;
	public SimulationPreview(Action<string, string> log) { this.log = log; advanceTick = RunTick; }
	private static EntityState[] InitialEntities() =>
	[
		new(Player, Region, new(384, 384)),
		new(new(2), Dungeon, new(1408, 384), Kind: EntityKind.Monster, Health: 36, MaxHealth: 36),
		new(new(3), Dungeon, new(1664, 1664), Kind: EntityKind.Monster, Health: 36, MaxHealth: 36),
		new(new(4), Dungeon, new(3200, 1408), Kind: EntityKind.Monster, Health: 48, MaxHealth: 48)
	];
	private static CollisionGrid Arena(RegionId? region = null)
	{
		var cells = new CollisionCell[16 * 10];
		for (int y = 0; y < 10; y++) for (int x = 0; x < 16; x++)
			cells[y * 16 + x] = x == 0 || y == 0 || x == 15 || y == 9 || (x == 8 && y is >= 2 and <= 7 && y != 5) ? CollisionCell.Blocked : CollisionCell.Open;
		cells[7 * 16 + 3] = CollisionCell.Unknown;
		return new(region ?? Region, 16, 10, cells);
	}
	private static WorldDefinition DemoWorld()
	{
		var cells = new CollisionCell[12 * 8];
		for (int y = 0; y < 8; y++) for (int x = 0; x < 12; x++)
			cells[y * 12 + x] = x == 0 || y == 0 || x == 11 || y == 7 || (y == 4 && x is >= 4 and <= 6) ? CollisionCell.Blocked : CollisionCell.Open;
		return new([new("Camp", new(Region, 12, 8, cells)), new("Cellar", Arena(Dungeon))],
			[new(new(11), Region, new(1408, 384), Dungeon, new(384, 384)), new(new(12), Dungeon, new(384, 384), Region, new(1408, 384))],
			new(new(10), Region, new(640, 384), "Camp Guide"), [new(2), new(3), new(4)], "Clear the cellar");
	}
	public override void _Ready()
	{
		smokeTest = OS.GetCmdlineUserArgs().Contains("--smoke-test");
		AddChild(new Label { Text = "Town / dungeon slice — 25 ticks per second" });
		AddChild(new Label { Text = "Click the grid: arrows move, Space attacks, E talks / uses a portal.\nGreen: NPC. Gold: portal. Gray walls and purple unknown cells block movement.\nSynthetic maps and rules; original game artwork is not loaded.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		var controls = new HFlowContainer(); AddChild(controls);
		controls.AddChild(new Label { Text = "Seed" }); controls.AddChild(seedInput);
		foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay }) controls.AddChild(button);
		AddChild(questInfo); AddChild(view); AddChild(details); AddChild(status);
		restart.Pressed += NewRun; pause.Pressed += () => { SetPaused(!paused); StopInput(); Refresh(); };
		singleStep.Pressed += () => { SetPaused(true); StopInput(); if (!verifying) { RunTick(); ShowFrame(); Refresh(); } };
		signal.Pressed += () => Submit(CommandKind.Signal); attack.Pressed += AttackNearest; interact.Pressed += InteractNearest;
		replay.Pressed += VerifyReplay;
		Smoke(); CombatSmoke(); WorldSmoke(); NewRun();
	}
	private void NewRun()
	{
		seed = (uint)seedInput.Value; simulation = new(seed, InitialEntities(), world: DemoWorld()); view.SetSimulation(simulation);
		trace.Clear(); sequence = 0; lastAttackTick = -GameSimulation.PlayerAttackInterval; requestedX = requestedY = 0; clock.Reset(); tickMetrics = new(); elapsed = 0;
		previous = current = simulation.GetEntity(Player); view.SignalValue = -1; interactDown = false; SetPaused(false);
		status.Text = "Talk to the Camp Guide, clear the cellar, then return to the guide."; ShowFrame(); Refresh();
		log("simulation_started", $"rules={GameSimulation.RulesVersion}, seed={seed}");
	}
	private void SetPaused(bool value) { paused = value; previous = current; pause.Text = paused ? "Resume" : "Pause"; }
	private bool Submit(CommandKind kind, int x = 0, int y = 0, EntityId target = default)
	{
		if (verifying || simulation.Tick >= MaxRecordingTicks || trace.Count >= MaxRecordingCommands)
		{ SetPaused(true); status.Text = "Recording limit reached. Start a new run to continue."; return false; }
		var command = new GameCommand(simulation.Tick + 1, sequence + 1, Player, simulation.ActiveRegion, kind, x, y, target);
		var result = simulation.Submit(command);
		if (result != CommandResult.Accepted) { status.Text = $"Command rejected: {result}"; return false; }
		sequence++; trace.Add(new(simulation.Tick, command)); return true;
	}
	private void AttackNearest()
	{
		if (!current.IsAlive || verifying) return;
		EntityId nearest = default; long best = long.MaxValue;
		foreach (var e in simulation.Entities)
		{
			if (e.Region != simulation.ActiveRegion || e.Kind != EntityKind.Monster || !e.IsAlive) continue;
			long x = (long)e.Position.X - current.Position.X, y = (long)e.Position.Y - current.Position.Y, distance = x * x + y * y;
			if (distance < best) { best = distance; nearest = e.Id; }
		}
		if (nearest == default) { status.Text = "No living monsters in this region. Use E near an NPC or portal."; return; }
		if (Submit(CommandKind.Attack, target: nearest)) lastAttackTick = simulation.Tick;
	}
	private void InteractNearest()
	{
		if (!current.IsAlive || verifying || simulation.World is not { } world) return;
		EntityId nearest = default; long best = long.MaxValue;
		void Consider(EntityId id, RegionId region, GamePosition position)
		{
			if (region != current.Region) return;
			long x = (long)position.X - current.Position.X, y = (long)position.Y - current.Position.Y, distance = x * x + y * y;
			if (distance < best || (distance == best && id.Value < nearest.Value)) { nearest = id; best = distance; }
		}
		Consider(world.QuestGiver.Id, world.QuestGiver.Region, world.QuestGiver.Position);
		foreach (var portal in world.Portals) Consider(portal.Id, portal.Region, portal.Position);
		if (nearest != default) Submit(CommandKind.Interact, target: nearest);
	}
	private void StopInput()
	{
		if ((requestedX != 0 || requestedY != 0) && Submit(CommandKind.SetMove)) requestedX = requestedY = 0;
	}
	public override void _Process(double delta)
	{
		if (simulation is null || verifying) return;
		bool active = current.IsAlive && IsVisibleInTree() && view.HasFocus() && GetWindow().HasFocus();
		int x = active && !paused ? (Input.IsKeyPressed(Key.Right) ? 1 : 0) - (Input.IsKeyPressed(Key.Left) ? 1 : 0) : 0;
		int y = active && !paused ? (Input.IsKeyPressed(Key.Down) ? 1 : 0) - (Input.IsKeyPressed(Key.Up) ? 1 : 0) : 0;
		if ((x != requestedX || y != requestedY) && Submit(CommandKind.SetMove, x, y)) { requestedX = x; requestedY = y; }
		if (active && !paused && Input.IsKeyPressed(Key.Space) && simulation.Tick - lastAttackTick >= GameSimulation.PlayerAttackInterval) AttackNearest();
		bool pressed = active && !paused && Input.IsKeyPressed(Key.E);
		if (pressed && !interactDown) InteractNearest(); interactDown = pressed;
		try { if (!paused && IsVisibleInTree()) clock.Advance(TimeSpan.FromSeconds(delta), advanceTick); }
		catch (Exception error) { SetPaused(true); status.Text = "Simulation stopped: " + error.Message; log("simulation_error", error.ToString()); }
		ShowFrame(); elapsed += delta;
		if (elapsed >= 1) { elapsed = 0; Refresh(); }
	}
	private void RunTick()
	{
		if (simulation.Tick >= MaxRecordingTicks) { SetPaused(true); status.Text = "Ten-minute recording limit reached. Start a new run."; return; }
		previous = current; long start = Stopwatch.GetTimestamp(); simulation.Step();
		tickMetrics.Record(Stopwatch.GetElapsedTime(start).TotalSeconds); current = simulation.GetEntity(Player);
		bool worldChanged = previous.Region != current.Region;
		if (worldChanged) { previous = current; requestedX = requestedY = 0; }
		if (!current.IsAlive) requestedX = requestedY = 0;
		foreach (var item in simulation.Events)
		{
			if (item.Kind == SimulationEventKind.Signaled) view.SignalValue = item.Value;
			else if (item.Kind == SimulationEventKind.Hit) status.Text = $"Entity {item.Actor.Value} hit {item.Target.Value}: {item.Value} damage";
			else if (item.Kind == SimulationEventKind.AttackFailed && item.Actor == Player) status.Text = $"Attack: {(AttackFailure)item.Value}";
			else if (item.Kind == SimulationEventKind.Died) status.Text = item.Actor == Player ? "You died. New run restarts the arena." : $"Monster {item.Actor.Value} defeated.";
			else if (item.Kind == SimulationEventKind.RegionChanged) status.Text = $"Entered {simulation.World!.GetRegion(current.Region).Name}. Region progress is retained.";
			else if (item.Kind == SimulationEventKind.InteractionFailed) status.Text = $"Interaction: {(InteractionFailure)item.Value}. Approach the marker; release Space before using E.";
			else if (item.Kind == SimulationEventKind.NpcTalked || item.Kind == SimulationEventKind.QuestChanged)
			{
				worldChanged = true;
				status.Text = simulation.QuestState switch
				{
					QuestStage.Active => "Guide: Defeat the three cellar monsters and return.",
					QuestStage.ReadyToTurnIn => "Cellar cleared. Return to the Camp Guide and use E.",
					QuestStage.Completed => "Quest completed. The guide restored your health once.", _ => "Talk to the Camp Guide."
				};
			}
		}
		if (worldChanged) Refresh();
		if (smokeTest && simulation.Tick == 2) GD.Print("OPEND2_M201_TICK_LOOP_READY");
	}
	private void ShowFrame() => view.SetPositions(previous.Position, current.Position, paused ? 1 : clock.Alpha);
	private void Refresh()
	{
		double p99 = tickMetrics.P99Milliseconds();
		var quest = simulation.Quest;
		questInfo.Text = $"{simulation.World!.GetRegion(current.Region).Name} | {simulation.World.QuestTitle}\n{quest.Stage} — {quest.Defeated}/{quest.Required} defeated. Reward: one health restoration.";
		details.Text = $"Tick {simulation.Tick} | region {current.Region.Value}, entity {current.Id.Value}\nPosition {current.Position.X}, {current.Position.Y} / {GameSimulation.UnitsPerTile} units per navigation cell\nHP {current.Health}/{current.MaxHealth} | cooldown {current.AttackCooldown}, hit stun {current.HitStun} ticks\nCommands {trace.Count}/{MaxRecordingCommands}, queued {simulation.PendingCommands} | RNG {simulation.RandomState}\nTick p99 {p99:F3} ms | dropped wall time {clock.DroppedTime.TotalMilliseconds:F1} ms\nState {simulation.ComputeStateHash()[..16]}";
		log("simulation_metrics", $"tick={simulation.Tick}, p99_ms={p99:F3}, pending={simulation.PendingCommands}, dropped_ms={clock.DroppedTime.TotalMilliseconds:F1}");
	}
	private async void VerifyReplay()
	{
		SetPaused(true); StopInput(); verifying = true;
		foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay }) button.Disabled = true;
		uint recordedSeed = seed; long target = simulation.Tick; var recorded = trace.ToArray(); string expected = simulation.ComputeStateHash();
		status.Text = "Replaying recorded commands...";
		try
		{
			string actual = await Task.Run(() => GameSimulation.Replay(recordedSeed, InitialEntities(), recorded, target, world: DemoWorld()).ComputeStateHash());
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			bool match = actual == expected;
			status.Text = match ? $"Replay matched at tick {target}: {actual}" : $"Replay mismatch: {actual}";
			log(match ? "replay_matched" : "replay_failed", $"tick={target}, expected={expected}, actual={actual}");
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Replay failed: " + error.Message; }
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree())
			{ verifying = false; foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay }) button.Disabled = false; Refresh(); }
		}
	}
	private static void WorldSmoke()
	{
		var cells = Enumerable.Repeat(CollisionCell.Open, 100).ToArray();
		var world = new WorldDefinition([new("Camp", new(Region, 10, 10, cells)), new("Cellar", new(Dungeon, 10, 10, cells))],
			[new(new(11), Region, new(640, 384), Dungeon, new(384, 384)), new(new(12), Dungeon, new(384, 640), Region, new(384, 384))],
			new(new(10), Region, new(384, 640), "Guide"), [new(2)], "Clear cellar");
		EntityState[] initial = [new(Player, Region, new(384, 384), Health: 50), new(new(2), Dungeon, new(640, 384), Kind: EntityKind.Monster, Health: 1)];
		var sample = new GameSimulation(1, initial, world: world);
		RecordedCommand[] script = [new(0, new(1, 1, Player, Region, CommandKind.Interact, Target: new(10))),
			new(1, new(2, 2, Player, Region, CommandKind.Interact, Target: new(11))), new(2, new(3, 3, Player, Dungeon, CommandKind.Attack, Target: new(2))),
			new(3, new(4, 4, Player, Dungeon, CommandKind.Interact, Target: new(12))), new(4, new(5, 5, Player, Region, CommandKind.Interact, Target: new(10)))];
		foreach (var entry in script)
		{
			if (sample.Submit(entry.Command) != CommandResult.Accepted) throw new InvalidDataException("World smoke command rejected."); sample.Step();
		}
		if (sample.ActiveRegion != Region || sample.QuestState != QuestStage.Completed || sample.GetEntity(new(2)).IsAlive || sample.GetEntity(Player).Health != 100 ||
			sample.ComputeStateHash() != GameSimulation.Replay(1, initial, script, 5, world: world).ComputeStateHash()) throw new InvalidDataException("World quest/replay smoke failed.");
		GD.Print("OPEND2_M203_WORLD_READY");
	}
	private void CombatSmoke()
	{
		EntityState[] initial = [new(Player, Region, new(320, 384)), new(new(2), Region, new(576, 384), Kind: EntityKind.Monster, Health: 1)];
		var grid = Arena(); var sample = new GameSimulation(1, initial, grid);
		RecordedCommand[] script = [new(0, new(1, 1, Player, Region, CommandKind.Attack, Target: new(2))), new(0, new(2, 2, Player, Region, CommandKind.SetMove, -1))];
		foreach (var c in script) if (sample.Submit(c.Command) != CommandResult.Accepted) throw new InvalidDataException("Combat smoke command rejected.");
		sample.Step();
		if (sample.GetEntity(new(2)).IsAlive || sample.GetEntity(Player).Health != 100) throw new InvalidDataException("Combat death smoke failed.");
		sample.Step();
		if (sample.GetEntity(Player).Position != initial[0].Position || sample.Events[0].Kind != SimulationEventKind.Blocked || sample.ComputeStateHash() != GameSimulation.Replay(1, initial, script, 2, grid).ComputeStateHash()) throw new InvalidDataException("Combat collision/replay smoke failed.");
		GD.Print("OPEND2_M202_COMBAT_READY");
	}
	private void Smoke()
	{
		EntityState[] initial = [new(Player, Region, new(0, 0))];
		var sample = new GameSimulation(1, initial);
		RecordedCommand[] script = [new(0, new(1, 1, Player, Region, CommandKind.SetMove, 1)), new(0, new(2, 2, Player, Region, CommandKind.Signal))];
		foreach (var item in script) if (sample.Submit(item.Command) != CommandResult.Accepted) throw new InvalidDataException("Simulation smoke command rejected.");
		var sampleClock = new FixedTickClock(); sampleClock.Advance(TimeSpan.FromMilliseconds(80), sample.Step);
		var copy = GameSimulation.Replay(1, initial, script, 2);
		if (sample.GetEntity(Player).Position != new GamePosition(64, 0) || sample.RandomState != 270369 || sample.Events[0].Kind != SimulationEventKind.Signaled || sample.ComputeStateHash() != copy.ComputeStateHash())
			throw new InvalidDataException("Simulation replay smoke failed.");
		view.SetPositions(new(0, 0), new(64, 0), 0.5);
		if (view.DisplayPosition != new Vector2(32, 0)) throw new InvalidDataException("Simulation interpolation smoke failed.");
		GD.Print("OPEND2_M201_SIMULATION_READY");
	}
}

public partial class SimulationCanvas : Control
{
	public Vector2 DisplayPosition { get; private set; }
	public int SignalValue { get; set; } = -1;
	private GameSimulation? simulation;
	public void SetSimulation(GameSimulation value) { simulation = value; QueueRedraw(); }
	public SimulationCanvas() { FocusMode = FocusModeEnum.All; MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }
	public void SetPositions(GamePosition previous, GamePosition current, double alpha)
	{
		DisplayPosition = new Vector2(previous.X, previous.Y).Lerp(new Vector2(current.X, current.Y), (float)alpha); QueueRedraw();
	}
	public override void _GuiInput(InputEvent input)
	{
		if (input is InputEventMouseButton { Pressed: true }) GrabFocus();
		if (input is InputEventKey { Keycode: Key.Up or Key.Down or Key.Left or Key.Right or Key.Space or Key.E }) AcceptEvent();
	}
	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.035f, 0.045f, 0.065f));
		Vector2 center = Size / 2;
		if (simulation?.Collision is not { } grid) return;
		Vector2 Project(GamePosition p) => center + (new Vector2(p.X, p.Y) - DisplayPosition) / GameSimulation.UnitsPerTile * 40;
		int left = Math.Max(0, (int)Math.Floor((DisplayPosition.X - center.X / 40 * 256 - grid.Origin.X) / 256));
		int top = Math.Max(0, (int)Math.Floor((DisplayPosition.Y - center.Y / 40 * 256 - grid.Origin.Y) / 256));
		int right = Math.Min(grid.Width, left + (int)(Size.X / 40) + 3), bottom = Math.Min(grid.Height, top + (int)(Size.Y / 40) + 3);
		for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
		{
			Color color = grid.At(x, y) switch { CollisionCell.Open => new(0.10f, 0.14f, 0.18f), CollisionCell.Blocked => new(0.32f, 0.35f, 0.40f), _ => new(0.32f, 0.13f, 0.38f) };
			DrawRect(new Rect2(Project(new(grid.Origin.X + x * 256, grid.Origin.Y + y * 256)), new Vector2(39, 39)), color);
		}
		if (simulation.World is { } world)
		{
			foreach (var portal in world.Portals)
			{
				if (portal.Region != simulation.ActiveRegion) continue;
				Vector2 point = Project(portal.Position);
				DrawRect(new Rect2(point - new Vector2(13, 13), new Vector2(26, 26)), Colors.Gold, false, 2);
				DrawString(GetThemeDefaultFont(), point + new Vector2(16, 5), world.GetRegion(portal.Destination).Name, fontSize: 14, modulate: Colors.Gold);
			}
			var npc = world.QuestGiver;
			if (npc.Region == simulation.ActiveRegion)
			{
				Vector2 point = Project(npc.Position); DrawCircle(point, 10, Colors.MediumSeaGreen);
				DrawString(GetThemeDefaultFont(), point + new Vector2(16, 5), npc.Name, fontSize: 14, modulate: Colors.MediumSeaGreen);
			}
		}
		foreach (var e in simulation.Entities)
		{
			if (e.Region != simulation.ActiveRegion) continue;
			Vector2 point = e.Kind == EntityKind.Player ? center : Project(e.Position);
			Color color = !e.IsAlive ? Colors.DimGray : e.Kind == EntityKind.Monster ? Colors.IndianRed : SignalValue < 0 ? Colors.CornflowerBlue : Color.FromHsv(SignalValue / 6f, 0.7f, 0.95f);
			DrawCircle(point, 9, color);
			if (e.IsAlive)
			{
				DrawRect(new Rect2(point + new Vector2(-14, -18), new Vector2(28, 4)), Colors.DarkRed);
				DrawRect(new Rect2(point + new Vector2(-14, -18), new Vector2(28f * e.Health / e.MaxHealth, 4)), Colors.LimeGreen);
			}
			else { DrawLine(point + new Vector2(-7, -7), point + new Vector2(7, 7), Colors.Gray, 2); DrawLine(point + new Vector2(-7, 7), point + new Vector2(7, -7), Colors.Gray, 2); }
		}
		if (HasFocus()) DrawArc(center, 14, 0, Mathf.Tau, 32, Colors.White, 1, true);
	}
}
