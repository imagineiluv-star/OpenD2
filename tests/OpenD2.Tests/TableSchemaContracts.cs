using System.Text;
using OpenD2.Assets;

// Synthetic only: tiny hand-written tables, no genuine data.
internal static class TableSchemaContracts
{
	static void Check(bool value, string what) { if (!value) throw new Exception("Assertion failed " + what); }
	static ExcelTextTable Txt(params string[] lines) => ExcelTextTable.Parse(Encoding.ASCII.GetBytes(string.Join("\r\n", lines) + "\r\n"));
	static byte[] Bin(int count, int size) { var b = new byte[4 + count * size]; BitConverter.GetBytes(count).CopyTo(b, 0); return b; }

	public static void Run(Action<string, Action> test)
	{
		test("table schemas: valid rows pass; separators and duplicate keys are classified", () =>
		{
			var schema = new TableSchema("x.txt", "code", true, [new("lvl", ColumnKind.Int, true), new("on", ColumnKind.Flag)]);
			var r = TableSchemas.Validate(Txt("code\tlvl\ton", "aa\t1\t1", "Expansion\t\t", "\t\t", "bb\t-3\t", "aa\t2\t0"), schema);
			Check(r.Valid && r.Records == 3 && r.Separators == 2 && r.Warnings.Count == 1 && r.Warnings[0].Contains("'aa'"), "classification");
		});
		test("table schemas: bad integers, flags, empty required values and missing columns are errors", () =>
		{
			var schema = new TableSchema("x.txt", "code", true, [new("lvl", ColumnKind.Int, true), new("on", ColumnKind.Flag), new("gone", ColumnKind.Text)]);
			var r = TableSchemas.Validate(Txt("code\tlvl\ton", "aa\tx\t1", "bb\t\t2"), schema);
			Check(r.Errors.Count == 4 && r.Errors.Any(e => e.Contains("'gone'")) && r.Errors.Any(e => e.Contains("not an integer")) && r.Errors.Any(e => e.Contains("0/1 flag")) && r.Errors.Any(e => e.Contains("empty required")), string.Join("; ", r.Errors));
			Check(!TableSchemas.Validate(Txt("id\tlvl", "a\t1"), schema).Valid, "missing key");
			Check(TableSchemas.For("data\\global\\excel\\Skills.txt")?.Key == "skill" && TableSchemas.For("nope.txt") is null, "lookup");
		});
		test("table schemas: BIN count drops only exact 'Expansion' rows and the record size must divide evenly", () =>
		{
			var txt = Txt("name\tv", "a\t1", "Expansion\t", "EXPANSION\t", "b\t2");
			var ok = TableSchemas.CheckBin("t.bin", txt, Bin(3, 8), 8);
			Check(ok.Errors.Count == 0 && ok.TxtRecords == 3 && ok.RecordSize == 8, "ok");
			Check(TableSchemas.CheckBin("t.bin", txt, Bin(4, 8)).Errors.Any(e => e.Contains("count")), "count");
			var odd = Bin(3, 8); Array.Resize(ref odd, odd.Length + 1);
			Check(TableSchemas.CheckBin("t.bin", txt, odd).Errors.Any(e => e.Contains("divisible")), "divisible");
			Check(TableSchemas.CheckBin("t.bin", txt, Bin(3, 8), 12).Errors.Any(e => e.Contains("expected")), "size");
			Check(TableSchemas.CheckBin("t.bin", txt, [1, 0]).Errors.Count == 1, "short");
		});
		test("content inventory: counts come from the tables and quest title keys", () =>
		{
			var files = new Dictionary<string, string>
			{
				["skills.txt"] = "skill\tId\tcharclass\nskill_a\t0\t\nskill_b\t10\tama\nskill_c\t36\tsor\nskill_d\t8\tama",
				["monstats.txt"] = "Id\tenabled\tnpc\tkillable\nmon_a\t1\t\t1\nExpansion\t\t\t\nnpc_a\t1\t1\t1\ntrap_a\t1\t\t\nmon_off\t\t\t1",
				["superuniques.txt"] = "Superunique\nboss_a",
				["weapons.txt"] = "name\tcode\tspawnable\tquest\nweapon_a\twa\t1\t\nExpansion\t\t\t\nquest_weapon\twq\t1\t1",
				["armor.txt"] = "name\tcode\tspawnable\narmor_a\taa\t1\narmor_b\tab\t0",
				["misc.txt"] = "name\tcode\tspawnable\nmisc_a\tma\t1",
				["uniqueitems.txt"] = "index\tenabled\nunique_a\t1\nExpansion\t\nunique_off\t0",
				["setitems.txt"] = "index\nset_item_a",
				["runes.txt"] = "Name\tcomplete\nRuneword1\t\nRuneword2\t1",
				["levels.txt"] = "Name\tId\tAct\nNull\t0\t\nTown A\t1\t0\nExpansion\t\t\nTown E\t109\t4\nAct 5 - Pandemonium 1\t133\t4",
			};
			var inv = ContentInventoryBuilder.Build(p => files.TryGetValue(p.Split('\\')[^1], out var s) ? Encoding.ASCII.GetBytes(s.Replace("\n", "\r\n")) : null);
			Check(inv.Rows.Skills == 4 && inv.Playable.Skills == 3 && inv.SkillsByClass["ama"] == 2, "skills");
			Check(inv.Rows.Monsters == 4 && inv.Playable.Monsters == 1 && inv.Playable.SuperUniques == 1, "monsters drop NPCs, unkillable and disabled rows");
			Check(inv.Rows.BaseItems == 5 && inv.Playable.BaseItems == 3 && inv.Playable.Weapons == 1, "base items drop unspawnable and quest rows");
			Check(inv.Rows.UniqueItems == 2 && inv.Playable.UniqueItems == 1 && inv.Playable.SetItems == 1 && inv.Rows.Runewords == 2 && inv.Playable.Runewords == 1, "items");
			Check(inv.Rows.Areas == 3 && inv.Playable.Areas == 2 && inv.UberAreas == 1 && inv.AreasByAct[1] == 1 && inv.AreasByAct[5] == 1 && inv.Playable.Quests == 0 && inv.SetDefinitions is null, "areas");
			bool threw = false; try { ContentInventoryBuilder.Build(_ => null); } catch (FileNotFoundException) { threw = true; }
			Check(threw, "missing table");
		});
	}
}
