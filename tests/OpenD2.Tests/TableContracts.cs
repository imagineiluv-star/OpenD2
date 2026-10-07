using System.Buffers.Binary;
using System.Text;
using OpenD2.Assets;

internal static class TableContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Table/cache assertion failed."); }
	private static void Bad(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected invalid table."); }
	private static byte[] Text(string value) => Encoding.Latin1.GetBytes(value);
	public static void Run(string root, Action<string, Action> test)
	{
		test("TXT preserves empty, unknown, duplicate columns, quoted text and Latin1 bytes", () =>
		{
			var table = ExcelTextTable.Parse(Text("Name\tValue\tValue\t\r\n\"caf\u00e9\"\t\t7\t\r\nshort\r\n"));
			Check(table.Columns.Count == 4 && table.Rows.Count == 2 && table.Rows[0][0] == "\"caf\u00e9\"" && table.Rows[0][1] == "" && table.Rows[1][3] == "");
			Check(table.LineNumbers.SequenceEqual(new[] { 2, 3 })); Bad(() => table.Column("value")); Bad(() => table.Column("missing"));
		});
		test("TXT UTF8 BOM, mixed newlines and signed DWORD masks", () =>
		{
			var table = ExcelTextTable.Parse([239, 187, 191, .. Encoding.UTF8.GetBytes("Name\tMask\n한글\t-1\rnext\t4294967295\r\nblank\t\n")]);
			Check(table.Value(0, "name") == "한글" && table.Number(0, "mask") == uint.MaxValue && table.Number(1, "Mask") == uint.MaxValue && table.Number(2, "Mask") == 0);
			foreach (string value in new[] { "4294967296", "-2147483649", "1.5", "0x10", "1oops" }) Bad(() => ExcelTextTable.Parse(Text("N\n" + value)).Number(0, "N"));
		});
		test("TXT rejects malformed encoding, header, rows and oversized cells", () =>
		{
			foreach (byte[] bad in new byte[][] { [], [239, 187, 191, 255], [65, 0], Text("\t\n"), Text("A\n1\t2"), Text("A\n" + new string('x', 16385)), Text(new string('x', 257)) }) Bad(() => ExcelTextTable.Parse(bad));
			Bad(() => ExcelTextTable.Parse(Text(string.Join('\t', Enumerable.Repeat("A", 513)))));
		});
		test("TXT row and aggregate cell budgets apply before retaining rows", () =>
		{
			Bad(() => ExcelTextTable.Parse(Text("A\n" + string.Concat(Enumerable.Repeat("x\n", ExcelTextTable.MaxRows + 1)))));
			string header = string.Join('\t', Enumerable.Range(0, 512));
			Bad(() => ExcelTextTable.Parse(Text(header + "\n" + string.Concat(Enumerable.Repeat("x\n", 2049)))));
		});
		test("BIN explicit layouts, raw field preservation and owned input", () =>
		{
			var bytes = Bin(MapBinSchema.LevelDefs110f, 2); U32(bytes, 4 + 0x34, 7); bytes[5] = 0xEF;
			var table = ExcelBinTable.Parse(bytes, MapBinSchema.LevelDefs110f); Array.Fill(bytes, (byte)0);
			Check(table.Count == 2 && table.RecordSize == 156 && table.Number(0, 0x34) == 7 && table.Record(0)[1] == 0xEF);
			Bad(() => table.Number(0, 154)); Bad(() => table.Text(0, -1, 60));
		});
		test("BIN every truncated prefix, trailing bytes, record count overflow and wrong schema fail", () =>
		{
			foreach (var schema in Enum.GetValues<MapBinSchema>())
			{
				var bytes = Bin(schema, 1); for (int i = 0; i < bytes.Length; i++) { var prefix = bytes[..i]; Bad(() => ExcelBinTable.Parse(prefix, schema)); }
				Bad(() => ExcelBinTable.Parse([.. bytes, 0], schema)); U32(bytes, 0, uint.MaxValue); Bad(() => ExcelBinTable.Parse(bytes, schema));
			}
			Bad(() => ExcelBinTable.Parse(Bin(MapBinSchema.LvlTypes110f, 1), MapBinSchema.LvlPrest110f));
		});
		test("BIN string terminator and schema/index API bounds", () =>
		{
			var bytes = Bin(MapBinSchema.LvlTypes110f, 1); bytes.AsSpan(4, 60).Fill(65);
			Bad(() => ExcelBinTable.Parse(bytes, MapBinSchema.LvlTypes110f).Text(0, 0, 60));
			try { ExcelBinTable.Parse(bytes, (MapBinSchema)99); throw new Exception("Expected schema rejection."); } catch (ArgumentOutOfRangeException) { }
			try { ExcelBinTable.Parse(bytes, MapBinSchema.LvlTypes110f).Record(1); throw new Exception("Expected index rejection."); } catch (ArgumentOutOfRangeException) { }
		});
		test("TXT and BIN map projections resolve the same paths including bit31", () =>
		{
			var txt = Load(TxtFiles(), MapTableMode.Txt); var bin = Load(BinFiles(), MapTableMode.Bin110f);
			foreach (var tables in new[] { txt, bin })
			{
				Check(tables.LevelCount == 2 && tables.TypeCount == 2 && tables.PresetCount == 1 && tables.Validate().Count == 0);
				var plan = tables.Resolve(1, 7);
				Check(plan.MapPath == "data\\global\\tiles\\act1\\town.ds1" && plan.Tilesets.SequenceEqual(new[] { "data\\global\\tiles\\act1\\floor.dt1", "data\\global\\tiles\\act1\\edge.dt1" }));
				Check(tables.Sources.Count == 3 && tables.Sources.All(s => s.Sha256.Length == 64 && s.Bytes > 0));
			}
		});
		test("TXT map projection skips Expansion separators and detects duplicate IDs", () =>
		{
			var files = TxtFiles(); files["levels.txt"] = Text("Id\tLevelType\n0\t0\nExpansion\t\n1\t1\n");
			Check(Load(files, MapTableMode.Txt).Resolve(1, 7).LevelType == 1);
			files["levels.txt"] = Text("Id\tLevelType\n1\t0\n1\t1\n"); Bad(() => Load(files, MapTableMode.Txt));
			files = TxtFiles(); files["lvlprest.txt"] = Text(Encoding.Latin1.GetString(files["lvlprest.txt"]) + "7\t1\t0\t0\n"); Bad(() => Load(files, MapTableMode.Txt));
		});
		test("Map reference audit reports missing levels, types, files and mask slots", () =>
		{
			var files = TxtFiles(); files["levels.txt"] = Text("Id\tLevelType\n0\t99\n1\t1\n");
			files["lvlprest.txt"] = Text(PresetHeader + "7\t1\t2\t3\tact1/town.ds1\n8\t99\t1\t1\tact1/town.ds1\n9\t1\t1\t0\tact1/town.ds1\n");
			var issues = Load(files, MapTableMode.Txt).Validate();
			Check(new[] { "missing_level_type", "missing_map_path", "missing_dt1_slot", "missing_level", "empty_dt1_mask" }.All(code => issues.Any(i => i.Code == code && i.IsError)));
			Bad(() => Load(files, MapTableMode.Txt).Resolve(1, 7));
		});
		test("Context-dependent presets require the containing level without guessing", () =>
		{
			var files = TxtFiles(); files["lvlprest.txt"] = Text(PresetHeader + "7\t0\t1\t1\tact1/town.ds1\n");
			var tables = Load(files, MapTableMode.Txt);
			Check(tables.Validate().Single().Code == "context_required" && !tables.Validate().Single().IsError && tables.Resolve(1, 7).LevelType == 1);
			Bad(() => tables.Resolve(99, 7)); Bad(() => tables.Resolve(1, 99)); Bad(() => tables.Resolve(1, 7, -1)); Bad(() => tables.Resolve(1, 7, 1));
			Bad(() => Load(TxtFiles(), MapTableMode.Txt).Resolve(0, 7));
		});
		test("Map projection rejects traversal, wrong file kinds and out-of-range fields", () =>
		{
			foreach (string path in new[] { "../town.ds1", "a/../town.ds1", "C:/town.ds1", "/town.ds1", "a//town.ds1", "town.exe" })
			{ var files = TxtFiles(); files["lvlprest.txt"] = Text(PresetHeader + $"7\t1\t1\t1\t{path}\n"); Bad(() => Load(files, MapTableMode.Txt)); }
			foreach (string row in new[] { "7\t1\t7\t1\ta.ds1", "7\t-1\t1\t1\ta.ds1", "65536\t1\t1\t1\ta.ds1" })
			{ var files = TxtFiles(); files["lvlprest.txt"] = Text(PresetHeader + row + "\n"); Bad(() => Load(files, MapTableMode.Txt)); }
		});
		test("Explicit BIN mode does not fall back to TXT on missing or corrupt BIN", () =>
		{
			var requests = new List<string>();
			Bad(() => MapTables.Load(path => { requests.Add(path); return [1, 2]; }, MapTableMode.Bin110f));
			Check(requests.SequenceEqual(new[] { "data\\global\\excel\\leveldefs.bin" }));
		});
		test("Map tables integrate with read-only MPQs and preserve patch precedence", () =>
		{
			string directory = Path.Combine(root, "table-mpq"); Directory.CreateDirectory(directory);
			var files = TxtFiles(); string[] archives = ["d2data.mpq", "d2exp.mpq", "d2char.mpq"]; int a = 0;
			foreach (var (name, bytes) in files) Check(MpqContracts.Fixture(Path.Combine(directory, archives[a++]), "data\\global\\excel\\" + name, bytes, (uint)bytes.Length, 1) == 0);
			string first = Path.Combine(directory, archives[0]); byte[] before = File.ReadAllBytes(first);
			var tables = MapTables.Load(path => AssetDecoders.ReadFromInstall(directory, path), MapTableMode.Txt);
			Check(tables.Resolve(1, 7).Tilesets.Count == 2 && File.ReadAllBytes(first).SequenceEqual(before));
			var audit = AssetInventory.Scan(directory, new AuditOptions(Decode: true));
			Check(audit.Entries.Count(e => e.Format == "txt" && e.DecodeStatus == "validated") == 3 && !audit.Complete);
			byte[] corrupt = [0]; Check(MpqContracts.Fixture(Path.Combine(directory, "patch_d2.mpq"), "data\\global\\excel\\levels.txt", corrupt, 1, 1) == 0);
			Bad(() => MapTables.Load(path => AssetDecoders.ReadFromInstall(directory, path), MapTableMode.Txt));
		});
		test("Tile cache reuses content across parsed instances and isolates caller mutations", () =>
		{
			var cache = new TileFrameCache(); byte[] bytes = MapContracts.Dt1(); var set = Dt1Tileset.Parse(bytes);
			var first = cache.GetFrame(set, 0); byte expected = first.Indices[14]; first.Indices[14] = 99;
			var second = cache.GetFrame(Dt1Tileset.Parse(bytes), 0); Check(second.Indices[14] == expected && cache.Stats.Hits == 1 && cache.Stats.Misses == 1);
			second.Indices[14] = 55; Check(cache.GetFrame(set, 0).Indices[14] == expected);
		});
		test("Tile cache source edits and tile index separate cached results", () =>
		{
			var cache = new TileFrameCache(); var raw = MapContracts.Dt1(); var a = Dt1Tileset.Parse(raw);
			cache.GetFrame(a, 0); raw[^1] = 9; var b = Dt1Tileset.Parse(raw); var fresh = cache.GetFrame(b, 0);
			Check(a.ContentHash != b.ContentHash && fresh.Indices.Contains((byte)9) && cache.Stats.Misses == 2);
			var multiple = Dt1Tileset.Parse(MapContracts.Dt1(new(), new(Main: 2))); cache.GetFrame(multiple, 0); cache.GetFrame(multiple, 1); Check(cache.Stats.Entries == 4);
		});
		test("Tile cache byte LRU promotes hits and evicts the least recent entry", () =>
		{
			var sets = Enumerable.Range(1, 3).Select(i => Dt1Tileset.Parse(MapContracts.Dt1(new MapContracts.Tile(Main: i)))).ToArray();
			var probe = new TileFrameCache(); probe.GetFrame(sets[0], 0); long charge = probe.Stats.RetainedBytes;
			var cache = new TileFrameCache(charge * 2); cache.GetFrame(sets[0], 0); cache.GetFrame(sets[1], 0); cache.GetFrame(sets[0], 0); cache.GetFrame(sets[2], 0);
			Check(cache.Stats.Entries == 2 && cache.Stats.Evictions == 1 && cache.Stats.RetainedBytes <= cache.Stats.BudgetBytes);
			cache.GetFrame(sets[0], 0); Check(cache.Stats.Hits == 2); cache.GetFrame(sets[1], 0); Check(cache.Stats.Misses == 4 && cache.Stats.Evictions == 2);
		});
		test("Tile cache disabled, oversized and failed decodes do not retain entries", () =>
		{
			var set = Dt1Tileset.Parse(MapContracts.Dt1());
			foreach (long budget in new long[] { 0, 1 }) { var cache = new TileFrameCache(budget); cache.GetFrame(set, 0); cache.GetFrame(set, 0); Check(cache.Stats.Entries == 0 && cache.Stats.Misses == 2); }
			var invalid = Dt1Tileset.Parse(MapContracts.Dt1(new MapContracts.Tile(Blocks: [new(0, 0, 0x1001, [0, 2, 9])])));
			var failed = new TileFrameCache(); Bad(() => failed.GetFrame(invalid, 0)); Bad(() => failed.GetFrame(invalid, 0)); Check(failed.Stats.Entries == 0 && failed.Stats.Misses == 2);
		});
		test("Tile cache entry ceiling, concurrent access and clear keep bounded ownership", () =>
		{
			var cache = new TileFrameCache(); var set = Dt1Tileset.Parse(MapContracts.Dt1());
			Parallel.For(0, 16, _ => Check(cache.GetFrame(set, 0).Indices.Count(p => p == 1) == 256));
			Check(cache.Stats.Misses == 1 && cache.Stats.Hits == 15);
			for (int i = 2; i <= 258; i++) cache.GetFrame(Dt1Tileset.Parse(MapContracts.Dt1(new MapContracts.Tile(Main: i))), 0);
			Check(cache.Stats.Entries == 256 && cache.Stats.Evictions == 2); cache.Clear(); Check(cache.Stats.Entries == 0 && cache.Stats.RetainedBytes == 0);
		});
		test("Map scenes use cache without changing pixels or collision after eviction", () =>
		{
			var cache = new TileFrameCache(); var map = Ds1Map.Parse(MapContracts.Ds1()); MapTileset[] sets = [new("test", Dt1Tileset.Parse(MapContracts.Dt1()))];
			var first = MapScene.Build(map, sets, cache); var second = MapScene.Build(map, sets, cache); cache.Clear();
			Check(cache.Stats.Hits == 1 && first.Images[0].Frame.Indices.SequenceEqual(second.Images[0].Frame.Indices) && first.CollisionAt(0, 0) == second.CollisionAt(0, 0));
			second.Images[0].Frame.Indices[14] = 9; Check(first.Images[0].Frame.Indices[14] == 1);
		});
	}
	private const string PresetHeader = "Def\tLevelId\tFiles\tDt1Mask\tFile1\tFile2\tFile3\tFile4\tFile5\tFile6\n";
	internal static Dictionary<string, byte[]> TxtFiles()
	{
		string headers = string.Join('\t', Enumerable.Range(1, 32).Select(i => $"File {i}"));
		var files = Enumerable.Repeat("0", 32).ToArray(); files[0] = "ACT1/floor.dt1"; files[31] = "act1/edge.dt1";
		return new()
		{
			["levels.txt"] = Text("Id\tLevelType\n0\t0\n1\t1\n"),
			["lvltypes.txt"] = Text(headers + "\n0\n" + string.Join('\t', files) + "\n"),
			["lvlprest.txt"] = Text(PresetHeader + "7\t1\t1\t2147483649\tact1/town.ds1\n")
		};
	}
	private static MapTables Load(Dictionary<string, byte[]> files, MapTableMode mode) => MapTables.Load(p => files[p.Split('\\')[^1]], mode);
	private static Dictionary<string, byte[]> BinFiles()
	{
		var levels = Bin(MapBinSchema.LevelDefs110f, 2); U32(levels, 4 + 156 + 0x34, 1);
		var types = Bin(MapBinSchema.LvlTypes110f, 2); Text("act1/floor.dt1").CopyTo(types, 4 + 1928); Text("act1/edge.dt1").CopyTo(types, 4 + 1928 + 31 * 60);
		var presets = Bin(MapBinSchema.LvlPrest110f, 1); U32(presets, 4, 7); U32(presets, 8, 1); U32(presets, 4 + 0x40, 1); U32(presets, 4 + 0x1AC, 0x80000001); Text("act1/town.ds1").CopyTo(presets, 4 + 0x44);
		return new() { ["leveldefs.bin"] = levels, ["lvltypes.bin"] = types, ["lvlprest.bin"] = presets };
	}
	private static byte[] Bin(MapBinSchema schema, int count)
	{
		int size = schema switch { MapBinSchema.LevelDefs110f => 156, MapBinSchema.LvlTypes110f => 1928, _ => 432 };
		var result = new byte[4 + size * count]; U32(result, 0, (uint)count); return result;
	}
	private static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
}
