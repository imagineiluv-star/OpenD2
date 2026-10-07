using System.Buffers.Binary;
using System.Text;
using OpenD2.Assets;

internal static class LegacyFormatContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Format assertion failed"); }
	private static void Invalid(Action action)
	{
		try { action(); } catch (InvalidDataException) { return; }
		throw new Exception("Expected InvalidDataException");
	}
	private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
	internal static byte[] Dc6(bool flip = false)
	{
		// Two rows, 3 pixels. Encoded first row: visible 0,1 then transparent.
		byte[] data = new byte[72]; U32(data, 0, 6); U32(data, 4, 1); U32(data, 12, 0xeeeeeeee);
		U32(data, 16, 1); U32(data, 20, 1); U32(data, 24, 28);
		U32(data, 28, flip ? 1u : 0); U32(data, 32, 3); U32(data, 36, 2);
		U32(data, 40, unchecked((uint)-5)); U32(data, 44, 7); U32(data, 56, 9);
		new byte[] { 2, 0, 1, 0x81, 0x80, 0x81, 1, 2, 0x80, 0xee, 0xee, 0xee }.CopyTo(data, 60);
		return data;
	}
	private static byte[] Tbl()
	{
		byte[] data = new byte[48]; data[2] = 1; U32(data, 4, 1); data[8] = 1;
		U32(data, 9, 40); U32(data, 17, 48); data[23] = 1;
		U32(data, 30, 40); U32(data, 34, 42); data[38] = 6;
		new byte[] { 75, 0, 104, 101, 108, 108, 111, 0 }.CopyTo(data, 40);
		return data;
	}
	public static void Run(Action<string, Action> test)
	{
		test("palette BGR channels and explicit opacity", () =>
		{
			var palette = new byte[768]; palette[0] = 10; palette[1] = 20; palette[2] = 30;
			Check(Palette.Parse(palette).ToRgba([0, 0], [255, 0]).SequenceEqual(new byte[] { 30, 20, 10, 255, 30, 20, 10, 0 }));
			Invalid(() => Palette.Parse(new byte[767])); Invalid(() => Palette.Parse(new byte[769]));
		});
		test("DC6 row orientation transparency and signed offsets", () =>
		{
			var bottom = Dc6Image.Parse(Dc6()).DecodeFrame(0, 0);
			Check(bottom.OffsetX == -5 && bottom.OffsetY == 7);
			Check(bottom.Indices.SequenceEqual(new byte[] { 0, 2, 0, 0, 1, 0 }));
			Check(bottom.Opacity.SequenceEqual(new byte[] { 0, 255, 0, 255, 255, 0 }));
			Check(Dc6Image.Parse(Dc6(true)).DecodeFrame(0, 0).Indices.SequenceEqual(new byte[] { 0, 1, 0, 0, 2, 0 }));
		});
		test("DC6 truncation and metadata bounds", () =>
		{
			for (int i = 0; i < 69; i++) { int length = i; Invalid(() => Dc6Image.Parse(Dc6().AsSpan(0, length))); }
			foreach (int offset in new[] { 0, 16, 20, 24, 32, 36, 56 })
			{ var data = Dc6(); U32(data, offset, uint.MaxValue); Invalid(() => Dc6Image.Parse(data)); }
		});
		test("DC6 malformed runs and missing row endings", () =>
		{
			foreach (byte code in new byte[] { 0, 4, 0x84 })
			{ var data = Dc6(); data[60] = code; Invalid(() => Dc6Image.Parse(data).DecodeFrame(0, 0)); }
			var truncated = Dc6(); U32(truncated, 56, 2); Invalid(() => Dc6Image.Parse(truncated).DecodeFrame(0, 0));
			var missing = Dc6(); U32(missing, 56, 8); Invalid(() => Dc6Image.Parse(missing).DecodeFrame(0, 0));
			var extra = Dc6(); U32(extra, 56, 10); Invalid(() => Dc6Image.Parse(extra).DecodeFrame(0, 0));
		});
		test("DC6 multiple directions and pixel budget", () =>
		{
			var one = Dc6(); var data = new byte[124]; one.AsSpan(0, 24).CopyTo(data);
			U32(data, 16, 2); U32(data, 24, 32); U32(data, 28, 80);
			one.AsSpan(28).CopyTo(data.AsSpan(32)); one.AsSpan(28).CopyTo(data.AsSpan(80));
			Check(Dc6Image.Parse(data).DecodeFrame(1, 0).OffsetX == -5);
			U32(data, 28, 32); Invalid(() => Dc6Image.Parse(data));
			U32(data, 28, 80); U32(data, 36, 4096); U32(data, 40, 4096); Invalid(() => Dc6Image.Parse(data));
		});
		test("text TBL active entries and explicit encoding", () =>
		{
			var table = StringTable.Parse(Tbl()); Check(table.Entries.Count == 1);
			Check(table.Entries[0].Key(Encoding.ASCII) == "K" && table.Entries[0].Value(Encoding.UTF8) == "hello");
			var bytes = Tbl(); bytes[42] = 0xff; Check(StringTable.Parse(bytes).Entries[0].ValueBytes.Span[0] == 0xff);
			bytes[23] = 0; Check(StringTable.Parse(bytes).Entries.Count == 0);
			bytes[8] = 0; Check(StringTable.Parse(bytes).Entries.Count == 0);
		});
		test("text TBL bounds indices and terminators", () =>
		{
			for (int i = 0; i < 48; i++) { int length = i; Invalid(() => StringTable.Parse(Tbl().AsSpan(0, length))); }
			foreach (int offset in new[] { 4, 9, 17, 30, 34 })
			{ var data = Tbl(); U32(data, offset, uint.MaxValue); Invalid(() => StringTable.Parse(data)); }
			// Many nodes aliasing a long string must not multiply work without a bound.
			var aliased = new byte[76958]; BinaryPrimitives.WriteUInt16LittleEndian(aliased.AsSpan(2), 600);
			U32(aliased, 4, 600); U32(aliased, 9, 11421); U32(aliased, 17, (uint)aliased.Length);
			for (int i = 0; i < 600; i++)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(aliased.AsSpan(21 + i * 2), (ushort)i);
				int at = 1221 + i * 17; aliased[at] = 1;
				BinaryPrimitives.WriteUInt16LittleEndian(aliased.AsSpan(at + 1), (ushort)i);
				U32(aliased, at + 7, 11421); U32(aliased, at + 11, 11423);
				BinaryPrimitives.WriteUInt16LittleEndian(aliased.AsSpan(at + 15), 65535);
			}
			Invalid(() => StringTable.Parse(aliased));
			var noKeyEnd = new byte[52]; Tbl().CopyTo(noKeyEnd, 0); U32(noKeyEnd, 17, 52); U32(noKeyEnd, 30, 48);
			noKeyEnd.AsSpan(48).Fill(255); Invalid(() => StringTable.Parse(noKeyEnd));
			foreach (int offset in new[] { 8, 21, 24, 38, 47 })
			{ var data = Tbl(); data[offset] = 255; Invalid(() => StringTable.Parse(data)); }
		});
		test("decoder dispatch keeps font tables and unrelated DAT unsupported", () =>
		{
			Check(AssetDecoders.Kind("data/global/ui/font/font16.tbl") == null);
			Check(AssetDecoders.Kind("unrelated.dat") == null);
			Check(AssetDecoders.Kind("DATA/GLOBAL/PALETTE/ACT1/PAL.DAT") == "palette");
			AssetDecoders.Validate("dc6", Dc6()); AssetDecoders.Validate("text_tbl", Tbl());
		});
	}
}
