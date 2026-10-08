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
	private MeshInstance3D model = null!;
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
			Engine.MaxFps = settings.MaxFps;
			dataPath.Text = settings.GameDataPath;
			status.Text = "Offline ready. Asset tools and simulation inspector — no game content loaded.";
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
		var camera = new Camera3D { Position = new Vector3(0, 2, 5), Current = true };
		AddChild(camera); camera.LookAt(Vector3.Zero);
		AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-40, -30, 0) });
		model = new MeshInstance3D
		{
			Mesh = new BoxMesh(), Position = new Vector3(1.8f, 0, 0),
			MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.45f, 0.2f), Roughness = 0.5f }
		};
		AddChild(model);
		var canvas = new CanvasLayer(); AddChild(canvas);
		var panel = new VBoxContainer { Position = new Vector2(32, 32), CustomMinimumSize = new Vector2(550, 0) };
		canvas.AddChild(panel);
		panel.AddChild(new Label { Text = "OPEND2 / FOUNDATION", ThemeTypeVariation = "HeaderLarge" });
		panel.AddChild(new Label { Text = "C# core + Godot 3D | Offline | M2 foundation" });
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
		var previews = new TabContainer { Position = new Vector2(620, 32), Size = new Vector2(500, 640) };
		canvas.AddChild(previews);
		previews.AddChild(new AssetPreview(() => dataPath.Text) { Name = "DC6" });
		previews.AddChild(new AnimationPreview(() => dataPath.Text) { Name = "DCC-COF" });
		var mapScroll = new ScrollContainer { Name = "Map", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		previews.AddChild(mapScroll);
		mapScroll.AddChild(new MapPreview(() => dataPath.Text) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		var simulationScroll = new ScrollContainer { Name = "Simulation", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
		previews.AddChild(simulationScroll);
		simulationScroll.AddChild(new SimulationPreview((name, message) => log?.Write(name, message), paths.Saves, Path.Combine(paths.Root, "npc-models")) { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
		if (OS.GetCmdlineUserArgs().Contains("--smoke-test")) previews.CurrentTab = 3;
		var quit = new Button { Text = "Quit" }; panel.AddChild(quit); quit.Pressed += () => GetTree().Quit();
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
			var next = settings with { GameDataPath = path };
			next.Save(paths.SettingsFile); settings = next;
			status.Text = probe is null ? "Settings saved. No game directory selected."
				: $"Settings saved. {probe.Archives.Count} archives; {probe.MissingArchives.Count} required archives missing. Version compatibility unverified.";
			log?.Write("settings_saved", "Game data directory preference updated");
		}
		catch (Exception error) { status.Text = error.Message; }
	}

	public override void _Process(double delta)
	{
		metrics.Record(delta); elapsed += delta;
		model.RotateY((float)delta * 0.4f);
		if (elapsed < 1) return;
		elapsed = 0;
		performance.Text = $"FPS {Engine.GetFramesPerSecond()} | frame p95 {metrics.P95Milliseconds():F2} ms | managed {GC.GetTotalMemory(false) / 1048576.0:F1} MiB";
	}

	public override void _ExitTree() { log?.Write("shutdown", "Client closed"); log?.Dispose(); }
}
