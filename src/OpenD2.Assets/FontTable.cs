namespace OpenD2.Assets;

/// <summary>One glyph. Frame is the index of the glyph image in the font's DC6.</summary>
public sealed record FontGlyph(char Character, byte Advance, byte Height, ushort Frame, byte Flags);

// data\local\font\<locale>\*.tbl ("Woo!" tables). Field meanings were resolved against all 28 genuine
// 1.11b font tables (14 latin, 14 kor). Compared with Engine/TBL_Font.hpp:
//  - the header DWORD at offset 6 called dwLocale is really a reserved WORD (0) plus the glyph COUNT (256 latin, 11,374 kor);
//  - glyph bytes 8-9 (nImageIndex + "nChar") are one little-endian WORD: the DC6 frame index (always the glyph's position);
//  - glyph byte 2, bytes 6-7 and bytes 10-13 are always zero on disk (10-13 is a runtime atlas slot);
//  - glyph byte 5 is always 1 (kept as Flags); glyph height always equals the header line height.
// Header byte 11 (CapHeight in the old header) varies (1, 2, 7, 8, 9, 12, 20) without a confirmed meaning; it is exposed raw.
public sealed class FontTable
{
	public const int MaxGlyphs = 16384;
	public ushort Version { get; }
	public byte LineHeight { get; }
	public byte HeaderByte11 { get; }
	public IReadOnlyList<FontGlyph> Glyphs { get; }
	private readonly Dictionary<char, FontGlyph> byChar;
	private FontTable(ushort version, byte lineHeight, byte byte11, FontGlyph[] glyphs)
	{
		Version = version; LineHeight = lineHeight; HeaderByte11 = byte11; Glyphs = Array.AsReadOnly(glyphs);
		byChar = new();
		foreach (var g in glyphs) byChar.TryAdd(g.Character, g);
	}
	public FontGlyph? Find(char character) => byChar.TryGetValue(character, out var g) ? g : null;

	/// <summary>Rejects a font whose glyphs point past the frames of its DC6 (which would fail later at draw time).</summary>
	public void RequireFramesWithin(Dc6Image image)
	{
		int max = Glyphs.Count == 0 ? -1 : Glyphs.Max(g => g.Frame);
		AssetBinary.Require(image.Directions >= 1 && max < image.FramesPerDirection, $"Font glyph frame {max} exceeds the DC6's {image.FramesPerDirection} frames.");
	}

	public static FontTable Parse(ReadOnlySpan<byte> data)
	{
		AssetBinary.Require(data.Length >= 12 && data[..4].SequenceEqual("Woo!"u8), "Font table must start with Woo!.");
		ushort version = AssetBinary.U16(data, 4), reserved = AssetBinary.U16(data, 6), count = AssetBinary.U16(data, 8);
		AssetBinary.Require(version == 1 && reserved == 0, "Unsupported font table version.");
		AssetBinary.Require(count is >= 1 and <= MaxGlyphs && data.Length == 12 + count * 14, "Font table length does not match its glyph count.");
		var glyphs = new FontGlyph[count];
		for (int i = 0; i < count; i++)
		{
			var g = data.Slice(12 + i * 14, 14);
			ushort frame = AssetBinary.U16(g, 8);
			AssetBinary.Require(frame < count, "Font glyph frame index out of range.");
			glyphs[i] = new((char)AssetBinary.U16(g, 0), g[3], g[4], frame, g[5]);
		}
		return new(version, data[10], data[11], glyphs);
	}
}
