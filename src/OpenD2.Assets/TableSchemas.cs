using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenD2.Assets;

public enum ColumnKind { Text, Int, Flag }
public sealed record ColumnSpec(string Name, ColumnKind Kind, bool Required = false);
// Key: the row identity column. Rows whose key is empty or "Expansion" are separators, not records.
// UniqueKey duplicates are reported as warnings: the genuine 1.11b data has a few (e.g. monstats cr_lancer8).
public sealed record TableSchema(string File, string Key, bool UniqueKey, IReadOnlyList<ColumnSpec> Columns);
public sealed record TableSchemaReport(string File, int Rows, int Records, int Separators, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
	public bool Valid => Errors.Count == 0;
}
public sealed record BinConsistencyReport(string File, int TxtRecords, int BinCount, int RecordSize, IReadOnlyList<string> Errors);

// Semantic column contracts for the Excel tables the engine reads. These check meaning (types, flags,
// identity), not layout; the TXT parser already checks structure. Profile: LoD 1.10f/1.11b column names.
public static class TableSchemas
{
	static ColumnSpec T(string n, bool r = false) => new(n, ColumnKind.Text, r);
	static ColumnSpec I(string n, bool r = false) => new(n, ColumnKind.Int, r);
	static ColumnSpec F(string n) => new(n, ColumnKind.Flag);

	public static IReadOnlyList<TableSchema> All { get; } =
	[
		new("skills.txt", "skill", true, [I("Id", true), T("charclass"), T("skilldesc"), I("reqlevel"), I("maxlvl"), T("reqskill1")]),
		new("monstats.txt", "Id", true, [I("hcIdx", true), T("BaseId", true), T("NameStr"), F("enabled"), I("Level"), I("Level(N)"), I("Level(H)"), F("isSpawn"), F("boss"), F("npc")]),
		new("superuniques.txt", "Superunique", true, [T("Name", true), T("Class", true), I("hcIdx", true)]),
		new("levels.txt", "Name", true, [I("Id", true), I("Act", true), I("Pal"), I("SizeX"), I("SizeY"), I("MonLvl1Ex"), T("LevelName"), T("LevelWarp")]),
		new("weapons.txt", "code", true, [T("name"), T("type", true), I("levelreq"), I("level"), I("mindam"), I("maxdam"), I("2handmindam"), I("2handmaxdam"), F("spawnable"), I("invwidth"), I("invheight")]),
		new("armor.txt", "code", true, [T("name"), T("type", true), I("levelreq"), I("level"), I("minac"), I("maxac"), F("spawnable"), I("invwidth"), I("invheight")]),
		new("misc.txt", "code", true, [T("name"), T("type", true), I("levelreq"), I("level"), F("spawnable"), I("invwidth"), I("invheight")]),
		new("itemtypes.txt", "Code", true, [T("ItemType"), T("Equiv1"), T("Equiv2"), F("Body"), F("Throwable"), F("Beltable")]),
		new("uniqueitems.txt", "index", true, [T("code"), I("lvl"), I("lvl req"), F("enabled"), F("ladder")]),
		new("setitems.txt", "index", true, [T("set", true), T("item", true), I("lvl"), I("lvl req")]),
		new("runes.txt", "Name", true, [T("Rune Name"), F("complete"), T("itype1"), T("Rune1")]),
		new("charstats.txt", "class", true, [I("str", true), I("dex", true), I("int", true), I("vit", true), I("stamina"), T("StartSkill")]),
		new("missiles.txt", "Missile", true, [I("Id", true), I("Vel"), I("Range")]),
		new("states.txt", "state", true, [I("Id", true)]),
		new("objects.txt", "Name", false, [I("Id", true), T("Token")]),
		new("treasureclassex.txt", "Treasure Class", true, [I("Picks"), I("NoDrop"), T("Item1"), I("Prob1")]),
	];

	public static TableSchema? For(string file) => All.FirstOrDefault(s => string.Equals(s.File, Path.GetFileName(file.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase));

	public static bool IsSeparator(string key) => key.Length == 0 || key.Equals("Expansion", StringComparison.OrdinalIgnoreCase);

	public static TableSchemaReport Validate(ExcelTextTable table, TableSchema schema, int maxMessages = 50)
	{
		var errors = new List<string>(); var warnings = new List<string>();
		void Error(string message) { if (errors.Count < maxMessages) errors.Add(message); }
		int key = Find(table, schema.Key);
		if (key < 0) return new(schema.File, table.Rows.Count, 0, 0, [$"missing key column '{schema.Key}'"], []);
		var columns = new List<(ColumnSpec Spec, int Index)>();
		foreach (var spec in schema.Columns)
		{
			int index = Find(table, spec.Name);
			if (index < 0) Error($"missing column '{spec.Name}'"); else columns.Add((spec, index));
		}
		var seen = new HashSet<string>(StringComparer.Ordinal); int records = 0, separators = 0;
		for (int row = 0; row < table.Rows.Count; row++)
		{
			var fields = table.Rows[row]; string id = fields[key].Trim(); int line = table.LineNumbers[row];
			if (IsSeparator(id)) { separators++; continue; }
			records++;
			if (schema.UniqueKey && !seen.Add(id) && warnings.Count < maxMessages) warnings.Add($"line {line}: duplicate {schema.Key} '{id}'");
			foreach (var (spec, index) in columns)
			{
				string value = fields[index].Trim();
				if (value.Length == 0) { if (spec.Required) Error($"line {line} ({id}): empty required {spec.Name}"); continue; }
				switch (spec.Kind)
				{
					case ColumnKind.Int when !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _):
						Error($"line {line} ({id}): {spec.Name} '{value}' is not an integer"); break;
					case ColumnKind.Flag when value is not ("0" or "1"):
						Error($"line {line} ({id}): {spec.Name} '{value}' is not a 0/1 flag"); break;
				}
			}
		}
		return new(schema.File, table.Rows.Count, records, separators, errors, warnings);
	}

	static int Find(ExcelTextTable table, string name)
	{
		for (int i = 0; i < table.Columns.Count; i++) if (string.Equals(table.Columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
		return -1;
	}

	// Compiled .bin files: u32 count followed by count fixed-size records. Layouts differ per version, so this
	// checks only what holds for every version: the size divides evenly and the count equals the TXT rows the
	// game compiles: every data row except those whose first column is exactly "Expansion" (case-sensitive: objgroup.txt keeps its "EXPANSION" row; verified on all 69 1.11b BINs).
	public static BinConsistencyReport CheckBin(string file, ExcelTextTable txt, ReadOnlySpan<byte> bin, int? expectedRecordSize = null)
	{
		var errors = new List<string>();
		int compiled = txt.Rows.Count(r => r[0].Trim() != "Expansion");
		if (bin.Length < 4) return new(file, compiled, 0, 0, ["BIN shorter than its count field"]);
		uint count = AssetBinary.U32(bin, 0); int size = 0;
		if (count == 0) { if (bin.Length != 4) errors.Add("zero count with trailing bytes"); }
		else if ((bin.Length - 4) % count != 0) errors.Add($"payload {bin.Length - 4} not divisible by count {count}");
		else size = (int)((bin.Length - 4) / count);
		if (expectedRecordSize is { } expected && size != expected) errors.Add($"record size {size} != expected {expected}");
		if (count != compiled) errors.Add($"BIN count {count} != compiled TXT rows {compiled}");
		return new(file, compiled, (int)Math.Min(count, int.MaxValue), size, errors);
	}
}

public sealed record ContentInventory(
	int Skills, int PlayerSkills, IReadOnlyDictionary<string, int> SkillsByClass,
	int Monsters, int EnabledMonsters, int SuperUniques,
	int BaseItems, int Weapons, int Armor, int Misc, int UniqueItems, int EnabledUniqueItems, int SetItems, int Runewords, int CompleteRunewords,
	int Quests, IReadOnlyDictionary<int, int> QuestsByAct,
	int Areas, IReadOnlyDictionary<int, int> AreasByAct,
	IReadOnlyList<string> Sources);

// M4-01: counts the playable content defined by the installed data. Every number comes from the files; nothing
// is hard-coded. Quests come from the quest title strings (qstsa<act>q<n>) because 1.11b has no quest table.
public static class ContentInventoryBuilder
{
	static readonly Regex QuestKey = new(@"^qstsa([1-5])q([1-6])$", RegexOptions.CultureInvariant);
	public static readonly string[] StringTables = ["data\\local\\lng\\eng\\string.tbl", "data\\local\\lng\\eng\\expansionstring.tbl", "data\\local\\lng\\eng\\patchstring.tbl"];

	public static ContentInventory Build(Func<string, byte[]?> read)
	{
		var sources = new List<string>();
		ExcelTextTable Table(string name)
		{
			string path = "data\\global\\excel\\" + name;
			var bytes = read(path) ?? throw new FileNotFoundException("Missing table " + path);
			sources.Add(path); return ExcelTextTable.Parse(bytes);
		}
		IEnumerable<int> Records(ExcelTextTable t, string key) { int k = t.Column(key); for (int r = 0; r < t.Rows.Count; r++) if (!TableSchemas.IsSeparator(t.Rows[r][k].Trim())) yield return r; }
		bool On(ExcelTextTable t, int r, string c) => t.Value(r, c).Trim() == "1";

		var skills = Table("skills.txt"); var skillRows = Records(skills, "skill").ToList();
		var byClass = skillRows.Select(r => skills.Value(r, "charclass").Trim()).Where(c => c.Length > 0).GroupBy(c => c).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
		var monstats = Table("monstats.txt"); var monRows = Records(monstats, "Id").ToList();
		var supers = Table("superuniques.txt");
		var weapons = Table("weapons.txt"); var armor = Table("armor.txt"); var misc = Table("misc.txt");
		int w = Records(weapons, "code").Count(), a = Records(armor, "code").Count(), m = Records(misc, "code").Count();
		var uniques = Table("uniqueitems.txt"); var uniqueRows = Records(uniques, "index").ToList();
		var sets = Table("setitems.txt");
		var runes = Table("runes.txt"); var runeRows = Records(runes, "Name").ToList();
		var levels = Table("levels.txt");
		var areaRows = Records(levels, "Name").Where(r => levels.Number(r, "Id") > 0).ToList();
		var areasByAct = areaRows.GroupBy(r => (int)levels.Number(r, "Act") + 1).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

		var quests = new HashSet<(int Act, int Quest)>();
		foreach (string path in StringTables)
		{
			if (read(path) is not { } bytes) continue;
			sources.Add(path);
			foreach (var entry in StringTable.Parse(bytes).Entries)
				if (QuestKey.Match(entry.Key(Encoding.Latin1)) is { Success: true } match)
					quests.Add((match.Groups[1].Value[0] - '0', match.Groups[2].Value[0] - '0'));
		}
		var questsByAct = quests.GroupBy(q => q.Act).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

		return new(skillRows.Count, byClass.Values.Sum(), byClass,
			monRows.Count, monRows.Count(r => On(monstats, r, "enabled")), Records(supers, "Superunique").Count(),
			w + a + m, w, a, m, uniqueRows.Count, uniqueRows.Count(r => On(uniques, r, "enabled")), Records(sets, "index").Count(), runeRows.Count, runeRows.Count(r => On(runes, r, "complete")),
			quests.Count, questsByAct, areaRows.Count, areasByAct, sources);
	}
}
