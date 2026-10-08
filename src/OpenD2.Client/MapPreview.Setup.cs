using Godot;
using OpenD2.Assets;

namespace OpenD2.Client;

public partial class MapPreview
{
	private readonly string sceneDirectory;
	private readonly Action<string> openScene;
	private readonly Action<string> editArtwork;
	private readonly Button editCreatedArtwork = new() { Text = "Edit generated artwork", Disabled = true };
	private readonly LineEdit sceneTitle = new() { Text = "Original terrain preview", MaxLength = 80 };
	private readonly SpinBox[] cells = Enumerable.Range(0, 6).Select(i => new SpinBox { MinValue = 0, MaxValue = 4095, Step = 1, Value = i % 2 == 1 ? 1 : i / 2 + 1 }).ToArray();
	private readonly Button createScene = new() { Text = "Validate and create scene", Disabled = true };
	private readonly Button openCreatedScene = new() { Text = "Load generated scene", Disabled = true };
	private readonly Label setupStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private (string Directory, LegacyMapRequest Request)? loadedMap;
	private string createdScene = "";
	private bool busy;
	private int inputRevision;
	private void BuildSetup()
	{
		AddChild(new Label { Text = "Create a one-region playable preview" });
		AddChild(new Label { Text = "1. Check the data directory on the left. Resolve or enter DS1 / DT1 paths and the Act palette, then Load map.\n2. Enable Collision and hover the map to read cell X,Y. Choose three distinct walkable, connected cells.\n3. Create the scene, then load it. Actors use placeholders; use Edit generated artwork to add animations. Audio and campaign rules are not added here.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		AddChild(sceneTitle);
		var row = new HFlowContainer(); AddChild(row);
		for (int i = 0; i < 3; i++)
		{
			row.AddChild(new Label { Text = new[] { "Player X / Y", "Monster X / Y", "Guide X / Y" }[i] });
			row.AddChild(cells[i * 2]); row.AddChild(cells[i * 2 + 1]);
		}
		AddChild(createScene); AddChild(openCreatedScene); AddChild(editCreatedArtwork); AddChild(setupStatus);
		resource.TextChanged += _ => InvalidateMap(); palettePath.TextChanged += _ => InvalidateMap(); tilesets.TextChanged += InvalidateMap;
		createScene.Pressed += () => _ = CreateScene();
		openCreatedScene.Pressed += () => { if (!busy && createdScene.Length > 0) openScene(createdScene); };
		editCreatedArtwork.Pressed += () => { if (!busy && createdScene.Length > 0) editArtwork(createdScene); };
		setupStatus.Text = "Load your map before creating a scene. The startup map is synthetic.";
	}
	private void InvalidateMap() { inputRevision++; loadedMap = null; RefreshSetup(); }
	private void RefreshSetup()
	{
		createScene.Disabled = busy || loadedMap is null;
		openCreatedScene.Disabled = busy || createdScene.Length == 0; editCreatedArtwork.Disabled = openCreatedScene.Disabled;
	}
	private async Task CreateScene()
	{
		if (busy || loadedMap is not { } selected) return;
		Busy(true); setupStatus.Text = "Validating terrain, positions and quest connectivity...";
		try
		{
			if (gameDirectory() != selected.Directory) throw new InvalidDataException("Data directory changed. Load map again before creating a scene.");
			var request = LegacySceneSetup.Create(selected.Request, sceneTitle.Text,
				(int)cells[0].Value, (int)cells[1].Value, (int)cells[2].Value, (int)cells[3].Value, (int)cells[4].Value, (int)cells[5].Value);
			string file = Path.Combine(sceneDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".json");
			var ready = await Task.Run(() => LegacySceneSetup.SaveNew(file, request, path => AssetDecoders.ReadFromInstall(selected.Directory, path)));
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			createdScene = file;
			setupStatus.Text = $"Created: {file}\nConnected preview quest: {ready.QuestLoopReachable}. Actor artwork: {ready.ActorsWithArtwork}/{ready.Actors}. GUI QA: NOT_RUN.\nLoad generated scene uses this saved snapshot; later form edits require Create again. Save settings after loading to remember its path.";
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) setupStatus.Text = "Scene creation failed; current game and previous scene files retained. " + error.Message; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) Busy(false); }
	}
	private void SetupSmoke()
	{
		if (!createScene.Disabled || !openCreatedScene.Disabled) throw new InvalidDataException("Synthetic map exposed original scene setup actions.");
		loadedMap = ("test", new(1, "lod-1.10f", "test.ds1", "data/global/palette/act1/pal.dat", ["test.dt1"]));
		RefreshSetup();
		if (createScene.Disabled) throw new InvalidDataException("Loaded map did not enable scene setup.");
		Busy(true); if (!createScene.Disabled) throw new InvalidDataException("Busy setup allowed duplicate creation.");
		Busy(false); resource.Text = "changed.ds1"; resource.EmitSignal(LineEdit.SignalName.TextChanged, resource.Text);
		if (!createScene.Disabled || loadedMap is not null) throw new InvalidDataException("Edited map paths retained stale setup eligibility.");
		resource.Text = "";
		GD.Print("OPEND2_PLAY08_SETUP_READY");
	}
}
