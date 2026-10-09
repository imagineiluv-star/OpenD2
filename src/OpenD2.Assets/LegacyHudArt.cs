using System.Collections.ObjectModel;

namespace OpenD2.Assets;

public enum HudRole { Decoration, Health, Menu, Inventory, Mana }
public sealed record LegacyHudElement(string Role, string Path, int Frame, int X, int Y);
public sealed record LegacyHudRequest(string PalettePath, int Width, int Height, LegacyHudElement[] Elements);
public sealed record LegacyHudSprite(HudRole Role, Dc6Frame Frame, int X, int Y);

// Explicit UI frames/positions. DC6 offsets are retained in Frame but placement uses authored top-left X/Y.
public sealed class LegacyHudArt
{
	public const int MaxElements = 32;
	public const int MaxPixels = 4 * 1024 * 1024;
	public int Width { get; }
	public int Height { get; }
	public Palette Palette { get; }
	public IReadOnlyList<LegacyHudSprite> Sprites { get; }
	public long PixelCount { get; }
	public bool HasMana => Sprites.Any(s => s.Role == HudRole.Mana);
	public bool HasHealth => Sprites.Any(s => s.Role == HudRole.Health);
	private LegacyHudArt(int width, int height, Palette palette, List<LegacyHudSprite> sprites, long pixels)
	{ Width = width; Height = height; Palette = palette; Sprites = new ReadOnlyCollection<LegacyHudSprite>(sprites); PixelCount = pixels; }
	public static LegacyHudRequest Snapshot(LegacyHudRequest request)
	{
		if (request is null || request.Width is < 1 or > 4096 || request.Height is < 1 or > 1024 ||
			request.Elements is null || request.Elements.Length is < 1 or > MaxElements || AssetDecoders.Kind(request.PalettePath) != "palette")
			throw new InvalidDataException("HUD requires a palette, canvas 1..4096 by 1..1024 and 1..32 elements.");
		var snapshot = request with { Elements = request.Elements.ToArray() }; var roles = new HashSet<HudRole>();
		foreach (var element in snapshot.Elements)
		{
			if (element is null || !Enum.TryParse<HudRole>(element.Role, out var role) || !Enum.IsDefined(role) || element.Role != role.ToString() ||
				(role != HudRole.Decoration && !roles.Add(role)) || AssetDecoders.Kind(element.Path) != "dc6" || element.Frame is < 0 or > 4095 ||
				element.X < 0 || element.Y < 0 || element.X >= request.Width || element.Y >= request.Height)
				throw new InvalidDataException("HUD elements need valid roles, unique Health/Mana/Menu/Inventory, DC6 paths, frames and canvas positions.");
		}
		return snapshot;
	}
	public static LegacyHudArt Load(LegacyHudRequest request, Func<string, byte[]> read)
	{
		var snapshot = Snapshot(request); ArgumentNullException.ThrowIfNull(read);
		long input = 0, pixels = 0;
		byte[] Read(string path)
		{
			var bytes = read(MpqArchive.NormalizePath(path)); input += bytes.LongLength;
			if (bytes.Length > AssetDecoders.MaxInputBytes || input > 64L * 1024 * 1024) throw new InvalidDataException("HUD input budget exceeded.");
			return bytes;
		}
		var palette = Palette.Parse(Read(snapshot.PalettePath)); var sprites = new List<LegacyHudSprite>();
		// Repeated frame references share decode/storage. Every unique source still counts toward scene input.
		var files = new Dictionary<string, Dc6Image>(StringComparer.Ordinal);
		var frames = new Dictionary<(string, int), Dc6Frame>();
		foreach (var element in snapshot.Elements)
		{
			string path = MpqArchive.NormalizePath(element.Path);
			if (!files.TryGetValue(path, out var file)) { file = Dc6Image.Parse(Read(path)); files.Add(path, file); }
			if (element.Frame >= file.FrameCount) throw new InvalidDataException("HUD frame is outside the DC6 file.");
			if (!frames.TryGetValue((path, element.Frame), out var frame))
			{
				frame = file.DecodeFrame(element.Frame / file.FramesPerDirection, element.Frame % file.FramesPerDirection);
				pixels += frame.Indices.LongLength;
				if (pixels > MaxPixels) throw new InvalidDataException("HUD decoded pixel budget exceeded.");
				frames.Add((path, element.Frame), frame);
			}
			if (frame.Width > snapshot.Width - element.X || frame.Height > snapshot.Height - element.Y)
				throw new InvalidDataException("HUD frame extends beyond its canvas.");
			sprites.Add(new(Enum.Parse<HudRole>(element.Role), frame, element.X, element.Y));
		}
		return new(snapshot.Width, snapshot.Height, palette, sprites, pixels);
	}
	public static int FilledRows(int health, int maximum, int height)
	{
		if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));
		if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
		return (int)((long)Math.Clamp(health, 0, maximum) * height / maximum);
	}
	public HudRole? ActionAt(double x, double y)
	{
		if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
		// Later actions win overlaps, matching their drawing order. Decoration/health never trigger commands.
		for (int i = Sprites.Count - 1; i >= 0; i--)
		{
			var s = Sprites[i];
			if (s.Role is HudRole.Menu or HudRole.Inventory && x >= s.X && y >= s.Y && x < s.X + s.Frame.Width && y < s.Y + s.Frame.Height) return s.Role;
		}
		return null;
	}
}
