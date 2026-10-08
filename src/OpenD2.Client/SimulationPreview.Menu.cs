using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly VBoxContainer playPanel = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly VBoxContainer menuPanel = new() { Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly Button menuButton = new() { Text = "Menu (Esc)" };
	private readonly Button menuNew = new() { Text = "New game" };
	private readonly Button menuContinue = new() { Text = "Continue current session" };
	private readonly Button menuLoad = new() { Text = "Load checkpoint" };
	private readonly Label menuInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label menuStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private bool menuOpen, menuWasPaused, sessionStarted;

	private void BuildMenu()
	{
		AddChild(menuPanel);
		menuPanel.AddChild(new Label { Text = "OPEND2 — Offline play" });
		menuPanel.AddChild(menuInfo);
		foreach (var button in new[] { menuNew, menuContinue, menuLoad }) menuPanel.AddChild(button);
		menuPanel.AddChild(menuStatus);
		menuPanel.AddChild(new Label
		{
			Text = "The game is stopped while this menu is open. Settings remain available on the left.\nNew game uses the current scene and seed; checkpoint files are kept. Original scenes can be selected from the play screen.\nThis preview has one player and one quest loop. Character selection and skills are not available yet.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart
		});
		menuButton.Pressed += () => OpenMenu();
		menuNew.Pressed += RequestMenuNew;
		menuContinue.Pressed += ContinueMenuSession;
		menuLoad.Pressed += () =>
		{
			string file = ActiveSavePath;
			if (sessionStarted) ConfirmRestart(() => _ = LoadMenuCheckpoint(file));
			else _ = LoadMenuCheckpoint(file);
		};
	}
	private void OpenMenu(bool initial = false)
	{
		if (verifying || pendingRestart is not null || menuOpen) return;
		menuWasPaused = paused;
		// Queue the neutral input before rejecting gameplay commands from the menu.
		SetPaused(true); StopInput(); ResetDialogue(); menuOpen = true;
		if (initial) sessionStarted = false;
		playPanel.Hide(); menuPanel.Show();
		menuStatus.Text = initial ? "Choose New game or load an existing checkpoint." : "Session paused. Unsaved progress stays in memory until you quit.";
		RefreshMenu(); menuNew.GrabFocus();
	}
	private void RefreshMenu()
	{
		menuNew.Disabled = verifying;
		menuContinue.Disabled = verifying || !sessionStarted;
		menuContinue.Text = menuWasPaused ? "Return to paused session" : "Continue current session";
		bool exists = File.Exists(ActiveSavePath) || File.Exists(ActiveSavePath + ".bak");
		menuLoad.Disabled = verifying || !exists;
		menuInfo.Text = (legacyScene is null ? "Synthetic Camp / Cellar" : "Loaded original scene (preview rules)") +
			$" · seed {(uint)seedInput.Value}\nCheckpoint: {Path.GetFileName(ActiveSavePath)}\n" +
			(exists ? "Checkpoint or backup found. Compatibility is checked when loaded." : "No checkpoint or backup for this scene yet.");
	}
	private void CloseMenu(bool remainPaused)
	{
		menuOpen = false; menuPanel.Hide(); playPanel.Show(); SetPaused(remainPaused);
		ShowFrame(); Refresh(); view.GrabFocus();
	}
	private void ContinueMenuSession()
	{
		if (!menuOpen || verifying || pendingRestart is not null || !sessionStarted) return;
		CloseMenu(menuWasPaused);
	}
	private void RequestMenuNew()
	{
		if (!menuOpen || verifying || pendingRestart is not null) return;
		if (sessionStarted) ConfirmRestart(StartMenuGame);
		else StartMenuGame();
	}
	private void StartMenuGame()
	{
		NewRun(); CloseMenu(remainPaused: false);
	}
	private async Task LoadMenuCheckpoint(string file)
	{
		if (!menuOpen || verifying || pendingRestart is not null) return;
		menuStatus.Text = "Loading checkpoint...";
		bool loaded = await CheckpointFile(true, file);
		if (!IsInstanceValid(this) || !IsInsideTree()) return;
		if (loaded) menuWasPaused = false;
		menuStatus.Text = status.Text + (loaded ? " Select Continue current session when ready." : " Current session retained.");
		RefreshMenu();
		if (loaded) menuContinue.GrabFocus();
	}
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!IsVisibleInTree() || verifying || pendingRestart is not null) return;
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
		{
			if (menuOpen) ContinueMenuSession(); else OpenMenu();
			GetViewport().SetInputAsHandled();
		}
	}

	private async Task MenuSmoke()
	{
		string folder = Path.Combine(Path.GetTempPath(), "opend2-menu-smoke-" + Guid.NewGuid().ToString("N"));
		try
		{
			if (!menuOpen || !paused || !menuContinue.Disabled || playPanel.Visible)
				throw new InvalidDataException("Startup menu must stop the game and require a session choice.");
			_Process(3);
			if (simulation.Tick != 0) throw new InvalidDataException("Startup menu advanced the simulation.");
			RequestMenuNew(); Submit(CommandKind.SetMove, 1); requestedX = 1; RunTick();
			OpenMenu(); string retained = simulation.ComputeStateHash(); long tick = simulation.Tick;
			_Process(3);
			if (simulation.ComputeStateHash() != retained || Submit(CommandKind.Signal) || !paused || playPanel.Visible)
				throw new InvalidDataException("Menu input or elapsed time changed the session.");
			RequestMenuNew(); restartDialog.Hide(); restartDialog.EmitSignal(ConfirmationDialog.SignalName.Canceled);
			if (!menuOpen || !paused || simulation.ComputeStateHash() != retained || pendingRestart is not null)
				throw new InvalidDataException("Cancelled new game discarded current progress.");
			ContinueMenuSession(); _Process(0.04);
			if (menuOpen || paused || simulation.Tick != tick + 1 || current.MoveX != 0)
				throw new InvalidDataException($"Continue failed: open={menuOpen}, paused={paused}, tick={simulation.Tick} (expected {tick + 1}), move={current.MoveX}.");
			SetPaused(true); OpenMenu(); ContinueMenuSession();
			if (!paused) throw new InvalidDataException("Menu lost the previous pause state.");
			string file = Path.Combine(folder, "checkpoint.json");
			var savedSnapshot = simulation.CaptureSnapshot();
			GameSave.Save(file, savedSnapshot); string saved = simulation.ComputeStateHash();
			RunTick(); OpenMenu();
			var loading = LoadMenuCheckpoint(file);
			if (!verifying || !menuNew.Disabled || !menuContinue.Disabled || !menuLoad.Disabled)
				throw new InvalidDataException("Menu actions stayed enabled during checkpoint I/O.");
			ContinueMenuSession(); RequestMenuNew(); await loading;
			if (!menuOpen || !paused || menuContinue.Disabled || simulation.ComputeStateHash() != saved)
				throw new InvalidDataException("Menu checkpoint restore did not preserve the loaded state for review.");
			ContinueMenuSession(); RunTick(); OpenMenu(); retained = simulation.ComputeStateHash();
			File.WriteAllText(file, "{");
			await LoadMenuCheckpoint(file);
			if (!menuOpen || !paused || simulation.ComputeStateHash() != retained || !menuStatus.Text.Contains("failed"))
				throw new InvalidDataException("Failed menu load replaced the current session.");
			GameSave.Save(file + ".bak", savedSnapshot);
			await LoadMenuCheckpoint(file);
			if (!menuOpen || !paused || simulation.ComputeStateHash() != saved || !menuStatus.Text.Contains("Recovered"))
				throw new InvalidDataException("Menu backup recovery did not remain paused for review.");
			RequestMenuNew(); restartDialog.Hide(); restartDialog.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
			if (menuOpen || paused || simulation.Tick != 0 || File.ReadAllText(file) != "{" || !File.Exists(file + ".bak"))
				throw new InvalidDataException("Confirmed new game did not reset the session or altered checkpoint files.");
			GD.Print("OPEND2_M206_MENU_READY");
			npcSmokePending = true; dialogueInput.Text = "안녕"; SendDialogue();
		}
		catch (Exception error)
		{
			GD.PushError("Menu smoke failed: " + error); GetTree().Quit(1);
		}
		finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
	}
}
