using Godot;
using System.Runtime.InteropServices;
using OpenD2.Core;
using OpenD2.Assets;

namespace OpenD2.Client;

public partial class Main : Node3D
{
	private readonly FrameMetrics metrics = new();
	private readonly AppPaths paths = AppPaths.ForCurrentUser();
	private AppSettings settings = new();
	private SessionLog? log;
	private Label status = null!;
	private Label performance = null!;
	private LineEdit dataPath = null!;
	private SimulationPreview simulation = null!;
	private readonly SpinBox fpsLimit = new() { MinValue = 30, MaxValue = 240, Step = 1, Value = 60 };
	private readonly CheckButton fullscreen = new() { Text = "Fullscreen (F11)" };
	private readonly CheckButton diagnostics = new() { Text = "Show diagnostics" };
	private readonly SpinBox masterVolume = new() { MinValue = 0, MaxValue = 100, Step = 1, Value = 80 };
	private readonly SpinBox effectsVolume = new() { MinValue = 0, MaxValue = 100, Step = 1, Value = 80 };
	private readonly SpinBox musicVolume = new() { MinValue = 0, MaxValue = 100, Step = 1, Value = 50 };
	private readonly CheckButton muted = new() { Text = "Mute audio" };
	private double elapsed;
	private bool settingsLoaded;

	public override void _Ready()
	{
		BuildScene();
		try
		{
			if (OS.HasFeature("editor"))
			{
				string native = OS.GetName() == "Windows" ? "opend2_mpq.dll" : OS.GetName() == "macOS" ? "libopend2_mpq.dylib" : "libopend2_mpq.so";
				string file = ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/" + native);
				NativeLibrary.SetDllImportResolver(typeof(MpqArchive).Assembly, (name, assembly, search) =>
					name == "opend2_mpq" ? NativeLibrary.Load(file) : nint.Zero);
			}
			MpqArchive.VerifyBackend();
			paths.EnsureCreated();
			log = new SessionLog(paths.Logs);
			settings = AppSettings.Load(paths.SettingsFile);
			settingsLoaded = true;
			fpsLimit.Value = settings.MaxFps;
			fullscreen.SetPressedNoSignal(settings.Fullscreen);
			diagnostics.SetPressedNoSignal(settings.ShowDiagnostics);
			masterVolume.SetValueNoSignal(settings.MasterVolume); effectsVolume.SetValueNoSignal(settings.EffectsVolume);
			musicVolume.SetValueNoSignal(settings.MusicVolume); muted.SetPressedNoSignal(settings.Muted);
			ApplyDisplaySettings();
			ApplyAudioSettings();
			simulation.SetScenePath(settings.LastScenePath);
			dataPath.Text = settings.GameDataPath;
			status.Text = "Offline ready. Synthetic Camp / Cellar is playable. Original game data is optional and unverified.";
			log.Write("startup", "M0 offline client ready");
			GD.Print("OPEND2_M0_READY");
		}
		catch (Exception error)
		{
			status.Text = "Startup failed: " + error.Message;
			GD.PushError(error.ToString());
			if (OS.GetCmdlineUserArgs().Contains("--smoke-test")) GetTree().Quit(1);
		}
	}

	private void BuildScene()
	{
		GetWindow().MinSize = new Vector2I(1000, 680);
		var camera = new Camera3D { Position = new Vector3(0, 2, 5), Current = true };
		AddChild(camera); camera.LookAt(Vector3.Zero);
		AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, -30, 0) });
		var canvas = new CanvasLayer(); AddChild(canvas);
		var margin = new MarginContainer(); canvas.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		foreach (var side in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
		var columns = new HBoxContainer(); margin.AddChild(columns); columns.AddThemeConstantOverride("separation", 16);
		var settingsScroll = new ScrollContainer { CustomMinimumSize = new Vector2(280, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		columns.AddChild(settingsScroll);
		var panel = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		settingsScroll.AddChild(panel);
		panel.AddChild(new Label { Text = "OPEND2", ThemeTypeVariation = "HeaderLarge" });
		panel.AddChild(new Label { Text = "Offline play preview" });
		panel.AddChild(new Label { Text = "FPS limit (30–240)" }); panel.AddChild(fpsLimit);
		panel.AddChild(fullscreen); panel.AddChild(diagnostics);
		foreach (var (name, slider) in new[] { ("Master volume (%)", masterVolume), ("Effects volume (%)", effectsVolume), ("Music volume (%)", musicVolume) })
		{ panel.AddChild(new Label { Text = name }); panel.AddChild(slider); slider.ValueChanged += _ => ApplyAudioSettings(); }
		panel.AddChild(muted); muted.Toggled += _ => ApplyAudioSettings();
		fpsLimit.ValueChanged += _ => ApplyDisplaySettings();
		fullscreen.Toggled += _ => ApplyDisplaySettings(); diagnostics.Toggled += _ => ApplyDisplaySettings();
		panel.AddChild(new Label { Text = "Original LoD game data directory" });
		dataPath = new LineEdit { PlaceholderText = "Select your original game directory" }; panel.AddChild(dataPath);
		var browse = new Button { Text = "Choose directory" }; panel.AddChild(browse);
		var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem };
		canvas.AddChild(dialog); dialog.DirSelected += path => dataPath.Text = path;
		browse.Pressed += () => dialog.PopupCenteredRatio(0.7f);
		var save = new Button { Text = "Save settings" }; panel.AddChild(save);
		save.Pressed += SaveSettings;
		status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; panel.AddChild(status);
		performance = new Label(); panel.AddChild(performance);
		panel.AddChild(new Label { Text = "User data: " + paths.Root, AutowrapMode = TextServer.AutowrapMode.WordSmart });
		var previews = new TabContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		columns.AddChild(previews);
		var simulationScroll = new ScrollContainer { Name = "Simulation", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		previews.AddChild(simulationScroll);
		simulation = new SimulationPreview((name, message) => log?.Write(name, message), paths.Saves, Path.Combine(paths.Root, "npc-models"), () => dataPath.Text) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		simulationScroll.AddChild(simulation);
		previews.AddChild(new AssetPreview(() => dataPath.Text) { Name = "DC6" });
		previews.AddChild(new AnimationPreview(() => dataPath.Text) { Name = "DCC-COF" });
		var mapScroll = new ScrollContainer { Name = "Map", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		previews.AddChild(mapScroll);
		mapScroll.AddChild(new MapPreview(() => dataPath.Text) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		previews.CurrentTab = 0;
		var quit = new Button { Text = "Quit" }; panel.AddChild(quit); quit.Pressed += () => GetTree().Quit();
	}
	private void ApplyDisplaySettings()
	{
		Engine.MaxFps = (int)fpsLimit.Value;
		if (DisplayServer.GetName() != "headless") GetWindow().Mode = fullscreen.ButtonPressed ? Window.ModeEnum.Fullscreen : Window.ModeEnum.Windowed;
		if (simulation is not null) simulation.SetDiagnosticsVisible(diagnostics.ButtonPressed);
		if (performance is not null) performance.Visible = diagnostics.ButtonPressed;
	}
	private void ApplyAudioSettings() => simulation?.SetAudioVolume((int)masterVolume.Value, (int)effectsVolume.Value, (int)musicVolume.Value, muted.ButtonPressed);
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F11 })
		{ fullscreen.ButtonPressed = !fullscreen.ButtonPressed; GetViewport().SetInputAsHandled(); }
	}

	private void SaveSettings()
	{
		if (!settingsLoaded)
		{
			status.Text = "Settings could not be loaded. Back up and repair the settings file before saving.";
			return;
		}
		try
		{
			var path = string.IsNullOrWhiteSpace(dataPath.Text) ? "" : DataDirectory.Validate(dataPath.Text);
			var probe = path.Length == 0 ? null : GameInstall.Probe(path);
			var next = settings with { GameDataPath = path, MaxFps = (int)fpsLimit.Value,
				Fullscreen = fullscreen.ButtonPressed, ShowDiagnostics = diagnostics.ButtonPressed,
				MasterVolume = (int)masterVolume.Value, EffectsVolume = (int)effectsVolume.Value, MusicVolume = (int)musicVolume.Value, Muted = muted.ButtonPressed, LastScenePath = simulation.ScenePath };
			next.Save(paths.SettingsFile); settings = next;
			status.Text = probe is null ? "Settings saved. No game directory selected."
				: $"Settings saved. {probe.Archives.Count} archives; {probe.MissingArchives.Count} required archives missing. Version compatibility unverified.";
			log?.Write("settings_saved", "Game data and display preferences updated");
		}
		catch (Exception error) { status.Text = error.Message; }
	}

	public override void _Process(double delta)
	{
		metrics.Record(delta); elapsed += delta;
		if (elapsed < 1) return;
		elapsed = 0;
		performance.Text = $"FPS {Engine.GetFramesPerSecond()} | frame p95 {metrics.P95Milliseconds():F2} ms | managed {GC.GetTotalMemory(false) / 1048576.0:F1} MiB";
	}

	public override void _ExitTree() { log?.Write("shutdown", "Client closed"); log?.Dispose(); }
}
