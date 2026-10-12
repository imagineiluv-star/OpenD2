using System.Globalization;
using System.Security.Cryptography;
using OpenD2.Core;

namespace OpenD2.Assets;

// Bounded projection of the owner's MonStats table into MonsterTable. The column mapping is the documented lod-1.10f layout and has
// not been checked against an original install: a table that loads is well formed, not yet known to equal Diablo II.
public sealed class MonsterTables
{
	public const string Profile = "lod-1.10f";
	public MonsterTable Monsters { get; }
	public IReadOnlyList<LegacyAssetSource> Sources { get; }
	private MonsterTables(MonsterTable monsters, List<LegacyAssetSource> sources) { Monsters = monsters; Sources = sources.AsReadOnly(); }

	public static MonsterTables Load(Func<string, byte[]> read, string profile = Profile)
	{
		ArgumentNullException.ThrowIfNull(read);
		if (profile != Profile) throw new InvalidDataException("Monster tables currently require the explicit lod-1.10f TXT profile.");
		string path = "data\\global\\excel\\monstats.txt"; byte[] bytes = read(path);
		if (bytes.Length > ExcelTextTable.MaxBytes) throw new InvalidDataException("Monster table input budget exceeded.");
		var sources = new List<LegacyAssetSource> { new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))) };
		var table = ExcelTextTable.Parse(bytes);
		var kinds = new List<MonsterDefinition>();
		for (int r = 0; r < table.Rows.Count; r++)
		{
			var row = table.Rows[r];
			string code = table.Value(r, "Id").Trim();
			if (row.All(string.IsNullOrWhiteSpace) || (code == "Expansion" && row.Skip(1).All(string.IsNullOrWhiteSpace))) continue;
			// NameStr is a string-table key, not display text; resolving it needs the TBL files, which is a later step.
			kinds.Add(new(code, table.Value(r, "NameStr").Trim(), Number(path, table, r, "Level"), Number(path, table, r, "Exp")));
		}
		string origin = Profile + "; monstats " + sources[0].Sha256[..12];
		try { return new(new MonsterTable(kinds, origin, true), sources); }
		catch (ArgumentException error) { throw new InvalidDataException(path + ": " + error.Message, error); }
	}

	// A blank number is 0 (unused or non-combat rows); anything else must be plain digits.
	private static int Number(string path, ExcelTextTable table, int row, string column)
	{
		string value = table.Value(row, column).Trim();
		if (value.Length == 0) return 0;
		if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number > MonsterTable.MaxExperience)
			throw new InvalidDataException($"{path}, line {table.LineNumbers[row]}: Invalid {column} value.");
		return number;
	}
}
