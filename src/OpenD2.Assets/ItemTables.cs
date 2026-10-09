using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace OpenD2.Assets;

public enum ItemTableKind { Weapon, Armor, Misc }
public sealed record ItemBaseStats(int MinimumDamage, int MaximumDamage, int TwoHandMinimum, int TwoHandMaximum,
	int ThrowMinimum, int ThrowMaximum, int MinimumDefense, int MaximumDefense, int RequiredStrength, int RequiredDexterity, int RequiredLevel);
public sealed record ItemBaseDefinition(string Code, string Name, string NameKey, ItemTableKind Kind, string Type, string Type2,
	int Width, int Height, string InventoryPath, bool Stackable, bool Equippable, string BodyLoc1, string BodyLoc2,
	ItemBaseStats Stats, string Source, int Line);

// Bounded projection of owned LoD TXT data. Raw base values are not a gameplay rule compiler.
public sealed class ItemTables
{
	public const string Profile = "lod-1.10f";
	public const int MaxItems = 8192, MaxTypes = 1024, MaxInputBytes = 32 * 1024 * 1024;
	public IReadOnlyDictionary<string, ItemBaseDefinition> Items { get; }
	public IReadOnlyList<LegacyAssetSource> Sources { get; }
	private sealed record ItemType(string Equiv1, string Equiv2, bool Body, string Loc1, string Loc2);
	private ItemTables(Dictionary<string, ItemBaseDefinition> items, List<LegacyAssetSource> sources)
	{ Items = new ReadOnlyDictionary<string, ItemBaseDefinition>(items); Sources = sources.AsReadOnly(); }
	public ItemBaseDefinition Get(string code) => Items.TryGetValue(code, out var item) ? item : throw new InvalidDataException($"Unknown item code: {code}.");
	public static ItemTables Load(Func<string, byte[]> read, string profile = Profile)
	{
		ArgumentNullException.ThrowIfNull(read);
		if (profile != Profile) throw new InvalidDataException("Item definitions currently require the explicit lod-1.10f TXT profile.");
		var sources = new List<LegacyAssetSource>(); long input = 0;
		Table Read(string name)
		{
			string path = "data\\global\\excel\\" + name + ".txt"; byte[] bytes = read(path); input += bytes.LongLength;
			if (bytes.Length > ExcelTextTable.MaxBytes || input > MaxInputBytes) throw new InvalidDataException("Item tables input budget exceeded.");
			sources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return new(path, ExcelTextTable.Parse(bytes));
		}
		var locations = new HashSet<string>(StringComparer.Ordinal); var bodyTable = Read("bodylocs"); bodyTable.Require("Code");
		foreach (int r in bodyTable.Rows())
		{
			string code = Code(bodyTable.Text(r, "Code"));
			if (locations.Count >= 64 || !locations.Add(code)) throw bodyTable.Error(r, "Duplicate body location or count exceeded.");
		}
		var types = new Dictionary<string, ItemType>(StringComparer.Ordinal); var typeTable = Read("itemtypes");
		typeTable.Require("Code", "Equiv1", "Equiv2", "Body", "BodyLoc1", "BodyLoc2");
		foreach (int r in typeTable.Rows())
		{
			string code = Code(typeTable.Text(r, "Code")), loc1 = OptionalCode(typeTable.Text(r, "BodyLoc1")), loc2 = OptionalCode(typeTable.Text(r, "BodyLoc2"));
			bool body = typeTable.Number(r, "Body", 1) != 0;
			if ((loc1.Length > 0 && !locations.Contains(loc1)) || (loc2.Length > 0 && !locations.Contains(loc2)) || (body && loc1.Length == 0 && loc2.Length == 0))
				throw typeTable.Error(r, "Missing body location reference.");
			var type = new ItemType(OptionalCode(typeTable.Text(r, "Equiv1")), OptionalCode(typeTable.Text(r, "Equiv2")), body, loc1, loc2);
			if (types.Count >= MaxTypes || !types.TryAdd(code, type)) throw typeTable.Error(r, "Duplicate item type or count exceeded.");
		}
		// Validate type references/cycles without inheriting equipment rules from equivalences.
		var depths = new Dictionary<string, int>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
		int Visit(string code, int depth)
		{
			if (code.Length == 0) return -1;
			if (depth > 64 || !types.TryGetValue(code, out var type)) throw new InvalidDataException($"Missing item type or equivalence depth exceeded: {code}.");
			if (depths.TryGetValue(code, out int known)) return known;
			if (!visiting.Add(code)) throw new InvalidDataException($"Cyclic item type: {code}.");
			int result = 1 + Math.Max(Visit(type.Equiv1, depth + 1), Visit(type.Equiv2, depth + 1));
			if (result > 64) throw new InvalidDataException($"Item equivalence depth exceeded: {code}.");
			visiting.Remove(code); depths.Add(code, result); return result;
		}
		foreach (string code in types.Keys) Visit(code, 0);
		var items = new Dictionary<string, ItemBaseDefinition>(StringComparer.Ordinal);
		foreach (var (name, kind) in new[] { ("weapons", ItemTableKind.Weapon), ("armor", ItemTableKind.Armor), ("misc", ItemTableKind.Misc) })
		{
			var table = Read(name); table.Require("code", "name", "namestr", "type", "type2", "invwidth", "invheight", "invfile", "levelreq", "stackable");
			if (kind == ItemTableKind.Weapon) table.Require("mindam", "maxdam", "2handmindam", "2handmaxdam", "minmisdam", "maxmisdam", "reqstr", "reqdex");
			if (kind == ItemTableKind.Armor) table.Require("minac", "maxac", "reqstr");
			foreach (int r in table.Rows())
			{
				string code = Code(table.Text(r, "code")), primary = Code(table.Text(r, "type")), secondary = OptionalCode(table.Text(r, "type2"));
				if (!types.TryGetValue(primary, out var type) || (secondary.Length > 0 && !types.ContainsKey(secondary))) throw table.Error(r, "Missing item type reference.");
				int width = table.Number(r, "invwidth", 10), height = table.Number(r, "invheight", 10);
				if (width == 0 || height == 0) throw table.Error(r, "Inventory dimensions must be 1..10.");
				string file = table.Text(r, "invfile");
				if (file.Length is < 1 or > 31 || file.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw table.Error(r, "Inventory image must be a DC6 basename without directories or extension.");
				int Weapon(string column) => kind == ItemTableKind.Weapon ? table.Number(r, column, 255) : 0;
				int Armor(string column) => kind == ItemTableKind.Armor ? table.Number(r, column, 1000000) : 0;
				var stats = new ItemBaseStats(Weapon("mindam"), Weapon("maxdam"), Weapon("2handmindam"), Weapon("2handmaxdam"), Weapon("minmisdam"), Weapon("maxmisdam"),
					Armor("minac"), Armor("maxac"), kind == ItemTableKind.Misc ? 0 : table.Number(r, "reqstr", 65535),
					kind == ItemTableKind.Weapon ? table.Number(r, "reqdex", 65535) : 0, table.Number(r, "levelreq", 255));
				if (stats.MinimumDamage > stats.MaximumDamage || stats.TwoHandMinimum > stats.TwoHandMaximum || stats.ThrowMinimum > stats.ThrowMaximum || stats.MinimumDefense > stats.MaximumDefense)
					throw table.Error(r, "Inverted item base range.");
				var item = new ItemBaseDefinition(code, table.Text(r, "name"), table.Text(r, "namestr"), kind, primary, secondary, width, height,
					MpqArchive.NormalizePath("data/global/items/" + file + ".dc6"), table.Number(r, "stackable", 1) != 0, type.Body, type.Loc1, type.Loc2, stats, table.Path, table.Data.LineNumbers[r]);
				if (item.Name.Length == 0 || item.NameKey.Length == 0 || items.Count >= MaxItems || !items.TryAdd(code, item)) throw table.Error(r, "Missing name/key, duplicate item code or count exceeded.");
			}
		}
		if (locations.Count == 0 || types.Count == 0 || items.Count == 0) throw new InvalidDataException("Item definitions cannot be empty.");
		return new(items, sources);
	}
	internal static string Code(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 4 || value.Any(c => !char.IsAsciiLetterOrDigit(c))) throw new InvalidDataException($"Invalid item/type/location code: {value}.");
		return value; // Do not silently merge case-sensitive identifiers.
	}
	private static string OptionalCode(string value) => value is "" or "0" ? "" : Code(value);
	private sealed class Table(string path, ExcelTextTable data)
	{
		public string Path => path;
		public ExcelTextTable Data => data;
		private readonly Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
		public void Require(params string[] names) { foreach (string name in names) columns[name] = data.Column(name); }
		public string Text(int row, string name)
		{
			string value = data.Rows[row][columns[name]].Trim();
			if (value.Length > 255 || value.Any(char.IsControl)) throw Error(row, $"Invalid {name} text."); return value;
		}
		public int Number(int row, string name, int maximum)
		{
			string value = Text(row, name); if (value.Length == 0) return 0;
			if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > maximum) throw Error(row, $"Invalid {name} value.");
			return number;
		}
		public IEnumerable<int> Rows()
		{
			for (int r = 0; r < data.Rows.Count; r++)
			{
				var row = data.Rows[r];
				if (row.All(string.IsNullOrWhiteSpace) || (row[0].Trim() == "Expansion" && row.Skip(1).All(string.IsNullOrWhiteSpace))) continue;
				yield return r;
			}
		}
		public InvalidDataException Error(int row, string message) => new($"{path}, line {data.LineNumbers[row]}: {message}");
	}
}
