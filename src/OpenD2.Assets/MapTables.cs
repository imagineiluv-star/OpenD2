using System.Security.Cryptography;

namespace OpenD2.Assets;

public enum MapTableMode { Txt, Bin110f }
public sealed record MapTableSource(string Path, string Sha256, int Bytes);
public sealed record MapTableIssue(string Code, string Table, int Id, string Detail, bool IsError = true);
public sealed record MapResourcePlan(int LevelId, int PresetId, int LevelType, string MapPath, IReadOnlyList<string> Tilesets);

// A map-resource projection, not a compiler for every gameplay table.
public sealed class MapTables
{
	private sealed record Preset(int Level, int Files, string[] Paths, uint Mask);
	private readonly Dictionary<int, int> levels = new();
	private readonly Dictionary<int, string[]> types = new();
	private readonly Dictionary<int, Preset> presets = new();
	private readonly List<MapTableSource> sources = new();
	public MapTableMode Mode { get; }
	public IReadOnlyList<MapTableSource> Sources => sources.AsReadOnly();
	public int LevelCount => levels.Count;
	public int TypeCount => types.Count;
	public int PresetCount => presets.Count;
	private MapTables(MapTableMode mode) { Mode = mode; }
	public static MapTables Load(Func<string, byte[]> read, MapTableMode mode)
	{
		ArgumentNullException.ThrowIfNull(read);
		if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
		var result = new MapTables(mode);
		byte[] Read(string name)
		{
			string path = "data\\global\\excel\\" + name;
			byte[] bytes = read(path);
			AssetBinary.Require(bytes.Length <= AssetDecoders.MaxInputBytes, "Table input exceeds budget.");
			result.sources.Add(new(path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length)); return bytes;
		}
		if (mode == MapTableMode.Txt)
		{
			var levelTable = ExcelTextTable.Parse(Read("levels.txt"));
			foreach (int r in DataRows(levelTable)) Add(result.levels, Id(levelTable.Number(r, "Id")), Id(levelTable.Number(r, "LevelType")), "Levels");
			var typeTable = ExcelTextTable.Parse(Read("lvltypes.txt"));
			int typeId = 0;
			foreach (int r in DataRows(typeTable))
			{
				// LvlTypes Id is documentation; the game's reference is the row ordinal.
				var paths = Enumerable.Range(1, 32).Select(i => TilePath(typeTable.Value(r, $"File {i}"), ".dt1")).ToArray();
				Add(result.types, typeId++, paths, "LvlTypes");
			}
			var presetTable = ExcelTextTable.Parse(Read("lvlprest.txt"));
			foreach (int r in DataRows(presetTable))
			{
				var paths = Enumerable.Range(1, 6).Select(i => TilePath(presetTable.Value(r, $"File{i}"), ".ds1")).ToArray();
				Add(result.presets, Id(presetTable.Number(r, "Def")), new(Id(presetTable.Number(r, "LevelId")), FileCount(presetTable.Number(r, "Files")), paths, presetTable.Number(r, "Dt1Mask")), "LvlPrest");
			}
		}
		else
		{
			var levelTable = ExcelBinTable.Parse(Read("leveldefs.bin"), MapBinSchema.LevelDefs110f);
			for (int r = 0; r < levelTable.Count; r++) Add(result.levels, r, Id(levelTable.Number(r, 0x34)), "LevelDefs");
			var typeTable = ExcelBinTable.Parse(Read("lvltypes.bin"), MapBinSchema.LvlTypes110f);
			for (int r = 0; r < typeTable.Count; r++) Add(result.types, r, Enumerable.Range(0, 32).Select(i => TilePath(typeTable.Text(r, i * 60, 60), ".dt1")).ToArray(), "LvlTypes");
			var presetTable = ExcelBinTable.Parse(Read("lvlprest.bin"), MapBinSchema.LvlPrest110f);
			for (int r = 0; r < presetTable.Count; r++)
			{
				var paths = Enumerable.Range(0, 6).Select(i => TilePath(presetTable.Text(r, 0x44 + i * 60, 60), ".ds1")).ToArray();
				Add(result.presets, Id(presetTable.Number(r, 0)), new(Id(presetTable.Number(r, 4)), FileCount(presetTable.Number(r, 0x40)), paths, presetTable.Number(r, 0x1AC)), "LvlPrest");
			}
		}
		AssetBinary.Require(result.LevelCount > 0 && result.TypeCount > 0 && result.PresetCount > 0, "Map tables must not be empty.");
		return result;
	}
	public IReadOnlyList<MapTableIssue> Validate()
	{
		var issues = new List<MapTableIssue>();
		void Issue(string code, string table, int id, string detail, bool error = true)
		{
			AssetBinary.Require(issues.Count < 4096, "Map table issue budget exceeded."); issues.Add(new(code, table, id, detail, error));
		}
		foreach (var (id, type) in levels)
			if (!types.ContainsKey(type)) Issue("missing_level_type", "levels", id, $"LevelType {type} does not exist.");
		foreach (var (id, p) in presets)
		{
			for (int f = 0; f < p.Files; f++) if (p.Paths[f].Length == 0) Issue("missing_map_path", "lvlprest", id, $"File{f + 1} is empty.");
			if (p.Level == 0)
			{
				if (p.Files > 0) Issue("context_required", "lvlprest", id, "LevelId 0: supply the containing level to check Dt1Mask.", false);
				continue;
			}
			if (!levels.TryGetValue(p.Level, out int type)) { Issue("missing_level", "lvlprest", id, $"LevelId {p.Level} does not exist."); continue; }
			if (types.TryGetValue(type, out var paths))
				for (int i = 0; i < 32; i++) if ((p.Mask & (1u << i)) != 0 && paths[i].Length == 0) Issue("missing_dt1_slot", "lvlprest", id, $"LevelType {type}, File {i + 1} is selected but empty.");
			if (p.Files > 0 && p.Mask == 0) Issue("empty_dt1_mask", "lvlprest", id, "No DT1 slots selected.");
		}
		return issues.AsReadOnly();
	}
	public MapResourcePlan Resolve(int levelId, int presetId, int fileIndex = 0)
	{
		AssetBinary.Require(levels.TryGetValue(levelId, out int type) && types.ContainsKey(type), $"Unresolved level {levelId} or its LevelType.");
		if (!presets.TryGetValue(presetId, out var preset)) throw new InvalidDataException($"Unknown preset Def {presetId}.");
		AssetBinary.Require(preset.Level == 0 || preset.Level == levelId, "Preset belongs to a different level.");
		AssetBinary.Require(fileIndex >= 0 && fileIndex < preset.Files && preset.Paths[fileIndex].Length > 0, "Preset file slot is missing or outside Files count.");
		var selected = new List<string>();
		for (int i = 0; i < 32; i++) if ((preset.Mask & (1u << i)) != 0)
		{
			string path = types[type][i]; AssetBinary.Require(path.Length > 0, $"Dt1Mask selects empty File {i + 1} of LevelType {type}."); selected.Add(path);
		}
		AssetBinary.Require(selected.Count > 0, "Preset has no DT1 slots selected.");
		return new(levelId, presetId, type, preset.Paths[fileIndex], selected.AsReadOnly());
	}
	private static IEnumerable<int> DataRows(ExcelTextTable table)
	{
		for (int r = 0; r < table.Rows.Count; r++)
		{
			var row = table.Rows[r];
			if (row.All(string.IsNullOrEmpty) || (row[0].Equals("Expansion", StringComparison.OrdinalIgnoreCase) && row.Skip(1).All(string.IsNullOrEmpty))) continue;
			yield return r;
		}
	}
	private static int Id(uint value) { AssetBinary.Require(value < 65536, "Map table ID exceeds bounds."); return (int)value; }
	private static int FileCount(uint value) { AssetBinary.Require(value <= 6, "LvlPrest Files must be 0..6."); return (int)value; }
	private static void Add<T>(Dictionary<int, T> target, int id, T value, string table)
	{ AssetBinary.Require(target.Count < 65536 && target.TryAdd(id, value), $"Duplicate ID {id} or row budget exceeded in {table}."); }
	private static string TilePath(string value, string extension)
	{
		value = value.Trim(); if (value is "" or "0") return "";
		try
		{
			string path = MpqArchive.NormalizePath(value);
			if (!path.StartsWith("data\\global\\tiles\\", StringComparison.Ordinal)) path = "data\\global\\tiles\\" + path;
			AssetBinary.Require(path.EndsWith(extension, StringComparison.Ordinal), $"Expected {extension} tile path."); return path;
		}
		catch (ArgumentException e) { throw new InvalidDataException("Unsafe map table resource path.", e); }
	}
}
