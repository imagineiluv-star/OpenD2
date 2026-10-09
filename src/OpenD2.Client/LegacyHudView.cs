using Godot;
using OpenD2.Assets;

namespace OpenD2.Client;

// A bounded, aspect-preserving DC6 strip. Existing text controls remain accessible alongside it.
public partial class LegacyHudView : Control
{
	public sealed class Prepared : IDisposable
	{
		public LegacyHudArt Art { get; }
		public Dictionary<Dc6Frame, ImageTexture> Textures { get; } = new(ReferenceEqualityComparer.Instance);
		public Prepared(LegacyHudArt art)
		{
			Art = art;
			try
			{
				foreach (var s in art.Sprites)
				{
					if (Textures.ContainsKey(s.Frame)) continue;
					using var image = Image.CreateFromData(s.Frame.Width, s.Frame.Height, false, Image.Format.Rgba8, art.Palette.ToRgba(s.Frame.Indices, s.Frame.Opacity));
					Textures.Add(s.Frame, ImageTexture.CreateFromImage(image));
				}
			}
			catch { Dispose(); throw; }
		}
		public void Dispose() { foreach (var texture in Textures.Values) texture.Dispose(); Textures.Clear(); }
	}
	private Prepared? artwork;
	private int health, maximum = 1, mana, maxMana = 1;
	public bool HasArtwork => artwork is not null;
	public int HealthRows => artwork?.Art.Sprites.FirstOrDefault(s => s.Role == HudRole.Health) is { } s ? LegacyHudArt.FilledRows(health, maximum, s.Frame.Height) : 0;
	public int ManaRows => artwork?.Art.Sprites.FirstOrDefault(s => s.Role == HudRole.Mana) is { } s ? LegacyHudArt.FilledRows(mana, Math.Max(1, maxMana), s.Frame.Height) : 0;
	public event Action<HudRole>? ActionRequested;
	public LegacyHudView()
	{
		Visible = false; MouseFilter = MouseFilterEnum.Stop; TextureFilter = TextureFilterEnum.Nearest; ClipContents = true;
		SizeFlagsHorizontal = SizeFlags.ExpandFill; TooltipText = "Configured HUD artwork. Menu and Inventory use the existing game controls.";
		Resized += ResizeStrip;
	}
	// Takes ownership only after all scene resources are prepared successfully.
	public void SetArtwork(Prepared? next)
	{
		if (ReferenceEquals(artwork, next)) return;
		artwork?.Dispose(); artwork = next; Visible = next is not null; ResizeStrip(); QueueRedraw();
	}
	private void ResizeStrip()
	{
		float height = artwork is null ? 0 : artwork.Art.Height * Math.Min(1f, Math.Max(1f, Size.X) / artwork.Art.Width);
		CustomMinimumSize = new Vector2(0, height); QueueRedraw();
	}
	public void SetHealth(int value, int max)
	{
		if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
		if (health == value && maximum == max) return;
		health = value; maximum = max; QueueRedraw();
	}
	public void SetMana(int value, int max)
	{
		if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
		if (mana == value && maxMana == max) return;
		mana = value; maxMana = max; QueueRedraw();
	}
	private (float Scale, Vector2 Origin) Placement()
	{
		if (artwork is null) return (0, Vector2.Zero);
		float scale = Math.Min(1f, Math.Min(Size.X / artwork.Art.Width, Size.Y / artwork.Art.Height));
		return (scale, (Size - new Vector2(artwork.Art.Width, artwork.Art.Height) * scale) / 2);
	}
	public override void _Draw()
	{
		if (artwork is null) return;
		var (scale, origin) = Placement(); if (scale <= 0) return;
		foreach (var s in artwork.Art.Sprites)
		{
			int rows = s.Role == HudRole.Health ? LegacyHudArt.FilledRows(health, maximum, s.Frame.Height) : s.Role == HudRole.Mana ? LegacyHudArt.FilledRows(mana, Math.Max(1, maxMana), s.Frame.Height) : s.Frame.Height;
			if (rows == 0) continue;
			int top = s.Frame.Height - rows;
			DrawTextureRectRegion(artwork.Textures[s.Frame], new Rect2(origin + new Vector2(s.X, s.Y + top) * scale, new Vector2(s.Frame.Width, rows) * scale),
				new Rect2(0, top, s.Frame.Width, rows));
		}
	}
	internal bool ActivateAt(Vector2 point)
	{
		if (artwork is null) return false;
		var (scale, origin) = Placement(); if (scale <= 0) return false;
		var local = (point - origin) / scale;
		if (artwork.Art.ActionAt(local.X, local.Y) is not { } role) return false;
		ActionRequested?.Invoke(role); return true;
	}
	public override void _GuiInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button && ActivateAt(button.Position)) AcceptEvent();
	}
	internal bool CheckTexture()
	{
		if (artwork is null || artwork.Textures.Count == 0) return false;
		foreach (var pair in artwork.Textures)
		{
			using var image = pair.Value.GetImage();
			if (image.GetWidth() != pair.Key.Width || image.GetHeight() != pair.Key.Height || !image.GetData().SequenceEqual(artwork.Art.Palette.ToRgba(pair.Key.Indices, pair.Key.Opacity))) return false;
		}
		return true;
	}
	public override void _ExitTree() { artwork?.Dispose(); artwork = null; }
}
