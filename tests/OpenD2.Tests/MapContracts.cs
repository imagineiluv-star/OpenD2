using System.Buffers.Binary;
using OpenD2.Assets;

internal static class MapContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Map assertion failed."); }
	private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid map."); }
	private static void Range(Action action) { try { action(); } catch (ArgumentOutOfRangeException) { return; } throw new Exception("Expected invalid index."); }
	internal sealed record Block(short X, short Y, ushort Format, byte[] Data);
	internal sealed record Tile(int Orientation = 0, int Main = 1, int Sub = 0, short Roof = 0, byte[]? Flags = null, Block[]? Blocks = null);
	public static void Run(Action<string, Action> test)
	{
		test("DT1 isometric expected pixels, signed coordinates and input ownership", () =>
		{
			byte[] payload = Enumerable.Range(0, 256).Select(i => (byte)(1 + i % 254)).ToArray();
			var bytes = Dt1(new Tile(Blocks: [new(-2, -3, 1, payload)])); var set = Dt1Tileset.Parse(bytes); Array.Fill(bytes, (byte)0);
			var frame = set.DecodeTile(0); Check(frame.Width == 32 && frame.Height == 15 && frame.Left == -2 && frame.Top == -3);
			Check(frame.Indices.Take(14).All(p => p == 0) && frame.Indices.AsSpan(14, 4).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
			Check(frame.Indices[32 + 12] == 5 && frame.Indices[7 * 32] == 113 && frame.Indices.Count(p => p != 0) == 256);
			Check(set.Tiles[0].DeclaredHeight == -80 && set.DecodeTile(0).Indices.SequenceEqual(frame.Indices));
		});
		test("DT1 both RLE formats retain skips, zero pixels and row positions", () =>
		{
			foreach (ushort format in new ushort[] { 0x1001, 0x2005 })
			{
				var frame = Dt1Tileset.Parse(Dt1(new Tile(Blocks: [new(3, -8, format, [2, 3, 7, 0, 9, 0, 0, 1, 1, 5, 0, 0])]))).DecodeTile(0);
				Check(frame.Left == 3 && frame.Top == -8 && frame.Indices[2] == 7 && frame.Indices[3] == 0 && frame.Indices[4] == 9 && frame.Indices[33] == 5);
			}
		});
		test("DT1 multiple blocks use a stable union without height mutation", () =>
		{
			var set = Dt1Tileset.Parse(Dt1(new Tile(Orientation: 1, Blocks: [new(-16, -32, 0x1001, [0, 1, 7]), new(16, 0, 0x1001, [0, 1, 9])])));
			var a = set.DecodeTile(0); var b = set.DecodeTile(0);
			Check(a.Width == 64 && a.Height == 64 && a.Indices[0] == 7 && a.Indices[32 * 64 + 32] == 9 && a.Indices.SequenceEqual(b.Indices));
			Check(set.Tiles[0].DeclaredHeight == -80);
		});
		test("DT1 all truncated prefixes fail", () =>
		{
			byte[] raw = Dt1(); for (int i = 0; i < raw.Length; i++) { var prefix = raw[..i]; Bad(() => AssetDecoders.Validate("dt1", prefix)); }
		});
		test("DT1 versions, table pointers, dimensions and unknown codecs fail", () =>
		{
			foreach (int offset in new[] { 0, 4, 268, 272, 284, 288, 348, 352, 356, 382, 388 })
			{
				byte[] broken = Dt1(); U32(broken, offset, offset == 284 ? 0x80000000u : uint.MaxValue); Bad(() => AssetDecoders.Validate("dt1", broken));
			}
			var unknown = Dt1(); BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(380), 2); Bad(() => Dt1Tileset.Parse(unknown));
		});
		test("DT1 payload overlap and canvas budget fail before pixel allocation", () =>
		{
			var raw = Dt1(new Tile(Blocks: [new(0, 0, 1, new byte[256]), new(32, 0, 1, new byte[256])]));
			U32(raw, 372 + 20 + 16, 40); Bad(() => Dt1Tileset.Parse(raw));
			Bad(() => Dt1Tileset.Parse(Dt1(new Tile(Blocks: [new(short.MinValue, 0, 1, new byte[256]), new(short.MaxValue, 0, 1, new byte[256])]))));
			var big = new Block[] { new(0, 0, 0x1001, []), new(4064, 4064, 0x1001, []) };
			Bad(() => Dt1Tileset.Parse(Dt1(new Tile(Blocks: big), new(Main: 2, Blocks: big))));
		});
		test("DT1 malformed RLE packets and row overflow fail", () =>
		{
			foreach (byte[] payload in new byte[][] { [0], [0, 2, 9], [31, 2, 1, 2], [33, 0], new byte[66] })
				Bad(() => Dt1Tileset.Parse(Dt1(new Tile(Blocks: [new(0, 0, 0x1001, payload)]))).DecodeTile(0));
		});
		test("DT1 collision row mapping, all flags and API bounds", () =>
		{
			var flags = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray(); flags[20] = 255;
			var set = Dt1Tileset.Parse(Dt1(new Tile(Flags: flags))); var tile = set.Tiles[0];
			Check(tile.CollisionAt(0, 0) == 255 && tile.CollisionAt(4, 4) == 4 && tile.RawSubtileFlags.Span[0] == 0);
			Range(() => tile.CollisionAt(-1, 0)); Range(() => tile.CollisionAt(0, 5)); Range(() => set.DecodeTile(1));
		});
		test("DS1 v18 metadata, layer bits, objects, groups and unbound paths survive", () =>
		{
			var map = Ds1Map.Parse(Ds1(rich: true));
			Check(map.Width == 2 && map.Height == 1 && map.Version == 18 && map.Act == 3 && map.SubstitutionType == 1);
			Check(map.FileReferences.SequenceEqual(new[] { "tiles/a.dt1", "old.tg1" }) && map.Layers.Count == 5);
			var cell = map.Layers.First(l => l.Kind == MapLayerKind.Wall).Cells[0];
			Check(cell.Key == new TileKey(17, 35, 1) && cell.Hidden && cell.RawOrientation == 0x10001);
			Check(map.Objects.Count == 2 && map.Objects[1].Flags == 9 && map.Groups[0].Unknown == 7);
			Check(map.Paths.Count == 1 && map.Paths[0].X == 99 && map.Paths[0].Points[1] == new MapPathPoint(3, 4, 6));
		});
		test("DS1 version-dependent headers, layer order, flags and path actions", () =>
		{
			for (int version = 1; version <= 18; version++)
			{
				var map = Ds1Map.Parse(Ds1(version: version, rich: true));
				Check(map.Version == version && map.Layers.Count == (version < 4 || version >= 10 ? (version >= 16 ? 5 : 4) : 3));
				Check(map.Layers.Single(l => l.Kind == MapLayerKind.Floor && l.Index == 0).Cells[1].Key == new TileKey(1, 0, 0));
				if (version >= 2) Check(map.Objects[0].Flags == (version > 5 ? 8u : 0u));
				if (version == 14) Check(map.Paths[0].Points[0].Action == 1);
			}
		});
		test("DS1 all prefixes, missing optional sections and trailing data fail", () =>
		{
			byte[] raw = Ds1(rich: true); for (int i = 0; i < raw.Length; i++) { var prefix = raw[..i]; Bad(() => Ds1Map.Parse(prefix)); }
			Bad(() => Ds1Map.Parse([.. raw, 0]));
		});
		test("DS1 invalid counts, act, dimensions and string terminators fail", () =>
		{
			foreach (int offset in new[] { 0, 4, 8, 12, 16, 20, 24, 28, 64, 68 })
			{ var raw = Ds1(); U32(raw, offset, uint.MaxValue); Bad(() => Ds1Map.Parse(raw)); }
			var big = Ds1(); U32(big, 4, 1023); U32(big, 8, 1023); Bad(() => Ds1Map.Parse(big));
			var text = Ds1(); U32(text, 20, 1); Array.Fill(text, (byte)65, 24, text.Length - 24); Bad(() => Ds1Map.Parse(text));
		});
		test("DS1 legacy orientation mapping and rejection", () =>
		{
			var map = Ds1Map.Parse(Ds1(version: 6, orientation: 5)); Check(map.Layers[0].Cells[0].Orientation == 3);
			Bad(() => Ds1Map.Parse(Ds1(version: 6, orientation: 255)));
		});
		test("Map assembly reuses decoded tiles and preserves projection offsets", () =>
		{
			var scene = MapScene.Build(Ds1Map.Parse(Ds1()), [new("floor", Dt1Tileset.Parse(Dt1()))]);
			Check(scene.Images.Count == 1 && scene.Placements.Count == 2 && scene.MissingTiles == 0);
			Check(scene.Placements[0].PixelX == -80 && scene.Placements[0].PixelY == 0 && scene.Placements[1].PixelX == 0 && scene.Placements[1].PixelY == 40);
			Check(scene.CollisionAt(9, 4).Known && !scene.CollisionAt(9, 4).BlocksWalk); Range(() => scene.CollisionAt(10, 0));
		});
		test("Map floor and wall collision union uses the selected tile variant", () =>
		{
			byte[] floor = new byte[25], wall = new byte[25]; floor[20] = 2; wall[20] = 8;
			var set = Dt1Tileset.Parse(Dt1(new Tile(Flags: floor), new(Orientation: 1, Flags: wall, Roof: 5, Blocks: [new(0, -32, 0x1001, [0, 1, 2])])));
			var scene = MapScene.Build(Ds1Map.Parse(Ds1(wall: 0x00100001)), [new("both", set)]);
			Check(scene.CollisionAt(0, 0) == new MapCollision(10, true) && scene.CollisionAt(0, 0).BlocksWalk);
			Check(scene.Placements[^1].Key.Orientation == 1 && scene.Placements[^1].PixelY == 43);
		});
		test("Map missing tiles and no floor keep collision unknown", () =>
		{
			var none = MapScene.Build(Ds1Map.Parse(Ds1()), []);
			Check(none.MissingTiles == 2 && none.Images.Count == 0 && !none.CollisionAt(0, 0).Known && none.CollisionAt(0, 0).BlocksWalk);
			var partial = MapScene.Build(Ds1Map.Parse(Ds1(wall: 0x00100001)), [new("floor", Dt1Tileset.Parse(Dt1()))]);
			Check(partial.MissingTiles == 1 && !partial.CollisionAt(0, 0).Known && partial.CollisionAt(5, 0).Known);
			var empty = MapScene.Build(Ds1Map.Parse(Ds1(floor: 0)), [new("floor", Dt1Tileset.Parse(Dt1()))]); Check(empty.MissingTiles == 0 && !empty.CollisionAt(0, 0).Known);
		});
		test("Map duplicate keys are deterministic and hidden cells stay hidden", () =>
		{
			var set = Dt1Tileset.Parse(Dt1());
			var scene = MapScene.Build(Ds1Map.Parse(Ds1(floor: 0x80100001)), [new("first", set), new("second", set)]);
			Check(scene.DuplicateKeys == 1 && scene.Placements.Count == 1 && scene.Images[0].Source == "first" && !scene.CollisionAt(0, 0).Known);
		});
		test("Map tileset and aggregate decoded pixel budgets are enforced", () =>
		{
			var map = Ds1Map.Parse(Ds1()); var set = Dt1Tileset.Parse(Dt1());
			Bad(() => MapScene.Build(map, Enumerable.Repeat(new MapTileset("test", set), 33).ToArray()));
			var huge = new Block[] { new(0, 0, 0x1001, []), new(4064, 4064, 0x1001, []) };
			var a = Dt1Tileset.Parse(Dt1(new Tile(Blocks: huge))); var b = Dt1Tileset.Parse(Dt1(new Tile(Orientation: 1, Blocks: huge)));
			Bad(() => MapScene.Build(Ds1Map.Parse(Ds1(wall: 0x00100001)), [new("a", a), new("b", b)]));
		});
		test("Map formats participate in decoder dispatch and validation", () =>
		{
			Check(AssetDecoders.Kind("DATA/tiles/A.DT1") == "dt1" && AssetDecoders.Kind("a.DS1") == "ds1");
			AssetDecoders.Validate("dt1", Dt1()); AssetDecoders.Validate("ds1", Ds1());
		});
	}
	public static byte[] Dt1(params Tile[] tiles)
	{
		if (tiles.Length == 0) tiles = [new()];
		var blocks = tiles.Select(t => t.Blocks ?? [new Block(0, 0, 1, Enumerable.Repeat((byte)1, 256).ToArray())]).ToArray();
		int size = 276 + tiles.Length * 96 + blocks.Sum(b => b.Length * 20 + b.Sum(v => v.Data.Length));
		byte[] data = new byte[size]; U32(data, 0, 7); U32(data, 4, 6); U32(data, 268, (uint)tiles.Length); U32(data, 272, 276);
		int pointer = 276 + tiles.Length * 96;
		for (int i = 0; i < tiles.Length; i++)
		{
			var t = tiles[i]; int h = 276 + i * 96, length = blocks[i].Length * 20 + blocks[i].Sum(b => b.Data.Length);
			U32(data, h, 3); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(h + 4), t.Roof);
			U32(data, h + 8, unchecked((uint)-80)); U32(data, h + 12, 160); U32(data, h + 20, (uint)t.Orientation);
			U32(data, h + 24, (uint)t.Main); U32(data, h + 28, (uint)t.Sub); U32(data, h + 32, 1);
			(t.Flags ?? new byte[25]).CopyTo(data, h + 40); U32(data, h + 72, (uint)pointer); U32(data, h + 76, (uint)length); U32(data, h + 80, (uint)blocks[i].Length);
			int offset = blocks[i].Length * 20;
			for (int j = 0; j < blocks[i].Length; j++)
			{
				var b = blocks[i][j]; int bh = pointer + j * 20;
				BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(bh), b.X); BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(bh + 2), b.Y);
				BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bh + 8), b.Format); U32(data, bh + 10, (uint)b.Data.Length); U32(data, bh + 16, (uint)offset);
				b.Data.CopyTo(data, pointer + offset); offset += b.Data.Length;
			}
			pointer += length;
		}
		return data;
	}
	public static byte[] Ds1(int version = 18, bool rich = false, uint floor = 0x00100001, uint wall = 0, uint orientation = 1)
	{
		using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
		w.Write(version); w.Write(1); w.Write(0);
		if (version >= 8) w.Write(rich ? 2 : 0); if (version >= 10) w.Write(rich ? 1 : 0);
		if (version >= 3) { w.Write(rich ? 2 : 0); if (rich) w.Write(System.Text.Encoding.Latin1.GetBytes("tiles/a.dt1\0old.tg1\0")); }
		if (version is >= 9 and <= 13) w.Write(new byte[8]);
		if (version >= 4) w.Write(1); if (version >= 16) w.Write(rich ? 2 : 1);
		void Wall() { w.Write(rich ? 0x81102301u : wall); w.Write(0); }
		void Floor() { w.Write(floor); w.Write(0x00100001); }
		void Orientation() { w.Write(rich ? 0x10001u : orientation); w.Write(0); }
		void Empty() { w.Write(0); w.Write(0); }
		if (version < 4) { Wall(); Floor(); Orientation(); Empty(); Empty(); }
		else { Wall(); Orientation(); Floor(); if (version >= 16 && rich) Empty(); Empty(); if (version >= 10 && rich) Empty(); }
		if (version >= 2)
		{
			w.Write(rich ? 2 : 0);
			if (rich) for (int i = 0; i < 2; i++) { w.Write(1); w.Write(12 + i); w.Write(1); w.Write(2); if (version > 5) w.Write(8 + i); }
		}
		if (version >= 12 && rich)
		{
			if (version >= 18) w.Write(0); w.Write(1); w.Write(0); w.Write(0); w.Write(1); w.Write(1); if (version >= 13) w.Write(7);
		}
		if (version >= 14)
		{
			w.Write(rich ? 1 : 0);
			if (rich) { w.Write(2); w.Write(99); w.Write(99); for (int p = 0; p < 2; p++) { w.Write(2 + p); w.Write(3 + p); if (version >= 15) w.Write(5 + p); } }
		}
		return stream.ToArray();
	}
	private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}
