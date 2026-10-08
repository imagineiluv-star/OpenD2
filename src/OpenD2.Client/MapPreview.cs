using Godot;
using OpenD2.Assets;
using System.Buffers.Binary;

namespace OpenD2.Client;

public partial class MapPreview : VBoxContainer
{
	private readonly Func<string> gameDirectory;
	private readonly LineEdit resource = new() { PlaceholderText = "MPQ path to map.ds1" };
	private readonly LineEdit palettePath = new() { Text = "data/global/palette/act1/pal.dat" };
	private readonly TextEdit tilesets = new() { PlaceholderText = "DT1 MPQ paths, one per line (first matching tile wins)", CustomMinimumSize = new Vector2(480, 65) };
	private readonly Button load = new() { Text = "Load map" };
	private readonly Button resolve = new() { Text = "Resolve table paths" };
	private readonly OptionButton tableMode = new();
	private readonly SpinBox levelId = new() { MinValue = 0, MaxValue = 65535, Value = 1 };
	private readonly SpinBox presetId = new() { MinValue = 0, MaxValue = 65535, Value = 1 };
	private readonly SpinBox fileSlot = new() { MinValue = 1, MaxValue = 6, Value = 1 };
	private readonly TileFrameCache cache = new();
	private readonly Button clearCache = new() { Text = "Clear tile cache" };
	private readonly HFlowContainer layers = new();
	private readonly Label status = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Label hover = new();
	private readonly MapCanvas view = new() { CustomMinimumSize = new Vector2(480, 260), SizeFlagsVertical = SizeFlags.ExpandFill };
	public MapPreview(Func<string> gameDirectory) { this.gameDirectory = gameDirectory; }
	public override void _Ready()
	{
		AddChild(new Label { Text = "DT1 / DS1 map preview" });
		var tableControls = new HFlowContainer(); AddChild(tableControls);
		tableMode.AddItem("TXT map tables"); tableMode.AddItem("BIN map tables (1.10f only)"); tableControls.AddChild(tableMode);
		foreach (var input in new[] { ("Level ID", levelId), ("Preset Def", presetId), ("File slot", fileSlot) })
		{ tableControls.AddChild(new Label { Text = input.Item1 }); tableControls.AddChild(input.Item2); }
		tableControls.AddChild(resolve); resolve.Pressed += ResolvePaths;
		AddChild(resource); AddChild(palettePath); AddChild(tilesets);
		AddChild(load); load.Pressed += LoadMap;
		var controls = new HBoxContainer(); AddChild(controls);
		var collision = new CheckButton { Text = "Collision" }; controls.AddChild(collision);
		collision.Toggled += on => { view.ShowCollision = on; view.QueueRedraw(); };
		var objects = new CheckButton { Text = "Objects" }; controls.AddChild(objects);
		objects.Toggled += on => { view.ShowObjects = on; view.QueueRedraw(); };
		var reset = new Button { Text = "Reset view" }; controls.AddChild(reset); reset.Pressed += view.ResetView;
		controls.AddChild(clearCache); clearCache.Pressed += () => { cache.Clear(); status.Text = "Tile cache cleared; active preview retained."; };
		AddChild(layers); AddChild(view); AddChild(hover); AddChild(status);
		view.Hovered += text => hover.Text = text;
		LoadSample();
	}
	private void Busy(bool value) { load.Disabled = value; resolve.Disabled = value; clearCache.Disabled = value; }
	private async void ResolvePaths()
	{
		Busy(true); status.Text = "Reading map tables and checking references...";
		try
		{
			string directory = gameDirectory(); int level = (int)levelId.Value, preset = (int)presetId.Value, slot = (int)fileSlot.Value - 1;
			var mode = tableMode.Selected == 0 ? MapTableMode.Txt : MapTableMode.Bin110f;
			var result = await Task.Run(() =>
			{
				var tables = MapTables.Load(p => AssetDecoders.ReadFromInstall(directory, p), mode);
				return (plan: tables.Resolve(level, preset, slot), issues: tables.Validate());
			});
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			resource.Text = result.plan.MapPath; tilesets.Text = string.Join('\n', result.plan.Tilesets);
			status.Text = $"Resolved {result.plan.Tilesets.Count} DT1 slots. Table errors: {result.issues.Count(i => i.IsError)}; context notices: {result.issues.Count(i => !i.IsError)}. Check the Act palette, then Load map. Version and file existence are not yet verified.";
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Table resolution failed; previous paths retained. " + error.Message; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) Busy(false); }
	}
	private async void LoadMap()
	{
		Busy(true); status.Text = "Reading and decoding map...";
		try
		{
			string directory = gameDirectory(), path = resource.Text, colors = palettePath.Text, sources = tilesets.Text;
			var loaded = await Task.Run(() =>
			{
				var request = new LegacyMapRequest(1, "lod-1.10f", path, colors,
					sources.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				return LegacyMapAsset.Load(directory, request, cache);
			});
			if (!IsInstanceValid(this) || !IsInsideTree()) { cache.Clear(); return; }
			ShowMap(loaded.Scene, loaded.Palette, path);
			status.Text += $"\nSelected sources checked: {loaded.Check.Sources.Count}; version/gameplay compatibility unverified.";
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Load failed; previous preview retained. " + error.Message; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) Busy(false); else cache.Clear(); }
	}
	private void ShowMap(MapScene scene, Palette palette, string name)
	{
		view.SetMap(scene, palette);
		foreach (Node child in layers.GetChildren()) { layers.RemoveChild(child); child.QueueFree(); }
		for (int i = 0; i < scene.Map.Layers.Count; i++)
		{
			int layerIndex = i; var layer = scene.Map.Layers[i]; if (layer.Kind == MapLayerKind.Tag) continue;
			var check = new CheckBox { Text = $"{layer.Kind} {layer.Index + 1}", ButtonPressed = true }; layers.AddChild(check);
			check.Toggled += on => { view.VisibleLayers[layerIndex] = on; view.QueueRedraw(); };
		}
		status.Text = $"{name}\n{scene.Map.Width} x {scene.Map.Height}, DS1 v{scene.Map.Version}, act {scene.Map.Act}; {scene.Images.Count} shared tiles; {scene.MissingTiles} missing; {scene.DuplicateKeys} duplicate keys.\nDrag to pan / wheel to zoom. Red: walk blocked, amber: other flags, magenta: unknown. Preview uses first tile variant; no gameplay or PL2 blending.";
		var stats = cache.Stats;
		status.Text += $"\nTile cache: {stats.RetainedBytes / 1024} / {stats.BudgetBytes / 1024} KiB, {stats.Hits} hits, {stats.Misses} misses, {stats.Evictions} evictions (active scene/GPU memory separate).";
	}
	public override void _ExitTree() { cache.Clear(); }
	internal static (byte[] Dt1, byte[] Ds1, byte[] Colors) SampleData()
	{
		// Own synthetic 2 x 2 floor map and a complete 25-block diamond; no game bytes.
		byte[] dt1 = new byte[276 + 96 + 25 * 20 + 25 * 256];
		void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dt1.AsSpan(offset), value);
		U32(0, 7); U32(4, 6); U32(268, 1); U32(272, 276); U32(276, 3);
		U32(284, unchecked((uint)-80)); U32(288, 160); U32(300, 1); U32(308, 1);
		U32(348, 372); U32(352, (uint)(dt1.Length - 372)); U32(356, 25); dt1[316 + 20] = 1;
		for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++)
		{
			int b = y * 5 + x, h = 372 + b * 20;
			BinaryPrimitives.WriteInt16LittleEndian(dt1.AsSpan(h), (short)(64 + (x - y) * 16));
			BinaryPrimitives.WriteInt16LittleEndian(dt1.AsSpan(h + 2), (short)((x + y) * 8));
			BinaryPrimitives.WriteUInt16LittleEndian(dt1.AsSpan(h + 8), 1); U32(h + 10, 256); U32(h + 16, (uint)(500 + b * 256));
			dt1.AsSpan(872 + b * 256, 256).Fill((byte)(1 + (x + y) % 2));
		}
		byte[] ds1 = new byte[72];
		void D32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(ds1.AsSpan(offset), value);
		D32(0, 18); D32(4, 1); D32(8, 1); D32(28, 1);
		for (int i = 0; i < 4; i++) D32(32 + i * 4, 0x00100001);
		byte[] colors = new byte[768]; colors[3] = 80; colors[4] = 150; colors[5] = 90; colors[6] = 55; colors[7] = 105; colors[8] = 60;
		return (dt1, ds1, colors);
	}
	private void LoadSample()
	{
		var (dt1, ds1, colors) = SampleData();
		var tableFiles = new Dictionary<string, string>
		{
			["levels.txt"] = "Id\tLevelType\n1\t0\n",
			["lvltypes.txt"] = string.Join('\t', Enumerable.Range(1, 32).Select(i => $"File {i}")) + "\nsynthetic.dt1\n",
			["lvlprest.txt"] = "Def\tLevelId\tFiles\tDt1Mask\tFile1\tFile2\tFile3\tFile4\tFile5\tFile6\n1\t1\t1\t1\tsynthetic.ds1\n"
		};
		var tables = MapTables.Load(p => System.Text.Encoding.Latin1.GetBytes(tableFiles[p.Split('\\')[^1]]), MapTableMode.Txt);
		var plan = tables.Resolve(1, 1);
		MapTileset[] sets = [new(plan.Tilesets[0], Dt1Tileset.Parse(dt1))];
		var scene = MapScene.Build(Ds1Map.Parse(ds1), sets, cache);
		MapScene.Build(Ds1Map.Parse(ds1), sets, cache);
		ShowMap(scene, Palette.Parse(colors), "Synthetic map (not game content)");
		if (scene.Placements.Count != 4 || !scene.CollisionAt(0, 0).BlocksWalk || !view.CheckSampleTexture()) throw new InvalidDataException("Map preview smoke failed.");
		GD.Print("OPEND2_M106_MAP_READY");
		if (tables.Validate().Count != 0 || cache.Stats.Hits != 1 || plan.MapPath != "data\\global\\tiles\\synthetic.ds1") throw new InvalidDataException("Map table/cache smoke failed.");
		GD.Print("OPEND2_M107_TABLE_CACHE_READY");
	}
}

public partial class MapCanvas : Control
{
	private MapScene? scene;
	private ImageTexture[] textures = [];
	private Vector2 pan = new(240, 25);
	private float zoom = 0.75f;
	public bool[] VisibleLayers { get; private set; } = [];
	public bool ShowCollision { get; set; }
	public bool ShowObjects { get; set; }
	public event Action<string>? Hovered;
	public MapCanvas() { ClipContents = true; MouseFilter = MouseFilterEnum.Stop; TextureFilter = TextureFilterEnum.Nearest; }
	public void SetMap(MapScene next, Palette palette)
	{
		var prepared = new List<ImageTexture>();
		try
		{
			foreach (var asset in next.Images)
			{
				var opacity = new byte[asset.Frame.Indices.Length];
				for (int i = 0; i < opacity.Length; i++) opacity[i] = asset.Frame.Indices[i] == 0 ? (byte)0 : (byte)255;
				using var image = Image.CreateFromData(asset.Frame.Width, asset.Frame.Height, false, Image.Format.Rgba8, palette.ToRgba(asset.Frame.Indices, opacity));
				prepared.Add(ImageTexture.CreateFromImage(image));
			}
		}
		catch { foreach (var texture in prepared) texture.Dispose(); throw; }
		ClearTextures(); textures = prepared.ToArray(); scene = next; VisibleLayers = Enumerable.Repeat(true, next.Map.Layers.Count).ToArray(); ResetView();
	}
	public void ResetView() { zoom = 0.75f; pan = new(Math.Max(Size.X, 480) / 2, 25); QueueRedraw(); }
	public bool CheckSampleTexture()
	{
		if (textures.Length != 1) return false;
		using var image = textures[0].GetImage(); return image.GetWidth() == 160 && image.GetHeight() == 79 && image.GetPixel(78, 0).A > 0.99f;
	}
	public override void _GuiInput(InputEvent input)
	{
		if (input is InputEventMouseButton { Pressed: true } button && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
		{
			float next = Math.Clamp(zoom * (button.ButtonIndex == MouseButton.WheelUp ? 1.25f : 0.8f), 0.25f, 3f);
			pan = button.Position - (button.Position - pan) * (next / zoom); zoom = next; QueueRedraw(); AcceptEvent();
		}
		if (input is InputEventMouseMotion motion)
		{
			if ((motion.ButtonMask & (MouseButtonMask.Left | MouseButtonMask.Middle)) != 0) { pan += motion.Relative; QueueRedraw(); }
			if (scene is null) return;
			Vector2 p = (motion.Position - pan) / zoom;
			int sx = (int)Math.Floor((p.X / 160 + p.Y / 80) * 5), sy = (int)Math.Floor((p.Y / 80 - p.X / 160) * 5);
			if (sx < 0 || sy < 0 || sx >= scene.Map.Width * 5 || sy >= scene.Map.Height * 5) { Hovered?.Invoke(""); return; }
			var c = scene.CollisionAt(sx, sy); Hovered?.Invoke($"Tile {sx / 5},{sy / 5} / subtile {sx % 5},{sy % 5}: flags 0x{c.Flags:X2}, {(c.Known ? "known" : "unknown")}");
		}
	}
	public override void _Draw()
	{
		DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.06f, 0.07f, 0.08f)); if (scene is null) return;
		var viewport = new Rect2(-pan / zoom, Size / zoom); DrawSetTransform(pan, 0, Vector2.One * zoom);
		foreach (var p in scene.Placements)
		{
			if (!VisibleLayers[p.Layer]) continue;
			if (p.Image < 0)
			{
				Vector2 center = Project(p.X + 0.5f, p.Y + 0.5f); if (viewport.Grow(80).HasPoint(center)) Diamond(center, 80, 40, new Color(0.9f, 0.1f, 0.8f, 0.35f));
				continue;
			}
			var texture = textures[p.Image]; var rect = new Rect2(p.PixelX, p.PixelY, texture.GetWidth(), texture.GetHeight());
			if (viewport.Intersects(rect)) DrawTexture(texture, rect.Position);
		}
		if (ShowCollision)
		{
			// Bound overlay work to a conservative cell range around the visible viewport.
			Vector2[] corners = [viewport.Position, viewport.End, new(viewport.End.X, viewport.Position.Y), new(viewport.Position.X, viewport.End.Y)];
			float MinX(Vector2 p) => p.X / 160 + p.Y / 80;
			float MinY(Vector2 p) => p.Y / 80 - p.X / 160;
			int x0 = Math.Max(0, (int)Math.Floor(corners.Min(MinX)) - 1), x1 = Math.Min(scene.Map.Width, (int)Math.Ceiling(corners.Max(MinX)) + 1);
			int y0 = Math.Max(0, (int)Math.Floor(corners.Min(MinY)) - 1), y1 = Math.Min(scene.Map.Height, (int)Math.Ceiling(corners.Max(MinY)) + 1);
			for (int y = y0 * 5; y < y1 * 5; y++) for (int x = x0 * 5; x < x1 * 5; x++)
			{
				var c = scene.CollisionAt(x, y); if (c.Known && c.Flags == 0) continue;
				Vector2 center = Project((x + 0.5f) / 5, (y + 0.5f) / 5); if (!viewport.Grow(16).HasPoint(center)) continue;
				Diamond(center, 16, 8, !c.Known ? new Color(0.9f, 0.1f, 0.8f, 0.45f) : c.BlocksWalk ? new Color(1, 0.1f, 0.1f, 0.5f) : new Color(1, 0.7f, 0.1f, 0.5f));
			}
		}
		if (ShowObjects) foreach (var obj in scene.Map.Objects)
		{
			Vector2 point = Project(obj.X / 5f, obj.Y / 5f); if (viewport.Grow(4).HasPoint(point)) DrawCircle(point, 4, Colors.Cyan);
		}
		DrawSetTransform(Vector2.Zero);
	}
	private static Vector2 Project(float x, float y) => new((x - y) * 80, (x + y) * 40);
	private void Diamond(Vector2 center, float w, float h, Color color) => DrawColoredPolygon([center + new Vector2(0, -h), center + new Vector2(w, 0), center + new Vector2(0, h), center + new Vector2(-w, 0)], color);
	private void ClearTextures() { foreach (var texture in textures) texture.Dispose(); textures = []; }
	public override void _ExitTree() { ClearTextures(); scene = null; }
}
