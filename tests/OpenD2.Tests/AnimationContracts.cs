using System.Buffers.Binary;
using OpenD2.Assets;

internal static class AnimationContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Animation assertion failed."); }
	private static void Bad(Action action)
	{ try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid animation."); }
	public static void Run(Action<string, Action> test)
	{
		test("DCC delta palettes, transparency, signed origin and direction isolation", () =>
		{
			var source = Dcc(); var dcc = DccAnimation.Parse(source); Array.Fill(source, (byte)0);
			Check(dcc.Directions == 2 && dcc.FramesPerDirection == 2);
			var frames = dcc.DecodeDirection(0);
			Check(frames[0].Left == -1 && frames[0].Top == -2 && frames[0].Width == 2 && frames[0].Height == 2);
			Check(frames[0].Indices.SequenceEqual(new byte[] { 1, 0, 1, 0 }));
			Check(frames[1].Indices.SequenceEqual(new byte[] { 0, 1, 0, 1 }));
			Check(dcc.DecodeDirection(1)[0].Indices.SequenceEqual(new byte[] { 0, 1, 0, 1 }));
		});
		test("DCC raw codes and equal-cell reuse", () =>
		{
			var frames = DccAnimation.Parse(Dcc(raw: true, equal: true)).DecodeDirection(0);
			Check(frames[0].Indices.SequenceEqual(frames[1].Indices));
			Check(frames[0].Indices.SequenceEqual(new byte[] { 1, 0, 1, 0 }));
		});
		test("DCC 5-pixel cells, displaced reuse, resize clearing and optional bytes", () =>
		{
			var moved = DccAnimation.Parse(Dcc(equal: true, move: true, optional: true)).DecodeDirection(0);
			Check(moved[0].Width == 2 && moved[1].Left == 0 && moved[1].Indices.SequenceEqual(moved[0].Indices));
			Check(DccAnimation.Parse(Dcc(wide: true)).DecodeDirection(0)[0].Width == 5);
			var resized = DccAnimation.Parse(Dcc(equal: true, resize: true)).DecodeDirection(0);
			Check(resized[1].Indices.All(x => x == 0));
		});
		test("DCC all input prefixes and invalid metadata are rejected", () =>
		{
			var raw = Dcc();
			for (int length = 0; length < raw.Length; length++)
			{
				var prefix = raw[..length]; Bad(() => { var a = DccAnimation.Parse(prefix); for (int d = 0; d < a.Directions; d++) a.DecodeDirection(d); });
			}
			foreach (int offset in new[] { 0, 1, 2, 3, 7, 15, 19 })
			{ var broken = (byte[])raw.Clone(); broken[offset] = 255; Bad(() => { var a = DccAnimation.Parse(broken); a.DecodeDirection(0); }); }
			Bad(() => DccAnimation.Parse(Dcc(bottomUp: true)).DecodeDirection(0));
			Bad(() => DccAnimation.Parse(Dcc(badCode: true)).DecodeDirection(0));
			Bad(() => DccAnimation.Parse(Dcc(extraMask: true)).DecodeDirection(0));
		});
		test("DCC palette updates, two-bit selectors and constant fills", () =>
		{
			var changed = DccAnimation.Parse(Dcc(change: true)).DecodeDirection(0);
			Check(changed[1].Indices.SequenceEqual(new byte[] { 0, 2, 0, 2 }));
			Check(DccAnimation.Parse(FourColors(false)).DecodeDirection(0)[0].Indices.SequenceEqual(new byte[] { 4, 3, 2, 1 }));
			Check(DccAnimation.Parse(FourColors(true)).DecodeDirection(0)[0].Indices.SequenceEqual(new byte[] { 3, 3, 3, 3 }));
		});
		test("DCC multi-row cell grid and more than sixteen cached cells", () =>
		{
			var decoded = DccAnimation.Parse(Grid()).DecodeDirection(0);
			byte[] expected = Enumerable.Range(0, 21 * 18).Select(i => (byte)((i % 21 + i / 21) % 2 == 0 ? 1 : 0)).ToArray();
			Check(decoded[0].Indices.SequenceEqual(expected) && decoded[1].Indices.SequenceEqual(expected));
		});
		test("DCC dimension, coordinate and decoded-pixel budgets fail before allocation", () =>
		{
			foreach (var size in new[] { (0, 1, 0), (4097, 1, 0), (4096, 4096, 0), (2, 1, int.MaxValue) })
			{
				var b = new Writer(); b.Put(0, 32); b.Put(0, 2);
				foreach (int code in new[] { 0, 15, 15, 15, 15, 0, 0 }) b.Put(code, 4);
				for (int f = 0; f < 2; f++) { b.Put(size.Item1, 32); b.Put(size.Item2, 32); b.Put(size.Item3, 32); b.Put(0, 32); b.Put(0, 1); }
				var raw = Wrap(b.Bytes(), 2); Bad(() => DccAnimation.Parse(raw).DecodeDirection(0));
			}
		});
		test("COF preserves metadata, events and per-frame component order", () =>
		{
			var bytes = Cof(); var cof = CofAnimation.Parse(bytes); Array.Fill(bytes, (byte)0);
			Check(cof.Directions == 2 && cof.FramesPerDirection == 2 && cof.Speed == 256);
			Check(cof.Layers[0].Component == 1 && cof.Layers[0].WeaponClass == "hth");
			Check(cof.Events.Span.SequenceEqual(new byte[] { 0, 1 }));
			Check(cof.DrawOrder(0, 0).SequenceEqual(new byte[] { 1, 0 }) && cof.DrawOrder(0, 1).SequenceEqual(new byte[] { 0, 1 }));
		});
		test("COF truncation, dimensions, duplicate components and order corruption", () =>
		{
			var bytes = Cof(); for (int i = 0; i < bytes.Length; i++) { var prefix = bytes[..i]; Bad(() => CofAnimation.Parse(prefix)); }
			foreach (int at in new[] { 0, 1, 2, 3, 28, 36, 48 })
			{ var broken = (byte[])bytes.Clone(); broken[at] = 255; Bad(() => CofAnimation.Parse(broken)); }
			var duplicate = Cof(); duplicate[37] = duplicate[28]; Bad(() => CofAnimation.Parse(duplicate));
			duplicate = Cof(); duplicate[49] = duplicate[48]; Bad(() => CofAnimation.Parse(duplicate));
		});
		test("COF composition uses frame order, original offsets and transparent holes", () =>
		{
			var a = new IndexedFrame(2, 1, -1, -2, [1, 0]); var b = new IndexedFrame(2, 1, -1, -2, [2, 2]);
			var clip = AnimationClip.Compose(CofAnimation.Parse(Cof()), 0, new Dictionary<byte, IReadOnlyList<IndexedFrame>> { [0] = new[] { a, a }, [1] = new[] { b, b } });
			Check(clip.Left == -1 && clip.Top == -2 && clip.Frames[0].Indices.SequenceEqual(new byte[] { 1, 2 }) && clip.Frames[1].Indices.SequenceEqual(new byte[] { 2, 2 }));
			Check(clip.FrameAt(0, 10) == 0 && clip.FrameAt(0.11, 10) == 1 && clip.FrameAt(0.21, 10) == 0);
			var moved = AnimationClip.Single(new[] { a, a with { Left = 1, Top = -1 } });
			Check(moved.Width == 4 && moved.Height == 2 && moved.Frames[0].Indices[0] == 1 && moved.Frames[1].Indices[6] == 1);
			Check(moved.ToRgba(0, Palette.Parse(new byte[768]))[7] == 0);
		});
		test("Animation refuses incomplete maps, effects, inconsistent frames and oversized canvases", () =>
		{
			var a = new IndexedFrame(1, 1, 0, 0, [1]);
			Bad(() => AnimationClip.Compose(CofAnimation.Parse(Cof()), 0, new Dictionary<byte, IReadOnlyList<IndexedFrame>> { [0] = new[] { a, a } }));
			var effect = Cof(); effect[31] = 1;
			Bad(() => AnimationClip.Compose(CofAnimation.Parse(effect), 0, new Dictionary<byte, IReadOnlyList<IndexedFrame>> { [0] = new[] { a, a }, [1] = new[] { a, a } }));
			Bad(() => AnimationClip.Single(new[] { a, a with { Left = int.MaxValue } }));
			Bad(() => AnimationClip.Single(new[] { a with { Width = 2 } }));
		});
		test("DCC and COF audit dispatch validates streams without claiming composition", () =>
		{
			Check(AssetDecoders.Kind("data/global/chars/am/test.DCC") == "dcc" && AssetDecoders.Kind("test.cof") == "cof");
			AssetDecoders.Validate("dcc", Dcc()); AssetDecoders.Validate("cof", Cof());
			Bad(() => AssetDecoders.Validate("dcc", Dcc(badCode: true)));
		});
	}
	internal static byte[] Cof()
	{
		var bytes = new byte[56]; bytes[0] = 2; bytes[1] = 2; bytes[2] = 2; bytes[3] = 20; bytes[25] = 1;
		bytes[28] = 1; bytes[37] = 0; "hth"u8.CopyTo(bytes.AsSpan(33)); "hth"u8.CopyTo(bytes.AsSpan(42));
		bytes[47] = 1; new byte[] { 1, 0, 0, 1, 0, 1, 1, 0 }.CopyTo(bytes, 48); return bytes;
	}
	// Owned small encoder, only for deterministic fixtures. Pixel assertions above are explicit.
	internal static byte[] Dcc(bool raw = false, bool change = false, bool wide = false, bool equal = false, bool move = false, bool resize = false, bool optional = false, bool bottomUp = false, bool badCode = false, bool extraMask = false)
	{
		byte[] Direction(int direction)
		{
			var bits = new Writer(); bits.Put(0, 32); bits.Put((raw ? 1 : 0) | (equal ? 2 : 0), 2);
			foreach (int code in new[] { 0, 3, 2, 3, 3, optional ? 1 : 0, 0 }) bits.Put(code, 4);
			for (int f = 0; f < 2; f++)
			{
				bits.Put(wide ? 5 : resize && f == 1 ? 1 : 2, 4); bits.Put(2, 2);
				bits.Put(move && f == 1 ? 0 : -1, 4); bits.Put(-1, 4);
				if (optional) bits.Put(1, 1); bits.Put(bottomUp ? 1 : 0, 1);
			}
			if (optional) { bits.Align(); bits.Put(0x1234, 16); }
			if (equal) bits.Put(1, 20); bits.Put(equal ? 0 : extraMask ? 8 : 4, 20);
			if (raw) { bits.Put(1, 20); bits.Put(16, 20); }
			for (int i = 0; i < 256; i++) bits.Put(i < (change ? 3 : 2) ? 1 : 0, 1);
			if (equal) bits.Put(1, 1); else { bits.Put(change ? 1 : 0, 4); if (extraMask) bits.Put(0, 4); }
			if (raw) { bits.Put(1, 1); bits.Put(badCode ? 7 : 1, 8); bits.Put(1, 8); }
			else { bits.Put(badCode ? 7 : 1, 4); bits.Put(0, 4); }
			if (change) bits.Put(2, 4);
			int pixels = wide ? 10 : 4;
			for (int p = 0; p < pixels; p++) bits.Put((p + direction) % 2, 1);
			if (!equal) for (int p = 0; p < pixels; p++) bits.Put((p + direction + 1) % 2, 1);
			return bits.Bytes();
		}
		var a = Direction(0); var b = Direction(1); var result = new byte[23 + a.Length + b.Length];
		result[0] = 0x74; result[1] = 6; result[2] = 2;
		Set(result, 3, 2); Set(result, 7, 1); Set(result, 15, 23); Set(result, 19, (uint)(23 + a.Length)); a.CopyTo(result, 23); b.CopyTo(result, 23 + a.Length); return result;
	}
	private static byte[] Grid()
	{
		var bits = new Writer(); bits.Put(0, 32); bits.Put(2, 2);
		foreach (int code in new[] { 0, 4, 4, 0, 0, 0, 0 }) bits.Put(code, 4);
		for (int f = 0; f < 2; f++) { bits.Put(21, 6); bits.Put(18, 6); bits.Put(0, 1); }
		bits.Put(25, 20); bits.Put(0, 20);
		for (int i = 0; i < 256; i++) bits.Put(i < 2 ? 1 : 0, 1);
		for (int i = 0; i < 25; i++) bits.Put(1, 1);
		for (int i = 0; i < 25; i++) { bits.Put(1, 4); bits.Put(0, 4); }
		for (int row = 0; row < 5; row++) for (int col = 0; col < 5; col++)
			for (int y = 0; y < (row == 4 ? 2 : 4); y++) for (int x = 0; x < (col == 4 ? 5 : 4); x++) bits.Put((x + y) % 2, 1);
		return Wrap(bits.Bytes(), 2);
	}
	private static byte[] FourColors(bool constant)
	{
		var bits = new Writer(); bits.Put(0, 32); bits.Put(0, 2);
		foreach (int code in new[] { 0, 2, 2, 0, 0, 0, 0 }) bits.Put(code, 4);
		bits.Put(2, 2); bits.Put(2, 2); bits.Put(0, 1); bits.Put(0, 20);
		for (int i = 0; i < 256; i++) bits.Put(constant ? (i == 3 ? 1 : 0) : (i < 5 ? 1 : 0), 1);
		if (constant) bits.Put(0, 4);
		else { for (int i = 0; i < 4; i++) bits.Put(1, 4); for (int i = 0; i < 4; i++) bits.Put(i, 2); }
		return Wrap(bits.Bytes(), 1);
	}
	private static byte[] Wrap(byte[] direction, int frames)
	{
		var raw = new byte[19 + direction.Length]; raw[0] = 0x74; raw[1] = 6; raw[2] = 1;
		Set(raw, 3, (uint)frames); Set(raw, 7, 1); Set(raw, 15, 19); direction.CopyTo(raw, 19); return raw;
	}
	private static void Set(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), value);
	private sealed class Writer
	{
		private readonly List<byte> bytes = []; private int position;
		public void Put(int value, int width)
		{ for (int i = 0; i < width; i++, position++) { if (position % 8 == 0) bytes.Add(0); if (((uint)value & (1u << i)) != 0) bytes[^1] |= (byte)(1 << (position % 8)); } }
		public void Align() { while (position % 8 != 0) Put(0, 1); }
		public byte[] Bytes() => bytes.ToArray();
	}
}
