using System.Security.Cryptography;
using System.Text;
using OpenD2.Assets;
using OpenD2.Core;

// Monster kinds, the roster that assigns them to a room's monsters, and the monstats.txt loader. The fixture is synthetic: it proves
// the structure and the loader's column mapping, not equality with Diablo II data.
internal static class MonsterContracts
{
	private static void Check(bool value) { if (!value) throw new Exception("Monster assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
	private const string Header = "Id\thcIdx\tNameStr\tLevel\tLevel(N)\tLevel(H)\tExp\tExp(N)\tExp(H)\n";
	private const string Rows = "Expansion\t\t\t\t\t\t\t\t\nzombie1\t4\tzombie\t3\t\t\t6\t\t\nHell Bovine\t1\tcow\t30\t\t\t200\t\t\nhcnpc\t2\tgheed\t\t\t\t\t\t\n";
	private static Func<string, byte[]> Reader(string text) => path => path.EndsWith("monstats.txt") ? Encoding.ASCII.GetBytes(text) : throw new InvalidDataException("Unexpected test table: " + path);
	private static MonsterDefinition Kind(string code = "a", string name = "A", int level = 1, int experience = 1) => new(code, name, level, experience);
	private static EntityState Monster(uint id) => new(new(id), new(1), new(384, 384), Kind: EntityKind.Monster, Health: 10, MaxHealth: 10);

	public static void Run(Action<string, Action> test)
	{
		test("the preview monster table has two distinct kinds and looks codes up without regard to case", () =>
		{
			var table = MonsterTable.Preview;
			Check(table.All.Count == 2 && !table.Original && table.Source == "preview");
			Check(table.Get("preview-grunt") == new MonsterDefinition("preview-grunt", "Preview grunt", 1, 60) && table.Get("PREVIEW-BRUTE").Level == 2);
			Check(table.TryGet("Preview-Grunt", out var grunt) && grunt.Experience == 60 && !table.TryGet("nope", out _));
			Throws<ArgumentException>(() => table.Get("nope"));
		});

		test("a monster table rejects malformed codes, names, levels, experience, duplicates and excess", () =>
		{
			_ = new MonsterTable([Kind("zombie1"), Kind("Hell Bovine"), Kind("a-b_c", "Name"), Kind("x", level: 255, experience: 100_000_000), Kind("y", level: 0, experience: 0)], "x", false);
			foreach (var bad in new[] { Kind(""), Kind(" x"), Kind("x "), Kind("a/b"), Kind("a.b"), Kind(new string('a', 33)), Kind(name: ""), Kind(name: new string('n', 65)), Kind(name: "bad\nname"),
				Kind(level: -1), Kind(level: 256), Kind(experience: -1), Kind(experience: 100_000_001), Kind(code: null!), Kind(name: null!) })
				Throws<ArgumentException>(() => new MonsterTable([bad], "x", false));
			Throws<ArgumentException>(() => new MonsterTable([Kind("dup"), Kind("DUP")], "x", false)); // codes are not case sensitive
			Throws<ArgumentException>(() => new MonsterTable([], "x", false));
			Throws<ArgumentException>(() => new MonsterTable(Enumerable.Range(0, MonsterTable.MaxMonsters + 1).Select(i => Kind("m" + i)), "x", false));
			_ = new MonsterTable(Enumerable.Range(0, MonsterTable.MaxMonsters).Select(i => Kind("m" + i)), "x", false);
			Throws<ArgumentNullException>(() => new MonsterTable(null!, "x", false));
		});

		test("a roster names the kind of each monster and nothing else, and is checked against the table", () =>
		{
			var roster = new MonsterRoster([(100, "preview-grunt"), (101, "preview-brute")]);
			Check(roster.KindOf(new(100)) == "preview-grunt" && roster.KindOf(new(101)) == "preview-brute" && roster.KindOf(new(1)) is null && roster.Kinds.Count == 2);
			Throws<ArgumentException>(() => new MonsterRoster([(100, "a"), (100, "b")]));
			Throws<ArgumentException>(() => new MonsterRoster([(0, "a")]));
			Throws<ArgumentException>(() => new MonsterRoster([(1, null!)]));
			EntityState[] room = [new(new(1), new(1), new(384, 384)), Monster(100), Monster(101)];
			roster.Validate(MonsterTable.Preview, room);
			Throws<InvalidDataException>(() => roster.Validate(MonsterTable.Preview, [.. room, Monster(102)])); // a monster without a kind
			Throws<InvalidDataException>(() => roster.Validate(MonsterTable.Preview, room.Take(2))); // a kind for a monster that is not there
			Throws<InvalidDataException>(() => new MonsterRoster([(1, "preview-grunt"), (100, "preview-grunt"), (101, "preview-brute")]).Validate(MonsterTable.Preview, room)); // a kind for a player
			Throws<InvalidDataException>(() => roster.Validate(new MonsterTable([Kind("preview-grunt")], "other", false), room)); // the table lacks a kind
			roster.Validate(new MonsterTable([Kind("Preview-Grunt"), Kind("PREVIEW-BRUTE")], "other", false), room); // case differences are fine
		});

		test("monstats TXT loads codes, name keys, levels and experience and keeps provenance", () =>
		{
			string text = Header + Rows;
			var loaded = MonsterTables.Load(Reader(text));
			var table = loaded.Monsters;
			Check(table.Original && table.All.Count == 3 && table.Source.StartsWith("lod-1.10f; monstats "));
			Check(table.Get("zombie1") == new MonsterDefinition("zombie1", "zombie", 3, 6) && table.Get("hell bovine") == new MonsterDefinition("Hell Bovine", "cow", 30, 200));
			Check(table.Get("hcnpc").Level == 0 && table.Get("hcnpc").Experience == 0); // blank numbers are zero
			Check(loaded.Sources.Count == 1 && loaded.Sources[0].Bytes == text.Length && loaded.Sources[0].Sha256 == Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(text))));
		});

		test("monstats TXT rejects duplicates, bad numbers, bad codes, missing columns, empty tables and oversized input", () =>
		{
			void Bad(string text) => Throws<InvalidDataException>(() => MonsterTables.Load(Reader(text)));
			Bad(Header + Rows + "ZOMBIE1\t4\tzombie\t3\t\t\t6\t\t\n"); // duplicate Id without regard to case
			Bad(Header + "zombie1\t4\tzombie\tx\t\t\t6\t\t\n"); // not a number
			Bad(Header + "zombie1\t4\tzombie\t-1\t\t\t6\t\t\n");
			Bad(Header + "zombie1\t4\tzombie\t256\t\t\t6\t\t\n"); // above the level range
			Bad(Header + "zombie1\t4\tzombie\t3\t\t\t100000001\t\t\n"); // above the experience range
			Bad(Header + "zombie1\t4\tzombie\t3\t\t\t99999999999\t\t\n");
			Bad(Header + "zom/bie\t4\tzombie\t3\t\t\t6\t\t\n"); // code characters
			Bad(Header + "zombie1\t4\t\t3\t\t\t6\t\t\n"); // no name key
			Bad(Header + "Expansion\t\t\t\t\t\t\t\t\n"); // nothing but the marker row
			Bad("Id\thcIdx\tNameStr\tLevel\n" + "zombie1\t4\tzombie\t3\n"); // no Exp column
			Bad(Header.Replace("NameStr", "Name") + Rows);
			Throws<InvalidDataException>(() => MonsterTables.Load(Reader(Header + Rows), "lod-1.13c"));
			Throws<InvalidDataException>(() => MonsterTables.Load(_ => new byte[ExcelTextTable.MaxBytes + 1]));
			Throws<InvalidDataException>(() => MonsterTables.Load(_ => throw new InvalidDataException("missing")));
		});
	}
}
