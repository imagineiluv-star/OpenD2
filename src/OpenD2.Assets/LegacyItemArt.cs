using System.Collections.ObjectModel;
using OpenD2.Core;

namespace OpenD2.Assets;

public sealed record LegacyItemIcon(string Definition, string Path, int Frame);
public sealed record LegacyItemRequest(string PalettePath, LegacyItemIcon[] Icons);

// Presentation-only mapping to the existing catalog. Does not import item rules or infer MPQ paths.
public sealed class LegacyItemArt
{
	public const int MaxDimension = 256;
	public Palette Palette { get; }
	public IReadOnlyDictionary<ItemDefinition, Dc6Frame> Icons { get; }
	public long PixelCount { get; }
	private LegacyItemArt(Palette palette, Dictionary<ItemDefinition, Dc6Frame> icons, long pixels)
	{ Palette = palette; Icons = new ReadOnlyDictionary<ItemDefinition, Dc6Frame>(icons); PixelCount = pixels; }
	public static LegacyItemRequest Snapshot(LegacyItemRequest request)
	{
		if (request is null || request.Icons is null || request.Icons.Length < 1 || request.Icons.Length > Enum.GetValues<ItemDefinition>().Length ||
			AssetDecoders.Kind(request.PalettePath) != "palette") throw new InvalidDataException("Item artwork requires a palette and at least one catalog icon.");
		var snapshot = request with { Icons = request.Icons.ToArray() }; var definitions = new HashSet<ItemDefinition>();
		foreach (var icon in snapshot.Icons)
			if (icon is null || !Enum.TryParse<ItemDefinition>(icon.Definition, out var definition) || !Enum.IsDefined(definition) || icon.Definition != definition.ToString() ||
				!definitions.Add(definition) || AssetDecoders.Kind(icon.Path) != "dc6" || icon.Frame is < 0 or > 4095)
				throw new InvalidDataException("Item icons need unique catalog names, DC6 paths and frame indices 0..4095.");
		return snapshot;
	}
	public static LegacyItemArt Load(LegacyItemRequest request, Func<string, byte[]> read)
	{
		var snapshot = Snapshot(request); ArgumentNullException.ThrowIfNull(read);
		long input = 0, pixels = 0;
		byte[] Read(string path)
		{
			var bytes = read(MpqArchive.NormalizePath(path)); input += bytes.LongLength;
			if (bytes.Length > AssetDecoders.MaxInputBytes || input > 64L * 1024 * 1024) throw new InvalidDataException("Item artwork input budget exceeded.");
			return bytes;
		}
		var palette = Palette.Parse(Read(snapshot.PalettePath)); var icons = new Dictionary<ItemDefinition, Dc6Frame>();
		var files = new Dictionary<string, Dc6Image>(StringComparer.Ordinal); var frames = new Dictionary<(string, int), Dc6Frame>();
		foreach (var icon in snapshot.Icons)
		{
			string path = MpqArchive.NormalizePath(icon.Path);
			if (!files.TryGetValue(path, out var file)) { file = Dc6Image.Parse(Read(path)); files.Add(path, file); }
			if (icon.Frame >= file.FrameCount) throw new InvalidDataException("Item icon frame is outside the DC6 file.");
			if (!frames.TryGetValue((path, icon.Frame), out var frame))
			{
				frame = file.DecodeFrame(icon.Frame / file.FramesPerDirection, icon.Frame % file.FramesPerDirection, MaxDimension);
				pixels += frame.Indices.LongLength; frames.Add((path, icon.Frame), frame);
			}
			icons.Add(Enum.Parse<ItemDefinition>(icon.Definition), frame);
		}
		return new(palette, icons, pixels);
	}
}
