using Godot;
using OpenD2.Assets;
using System.Buffers.Binary;

namespace OpenD2.Client;

public partial class AssetPreview : VBoxContainer
{
	private readonly Func<string> gameDirectory;
	private readonly LineEdit resource = new() { PlaceholderText = "MPQ path to an image.dc6" };
	private readonly LineEdit palettePath = new() { Text = "data/global/palette/units/pal.dat" };
	private readonly Label details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly TextureRect picture = new()
	{
		CustomMinimumSize = new Vector2(480, 320), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest
	};
	private readonly SpinBox frame = new() { MinValue = 0, MaxValue = 0, Step = 1 };
	private Dc6Image? current;
	private Palette? palette;
	private ImageTexture? texture;
	private string source = "Synthetic sample";
	public AssetPreview(Func<string> gameDirectory) { this.gameDirectory = gameDirectory; }
	public override void _Ready()
	{
		AddChild(new Label { Text = "M1 / DC6 PREVIEW" });
		AddChild(new Label { Text = "DC6 logical path" }); AddChild(resource);
		AddChild(new Label { Text = "Palette logical path" }); AddChild(palettePath);
		var load = new Button { Text = "Load from selected game directory" }; AddChild(load);
		load.Pressed += async () =>
		{
			load.Disabled = true;
			try
			{
				string dir = gameDirectory(), imagePath = resource.Text, colorsPath = palettePath.Text;
				details.Text = "Reading resource...";
				var loaded = await Task.Run(() =>
				{
					var image = Dc6Image.Parse(AssetDecoders.ReadFromInstall(dir, imagePath));
					var colors = Palette.Parse(AssetDecoders.ReadFromInstall(dir, colorsPath));
					image.DecodeFrame(0, 0); return (image, colors);
				});
				if (!IsInstanceValid(this) || !IsInsideTree()) return;
				current = loaded.image; palette = loaded.colors; source = imagePath;
				frame.MaxValue = current.FrameCount - 1; frame.SetValueNoSignal(0); ShowFrame();
			}
			catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) details.Text = "Load failed: " + error.Message; }
			finally { if (IsInstanceValid(load)) load.Disabled = false; }
		};
		AddChild(new Label { Text = "Frame index (direction-major)" }); AddChild(frame);
		frame.ValueChanged += _ => ShowFrame(); AddChild(picture); AddChild(details);
		LoadSyntheticSample();
	}
	private void ShowFrame()
	{
		if (current is null || palette is null) return;
		try
		{
			int index = (int)frame.Value, direction = index / current.FramesPerDirection, number = index % current.FramesPerDirection;
			var decoded = current.DecodeFrame(direction, number);
			using var image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, palette.ToRgba(decoded.Indices, decoded.Opacity));
			var next = ImageTexture.CreateFromImage(image); picture.Texture = next; texture?.Dispose(); texture = next;
			details.Text = $"{source}\nDirection {direction}, frame {number}: {decoded.Width} x {decoded.Height}, offset ({decoded.OffsetX}, {decoded.OffsetY})";
		}
		catch (Exception error) { picture.Texture = null; texture?.Dispose(); texture = null; details.Text = "Decode failed: " + error.Message; }
	}
	private void LoadSyntheticSample()
	{
		// Owned fixture, generated in memory. Never substitutes for real compatibility evidence.
		var colors = new byte[768]; colors[3] = 40; colors[4] = 170; colors[5] = 240;
		int length = 34 * 32; var raw = new byte[60 + length + 3];
		void Set(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset), value);
		Set(0, 6); Set(4, 1); Set(12, 0xeeeeeeee); Set(16, 1); Set(20, 1); Set(24, 28);
		Set(28, 1); Set(32, 32); Set(36, 32); Set(56, (uint)length);
		for (int y = 0; y < 32; y++)
		{
			int at = 60 + y * 34; raw[at] = 32;
			for (int x = 0; x < 32; x++) raw[at + 1 + x] = (byte)((x / 4 + y / 4) % 2);
			raw[at + 33] = 0x80;
		}
		current = Dc6Image.Parse(raw); palette = Palette.Parse(colors); ShowFrame();
		if (texture is null) throw new InvalidDataException("Preview sample failed.");
		using var actual = texture.GetImage();
		var pixel = actual.GetPixel(4, 0);
		if (Math.Abs(pixel.R - 240 / 255f) > 0.01f || pixel.A < 0.99f) throw new InvalidDataException("Preview color mismatch.");
		GD.Print("OPEND2_M104_PREVIEW_READY");
	}
	public override void _ExitTree() { picture.Texture = null; texture?.Dispose(); texture = null; }
}
