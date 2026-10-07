using Godot;
using OpenD2.Core;
using System.Diagnostics;

namespace OpenD2.Client;

// Development inspector for M2-01; world collision/combat are the next milestone.
public partial class SimulationPreview : VBoxContainer
{
	private const int MaxRecordingTicks = 15000, MaxRecordingCommands = 4096;
	private static readonly EntityId Player = new(1);
	private static readonly RegionId Region = new(1);
	private readonly FixedTickClock clock = new();
	private FrameMetrics tickMetrics = new();
	private readonly List<RecordedCommand> trace = new(MaxRecordingCommands);
	private readonly SpinBox seedInput = new() { MinValue = 1, MaxValue = uint.MaxValue, Value = 1, Step = 1 };
	private readonly Button restart = new() { Text = "New run" };
	private readonly Button pause = new() { Text = "Pause" };
	private readonly Button singleStep = new() { Text = "Step one tick" };
	private readonly Button signal = new() { Text = "Signal" };
	private readonly Button replay = new() { Text = "Verify replay" };
	private readonly Label details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly SimulationCanvas view = new() { CustomMinimumSize = new Vector2(460, 300), SizeFlagsVertical = SizeFlags.ExpandFill };
	private readonly Action<string, string> log;
	private readonly Action advanceTick;
	private GameSimulation simulation = null!;
	private EntityState previous, current;
	private uint seed;
	private ulong sequence;
	private int requestedX, requestedY;
	private bool paused, verifying;
	private bool smokeTest;
	private double elapsed;
	public SimulationPreview(Action<string, string> log) { this.log = log; advanceTick = RunTick; }
	private static EntityState[] InitialEntities() => [new(Player, Region, new(0, 0))];
	public override void _Ready()
	{
		smokeTest = OS.GetCmdlineUserArgs().Contains("--smoke-test");
		AddChild(new Label { Text = "Simulation inspector / 25 ticks per second" });
		AddChild(new Label { Text = "Click the grid, then use arrow keys. Signal emits a seeded color event.\nSynthetic free movement; world collision and combat are not loaded.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		var controls = new HFlowContainer(); AddChild(controls);
		controls.AddChild(new Label { Text = "Seed" }); controls.AddChild(seedInput);
		foreach (var button in new[] { restart, pause, singleStep, signal, replay }) controls.AddChild(button);
		AddChild(view); AddChild(details); AddChild(status);
		restart.Pressed += NewRun; pause.Pressed += () => { SetPaused(!paused); StopInput(); Refresh(); };
		singleStep.Pressed += () => { SetPaused(true); StopInput(); if (!verifying) { RunTick(); ShowFrame(); Refresh(); } };
		signal.Pressed += () => Submit(CommandKind.Signal);
		replay.Pressed += VerifyReplay;
		Smoke(); NewRun();
	}
	private void NewRun()
	{
		seed = (uint)seedInput.Value; simulation = new(seed, InitialEntities());
		trace.Clear(); sequence = 0; requestedX = requestedY = 0; clock.Reset(); tickMetrics = new(); elapsed = 0;
		previous = current = simulation.GetEntity(Player); view.SignalValue = -1; SetPaused(false);
		status.Text = "Ready. The seed and accepted command log reproduce this run."; ShowFrame(); Refresh();
		log("simulation_started", $"rules={GameSimulation.RulesVersion}, seed={seed}");
	}
	private void SetPaused(bool value) { paused = value; previous = current; pause.Text = paused ? "Resume" : "Pause"; }
	private bool Submit(CommandKind kind, int x = 0, int y = 0)
	{
		if (verifying || simulation.Tick >= MaxRecordingTicks || trace.Count >= MaxRecordingCommands)
		{ SetPaused(true); status.Text = "Recording limit reached. Start a new run to continue."; return false; }
		var command = new GameCommand(simulation.Tick + 1, sequence + 1, Player, Region, kind, x, y);
		var result = simulation.Submit(command);
		if (result != CommandResult.Accepted) { status.Text = $"Command rejected: {result}"; return false; }
		sequence++; trace.Add(new(simulation.Tick, command)); return true;
	}
	private void StopInput()
	{
		if ((requestedX != 0 || requestedY != 0) && Submit(CommandKind.SetMove)) requestedX = requestedY = 0;
	}
	public override void _Process(double delta)
	{
		if (simulation is null || verifying) return;
		bool active = IsVisibleInTree() && view.HasFocus() && GetWindow().HasFocus();
		int x = active && !paused ? (Input.IsKeyPressed(Key.Right) ? 1 : 0) - (Input.IsKeyPressed(Key.Left) ? 1 : 0) : 0;
		int y = active && !paused ? (Input.IsKeyPressed(Key.Down) ? 1 : 0) - (Input.IsKeyPressed(Key.Up) ? 1 : 0) : 0;
		if ((x != requestedX || y != requestedY) && Submit(CommandKind.SetMove, x, y)) { requestedX = x; requestedY = y; }
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
		foreach (var item in simulation.Events) if (item.Kind == SimulationEventKind.Signaled) view.SignalValue = item.Value;
		if (smokeTest && simulation.Tick == 2) GD.Print("OPEND2_M201_TICK_LOOP_READY");
	}
	private void ShowFrame() => view.SetPositions(previous.Position, current.Position, paused ? 1 : clock.Alpha);
	private void Refresh()
	{
		double p99 = tickMetrics.P99Milliseconds();
		details.Text = $"Tick {simulation.Tick} | region {current.Region.Value}, entity {current.Id.Value}\nPosition {current.Position.X}, {current.Position.Y} / {GameSimulation.UnitsPerTile} units per tile\nCommands {trace.Count}/{MaxRecordingCommands}, queued {simulation.PendingCommands} | RNG {simulation.RandomState}\nTick p99 {p99:F3} ms | dropped wall time {clock.DroppedTime.TotalMilliseconds:F1} ms\nState {simulation.ComputeStateHash()[..16]}";
		log("simulation_metrics", $"tick={simulation.Tick}, p99_ms={p99:F3}, pending={simulation.PendingCommands}, dropped_ms={clock.DroppedTime.TotalMilliseconds:F1}");
	}
	private async void VerifyReplay()
	{
		SetPaused(true); StopInput(); verifying = true;
		foreach (var button in new[] { restart, pause, singleStep, signal, replay }) button.Disabled = true;
		uint recordedSeed = seed; long target = simulation.Tick; var recorded = trace.ToArray(); string expected = simulation.ComputeStateHash();
		status.Text = "Replaying recorded commands...";
		try
		{
			string actual = await Task.Run(() => GameSimulation.Replay(recordedSeed, InitialEntities(), recorded, target).ComputeStateHash());
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			bool match = actual == expected;
			status.Text = match ? $"Replay matched at tick {target}: {actual}" : $"Replay mismatch: {actual}";
			log(match ? "replay_matched" : "replay_failed", $"tick={target}, expected={expected}, actual={actual}");
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Replay failed: " + error.Message; }
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree())
			{ verifying = false; foreach (var button in new[] { restart, pause, singleStep, signal, replay }) button.Disabled = false; Refresh(); }
		}
	}
	private void Smoke()
	{
		var sample = new GameSimulation(1, InitialEntities());
		RecordedCommand[] script = [new(0, new(1, 1, Player, Region, CommandKind.SetMove, 1)), new(0, new(2, 2, Player, Region, CommandKind.Signal))];
		foreach (var item in script) if (sample.Submit(item.Command) != CommandResult.Accepted) throw new InvalidDataException("Simulation smoke command rejected.");
		var sampleClock = new FixedTickClock(); sampleClock.Advance(TimeSpan.FromMilliseconds(80), sample.Step);
		var copy = GameSimulation.Replay(1, InitialEntities(), script, 2);
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
	public SimulationCanvas() { FocusMode = FocusModeEnum.All; MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }
	public void SetPositions(GamePosition previous, GamePosition current, double alpha)
	{
		DisplayPosition = new Vector2(previous.X, previous.Y).Lerp(new Vector2(current.X, current.Y), (float)alpha); QueueRedraw();
	}
	public override void _GuiInput(InputEvent input)
	{
		if (input is InputEventMouseButton { Pressed: true }) GrabFocus();
		if (input is InputEventKey { Keycode: Key.Up or Key.Down or Key.Left or Key.Right }) AcceptEvent();
	}
	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.035f, 0.045f, 0.065f));
		Vector2 center = Size / 2, offset = DisplayPosition / GameSimulation.UnitsPerTile * 32;
		for (float x = center.X - offset.X % 32; x < Size.X; x += 32) DrawLine(new(x, 0), new(x, Size.Y), new Color(0.15f, 0.19f, 0.23f));
		for (float x = center.X - offset.X % 32 - 32; x >= 0; x -= 32) DrawLine(new(x, 0), new(x, Size.Y), new Color(0.15f, 0.19f, 0.23f));
		for (float y = center.Y - offset.Y % 32; y < Size.Y; y += 32) DrawLine(new(0, y), new(Size.X, y), new Color(0.15f, 0.19f, 0.23f));
		for (float y = center.Y - offset.Y % 32 - 32; y >= 0; y -= 32) DrawLine(new(0, y), new(Size.X, y), new Color(0.15f, 0.19f, 0.23f));
		DrawCircle(center - offset, 4, Colors.Gray);
		Color color = SignalValue < 0 ? Colors.CornflowerBlue : Color.FromHsv(SignalValue / 6f, 0.7f, 0.95f);
		DrawCircle(center, 9, color); if (HasFocus()) DrawArc(center, 14, 0, Mathf.Tau, 32, Colors.White, 1, true);
	}
}
