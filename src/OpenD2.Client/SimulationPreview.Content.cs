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
	private readonly Label contentInfo = new() { Text = "Synthetic Camp / Cellar. Original resources are not loaded.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private string ActiveSavePath => legacyScene is null ? savePath : Path.Combine(Path.GetDirectoryName(savePath)!, "legacy-" + legacyScene.ContentId + ".json");
	private WorldDefinition ActiveWorld => legacyScene?.World ?? DemoWorld();
	private IEnumerable<EntityState> ActiveActors => legacyScene?.Actors ?? (IEnumerable<EntityState>)InitialEntities();
	private void ContentSmoke()
	{
		var bytes = MapPreview.SampleData();
		var map = new LegacyMapRequest(1, "lod-1.10f", "sample.ds1", "data/global/palette/act1/pal.dat", ["sample.dt1"]);
		var request = new LegacySceneRequest(1, "Synthetic integration check", [new(1, "Sample map", map)],
			[new(1, 1, 384, 384, true), new(2, 1, 1664, 1664, false)], new(10, 1, 640, 384, "Guide"), [], [2]);
		var content = LegacyPlayScene.Load(request, p => p.EndsWith(".ds1") ? bytes.Ds1 : p.EndsWith(".dt1") ? bytes.Dt1 : bytes.Colors);
		view.SetTerrain(content); legacyScene = content; NewRun();
		if (simulation.World != content.World || simulation.Collision!.Width != 10 || !view.CheckTerrainTexture() || ActiveSavePath == savePath)
			throw new InvalidDataException("Legacy terrain/session integration smoke failed.");
		Submit(CommandKind.SetMove, 1, 0); RunTick();
		if (simulation.GetEntity(Player).Position.X <= 384) throw new InvalidDataException("Legacy terrain movement smoke failed.");
		view.SetTerrain(null); legacyScene = null; NewRun();
		GD.Print("OPEND2_PLAY02_TERRAIN_READY");
	}
	private void BuildContentControls()
	{
		var row = new HFlowContainer(); AddChild(row); row.AddChild(loadScene); row.AddChild(demoScene); AddChild(contentInfo);
		var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; Legacy scene request"] };
		AddChild(dialog); loadScene.Pressed += () => { if (!verifying) dialog.PopupCenteredRatio(0.7f); };
		dialog.FileSelected += LoadLegacyScene;
		demoScene.Pressed += () =>
		{
			if (verifying) return;
			view.SetTerrain(null); legacyScene = null; NewRun(); contentInfo.Text = "Synthetic Camp / Cellar. Original resources are not loaded.";
		};
	}
	private async void LoadLegacyScene(string file)
	{
		if (verifying) return;
		bool wasPaused = paused; SetPaused(true); StopInput(); ResetDialogue(); verifying = true;
		loadScene.Disabled = true; demoScene.Disabled = true; restart.Disabled = true;
		contentInfo.Text = "Checking selected resources and gameplay placements...";
		try
		{
			string directory = gameDirectory();
			var next = await Task.Run(() => LegacyPlayScene.Load(directory, LegacySceneRequest.Read(file)));
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			// Prepare GPU resources before replacing the live session; failure retains old textures/state.
			view.SetTerrain(next); legacyScene = next; NewRun();
			contentInfo.Text = $"Legacy terrain: {next.World.Regions.Length} region(s). Content {next.ContentId[..16]}.\nExplicit placements and preview game rules; original campaign compatibility and actor artwork are not validated.";
			log("legacy_scene_loaded", $"content={next.ContentId}, regions={next.World.Regions.Length}");
		}
		catch (Exception error)
		{
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			SetPaused(wasPaused); contentInfo.Text = "Scene load failed; current session retained. " + error.Message;
			log("legacy_scene_failed", error.Message);
		}
		finally
		{
			if (IsInstanceValid(this) && IsInsideTree()) { verifying = false; loadScene.Disabled = false; demoScene.Disabled = false; restart.Disabled = false; }
		}
	}
}

public partial class SimulationCanvas
{
	private LegacyPlayScene? terrainContent;
	private Dictionary<RegionId, ImageTexture[]> terrainTextures = new();
	public void SetTerrain(LegacyPlayScene? content)
	{
		var prepared = new Dictionary<RegionId, ImageTexture[]>(); var owned = new List<ImageTexture>();
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
			}
		}
		catch { foreach (var texture in owned) texture.Dispose(); throw; }
		ClearTerrain(); terrainContent = content; terrainTextures = prepared; QueueRedraw();
	}
	private static Vector2 Iso(double x, double y) { var p = LegacyProjection.Project(x, y); return new((float)p.X, (float)p.Y); }
	private void DrawTerrain()
	{
		if (terrainContent is null || simulation is null) return;
		var asset = terrainContent.Terrain[simulation.ActiveRegion]; var textures = terrainTextures[simulation.ActiveRegion];
		Vector2 offset = Size / 2 - Iso(DisplayPosition.X, DisplayPosition.Y); var viewport = new Rect2(Vector2.Zero, Size);
		foreach (var placement in asset.Scene.Placements)
		{
			if (placement.Image < 0) continue; // Preflight disallows missing referenced tiles.
			var texture = textures[placement.Image]; var at = offset + new Vector2(placement.PixelX, placement.PixelY);
			if (viewport.Intersects(new Rect2(at, texture.GetSize()))) DrawTexture(texture, at);
		}
	}
	private void ClearTerrain()
	{
		foreach (var textures in terrainTextures.Values) foreach (var texture in textures) texture.Dispose();
		terrainTextures.Clear(); terrainContent = null;
	}
	public bool CheckTerrainTexture()
	{
		if (terrainTextures.Count != 1 || terrainTextures.Values.First().Length != 1) return false;
		using var image = terrainTextures.Values.First()[0].GetImage();
		return image.GetWidth() == 160 && image.GetHeight() == 79 && image.GetPixel(78, 0).A > 0.99f;
	}
	public override void _ExitTree() { ClearTerrain(); }
}
