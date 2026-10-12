using System.Globalization;
using System.Security.Cryptography;
using OpenD2.Core;

namespace OpenD2.Assets;

// Bounded projection of the owner's CharStats, Experience and Skills TXT tables into the Core progression rules.
// The column mapping below is the documented lod-1.10f layout and has not been checked against an original install:
// treat a table that loads as "well formed", not as "equal to Diablo II" until it has been compared with real data.
public sealed class CharacterTables
{
	public const string Profile = "lod-1.10f";
	public const int MaxInputBytes = 16 * 1024 * 1024;
	public ProgressionTables Progression { get; }
	public SkillTable Skills { get; }
	public IReadOnlyList<LegacyAssetSource> Sources { get; }
	private CharacterTables(ProgressionTables progression, SkillTable skills, List<LegacyAssetSource> sources)
	{ Progression = progression; Skills = skills; Sources = sources.AsReadOnly(); }

	private static readonly string[] ClassNames = ["Amazon", "Sorceress", "Necromancer", "Paladin", "Barbarian", "Druid", "Assassin"];
	private static readonly string[] ClassCodes = ["ama", "sor", "nec", "pal", "bar", "dru", "ass"];

	public static CharacterTables Load(Func<string, byte[]> read, string profile = Profile)
	{
		ArgumentNullException.ThrowIfNull(read);
		if (profile != Profile) throw new InvalidDataException("Character tables currently require the explicit lod-1.10f TXT profile.");
		var sources = new List<LegacyAssetSource>(); long input = 0;
		(string Path, ExcelTextTable Table) Read(string name)
		{
			string path = "data\\global\\excel\\" + name + ".txt"; byte[] bytes = read(path); input += bytes.LongLength;
			if (bytes.Length > ExcelTextTable.MaxBytes || input > MaxInputBytes) throw new InvalidDataException("Character tables input budget exceeded.");
			sources.Add(new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))); return (path, ExcelTextTable.Parse(bytes));
		}

		var (statsPath, stats) = Read("charstats");
		var definitions = new ClassDefinition?[ClassNames.Length];
		for (int r = 0; r < stats.Rows.Count; r++)
		{
			string name = stats.Value(r, "class").Trim();
			if (Blank(stats.Rows[r]) || (name == "Expansion" && stats.Rows[r].Skip(1).All(string.IsNullOrWhiteSpace))) continue;
			int index = Array.IndexOf(ClassNames, name);
			if (index < 0 || definitions[index] is not null) throw Error(statsPath, stats, r, "Unknown or duplicate class.");
			int Field(string column, int maximum) => Number(statsPath, stats, r, column, maximum);
			definitions[index] = new((CharacterClass)index, Field("str", 1000), Field("dex", 1000), Field("vit", 1000), Field("int", 1000),
				Field("hpadd", 100000), Field("int", 1000), Field("stamina", 100000), Field("LifePerLevel", 400), Field("ManaPerLevel", 400), Field("StaminaPerLevel", 400),
				Field("LifePerVitality", 400), Field("ManaPerMagic", 400), Field("StaminaPerVitality", 400), Field("StatPerLevel", ProgressionTables.MaxStatPointsPerLevel));
		}
		if (definitions.Any(d => d is null)) throw new InvalidDataException(statsPath + ": all seven classes are required.");

		var (experiencePath, experience) = Read("experience");
		var maxLevels = new int[ClassNames.Length]; var levelRows = new SortedDictionary<int, int>();
		for (int r = 0; r < experience.Rows.Count; r++)
		{
			string level = experience.Value(r, "Level").Trim();
			if (Blank(experience.Rows[r])) continue;
			if (level == "MaxLvl")
			{
				for (int c = 0; c < ClassNames.Length; c++) maxLevels[c] = Number(experiencePath, experience, r, ClassNames[c], ProgressionTables.LevelCap);
				continue;
			}
			if (!int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > 1000 || !levelRows.TryAdd(number, r))
				throw Error(experiencePath, experience, r, "Invalid or duplicate level row.");
		}
		var thresholds = new Dictionary<CharacterClass, uint[]>();
		for (int c = 0; c < ClassNames.Length; c++)
		{
			// Row N holds the experience for the level N + 1, so the table starts at row 0 (level 1 needs none).
			if (maxLevels[c] < 2) throw new InvalidDataException(experiencePath + $": missing MaxLvl for {ClassNames[c]}.");
			var list = new uint[maxLevels[c]];
			for (int level = 0; level < list.Length; level++)
			{
				if (!levelRows.TryGetValue(level, out int row)) throw new InvalidDataException(experiencePath + $": missing experience row {level} for {ClassNames[c]}.");
				list[level] = experience.Number(row, ClassNames[c]);
			}
			thresholds[(CharacterClass)c] = list;
		}

		var (skillsPath, skills) = Read("skills");
		var rows = new List<(int Row, int Id, string Name, CharacterClass Class)>(); var byName = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int r = 0; r < skills.Rows.Count; r++)
		{
			string code = skills.Value(r, "charclass").Trim();
			if (Blank(skills.Rows[r]) || code.Length == 0) continue; // Monster, item and shared skills are not character skills.
			int index = Array.IndexOf(ClassCodes, code);
			string name = skills.Value(r, "skill").Trim();
			if (index < 0 || name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw Error(skillsPath, skills, r, "Unknown class code or invalid skill name.");
			int id = Number(skillsPath, skills, r, "*Id", SkillTable.MaxSkills * 4);
			if (!byName.TryAdd(name, id)) throw Error(skillsPath, skills, r, "Duplicate class skill name.");
			rows.Add((r, id, name, (CharacterClass)index));
		}
		var definitionsList = new List<SkillDefinition>();
		foreach (var (row, id, name, cls) in rows)
		{
			var requires = new List<int>();
			foreach (string column in new[] { "reqskill1", "reqskill2", "reqskill3" })
			{
				string target = skills.Value(row, column).Trim();
				if (target.Length == 0) continue;
				if (!byName.TryGetValue(target, out int required)) throw Error(skillsPath, skills, row, $"{column} names an unknown class skill: {target}.");
				requires.Add(required);
			}
			definitionsList.Add(new(id, name, cls, Number(skillsPath, skills, row, "reqlevel", ProgressionTables.LevelCap), Number(skillsPath, skills, row, "maxlvl", SkillTable.MaxRankCap), requires.ToArray()));
		}
		if (definitionsList.Count == 0) throw new InvalidDataException(skillsPath + ": no class skills.");

		string origin = Profile + "; " + string.Join(", ", sources.Select(s => Path.GetFileNameWithoutExtension(s.Path.Replace('\\', '/')) + " " + s.Sha256[..12]));
		try { return new(new ProgressionTables(definitions!, thresholds, origin, true), new SkillTable(definitionsList, origin, true), sources); }
		catch (ArgumentException error) { throw new InvalidDataException("Character tables are inconsistent: " + error.Message, error); }
	}

	private static bool Blank(IReadOnlyList<string> row) => row.All(string.IsNullOrWhiteSpace);
	private static InvalidDataException Error(string path, ExcelTextTable table, int row, string message) => new($"{path}, line {table.LineNumbers[row]}: {message}");
	private static int Number(string path, ExcelTextTable table, int row, string column, int maximum)
	{
		string value = table.Value(row, column).Trim();
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > maximum) throw Error(path, table, row, $"Invalid {column} value.");
		return number;
	}
}
