using System.Globalization;

namespace OpenD2.Assets;

// Shared parsing for scene forms and the animation inspector. No filename/direction conventions inferred.
public static class LegacyArtworkSetup
{
	public static Dictionary<byte, string> ParseLayers(string text)
	{
		if (text is null || text.Length > 32768) throw new InvalidDataException("Layer mapping exceeds 32 KiB.");
		var paths = new Dictionary<byte, string>();
		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
			if (parts.Length != 2 || !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte component) || component > 15 ||
				AssetDecoders.Kind(parts[1]) != "dcc" || !paths.TryAdd(component, MpqArchive.NormalizePath(parts[1])))
				throw new InvalidDataException("Use unique component numbers 0..15=MPQ DCC path.");
		}
		return paths;
	}
	public static LegacyMotionRequest ParseMotion(string motion, string path, string layers, string directions, double fps)
	{
		if (!Enum.TryParse<ActorMotion>(motion, false, out var type) || !Enum.IsDefined(type) || type.ToString() != motion || !double.IsFinite(fps) || fps is <= 0 or > 120)
			throw new InvalidDataException("Choose Idle/Walk/Attack/Hit/Death and FPS greater than 0, at most 120.");
		if (directions is null || directions.Length > 128) throw new InvalidDataException("Enter eight direction indices separated by commas.");
		var parts = directions.Split(',', StringSplitOptions.TrimEntries); var indices = new int[8];
		if (parts.Length != 8) throw new InvalidDataException("Enter exactly eight direction indices separated by commas.");
		for (int i = 0; i < indices.Length; i++)
			if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out indices[i]) || indices[i] is < 0 or > 31)
				throw new InvalidDataException("Direction indices must be integers 0..31; confirm them in the source animation.");
		string? kind = AssetDecoders.Kind(path); var components = ParseLayers(layers);
		if (kind is not ("dcc" or "cof") || (kind == "dcc" && components.Count != 0) || (kind == "cof" && components.Count == 0))
			throw new InvalidDataException("DCC uses no component mapping; COF requires explicit component DCC paths.");
		return new(motion, MpqArchive.NormalizePath(path), kind == "cof" ? components : null, indices, fps);
	}
}
