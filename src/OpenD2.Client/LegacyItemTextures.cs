using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

// Owned by the scene/panel. Create the complete set before replacing live textures.
internal sealed class LegacyItemTextures : IDisposable
{
	private readonly LegacyItemArt art;
	private readonly Dictionary<Dc6Frame, ImageTexture> textures = new(ReferenceEqualityComparer.Instance);
	public LegacyItemTextures(LegacyItemArt art)
	{
		this.art = art;
		try
		{
			foreach (var frame in art.Icons.Values)
			{
				if (textures.ContainsKey(frame)) continue;
				using var image = Image.CreateFromData(frame.Width, frame.Height, false, Image.Format.Rgba8, art.Palette.ToRgba(frame.Indices, frame.Opacity));
				textures.Add(frame, ImageTexture.CreateFromImage(image));
			}
		}
		catch { Dispose(); throw; }
	}
	public ImageTexture? Get(ItemDefinition definition) => art.Icons.TryGetValue(definition, out var frame) ? textures[frame] : null;
	internal bool CheckTexture()
	{
		if (textures.Count == 0) return false;
		foreach (var pair in textures)
		{
			using var image = pair.Value.GetImage();
			if (image.GetWidth() != pair.Key.Width || image.GetHeight() != pair.Key.Height || !image.GetData().SequenceEqual(art.Palette.ToRgba(pair.Key.Indices, pair.Key.Opacity))) return false;
		}
		return true;
	}
	public void Dispose() { foreach (var texture in textures.Values) texture.Dispose(); textures.Clear(); }
}
