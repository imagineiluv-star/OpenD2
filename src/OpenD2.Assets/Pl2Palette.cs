namespace OpenD2.Assets;

public enum Pl2Blend { Alpha25 = 0, Alpha50 = 1, Alpha75 = 2, Additive, Multiplicative, MaxComponent }

// pal.pl2: the colour-mapping tables that sit next to each act's pal.dat (lighting, inventory tints,
// transparency/blend lookups, hue shifts and text colours). Layout validated against all 16 genuine
// 1.11b pal.pl2 files: 15 with 13 text colours (443,175 bytes) and loading\pal.pl2 with 12 (442,916 bytes).
public sealed class Pl2Palette
{
	public const int LightLevels = 32, InventoryShifts = 16, HueShifts = 111, UnknownShifts = 14, MaxTextColors = 13;
	private const int FixedBytes = 1024 + LightLevels * 256 + InventoryShifts * 256 + 256 + 3 * 65536 + 65536 + 65536
		+ HueShifts * 256 + 3 * 256 + UnknownShifts * 256 + 65536 + 256;
	private readonly byte[] data;
	private readonly int alpha, additive, multiplicative, maxComponent, textColors;
	public int TextColorCount { get; }
	/// <summary>Base palette as RGB triples (the file stores 4-byte R, G, B, unused entries).</summary>
	public byte[] BaseRgb { get; }

	private Pl2Palette(byte[] data, int textColorCount)
	{
		this.data = data; TextColorCount = textColorCount;
		int at = 1024; Light = Slice(ref at, LightLevels * 256); Inventory = Slice(ref at, InventoryShifts * 256); SelectedShift = Slice(ref at, 256);
		alpha = at; at += 3 * 65536; additive = at; at += 65536; multiplicative = at; at += 65536;
		Hue = Slice(ref at, HueShifts * 256); RedTones = Slice(ref at, 256); GreenTones = Slice(ref at, 256); BlueTones = Slice(ref at, 256);
		Unknown = Slice(ref at, UnknownShifts * 256); maxComponent = at; at += 65536; DarkenedShift = Slice(ref at, 256);
		textColors = at; at += textColorCount * 3; TextShifts = Slice(ref at, textColorCount * 256);
		BaseRgb = new byte[768];
		for (int i = 0; i < 256; i++) { BaseRgb[i * 3] = data[i * 4]; BaseRgb[i * 3 + 1] = data[i * 4 + 1]; BaseRgb[i * 3 + 2] = data[i * 4 + 2]; }
	}
	private ReadOnlyMemory<byte> Slice(ref int at, int length) { var m = new ReadOnlyMemory<byte>(data, at, length); at += length; return m; }

	public ReadOnlyMemory<byte> Light { get; }
	public ReadOnlyMemory<byte> Inventory { get; }
	public ReadOnlyMemory<byte> SelectedShift { get; }
	public ReadOnlyMemory<byte> Hue { get; }
	public ReadOnlyMemory<byte> RedTones { get; }
	public ReadOnlyMemory<byte> GreenTones { get; }
	public ReadOnlyMemory<byte> BlueTones { get; }
	public ReadOnlyMemory<byte> Unknown { get; }
	public ReadOnlyMemory<byte> DarkenedShift { get; }
	public ReadOnlyMemory<byte> TextShifts { get; }

	public static Pl2Palette Parse(ReadOnlySpan<byte> data)
	{
		long extra = data.Length - (long)FixedBytes;
		AssetBinary.Require(extra > 0 && extra % (3 + 256) == 0 && extra / (3 + 256) is >= 1 and <= MaxTextColors, "Unsupported PL2 size.");
		return new Pl2Palette(data.ToArray(), (int)(extra / (3 + 256)));
	}

	/// <summary>Palette index remapped for a light level (0 = darkest … 31 = full brightness in the game's tables).</summary>
	public byte ApplyLight(byte index, int level)
	{
		if ((uint)level >= LightLevels) throw new ArgumentOutOfRangeException(nameof(level));
		return Light.Span[level * 256 + index];
	}
	public byte ApplyHue(byte index, int shift)
	{
		if ((uint)shift >= HueShifts) throw new ArgumentOutOfRangeException(nameof(shift));
		return Hue.Span[shift * 256 + index];
	}
	/// <summary>Blend lookup: result index for drawing <paramref name="source"/> over <paramref name="destination"/>.</summary>
	public byte Blend(Pl2Blend mode, byte source, byte destination)
	{
		int table = mode switch
		{
			Pl2Blend.Alpha25 or Pl2Blend.Alpha50 or Pl2Blend.Alpha75 => alpha + (int)mode * 65536,
			Pl2Blend.Additive => additive, Pl2Blend.Multiplicative => multiplicative, Pl2Blend.MaxComponent => maxComponent,
			_ => throw new ArgumentOutOfRangeException(nameof(mode)),
		};
		return data[table + source * 256 + destination];
	}
	public (byte R, byte G, byte B) TextColor(int index)
	{
		if ((uint)index >= TextColorCount) throw new ArgumentOutOfRangeException(nameof(index));
		int at = textColors + index * 3; return (data[at], data[at + 1], data[at + 2]);
	}
}
