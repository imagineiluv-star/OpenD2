using System.Buffers.Binary;
using OpenD2.Assets;

// Synthetic PL2 lighting palettes and "Woo!" font tables. No game data.
internal static class Pl2FontContracts
{
	private static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }
	internal static byte[] Pl2(int textColors = 13)
	{
		var data = new byte[443175 - (13 - textColors) * 259];
		for (int i = 0; i < 256; i++) { data[i * 4] = (byte)i; data[i * 4 + 1] = (byte)(255 - i); data[i * 4 + 2] = 9; }
		int light = 1024; for (int level = 0; level < 32; level++) for (int i = 0; i < 256; i++) data[light + level * 256 + i] = (byte)(i * level / 31);
		int alpha50 = 1024 + 32 * 256 + 16 * 256 + 256 + 65536; data[alpha50 + 10 * 256 + 20] = 15;
		int text = data.Length - textColors * 259; data[text] = 255; data[text + 1] = 128; data[text + 2] = 1;
		return data;
	}
	internal static byte[] Font(int glyphs = 3, ushort badFrame = 0)
	{
		var data = new byte[12 + glyphs * 14];
		"Woo!"u8.CopyTo(data); data[4] = 1; BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)glyphs); data[10] = 10; data[11] = 20;
		for (int i = 0; i < glyphs; i++)
		{
			var g = data.AsSpan(12 + i * 14, 14);
			BinaryPrimitives.WriteUInt16LittleEndian(g, (ushort)(i == 2 ? 0xAC00 : 'A' + i)); g[3] = (byte)(5 + i); g[4] = 10; g[5] = 1;
			BinaryPrimitives.WriteUInt16LittleEndian(g[8..], badFrame != 0 && i == 0 ? badFrame : (ushort)i);
		}
		return data;
	}

	public static void Run(Action<string, Action> test)
	{
		test("PL2 parses light, blend, hue and text-colour tables for 13 and 12 text colours", () =>
		{
			var pl2 = Pl2Palette.Parse(Pl2());
			Check(pl2.TextColorCount == 13 && pl2.BaseRgb[3 * 7] == 7 && pl2.BaseRgb[3 * 7 + 1] == 248, "base rgb");
			Check(pl2.ApplyLight(200, 31) == 200 && pl2.ApplyLight(200, 0) == 0, "light");
			Check(pl2.Blend(Pl2Blend.Alpha50, 10, 20) == 15 && pl2.Blend(Pl2Blend.Additive, 10, 20) == 0, "blend");
			Check(pl2.TextColor(0) == (255, 128, 1), "text colour");
			Check(Pl2Palette.Parse(Pl2(12)).TextColorCount == 12, "loading variant");
			foreach (int size in new[] { 0, 1024, 443174, 443176, 443175 + 259 })
			{ try { Pl2Palette.Parse(new byte[size]); throw new Exception("bad size accepted " + size); } catch (InvalidDataException) { } }
			try { pl2.ApplyLight(0, 32); throw new Exception("light range"); } catch (ArgumentOutOfRangeException) { }
			Check(AssetDecoders.Kind("data\\global\\palette\\act1\\pal.pl2") == "pl2", "kind");
		});
		test("font TBL decodes glyph count, advance, height and 16-bit DC6 frame index", () =>
		{
			var font = FontTable.Parse(Font());
			Check(font.Glyphs.Count == 3 && font.LineHeight == 10 && font.HeaderByte11 == 20, "header");
			Check(font.Find('B') is { Advance: 6, Height: 10, Frame: 1, Flags: 1 } && font.Find('\uAC00')!.Frame == 2 && font.Find('Z') is null, "glyphs");
			var big = FontTable.Parse(Font(11374)); Check(big.Glyphs[^1].Frame == 11373, "16-bit frame index");
			try { FontTable.Parse(Font(badFrame: 3)); throw new Exception("frame out of range accepted"); } catch (InvalidDataException) { }
			try { FontTable.Parse(Font()[..^1]); throw new Exception("short table accepted"); } catch (InvalidDataException) { }
			var bad = Font(); bad[0] = (byte)'X'; try { FontTable.Parse(bad); throw new Exception("bad magic accepted"); } catch (InvalidDataException) { }
			Check(AssetDecoders.Kind("data\\local\\font\\latin\\font16.tbl") == "font_tbl" && AssetDecoders.Kind("data\\local\\font\\latin\\fonter.tbl") is null
				&& AssetDecoders.Kind("data\\local\\font\\latin\\default.tbl") is null, "font kinds");
			var oneFrame = Dc6Image.Parse(LegacyFormatContracts.Dc6());
			FontTable.Parse(Font(1)).RequireFramesWithin(oneFrame);
			try { font.RequireFramesWithin(oneFrame); throw new Exception("glyph frame past the DC6 accepted"); } catch (InvalidDataException) { }
		});
	}
}
