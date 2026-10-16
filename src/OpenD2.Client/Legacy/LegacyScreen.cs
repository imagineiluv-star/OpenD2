using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client.Legacy;

/// <summary>
/// Original mode: the fixed 800x600 (or 640x480) screen drawn into a SubViewport and scaled to the window, centred on
/// pure black. Default: aspect-preserving fit (1920x1080 gives 1.8x, 1440x1080 with pillarbox) with the Sharp filter
/// (nearest upscale to ceil(scale) in a second SubViewport, then linear down to the rect). Options: whole-number
/// pixel-perfect scale; plain nearest or linear filters. It owns no dev-tool UI; Main builds either this or the dev scene.
/// The pixels come from <see cref="LegacyFrameBuffer"/> on the CPU, the same path the headless --capture uses.
/// </summary>
public partial class LegacyScreen : Control
{
	private readonly LegacyCaptureRequest request;
	private readonly string dataPath;
	private readonly LegacyScaleMode scaleMode;
	private readonly LegacyScaleFilter filter;
	private SubViewport viewport = null!;
	private TextureRect presented = null!;
	private SubViewport? prescale;
	private TextureRect? prescaled;
	private Label? error;

	public LegacyScreen(string dataPath, LegacyCaptureRequest request, LegacyScaleMode scaleMode = LegacyScaleMode.Fit, LegacyScaleFilter filter = LegacyScaleFilter.Nearest)
	{ this.dataPath = dataPath; this.request = request; this.scaleMode = scaleMode; this.filter = filter; }

	public override void _Ready()
	{
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		AddChild(new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore }); GetChild<ColorRect>(0).SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		var (w, h) = request.Resolution.Size();
		viewport = new SubViewport { Size = new Vector2I(w, h), Disable3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest, Snap2DTransformsToPixel = true, Snap2DVerticesToPixel = true };
		AddChild(viewport);
		var screen = new TextureRect { TextureFilter = TextureFilterEnum.Nearest, StretchMode = TextureRect.StretchModeEnum.Keep, Size = new Vector2(w, h) };
		viewport.AddChild(screen);
		Texture2D shown = viewport.GetTexture();
		if (filter == LegacyScaleFilter.Sharp)
		{
			// Second stage: nearest upscale by a whole number; its size follows the window in Layout().
			prescale = new SubViewport { Size = new Vector2I(w, h), Disable3D = true, TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
				CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest };
			prescaled = new TextureRect { Texture = viewport.GetTexture(), TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
			prescale.AddChild(prescaled); AddChild(prescale); shown = prescale.GetTexture();
		}
		presented = new TextureRect { Texture = shown, TextureFilter = filter == LegacyScaleFilter.Nearest ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
		AddChild(presented);
		try
		{
			using var source = new MpqAssetSource(dataPath);
			byte[] Read(string path) => source.TryRead(path, AssetDecoders.MaxInputBytes, out var bytes) ? bytes : throw new System.IO.FileNotFoundException("Missing " + path);
			var capture = LegacyScreenRenderer.Render(request with { WindowWidth = null, WindowHeight = null }, Read);
			screen.Texture = ImageTexture.CreateFromImage(Image.CreateFromData(capture.Width, capture.Height, false, Image.Format.Rgb8, capture.Rgb));
		}
		catch (Exception e) when (e is System.IO.IOException or InvalidDataException or ArgumentException or InvalidOperationException)
		{
			error = new Label { Text = "Original mode needs a Diablo II installation: " + e.Message, Modulate = Colors.White };
			AddChild(error);
		}
		Resized += Layout; GetViewport().SizeChanged += Layout; Layout();
	}

	private void Layout()
	{
		var size = GetViewportRect().Size; var (w, h) = request.Resolution.Size();
		var layout = LetterboxLayout.Fit(Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y), w, h, scaleMode);
		presented.Position = new Vector2(layout.X, layout.Y); presented.Size = new Vector2(layout.Width, layout.Height);
		if (prescale is not null && prescaled is not null)
		{
			int k = LegacyScaleOptions.SharpPrescale(layout.Scale);
			prescale.Size = new Vector2I(w * k, h * k); prescaled.Position = Vector2.Zero; prescaled.Size = new Vector2(w * k, h * k);
			// A whole-number fit needs no resample: show the prescaled image 1:1.
			presented.TextureFilter = layout.IsInteger || filter == LegacyScaleFilter.Nearest ? TextureFilterEnum.Nearest : TextureFilterEnum.Linear;
		}
		GD.Print($"OPEND2_LEGACY_LAYOUT scale={layout.Scale:0.###} rect={layout.X},{layout.Y},{layout.Width}x{layout.Height} filter={filter} window={layout.WindowWidth}x{layout.WindowHeight}");
	}

	/// <summary>Headless capture: CPU render straight to PNG, no window or GPU, so the bytes depend only on commit, data and request.</summary>
	public static int Capture(string[] args)
	{
		string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
		try
		{
			string output = System.IO.Path.GetFullPath(Option("--out") ?? throw new ArgumentException("--out is required."));
			D2DataPaths.RequireOutsideGit(System.IO.Path.GetDirectoryName(output)!);
			string data = D2DataPaths.ResolveDataPath(Option("--data")) ?? throw new ArgumentException("Pass --data or set D2_DATA_PATH.");
			var request = new LegacyCaptureRequest(Option("--capture") ?? "gameselect", LegacyResolutions.Parse(Option("--resolution") ?? "800x600"), Option("--palette") ?? "units",
				int.Parse(Option("--seed") ?? "0"), int.Parse(Option("--tick") ?? "0"));
			using var source = new MpqAssetSource(data);
			byte[] Read(string path) => source.TryRead(path, AssetDecoders.MaxInputBytes, out var bytes) ? bytes : throw new System.IO.FileNotFoundException("Missing " + path);
			var png = LegacyScreenRenderer.EncodePng(LegacyScreenRenderer.Render(request, Read));
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
			System.IO.File.WriteAllBytes(output, png);
			GD.Print("OPEND2_CAPTURE_WRITTEN " + output);
			return 0;
		}
		catch (Exception e) when (e is System.IO.IOException or InvalidDataException or ArgumentException or FormatException or InvalidOperationException)
		{
			GD.PushError("Capture failed: " + e.Message); return 1;
		}
	}
}
