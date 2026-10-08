using Godot;
using OpenD2.Assets;

namespace OpenD2.Client;

public partial class AnimationPreview : VBoxContainer
{
	private readonly Func<string> gameDirectory;
	private readonly LineEdit resource = new() { PlaceholderText = "MPQ path to animation.dcc or composition.cof" };
	private readonly LineEdit palettePath = new() { Text = "data/global/palette/units/pal.dat" };
	private readonly TextEdit mapping = new() { PlaceholderText = "COF layers, one per line: component number=MPQ DCC path", CustomMinimumSize = new Vector2(480, 65) };
	private readonly SpinBox direction = new() { MinValue = 0, MaxValue = 31, Step = 1 };
	private readonly SpinBox frame = new() { MinValue = 0, MaxValue = 0, Step = 1 };
	private readonly SpinBox fps = new() { MinValue = 0, MaxValue = 120, Value = 12, Step = 0 };
	private readonly Button play = new() { Text = "Play", ToggleMode = true };
	private readonly Button load = new() { Text = "Load selected direction" };
	private readonly Label details = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly TextureRect picture = new() { CustomMinimumSize = new Vector2(480, 230), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
	private AnimationClip? clip;
	private Palette? palette;
	private ImageTexture? texture;
	private string source = "";
	private double elapsed;
	public AnimationPreview(Func<string> gameDirectory) { this.gameDirectory = gameDirectory; }
	public override void _Ready()
	{
		AddChild(new Label { Text = "DCC / COF animation preview" }); AddChild(resource); AddChild(palettePath); AddChild(mapping);
		var controls = new HBoxContainer(); AddChild(controls);
		controls.AddChild(new Label { Text = "Direction" }); controls.AddChild(direction); controls.AddChild(load);
		load.Pressed += LoadSelected;
		var playback = new HBoxContainer(); AddChild(playback);
		playback.AddChild(play); playback.AddChild(new Label { Text = "Frame" }); playback.AddChild(frame);
		playback.AddChild(new Label { Text = "Preview FPS" }); playback.AddChild(fps);
		frame.ValueChanged += _ => { elapsed = fps.Value > 0 ? frame.Value / fps.Value : 0; ShowFrame(); };
		fps.ValueChanged += _ => elapsed = fps.Value > 0 ? frame.Value / fps.Value : 0;
		play.Toggled += on => play.Text = on ? "Pause" : "Play";
		AddChild(picture); AddChild(details);
		LoadSample();
	}
	public bool InspectMotion(string colors, LegacyMotionRequest motion, int facing)
	{
		if (load.Disabled || facing is < 0 or > 7) return false;
		resource.Text = motion.Path; palettePath.Text = colors;
		mapping.Text = motion.Layers is null ? "" : string.Join('\n', motion.Layers.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
		direction.Value = motion.Directions[facing]; fps.Value = motion.Fps; LoadSelected(); return true;
	}
	private async void LoadSelected()
	{
		if (load.Disabled) return;
		play.ButtonPressed = false; load.Disabled = true; frame.Editable = false; direction.Editable = false;
		try
		{
			string dir = gameDirectory(), path = resource.Text, colorsPath = palettePath.Text, map = mapping.Text;
			int selected = (int)direction.Value; details.Text = "Reading and decoding selected direction...";
			var loaded = await Task.Run(() =>
			{
				var colors = Palette.Parse(AssetDecoders.ReadFromInstall(dir, colorsPath));
				var bytes = AssetDecoders.ReadFromInstall(dir, path); AnimationClip next;
				if (AssetDecoders.Kind(path) == "dcc") next = AnimationClip.Single(DccAnimation.Parse(bytes).DecodeDirection(selected));
				else if (AssetDecoders.Kind(path) == "cof")
				{
					var cof = CofAnimation.Parse(bytes); cof.DrawOrder(selected, 0);
					var paths = LegacyArtworkSetup.ParseLayers(map);
					if (paths.Count != cof.Layers.Count || cof.Layers.Any(l => !paths.ContainsKey(l.Component))) throw new InvalidDataException("Map every COF component exactly once.");
					var layers = new Dictionary<byte, IReadOnlyList<IndexedFrame>>(); long pixels = 0, inputBytes = bytes.Length;
					foreach (var entry in paths)
					{
						byte[] raw = AssetDecoders.ReadFromInstall(dir, entry.Value); inputBytes += raw.Length;
						if (inputBytes > AssetDecoders.MaxInputBytes * 2L) throw new InvalidDataException("Composition input exceeds 64 MiB.");
						var dcc = DccAnimation.Parse(raw);
						if (dcc.Directions != cof.Directions || dcc.FramesPerDirection != cof.FramesPerDirection) throw new InvalidDataException("DCC direction/frame counts must match COF; remapping is not implemented.");
						var decoded = dcc.DecodeDirection(selected); pixels += decoded.Sum(f => (long)f.Indices.Length);
						if (pixels > DccAnimation.MaxPixels) throw new InvalidDataException("Composition source pixel budget exceeded.");
						layers.Add(entry.Key, decoded);
					}
					next = AnimationClip.Compose(cof, selected, layers);
				}
				else throw new InvalidDataException("Choose a DCC or COF path.");
				return (next, colors);
			});
			if (!IsInstanceValid(this) || !IsInsideTree()) return;
			SetClip(loaded.next, loaded.colors, $"{path} / direction {selected}");
		}
		catch (Exception error)
		{
			if (IsInstanceValid(this) && IsInsideTree()) { ClearClip(); details.Text = "Load failed: " + error.Message; }
		}
		finally
		{
			if (IsInstanceValid(load)) { load.Disabled = false; frame.Editable = true; direction.Editable = true; }
		}
	}
	private void SetClip(AnimationClip next, Palette colors, string name)
	{
		ClearClip(); clip = next; palette = colors; source = name; elapsed = 0;
		frame.MaxValue = next.Frames.Count - 1; frame.SetValueNoSignal(0); ShowFrame();
	}
	private void ShowFrame()
	{
		if (clip is null || palette is null) return;
		using var image = Image.CreateFromData(clip.Width, clip.Height, false, Image.Format.Rgba8, clip.ToRgba((int)frame.Value, palette));
		if (texture is null) { texture = ImageTexture.CreateFromImage(image); picture.Texture = texture; } else texture.Update(image);
		details.Text = $"{source}\nFrame {(int)frame.Value}/{clip.Frames.Count - 1}, {clip.Width} x {clip.Height}, origin ({clip.Left}, {clip.Top})\nPreview FPS is manual; gameplay timing and PL2 effects are not applied.";
	}
	public override void _Process(double delta)
	{
		if (clip is null || !play.ButtonPressed || !IsVisibleInTree() || load.Disabled || fps.Value <= 0) return;
		elapsed = (elapsed + delta) % (clip.Frames.Count / fps.Value);
		int next = clip.FrameAt(elapsed, fps.Value);
		if ((int)frame.Value != next) { frame.SetValueNoSignal(next); ShowFrame(); }
	}
	private void ClearClip() { play.ButtonPressed = false; clip = null; picture.Texture = null; texture?.Dispose(); texture = null; }
	public override void _ExitTree() { ClearClip(); }
	private void LoadSample()
	{
		// Generated fixture bytes; no Blizzard assets. Both DCC decoding and COF ordering run here.
		byte[] raw = Convert.FromHexString(SampleDcc);
		var animation = DccAnimation.Parse(raw); var frames = animation.DecodeDirection(0);
		var alternate = animation.DecodeDirection(1);
		byte[] cofBytes = new byte[56]; cofBytes[0] = 2; cofBytes[1] = 2; cofBytes[2] = 2; cofBytes[3] = 20;
		cofBytes[28] = 1; new byte[] { 1, 0, 0, 1, 0, 1, 1, 0 }.CopyTo(cofBytes, 48);
		var sample = AnimationClip.Compose(CofAnimation.Parse(cofBytes), 0, new Dictionary<byte, IReadOnlyList<IndexedFrame>> { [0] = frames, [1] = alternate });
		byte[] colors = new byte[768]; colors[3] = 40; colors[4] = 170; colors[5] = 240;
		SetClip(sample, Palette.Parse(colors), "Synthetic two-layer animation (not game content)");
		if (texture is null || sample.FrameAt(0.1, 12) != 1) throw new InvalidDataException("Animation preview failed.");
		frame.SetValueNoSignal(1); ShowFrame();
		using var actual = texture.GetImage();
		if (actual.GetPixel(0, 0).A < 0.99f) throw new InvalidDataException("Animation texture mismatch.");
		GD.Print("OPEND2_M105_ANIMATION_READY");
	}
	internal const string SampleDcc = "740602020000000100000000000000170000004800000000000000C0C80C80F84FFC470000030000000000000000000000000000000000000000000000000000000000000010A00500000000C0C80C80F84FFC470000030000000000000000000000000000000000000000000000000000000000000010500A";
}
