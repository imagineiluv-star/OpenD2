using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private LegacyPlayScene? legacyScene;
	private readonly Func<string> gameDirectory;
	private readonly Button loadScene = new() { Text = "Load legacy scene JSON" };
	private readonly Button demoScene = new() { Text = "Use synthetic scene" };
	private readonly Button reloadScene = new() { Text = "Load remembered scene", Disabled = true };
	private readonly Label scenePathInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly ConfirmationDialog restartDialog = new() { Exclusive = true, Title = "Replace the current session?", DialogText = "Unsaved session progress will be lost. Existing checkpoint files are kept." };
	private Action? pendingRestart;
	private bool restartWasPaused;
	public string ScenePath { get; private set; } = "";
	public void SetScenePath(string path)
	{
		ScenePath = path; reloadScene.Disabled = verifying || path.Length == 0; menuReloadScene.Disabled = reloadScene.Disabled;
		scenePathInfo.Text = path.Length == 0 ? "No remembered scene. Choose a scene JSON once to reuse it." : "Remembered scene: " + path + "\nSave settings to keep this path. Loading remains an explicit action.";
	}
	private void ConfirmRestart(Action action)
	{
		if (verifying || pendingRestart is not null) return;
		restartWasPaused = paused; SetPaused(true); StopInput(); pendingRestart = action; restartDialog.PopupCentered();
	}
	private readonly Label contentInfo = new() { Text = "Synthetic Camp / Cellar. Original resources are not loaded.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private string ActiveSavePath => legacyScene is null ? savePath : Path.Combine(Path.GetDirectoryName(savePath)!, "legacy-" + legacyScene.ContentId + ".json");
	private WorldDefinition ActiveWorld => legacyScene?.World ?? DemoWorld();
	private IEnumerable<EntityState> ActiveActors => legacyScene?.Actors ?? (IEnumerable<EntityState>)InitialEntities();
	private void ContentSmoke()
	{
		var bytes = MapPreview.SampleData();
		var map = new LegacyMapRequest(1, "lod-1.10f", "sample.ds1", "data/global/palette/act1/pal.dat", ["sample.dt1"]);
		var request = new LegacySceneRequest(1, "Synthetic integration check", [new(1, "Sample map", map)],
			[new(1, 1, 384, 384, true), new(2, 1, 1664, 1664, false)], new(10, 1, 640, 384, "Guide"), [], [2],
			[new(1, map.PalettePath, Enum.GetNames<ActorMotion>().Select(m => new LegacyMotionRequest(m, "sample.dcc", null, new int[8], 10)).ToArray())]);
		request = request with { NpcArtwork = new(10, map.PalettePath, new("Idle", "sample.dcc", null, [0, 0, 0, 0, 1, 1, 1, 1], 10), 4), HudArtwork = SampleHud(), ItemArtwork = SampleItems(), ItemDefinitions = ItemDefinitionSmoke.Request() };
		var content = LegacyPlayScene.Load(request, p => p.EndsWith(".txt") ? ItemDefinitionSmoke.Read(p) : p.EndsWith(".ds1") ? bytes.Ds1 : p.EndsWith(".dt1") ? bytes.Dt1 : p.EndsWith(".dc6") ? AssetPreview.SampleDc6() : p.EndsWith(".dcc") ? Convert.FromHexString(AnimationPreview.SampleDcc) : bytes.Colors);
		SetSceneArt(content); legacyScene = content; NewRun();
		if (simulation.World != content.World || simulation.Collision!.Width != 10 || !view.CheckTerrainTexture() || !view.CheckActorTexture() || !view.CheckNpcTexture() || ActiveSavePath == savePath)
			throw new InvalidDataException("Legacy terrain/session integration smoke failed.");
		HudArtworkSmoke(); ItemArtworkSmoke();
		Submit(CommandKind.SetMove, 1, 0); RunTick();
		if (simulation.GetEntity(Player).Position.X <= 384) throw new InvalidDataException("Legacy terrain movement smoke failed.");
		NavigationSmoke();
		SetSceneArt(null); legacyScene = null; NewRun();
		if (view.CheckNpcTexture()) throw new InvalidDataException("NPC texture survived scene teardown.");
		if (itemTextures is not null || itemSlots.Any(b => b.Icon is not null)) throw new InvalidDataException("Item texture survived scene teardown.");
		if (legacyHud.HasArtwork || !healthBar.Visible) throw new InvalidDataException("HUD teardown did not restore the basic health bar.");
		GD.Print("OPEND2_PLAY10_NPC_READY");
		GD.Print("OPEND2_PLAY02_TERRAIN_READY");
		GD.Print("OPEND2_PLAY03_ACTOR_READY");
	}
	private void BuildContentControls()
	{
		var row = new HFlowContainer(); playPanel.AddChild(row); row.AddChild(loadScene); row.AddChild(reloadScene); row.AddChild(demoScene); playPanel.AddChild(scenePathInfo); playPanel.AddChild(contentInfo);
		SetScenePath(ScenePath); AddChild(restartDialog);
		restartDialog.Confirmed += () => { var action = pendingRestart; pendingRestart = null; SetPaused(restartWasPaused); action?.Invoke(); };
		restartDialog.Canceled += () => { pendingRestart = null; SetPaused(restartWasPaused); };
		var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; Legacy scene request"] };
		AddChild(dialog);
		void ChooseScene() { if (!verifying && pendingRestart is null) dialog.PopupCenteredRatio(0.7f); }
		loadScene.Pressed += ChooseScene; menuChooseScene.Pressed += ChooseScene;
		dialog.FileSelected += file => RequestSceneLoad(file);
		reloadScene.Pressed += () => RequestSceneLoad(ScenePath);
		menuReloadScene.Pressed += () => RequestSceneLoad(ScenePath);
		demoScene.Pressed += () => ConfirmRestart(() =>
		{
			if (verifying) return;
			SetSceneArt(null); legacyScene = null; NewRun(); contentInfo.Text = "Synthetic Camp / Cellar. Original resources are not loaded.";
		});
	}
	public bool RequestSceneLoad(string file)
	{
		if (verifying || pendingRestart is not null || string.IsNullOrWhiteSpace(file)) return false;
		if (sessionStarted) ConfirmRestart(() => _ = LoadLegacyScene(file));
		else _ = LoadLegacyScene(file);
		return true;
	}
	private async Task LoadLegacyScene(string file, Func<string, byte[]>? read = null)
	{
		if (verifying) return;
		bool wasPaused = paused; SetPaused(true); StopInput(); ResetDialogue(); verifying = true;
		loadScene.Disabled = true; reloadScene.Disabled = true; demoScene.Disabled = true; restart.Disabled = true; RefreshMenu();
		if (menuOpen) menuStatus.Text = "Checking original scene resources...";
		contentInfo.Text = "Checking selected resources and gameplay placements...";
		try
		{
			string directory = gameDirectory();
			var checkedScene = await Task.Run(() =>
			{
				var request = LegacySceneRequest.Read(file);
				var scene = read is null ? LegacyPlayScene.Load(directory, request) : LegacyPlayScene.Load(request, read);
				return (Scene: scene, Readiness: PlaySceneReadiness.Check(scene));
			});
			var next = checkedScene.Scene;
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			// Prepare GPU resources before replacing the live session; failure retains old textures/state.
			SetSceneArt(next); legacyScene = next; NewRun();
			SetScenePath(Path.GetFullPath(file));
			if (menuOpen) { SetPaused(true); menuWasPaused = false; }
			contentInfo.Text = $"Legacy terrain: {next.World.Regions.Length} region(s). Content {next.ContentId[..16]}.\nExplicit placements and preview game rules; original campaign compatibility is not validated. Artwork profiles: {next.Artwork.Count}/{next.Actors.Count}; NPC art: {next.NpcArtwork is not null}; HUD art: {next.HudArtwork is not null}; item icons: {next.ItemArtwork?.Icons.Count ?? 0}; item references: {next.ItemDefinitions?.Bindings.Count ?? 0}.";
			contentInfo.Text += $"\nStatic quest loop: {checkedScene.Readiness.QuestLoopReachable}; GUI QA: NOT_RUN. " + string.Join(", ", checkedScene.Readiness.Issues.Take(8));
			contentInfo.Text += $"\nAudio: {next.Audio?.Effects.Count ?? 0} effects, {next.Audio?.Music.Count ?? 0} region tracks. Listening QA: NOT_RUN.";
			if (menuOpen) menuStatus.Text = contentInfo.Text + "\nSelect Continue current session to play, or Load checkpoint for this scene.";
			log("legacy_scene_loaded", $"content={next.ContentId}, regions={next.World.Regions.Length}");
		}
		catch (Exception error)
		{
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			SetPaused(wasPaused); contentInfo.Text = "Scene load failed; current session retained. " + error.Message;
			if (menuOpen) menuStatus.Text = contentInfo.Text;
			log("legacy_scene_failed", error.Message);
		}
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree()) { verifying = false; loadScene.Disabled = false; demoScene.Disabled = false; restart.Disabled = false; SetScenePath(ScenePath); RefreshItems(); RefreshMenu(); }
		}
	}
}

public partial class SimulationCanvas
{
	private LegacyPlayScene? terrainContent;
	private Dictionary<RegionId, ImageTexture[]> terrainTextures = new();
	private Dictionary<RegionId, MapPlacement[]> foregroundWalls = new();
	private int wallCursor;
	private static bool IsForeground(MapScene scene, MapPlacement p) => scene.Map.Layers[p.Layer].Kind != MapLayerKind.Floor &&
		p.Key.Orientation < 16 && scene.Map.Layers[p.Layer].Kind != MapLayerKind.Shadow;
	private static int Depth(MapPlacement p) => (p.X + p.Y + 1) * 1280;
	public void SetTerrain(LegacyPlayScene? content)
	{
		var prepared = new Dictionary<RegionId, ImageTexture[]>(); var owned = new List<ImageTexture>();
		Dictionary<EntityId, ActorSprite>? preparedActors = null; ActorSprite? preparedNpc = null; var preparedWalls = new Dictionary<RegionId, MapPlacement[]>();
		try
		{
			if (content is not null) foreach (var pair in content.Terrain)
			{
				var textures = new List<ImageTexture>();
				foreach (var asset in pair.Value.Scene.Images)
				{
					var alpha = new byte[asset.Frame.Indices.Length];
					for (int i = 0; i < alpha.Length; i++) if (asset.Frame.Indices[i] != 0) alpha[i] = 255;
					using var image = Image.CreateFromData(asset.Frame.Width, asset.Frame.Height, false, Image.Format.Rgba8, pair.Value.Palette.ToRgba(asset.Frame.Indices, alpha));
					var texture = ImageTexture.CreateFromImage(image); textures.Add(texture); owned.Add(texture);
				}
				prepared.Add(pair.Key, textures.ToArray());
				preparedWalls.Add(pair.Key, pair.Value.Scene.Placements.Where(p => IsForeground(pair.Value.Scene, p) && p.Key.Orientation != 15).OrderBy(Depth).ThenBy(p => p.X).ThenBy(p => p.Layer).ToArray());
			}
			preparedActors = PrepareArtwork(content);
			if (content?.NpcArtwork is { } npcArt) preparedNpc = new(npcArt);
		}
		catch { preparedNpc?.Dispose(); foreach (var texture in owned) texture.Dispose(); if (preparedActors is not null) foreach (var sprite in preparedActors.Values) sprite.Dispose(); throw; }
		ClearTerrain(); terrainContent = content; terrainTextures = prepared; actorSprites = preparedActors; npcSprite = preparedNpc; foregroundWalls = preparedWalls; QueueRedraw();
	}
	private static Vector2 Iso(double x, double y) { var p = LegacyProjection.Project(x, y); return new((float)p.X, (float)p.Y); }
	private void DrawTerrain()
	{
		wallCursor = 0;
		if (terrainContent is null || simulation is null) return;
		var scene = terrainContent.Terrain[simulation.ActiveRegion].Scene;
		foreach (var p in scene.Placements) if (!IsForeground(scene, p)) DrawPlacement(p);
	}
	private void DrawForeground(int depth, bool roofs = false)
	{
		if (terrainContent is null || simulation is null) return;
		var walls = foregroundWalls[simulation.ActiveRegion];
		while (wallCursor < walls.Length && Depth(walls[wallCursor]) <= depth) DrawPlacement(walls[wallCursor++]);
		if (roofs) foreach (var p in terrainContent.Terrain[simulation.ActiveRegion].Scene.Placements)
			if (p.Key.Orientation == 15 && IsForeground(terrainContent.Terrain[simulation.ActiveRegion].Scene, p)) DrawPlacement(p);
	}
	private void DrawPlacement(MapPlacement placement)
	{
		if (placement.Image < 0 || simulation is null) return;
		var texture = terrainTextures[simulation.ActiveRegion][placement.Image];
		Vector2 at = Size / 2 - Iso(DisplayPosition.X, DisplayPosition.Y) + new Vector2(placement.PixelX, placement.PixelY);
		if (new Rect2(Vector2.Zero, Size).Intersects(new Rect2(at, texture.GetSize()))) DrawTexture(texture, at);
	}
	private void ClearTerrain()
	{
		foreach (var textures in terrainTextures.Values) foreach (var texture in textures) texture.Dispose();
		terrainTextures.Clear(); foregroundWalls.Clear(); terrainContent = null; ClearArtwork();
	}
	public bool CheckTerrainTexture()
	{
		if (terrainTextures.Count != 1 || terrainTextures.Values.First().Length != 1) return false;
		using var image = terrainTextures.Values.First()[0].GetImage();
		return image.GetWidth() == 160 && image.GetHeight() == 79 && image.GetPixel(78, 0).A > 0.99f;
	}
	public override void _ExitTree() { ClearTerrain(); }
}
