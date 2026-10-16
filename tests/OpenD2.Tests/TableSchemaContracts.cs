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
				["skills.txt"] = "skill\tId\tcharclass\nAttack\t0\t\nJab\t10\tama\nFire Bolt\t36\tsor\nInner Sight\t8\tama",
				["monstats.txt"] = "Id\tenabled\nskeleton1\t1\nExpansion\t\nnpc1\t",
				["superuniques.txt"] = "Superunique\nBishibosh",
				["weapons.txt"] = "name\tcode\nHand Axe\thax\nExpansion\t",
				["armor.txt"] = "name\tcode\nCap\tcap\nHelm\thlm",
				["misc.txt"] = "name\tcode\nRing\trin",
				["uniqueitems.txt"] = "index\tenabled\nThe Gnasher\t1\nExpansion\t\nUnused\t0",
				["setitems.txt"] = "index\nCiverb's Ward",
				["runes.txt"] = "Name\tcomplete\nRuneword1\t\nRuneword2\t1",
				["levels.txt"] = "Name\tId\tAct\nNull\t0\t\nRogue Encampment\t1\t0\nExpansion\t\t\nHarrogath\t109\t4",
			};
			var inv = ContentInventoryBuilder.Build(p => files.TryGetValue(p.Split('\\')[^1], out var s) ? Encoding.ASCII.GetBytes(s.Replace("\n", "\r\n")) : null);
			Check(inv.Skills == 4 && inv.PlayerSkills == 3 && inv.SkillsByClass["ama"] == 2, "skills");
			Check(inv.Monsters == 2 && inv.EnabledMonsters == 1 && inv.SuperUniques == 1, "monsters");
			Check(inv.BaseItems == 4 && inv.Weapons == 1 && inv.UniqueItems == 2 && inv.EnabledUniqueItems == 1 && inv.SetItems == 1 && inv.Runewords == 2 && inv.CompleteRunewords == 1, "items");
			Check(inv.Areas == 2 && inv.AreasByAct[1] == 1 && inv.AreasByAct[5] == 1 && inv.Quests == 0, "areas");
			bool threw = false; try { ContentInventoryBuilder.Build(_ => null); } catch (FileNotFoundException) { threw = true; }
			Check(threw, "missing table");
		});
	}
}
