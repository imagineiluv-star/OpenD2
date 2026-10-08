using Godot;
using OpenD2.Core;
using OpenD2.Npc;
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
	private readonly Button pickup = new() { Text = "Pick up nearest (F)" };
	private readonly Button equip = new() { Text = "Equip selected" };
	private readonly Button unequip = new() { Text = "Unequip selected" };
	private readonly Button drop = new() { Text = "Drop selected" };
	private readonly OptionButton ownedItems = new();
	private readonly Label gearInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly List<ItemId> itemChoices = new();
	private readonly Button save = new() { Text = "Save checkpoint" };
	private readonly Button load = new() { Text = "Load checkpoint" };
	private readonly NpcMindService npcMind;
	private readonly LineEdit dialogueInput = new() { PlaceholderText = "안녕 / 퀘스트 / 수락 / 완료", MaxLength = NpcDecisionGate.MaxInputChars, SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly Button dialogueSend = new() { Text = "Talk to Guide" };
	private readonly Button dialogueConfirm = new() { Text = "Confirm quest action", Disabled = true };
	private readonly Label dialogue = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private NpcResult? dialogueOffer;
	private NpcFacts? dialogueFacts;
	private bool npcSmokePending;
	private readonly string savePath;
	private SimulationSnapshot? replayCheckpoint;
	private long recordingStart;
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
	private bool paused, verifying, interactDown, pickupDown;
	private bool smokeTest;
	private double elapsed;
	public SimulationPreview(Action<string, string> log, string saveDirectory, string modelDirectory, Func<string> gameDirectory)
	{ this.log = log; this.gameDirectory = gameDirectory; savePath = Path.Combine(saveDirectory, "simulation-v1.json"); advanceTick = RunTick; npcMind = new(npcRuntime); npcStore = new(modelDirectory); }
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
		AddChild(new Label { Text = "Click ground to move / click a monster to attack. Arrows move, Space attacks, E talks / uses a portal, F picks up nearby loot.\nGreen: NPC. Gold: portal. Gray walls and purple unknown cells block movement.\nDefault: synthetic maps and rules. Load a checked legacy scene below for original terrain.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		BuildContentControls();
		view.MoveRequested += ClickMove; view.AttackRequested += ClickAttack;
		var controls = new HFlowContainer(); AddChild(controls);
		controls.AddChild(new Label { Text = "Seed" }); controls.AddChild(seedInput);
		foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay, save, load }) controls.AddChild(button);
		var inventory = new HFlowContainer(); AddChild(inventory);
		inventory.AddChild(ownedItems);
		foreach (var button in new[] { pickup, equip, unequip, drop }) inventory.AddChild(button);
		pickup.Pressed += PickupNearest;
		equip.Pressed += () => UseSelected(CommandKind.Equip);
		unequip.Pressed += () => UseSelected(CommandKind.Unequip);
		drop.Pressed += () => UseSelected(CommandKind.DropItem);
		AddChild(gearInfo); AddChild(questInfo); AddChild(view); AddChild(details); AddChild(status);
		AddChild(new Label { Text = "Camp Guide — 기본 대사 / 선택형 로컬 AI", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		BuildNpcSettings();
		AddChild(dialogueInput); var conversation = new HFlowContainer(); AddChild(conversation);
		conversation.AddChild(dialogueSend); conversation.AddChild(dialogueConfirm); AddChild(dialogue);
		dialogueSend.Pressed += SendDialogue; dialogueInput.TextSubmitted += _ => SendDialogue(); dialogueConfirm.Pressed += ConfirmDialogue;
		restart.Pressed += NewRun; pause.Pressed += () => { SetPaused(!paused); StopInput(); Refresh(); };
		singleStep.Pressed += () => { SetPaused(true); StopInput(); if (!verifying) { RunTick(); ShowFrame(); Refresh(); } };
		signal.Pressed += () => Submit(CommandKind.Signal); attack.Pressed += AttackNearest; interact.Pressed += InteractNearest;
		replay.Pressed += VerifyReplay; save.Pressed += () => CheckpointFile(false); load.Pressed += () => CheckpointFile(true);
		Smoke(); CombatSmoke(); WorldSmoke(); ItemSmoke(); SaveSmoke(); NewRun();
		if (smokeTest) ContentSmoke();
		if (smokeTest) { npcSmokePending = true; dialogueInput.Text = "안녕"; SendDialogue(); }
	}
	private void NewRun()
	{
		ResetDialogue(); ClearRoute();
		seed = (uint)seedInput.Value; simulation = new(seed, ActiveActors, world: ActiveWorld); view.SetSimulation(simulation);
		trace.Clear(); replayCheckpoint = null; recordingStart = 0; sequence = 0; lastAttackTick = -GameSimulation.PlayerAttackInterval; requestedX = requestedY = 0; clock.Reset(); tickMetrics = new(); elapsed = 0;
		previous = current = simulation.GetEntity(Player); view.SignalValue = -1; interactDown = pickupDown = false; SetPaused(false);
		status.Text = "Talk to the quest giver, defeat the marked targets, then return."; ShowFrame(); Refresh();
		log("simulation_started", $"rules={GameSimulation.RulesVersion}, seed={seed}");
	}
	private void SetPaused(bool value) { paused = value; previous = current; pause.Text = paused ? "Resume" : "Pause"; }
	private bool Submit(CommandKind kind, int x = 0, int y = 0, EntityId target = default, ItemId item = default)
	{
		if (verifying || simulation.Tick - recordingStart >= MaxRecordingTicks || trace.Count >= MaxRecordingCommands)
		{ SetPaused(true); status.Text = "Recording limit reached. Start a new run to continue."; return false; }
		var command = new GameCommand(simulation.Tick + 1, sequence + 1, Player, simulation.ActiveRegion, kind, x, y, target, item);
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
	private void PickupNearest()
	{
		ItemId nearest = default; long best = long.MaxValue;
		foreach (var item in simulation.Items)
		{
			if (item.Location != ItemLocation.Ground || item.Region != current.Region) continue;
			long x = (long)item.Position.X - current.Position.X, y = (long)item.Position.Y - current.Position.Y, distance = x * x + y * y;
			if (distance < best || (distance == best && item.Id.Value < nearest.Value)) { best = distance; nearest = item.Id; }
		}
		if (nearest == default) { status.Text = "No loot in this region."; return; }
		Submit(CommandKind.Pickup, item: nearest);
	}
	private void UseSelected(CommandKind kind)
	{
		int selected = ownedItems.Selected;
		if (selected >= 0 && selected < itemChoices.Count) Submit(kind, item: itemChoices[selected]);
	}
	private void RefreshItems()
	{
		ItemId selected = ownedItems.Selected >= 0 && ownedItems.Selected < itemChoices.Count ? itemChoices[ownedItems.Selected] : default;
		itemChoices.Clear(); ownedItems.Clear(); int bagCount = 0;
		foreach (var item in simulation.Items)
		{
			if (item.Owner != Player) continue;
			if (item.Location == ItemLocation.Inventory) bagCount++;
			var spec = ItemCatalog.Get(item.Definition);
			ownedItems.AddItem($"{(item.Location == ItemLocation.Equipped ? spec.Slot.ToString() : $"Bag {item.Slot + 1}")}: {spec.Name} #{item.Id.Value}");
			itemChoices.Add(item.Id);
			if (item.Id == selected) ownedItems.Select(itemChoices.Count - 1);
		}
		var stats = simulation.GetStats(Player);
		gearInfo.Text = $"Bag {bagCount}/{GameSimulation.InventoryCapacity} | Damage {stats.MinimumDamage}–{stats.MaximumDamage} | Armor {stats.Armor} (flat reduction, minimum hit 1)";
	}
	private void StopInput()
	{
		ClearRoute();
		if ((requestedX != 0 || requestedY != 0) && Submit(CommandKind.SetMove)) requestedX = requestedY = 0;
	}
	public override void _Process(double delta)
	{
		if (simulation is null) return;
		PollNpcSettings();
		PollDialogue();
		if (verifying) return;
		bool active = current.IsAlive && IsVisibleInTree() && view.HasFocus() && GetWindow().HasFocus();
		int x = active && !paused ? (Input.IsKeyPressed(Key.Right) ? 1 : 0) - (Input.IsKeyPressed(Key.Left) ? 1 : 0) : 0;
		int y = active && !paused ? (Input.IsKeyPressed(Key.Down) ? 1 : 0) - (Input.IsKeyPressed(Key.Up) ? 1 : 0) : 0;
		(x, y) = FollowRoute(active, x, y);
		if ((x != requestedX || y != requestedY) && Submit(CommandKind.SetMove, x, y)) { requestedX = x; requestedY = y; }
		if (active && !paused && Input.IsKeyPressed(Key.Space) && simulation.Tick - lastAttackTick >= GameSimulation.PlayerAttackInterval) AttackNearest();
		bool pressed = active && !paused && Input.IsKeyPressed(Key.E);
		if (pressed && !interactDown) InteractNearest(); interactDown = pressed;
		bool picking = active && !paused && Input.IsKeyPressed(Key.F);
		if (picking && !pickupDown) PickupNearest(); pickupDown = picking;
		try { if (!paused && IsVisibleInTree()) clock.Advance(TimeSpan.FromSeconds(delta), advanceTick); }
		catch (Exception error) { SetPaused(true); status.Text = "Simulation stopped: " + error.Message; log("simulation_error", error.ToString()); }
		ShowFrame(); elapsed += delta;
		if (elapsed >= 1) { elapsed = 0; Refresh(); }
	}
	private void RunTick()
	{
		if (simulation.Tick - recordingStart >= MaxRecordingTicks) { SetPaused(true); status.Text = "Ten-minute recording limit reached. Start a new run."; return; }
		previous = current; long start = Stopwatch.GetTimestamp(); simulation.Step();
		tickMetrics.Record(Stopwatch.GetElapsedTime(start).TotalSeconds); current = simulation.GetEntity(Player);
		ObserveRouteTick();
		bool worldChanged = previous.Region != current.Region;
		if (worldChanged || (previous.IsAlive && !current.IsAlive)) { ResetDialogue(); ClearRoute(); }
		if (worldChanged) { previous = current; requestedX = requestedY = 0; }
		if (!current.IsAlive) requestedX = requestedY = 0;
		foreach (var item in simulation.Events)
		{
			if (item.Kind == SimulationEventKind.Signaled) view.SignalValue = item.Value;
			else if (item.Kind == SimulationEventKind.Hit) status.Text = $"Entity {item.Actor.Value} hit {item.Target.Value}: {item.Value} damage";
			else if (item.Kind == SimulationEventKind.AttackFailed && item.Actor == Player) status.Text = $"Attack: {(AttackFailure)item.Value}";
			else if (item.Kind == SimulationEventKind.Died) status.Text = item.Actor == Player ? "You died. New run restarts the arena." : $"Monster {item.Actor.Value} defeated.";
			else if (item.Kind is SimulationEventKind.ItemDropped or SimulationEventKind.ItemChanged or SimulationEventKind.ItemFailed)
			{
				worldChanged = true;
				status.Text = item.Kind == SimulationEventKind.ItemFailed ? $"Item: {(ItemFailure)item.Value}" :
					item.Kind == SimulationEventKind.ItemDropped ? "Loot dropped. Approach it and press F." : $"Item #{item.Item.Value}: {(CommandKind)item.Value}";
			}
			else if (item.Kind == SimulationEventKind.RegionChanged) status.Text = $"Entered {simulation.World!.GetRegion(current.Region).Name}. Region progress is retained.";
			else if (item.Kind == SimulationEventKind.InteractionFailed) status.Text = $"Interaction: {(InteractionFailure)item.Value}. Approach the marker; release Space before using E.";
			else if (item.Kind == SimulationEventKind.NpcTalked || item.Kind == SimulationEventKind.QuestChanged)
			{
				worldChanged = true;
				status.Text = simulation.QuestState switch
				{
					QuestStage.Active => $"Guide: Defeat {simulation.Quest.Required} quest targets and return.",
					QuestStage.ReadyToTurnIn => "Targets cleared. Return to the quest giver and use E.",
					QuestStage.Completed => "Quest completed. The guide restored your health once.", _ => "Talk to the quest giver."
				};
			}
		}
		if (worldChanged) Refresh();
		if (smokeTest && simulation.Tick == 2) GD.Print("OPEND2_M201_TICK_LOOP_READY");
	}
	private void ShowFrame() => view.SetPositions(previous.Position, current.Position, paused ? 1 : clock.Alpha);
	private void ResetDialogue()
	{
		npcMind.Invalidate(); dialogueOffer = null; dialogueFacts = null; dialogueConfirm.Disabled = true;
		dialogueInput.Text = ""; dialogue.Text = "Approach the Camp Guide to talk. Dialogue is not saved.";
	}
	private void SendDialogue()
	{
		if (verifying || npcOperation is not null) return;
		var facts = NpcDecisionGate.Capture(simulation);
		var result = npcMind.Request(facts, dialogueInput.Text);
		if (result == NpcStart.Accepted) { dialogueFacts = facts; dialogueOffer = null; dialogueConfirm.Disabled = true; dialogueInput.Text = ""; }
		dialogue.Text = result switch
		{
			NpcStart.Accepted => "Guide is thinking... The game continues.",
			NpcStart.Busy => "A dialogue request is still running. Please wait.",
			NpcStart.Unavailable => "Approach the guide in Camp; you must be alive and able to act.",
			_ => "Enter 1–512 characters without control characters."
		};
	}
	private void PollDialogue()
	{
		var facts = NpcDecisionGate.Capture(simulation);
		if (dialogueFacts is { } expected && expected != facts)
		{
			npcMind.Invalidate(); dialogueFacts = null;
			dialogue.Text = "The situation changed; the pending reply was cancelled.";
		}
		if (npcMind.Poll(facts) is { } result)
		{
			dialogueFacts = null;
			dialogue.Text = result.Reply.Speech + (result.Outcome == NpcOutcome.Answer ? "" : $" [기본 대사: {result.Outcome}]");
			dialogueOffer = result.Reply.OffersInteraction ? result : null;
			log("npc_dialogue", $"request={result.Request.Id}, outcome={result.Outcome}, intent={result.Reply.Intent}");
			if (npcSmokePending)
			{
				npcSmokePending = false;
				if (result.Outcome == NpcOutcome.Answer && result.Reply.Intent == NpcIntent.Greeting) GD.Print("OPEND2_NPC01_DIALOGUE_READY");
				else GD.PushError("NPC dialogue smoke failed.");
			}
		}
		if (dialogueOffer is { } offer && !NpcDecisionGate.CanConfirm(offer, npcMind.Generation, facts)) dialogueOffer = null;
		dialogueSend.Disabled = verifying || npcMind.IsBusy || npcOperation is not null;
		dialogueInput.Editable = !verifying && npcOperation is null;
		dialogueConfirm.Disabled = verifying || dialogueOffer is null || npcOperation is not null;
	}
	private void ConfirmDialogue()
	{
		if (verifying || npcOperation is not null || dialogueOffer is not { } offer) return;
		dialogueOffer = null; dialogueConfirm.Disabled = true;
		if (!NpcDecisionGate.CanConfirm(offer, npcMind.Generation, NpcDecisionGate.Capture(simulation)))
		{ dialogue.Text = "The situation changed. Ask the guide again."; return; }
		if (Submit(CommandKind.Interact, target: offer.Request.Facts.Npc))
			dialogue.Text = paused ? "Action queued. Resume or step to resolve it." : "Action queued; the game rules will resolve it.";
	}
	public override void _ExitTree()
	{
		var cancelled = npcCancellation?.CancelAsync() ?? Task.CompletedTask;
		npcMind.Dispose(); npcRuntime.Dispose(); npcStore.Dispose();
		if (npcOperation is { } pending)
		{
			var cancellation = npcCancellation;
			_ = Task.WhenAll(pending, cancelled).ContinueWith(task => { _ = task.Exception; cancellation?.Dispose(); }, TaskScheduler.Default);
		}
	}
	private void Refresh()
	{
		RefreshItems();
		double p99 = tickMetrics.P99Milliseconds();
		var quest = simulation.Quest;
		questInfo.Text = $"{simulation.World!.GetRegion(current.Region).Name} | {simulation.World.QuestTitle}\n{quest.Stage} — {quest.Defeated}/{quest.Required} defeated. Reward: one health restoration.";
		details.Text = $"Tick {simulation.Tick} | region {current.Region.Value}, entity {current.Id.Value}\nPosition {current.Position.X}, {current.Position.Y} / {GameSimulation.UnitsPerTile} units per navigation cell\nHP {current.Health}/{current.MaxHealth} | cooldown {current.AttackCooldown}, hit stun {current.HitStun} ticks\nCommands {trace.Count}/{MaxRecordingCommands}, queued {simulation.PendingCommands} | RNG {simulation.RandomState}\nTick p99 {p99:F3} ms | dropped wall time {clock.DroppedTime.TotalMilliseconds:F1} ms\nState {simulation.ComputeStateHash()[..16]}";
		log("simulation_metrics", $"tick={simulation.Tick}, p99_ms={p99:F3}, pending={simulation.PendingCommands}, dropped_ms={clock.DroppedTime.TotalMilliseconds:F1}");
	}
	private async void VerifyReplay()
	{
		ResetDialogue();
		SetPaused(true); StopInput(); verifying = true;
		foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay, pickup, equip, unequip, drop, save, load }) button.Disabled = true;
		uint recordedSeed = seed; long target = simulation.Tick; var recorded = trace.ToArray(); string expected = simulation.ComputeStateHash();
		status.Text = "Replaying recorded commands...";
		try
		{
			var baseline = replayCheckpoint;
			string actual = await Task.Run(() => (baseline is null ? GameSimulation.Replay(recordedSeed, ActiveActors, recorded, target, world: ActiveWorld) : GameSimulation.Replay(baseline, recorded, target)).ComputeStateHash());
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			bool match = actual == expected;
			status.Text = match ? $"Replay matched at tick {target}: {actual}" : $"Replay mismatch: {actual}";
			log(match ? "replay_matched" : "replay_failed", $"tick={target}, expected={expected}, actual={actual}");
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Replay failed: " + error.Message; }
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree())
			{ verifying = false; foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay, pickup, equip, unequip, drop, save, load }) button.Disabled = false; Refresh(); }
		}
	}
	private async void CheckpointFile(bool loading)
	{
		if (verifying) return;
		ResetDialogue();
		SetPaused(true); StopInput(); verifying = true;
		foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay, pickup, equip, unequip, drop, save, load }) button.Disabled = true;
		var checkpoint = simulation.CaptureSnapshot(); status.Text = loading ? "Loading checkpoint..." : "Saving checkpoint...";
		try
		{
			if (loading)
			{
				var result = await Task.Run(() => GameSave.Load(ActiveSavePath, world: ActiveWorld));
				if (!IsInstanceValid(this) || !IsInsideTree()) return;
				if (result.Simulation.WorldPlayer != Player) throw new InvalidDataException("Checkpoint belongs to an unsupported player identity.");
				simulation = result.Simulation; view.SetSimulation(simulation);
				replayCheckpoint = simulation.CaptureSnapshot(); trace.Clear(); recordingStart = simulation.Tick;
				sequence = replayCheckpoint.Inputs.First(c => c.Actor == Player).Sequence;
				previous = current = simulation.GetEntity(Player); requestedX = current.MoveX; requestedY = current.MoveY;
				lastAttackTick = simulation.Tick - GameSimulation.PlayerAttackInterval; interactDown = pickupDown = false;
				clock.Reset(); tickMetrics = new(); view.SignalValue = -1; ShowFrame();
				status.Text = result.RecoveredFromBackup ? "Recovered the previous valid backup. Files preserved; paused for review." : "Checkpoint loaded. Press Resume to continue.";
				log("game_loaded", $"tick={simulation.Tick}, backup={result.RecoveredFromBackup}");
			}
			else
			{
				await Task.Run(() => GameSave.Save(ActiveSavePath, checkpoint));
				if (!IsInstanceValid(this) || !IsInsideTree()) return;
				status.Text = "Checkpoint saved: " + ActiveSavePath; log("game_saved", $"tick={checkpoint.Tick}");
			}
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) { status.Text = "Checkpoint failed: " + error.Message; log("checkpoint_error", error.ToString()); } }
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree())
			{ verifying = false; foreach (var button in new[] { restart, pause, singleStep, attack, interact, signal, replay, pickup, equip, unequip, drop, save, load }) button.Disabled = false; Refresh(); }
		}
	}
	private static void SaveSmoke()
	{
		string folder = Path.Combine(Path.GetTempPath(), "opend2-save-smoke-" + Guid.NewGuid().ToString("N"));
		try
		{
			string path = Path.Combine(folder, "checkpoint.json"); var game = new GameSimulation(1, InitialEntities(), world: DemoWorld());
			game.Step(); GameSave.Save(path, game.CaptureSnapshot()); string first = game.ComputeStateHash();
			game.Step(); GameSave.Save(path, game.CaptureSnapshot());
			var loaded = GameSave.Load(path, world: DemoWorld());
			if (loaded.RecoveredFromBackup || loaded.Simulation.ComputeStateHash() != game.ComputeStateHash()) throw new InvalidDataException("Save smoke round trip failed.");
			File.WriteAllText(path, "{"); var recovered = GameSave.Load(path, world: DemoWorld());
			if (!recovered.RecoveredFromBackup || recovered.Simulation.ComputeStateHash() != first) throw new InvalidDataException("Save smoke backup recovery failed.");
			GD.Print("OPEND2_M205_SAVE_READY");
		}
		finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
	}
	private static void ItemSmoke()
	{
		EntityState[] initial = [new(Player, Region, new(384, 384)), new(new(2), Region, new(640, 384), Kind: EntityKind.Monster, Health: 1)];
		var grid = Arena(); var sample = new GameSimulation(1, initial, grid);
		RecordedCommand[] script = [new(0, new(1, 1, Player, Region, CommandKind.Attack, Target: new(2))),
			new(1, new(2, 2, Player, Region, CommandKind.Pickup, Item: new(2))),
			new(2, new(3, 3, Player, Region, CommandKind.Equip, Item: new(2)))];
		foreach (var entry in script)
		{
			if (sample.Submit(entry.Command) != CommandResult.Accepted) throw new InvalidDataException("Item smoke command rejected."); sample.Step();
		}
		if (sample.Items.Length != 1 || sample.GetItem(new(2)).Location != ItemLocation.Equipped || sample.GetStats(Player).MinimumDamage != 20 ||
			sample.ComputeStateHash() != GameSimulation.Replay(1, initial, script, 3, grid).ComputeStateHash()) throw new InvalidDataException("Item ownership/stats/replay smoke failed.");
		GD.Print("OPEND2_M204_ITEMS_READY");
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
	private readonly List<EntityState> depthActors = new(GameSimulation.MaxCombatEntities);
	public void SetSimulation(GameSimulation value) { simulation = value; foreach (var sprite in actorSprites.Values) sprite.Reset(); QueueRedraw(); }
	public SimulationCanvas() { FocusMode = FocusModeEnum.All; MouseFilter = MouseFilterEnum.Stop; ClipContents = true; }
	public void SetPositions(GamePosition previous, GamePosition current, double alpha)
	{
		DisplayPosition = new Vector2(previous.X, previous.Y).Lerp(new Vector2(current.X, current.Y), (float)alpha); QueueRedraw();
	}
	public override void _GuiInput(InputEvent input)
	{
		if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mouse) { GrabFocus(); ClickWorld(mouse.Position); AcceptEvent(); }
		if (input is InputEventKey { Keycode: Key.Up or Key.Down or Key.Left or Key.Right or Key.Space or Key.E or Key.F }) AcceptEvent();
	}
	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.035f, 0.045f, 0.065f));
		Vector2 center = Size / 2;
		if (simulation?.Collision is not { } grid) return;
		Vector2 Project(GamePosition p) => terrainContent is null ? center + (new Vector2(p.X, p.Y) - DisplayPosition) / GameSimulation.UnitsPerTile * 40 : center + Iso(p.X - DisplayPosition.X, p.Y - DisplayPosition.Y);
		if (terrainContent is not null) DrawTerrain();
		int left = Math.Max(0, (int)Math.Floor((DisplayPosition.X - center.X / 40 * 256 - grid.Origin.X) / 256));
		int top = Math.Max(0, (int)Math.Floor((DisplayPosition.Y - center.Y / 40 * 256 - grid.Origin.Y) / 256));
		int right = Math.Min(grid.Width, left + (int)(Size.X / 40) + 3), bottom = Math.Min(grid.Height, top + (int)(Size.Y / 40) + 3);
		if (terrainContent is null) for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
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
		depthActors.Clear();
		foreach (var actor in simulation.Entities) if (actor.Region == simulation.ActiveRegion) depthActors.Add(actor);
		depthActors.Sort(static (a, b) => { int order = (a.Position.X + a.Position.Y).CompareTo(b.Position.X + b.Position.Y); return order != 0 ? order : a.Id.Value.CompareTo(b.Id.Value); });
		foreach (var e in depthActors)
		{
			DrawForeground(e.Position.X + e.Position.Y);
			Vector2 point = e.Kind == EntityKind.Player ? center : Project(e.Position);
			Color color = !e.IsAlive ? Colors.DimGray : e.Kind == EntityKind.Monster ? Colors.IndianRed : SignalValue < 0 ? Colors.CornflowerBlue : Color.FromHsv(SignalValue / 6f, 0.7f, 0.95f);
			bool hasArtwork = DrawActor(e, point);
			if (!hasArtwork) DrawCircle(point, 9, color);
			if (e.IsAlive)
			{
				DrawRect(new Rect2(point + new Vector2(-14, -18), new Vector2(28, 4)), Colors.DarkRed);
				DrawRect(new Rect2(point + new Vector2(-14, -18), new Vector2(28f * e.Health / e.MaxHealth, 4)), Colors.LimeGreen);
			}
			else if (!hasArtwork) { DrawLine(point + new Vector2(-7, -7), point + new Vector2(7, 7), Colors.Gray, 2); DrawLine(point + new Vector2(-7, 7), point + new Vector2(7, -7), Colors.Gray, 2); }
		}
		DrawForeground(int.MaxValue, roofs: true);
		foreach (var item in simulation.Items)
		{
			if (item.Location != ItemLocation.Ground || item.Region != simulation.ActiveRegion) continue;
			var point = Project(item.Position);
			DrawRect(new Rect2(point - new Vector2(4, 4), new Vector2(8, 8)), Colors.Cyan);
			DrawString(GetThemeDefaultFont(), point + new Vector2(12, 19), ItemCatalog.Get(item.Definition).Name, fontSize: 14, modulate: Colors.Cyan);
		}

		if (HasFocus()) DrawArc(center, 14, 0, Mathf.Tau, 32, Colors.White, 1, true);
	}
}
