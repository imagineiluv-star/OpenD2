using System.Text;
using OpenD2.Assets;
using OpenD2.Core;

// Rules of the Diablo II style progression and the TXT loader that feeds them. The fixture tables are generated from the
// built-in preview numbers: these tests prove the rules and the loader's column mapping, not equality with Diablo II data.
internal static class ProgressionContracts
{
	private static readonly ProgressionTables Tables = ProgressionTables.Preview;
	private static readonly SkillTable Skills = SkillTable.Preview;
	private static void Check(bool value) { if (!value) throw new Exception("Progression assertion failed."); }
	private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
	private static void Valid(CharacterProgress progress) => Progression.Validate(Tables, Skills, progress);
	private static CharacterProgress At(CharacterClass cls, int level) => Progression.Gain(Tables, Progression.Create(Tables, cls), Tables.ExperienceFor(cls, level)).Progress;

	private static string Charstats()
	{
		var text = new StringBuilder("class\tstr\tdex\tint\tvit\tstamina\thpadd\tLifePerLevel\tStaminaPerLevel\tManaPerLevel\tLifePerVitality\tStaminaPerVitality\tManaPerMagic\tStatPerLevel\n");
		text.Append("Expansion\t\t\t\t\t\t\t\t\t\t\t\t\t\n");
		foreach (var c in Enum.GetValues<CharacterClass>())
		{
			var d = Tables.Get(c);
			text.Append($"{c}\t{d.Strength}\t{d.Dexterity}\t{d.Energy}\t{d.Vitality}\t{d.BaseStamina}\t{d.BaseLife}\t{d.LifePerLevel}\t{d.StaminaPerLevel}\t{d.ManaPerLevel}\t{d.LifePerVitality}\t{d.StaminaPerVitality}\t{d.ManaPerEnergy}\t{d.StatPointsPerLevel}\n");
		}
		return text.ToString();
	}
	private static string Experience()
	{
		var classes = Enum.GetValues<CharacterClass>();
		var text = new StringBuilder("Level\t" + string.Join('\t', classes) + "\tExpRatio\n");
		text.Append("MaxLvl\t" + string.Join('\t', classes.Select(c => Tables.MaxLevel(c))) + "\t\n");
		for (int row = 0; row < ProgressionTables.LevelCap; row++)
			text.Append(row + "\t" + string.Join('\t', classes.Select(c => Tables.ExperienceFor(c, row + 1))) + "\t1024\n");
		return text.ToString();
	}
	private static readonly string[] Codes = ["ama", "sor", "nec", "pal", "bar", "dru", "ass"];
	private static string SkillText(string extra = "")
	{
		var text = new StringBuilder("skill\t*Id\tcharclass\treqlevel\tmaxlvl\treqskill1\treqskill2\treqskill3\nAttack\t0\t\t1\t20\t\t\t\nFixture monster skill\t500\t\t1\t20\t\t\t\n");
		foreach (var s in Skills.All.OrderBy(s => s.Id))
		{
			var requires = s.Requires.Select(r => Skills.Get(r).Name).Concat(Enumerable.Repeat("", 3)).Take(3);
			text.Append($"{s.Name}\t{s.Id}\t{Codes[(int)s.Class]}\t{s.RequiredLevel}\t{s.MaxLevel}\t{string.Join('\t', requires)}\n");
		}
		return text + extra;
	}
	private static Func<string, byte[]> Reader(string? charstats = null, string? experience = null, string? skills = null) => path => Encoding.ASCII.GetBytes(path.Split('\\', '/')[^1] switch
	{
		"charstats.txt" => charstats ?? Charstats(), "experience.txt" => experience ?? Experience(), "skills.txt" => skills ?? SkillText(),
		_ => throw new InvalidDataException("Unexpected test table: " + path)
	});

	public static void Run(Action<string, Action> test)
	{
		test("progression tables cover seven classes with strictly increasing experience", () =>
		{
			Check(Enum.GetValues<CharacterClass>().Length == 7 && !Tables.Original && Tables.Source == "preview");
			foreach (var c in Enum.GetValues<CharacterClass>())
			{
				var d = Tables.Get(c); Check(d.Class == c && d.StatPointsPerLevel == 5 && Tables.MaxLevel(c) == 99 && Tables.ExperienceFor(c, 1) == 0);
				for (int level = 2; level <= 99; level++) Check(Tables.ExperienceFor(c, level) > Tables.ExperienceFor(c, level - 1));
			}
			Throws<ArgumentOutOfRangeException>(() => Tables.ExperienceFor(CharacterClass.Amazon, 0));
			Throws<ArgumentOutOfRangeException>(() => Tables.ExperienceFor(CharacterClass.Amazon, 100));
			Throws<ArgumentOutOfRangeException>(() => Tables.Get((CharacterClass)99));
		});

		test("a new character starts at level 1 with class attributes and no points", () =>
		{
			foreach (var c in Enum.GetValues<CharacterClass>())
			{
				var p = Progression.Create(Tables, c); var d = Tables.Get(c);
				Check(p.Level == 1 && p.Experience == 0 && p.StatPoints == 0 && p.SkillPoints == 0 && p.Skills.Length == 0);
				Check(p.Strength == d.Strength && p.Dexterity == d.Dexterity && p.Vitality == d.Vitality && p.Energy == d.Energy); Valid(p);
				var r = Progression.Resources(Tables, p); Check(r == new CharacterResources(d.BaseLife, d.BaseMana, d.BaseStamina));
			}
			Check(Progression.Create(Tables, CharacterClass.Sorceress).Energy > Progression.Create(Tables, CharacterClass.Barbarian).Energy);
			Check(Progression.Create(Tables, CharacterClass.Barbarian).Strength > Progression.Create(Tables, CharacterClass.Sorceress).Strength);
		});

		test("experience raises levels at exact thresholds and grants five attribute points and one skill point per level", () =>
		{
			var start = Progression.Create(Tables, CharacterClass.Paladin); uint second = Tables.ExperienceFor(CharacterClass.Paladin, 2), third = Tables.ExperienceFor(CharacterClass.Paladin, 3);
			var below = Progression.Gain(Tables, start, second - 1); Check(below.LevelsGained == 0 && below.Progress.Level == 1 && below.Progress.Experience == second - 1 && below.Progress.StatPoints == 0);
			var exact = Progression.Gain(Tables, start, second); Check(exact.LevelsGained == 1 && exact.Progress.Level == 2 && exact.Progress.StatPoints == 5 && exact.Progress.SkillPoints == 1);
			var several = Progression.Gain(Tables, start, third + 10); Check(several.LevelsGained == 2 && several.Progress.Level == 3 && several.Progress.StatPoints == 10 && several.Progress.SkillPoints == 2);
			var nothing = Progression.Gain(Tables, several.Progress, 0); Check(nothing.Progress == several.Progress && nothing.LevelsGained == 0);
			Throws<ArgumentOutOfRangeException>(() => Progression.Gain(Tables, start, -1)); Valid(several.Progress);
		});

		test("the level cap stops experience and points, and stops experience from overflowing", () =>
		{
			var cap = Tables.ExperienceFor(CharacterClass.Druid, 99);
			var top = Progression.Gain(Tables, Progression.Create(Tables, CharacterClass.Druid), long.MaxValue); Check(top.Progress.Level == 99 && top.Progress.Experience == cap && top.LevelsGained == 98);
			Check(top.Progress.StatPoints == 98 * 5 && top.Progress.SkillPoints == 98); Valid(top.Progress);
			var more = Progression.Gain(Tables, top.Progress, 1_000_000); Check(more.LevelsGained == 0 && more.Progress == top.Progress);
		});

		test("attribute points are spent one at a time on the chosen attribute", () =>
		{
			var p = At(CharacterClass.Amazon, 3); Check(p.StatPoints == 10);
			var spent = Progression.AddStat(Progression.AddStat(Progression.AddStat(p, CharacterStat.Strength), CharacterStat.Vitality), CharacterStat.Vitality);
			Check(spent.StatPoints == 7 && spent.Strength == p.Strength + 1 && spent.Vitality == p.Vitality + 2 && spent.Dexterity == p.Dexterity && spent.Energy == p.Energy); Valid(spent);
			Throws<InvalidOperationException>(() => Progression.AddStat(Progression.Create(Tables, CharacterClass.Amazon), CharacterStat.Energy));
			Throws<ArgumentOutOfRangeException>(() => Progression.AddStat(p, (CharacterStat)9));
			var all = p; while (all.StatPoints > 0) all = Progression.AddStat(all, CharacterStat.Dexterity);
			Check(all.Dexterity == p.Dexterity + 10); Throws<InvalidOperationException>(() => Progression.AddStat(all, CharacterStat.Dexterity)); Valid(all);
		});

		test("life, mana and stamina follow level and attributes in quarter points", () =>
		{
			var start = Progression.Create(Tables, CharacterClass.Barbarian); var d = Tables.Get(CharacterClass.Barbarian);
			var vitality = start with { Vitality = start.Vitality + 3, Level = 1 };
			Check(Progression.Resources(Tables, vitality).Life == d.BaseLife + 3 * d.LifePerVitality / 4 && Progression.Resources(Tables, vitality).Stamina == d.BaseStamina + 3 * d.StaminaPerVitality / 4);
			var energy = start with { Energy = start.Energy + 2 }; Check(Progression.Resources(Tables, energy).Mana == d.BaseMana + 2 * d.ManaPerEnergy / 4);
			var level = At(CharacterClass.Barbarian, 11); Check(Progression.Resources(Tables, level).Life == d.BaseLife + 10 * d.LifePerLevel / 4);
			Check(Progression.Resources(Tables, At(CharacterClass.Sorceress, 20)).Mana > Progression.Resources(Tables, At(CharacterClass.Barbarian, 20)).Mana);
			// Quarter points accumulate before they round: 1.5 mana per level is +1 after one level and +3 after two, not +1 and +2.
			var amazon = Tables.Get(CharacterClass.Amazon); Check(amazon.ManaPerLevel == 6);
			Check(Progression.Resources(Tables, At(CharacterClass.Amazon, 2)).Mana == amazon.BaseMana + 1 && Progression.Resources(Tables, At(CharacterClass.Amazon, 3)).Mana == amazon.BaseMana + 3);
			// Fractions from different sources add before they round: +1.5 for the level and +1.5 for one energy point is +3, not 1 + 1.
			Check(Progression.Resources(Tables, Progression.AddStat(At(CharacterClass.Amazon, 2), CharacterStat.Energy)).Mana == amazon.BaseMana + 3);
			for (int level2 = 2; level2 <= 99; level2++)
			{
				var before = Progression.Resources(Tables, At(CharacterClass.Paladin, level2 - 1)); var after = Progression.Resources(Tables, At(CharacterClass.Paladin, level2));
				Check(after.Life >= before.Life && after.Mana >= before.Mana && after.Stamina >= before.Stamina);
			}
		});

		test("skill points need a class skill, its level, every prerequisite and a point to spend", () =>
		{
			int one = 101, two = 102, three = 103, sorceressSkill = 201;
			var p = At(CharacterClass.Amazon, 5); Check(p.SkillPoints == 4);
			Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, p, two)); // prerequisite has no point yet
			var first = Progression.AddSkill(Skills, p, one); Check(first.SkillLevel(one) == 1 && first.SkillPoints == 3);
			Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, first, two)); // level 6 required, this character is level 5
			Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, first, sorceressSkill)); // another class's skill
			Throws<ArgumentException>(() => Progression.AddSkill(Skills, first, 99999)); // unknown skill
			var level6 = At(CharacterClass.Amazon, 6); var chain = Progression.AddSkill(Skills, Progression.AddSkill(Skills, level6, one), two);
			Check(chain.SkillLevel(two) == 1 && chain.SkillPoints == 3); Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, chain, three));
			Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, Progression.Create(Tables, CharacterClass.Amazon), one)); // no points
			Valid(chain);
		});

		test("a skill stops at its own maximum, ranks stay sorted and every point is accounted for", () =>
		{
			var p = At(CharacterClass.Assassin, 25); int one = 701, two = 702;
			p = Progression.AddSkill(Skills, p, one);
			for (int i = 1; i < 20; i++) p = Progression.AddSkill(Skills, p, one);
			Check(p.SkillLevel(one) == 20); Throws<InvalidOperationException>(() => Progression.AddSkill(Skills, p, one));
			p = Progression.AddSkill(Skills, p, two); Check(p.Skills.Select(r => r.Skill).SequenceEqual([one, two]) && p.SkillPoints == 24 - 21); Valid(p);
		});

		test("validation rejects every way of forging progress", () =>
		{
			var good = Progression.AddSkill(Skills, Progression.AddStat(At(CharacterClass.Necromancer, 10), CharacterStat.Energy), 301); Valid(good);
			void Bad(CharacterProgress forged) => Throws<InvalidDataException>(() => Valid(forged));
			Bad(good with { StatPoints = good.StatPoints + 1 }); Bad(good with { SkillPoints = good.SkillPoints + 1 }); Bad(good with { StatPoints = -1 });
			Bad(good with { Strength = good.Strength + 1 }); Bad(good with { Vitality = Tables.Get(CharacterClass.Necromancer).Vitality - 1 });
			Bad(good with { Experience = good.Experience - 1 }); Bad(good with { Experience = Tables.ExperienceFor(CharacterClass.Necromancer, 11) });
			Bad(good with { Level = 0 }); Bad(good with { Level = 100 }); Bad(good with { Class = (CharacterClass)40 }); Bad(good with { Skills = null! });
			Bad(good with { Skills = [new(302, 1)] }); // prerequisite 301 missing
			Bad(good with { Skills = [new(201, 1)] }); // another class
			Bad(good with { Skills = [new(301, 21)] }); Bad(good with { Skills = [new(301, 0)] }); Bad(good with { Skills = [new(5, 1)] });
			Bad(good with { Skills = [new(301, 1), new(301, 1)] }); Bad(good with { Skills = [new(302, 1), new(301, 1)] }); // duplicate, unsorted
			Bad(good with { Level = 1, Experience = 0, StatPoints = 0, SkillPoints = 0 }); // spent points with no level to pay for them
			Throws<InvalidDataException>(() => Progression.Validate(Tables, Skills, null!));
		});

		test("random sequences of experience and spending never break the point accounting", () =>
		{
			foreach (var cls in Enum.GetValues<CharacterClass>())
			{
				var random = new Random(1234 + (int)cls); var p = Progression.Create(Tables, cls); int refused = 0, applied = 0;
				for (int step = 0; step < 400; step++)
				{
					try
					{
						p = random.Next(4) switch
						{
							0 => Progression.Gain(Tables, p, random.Next(0, 60_000)).Progress,
							1 => Progression.AddStat(p, (CharacterStat)random.Next(4)),
							_ => Progression.AddSkill(Skills, p, (((int)cls) + 1) * 100 + random.Next(1, 4))
						};
						applied++;
					}
					catch (InvalidOperationException) { refused++; }
					Valid(p);
				}
				Check(applied > 100 && refused > 0 && p.Level > 1);
			}
		});

		test("table constructors reject incomplete, duplicate, unordered and cyclic definitions", () =>
		{
			var definitions = Enum.GetValues<CharacterClass>().Select(Tables.Get).ToArray();
			var curve = Enumerable.Range(1, 99).Select(l => (uint)(250 * l * (l - 1))).ToArray();
			var all = Enum.GetValues<CharacterClass>().ToDictionary(c => c, _ => curve);
			Throws<ArgumentException>(() => new ProgressionTables(definitions.Skip(1), all, "x", false));
			Throws<ArgumentException>(() => new ProgressionTables(definitions.Append(definitions[0]), all, "x", false));
			Throws<ArgumentException>(() => new ProgressionTables(definitions.Select(d => d.Class == CharacterClass.Druid ? d with { Strength = 0 } : d), all, "x", false));
			Throws<ArgumentException>(() => new ProgressionTables(definitions.Select(d => d with { StatPointsPerLevel = 11 }), all, "x", false));
			Throws<ArgumentException>(() => new ProgressionTables(definitions, all.Where(p => p.Key != CharacterClass.Amazon).ToDictionary(p => p.Key, p => p.Value), "x", false));
			var flat = (uint[])curve.Clone(); flat[10] = flat[9];
			Throws<ArgumentException>(() => new ProgressionTables(definitions, all.ToDictionary(p => p.Key, p => p.Key == CharacterClass.Assassin ? flat : p.Value), "x", false));
			var shifted = curve.Select(v => v + 1).ToArray();
			Throws<ArgumentException>(() => new ProgressionTables(definitions, all.ToDictionary(p => p.Key, p => shifted), "x", false)); // level 1 must need no experience
			static SkillDefinition S(int id, int[]? requires = null, CharacterClass cls = CharacterClass.Amazon) => new(id, "S" + id, cls, 1, 20, requires ?? []);
			_ = new SkillTable([S(1), S(2, [1]), S(3, [1, 2])], "x", false);
			Throws<ArgumentException>(() => new SkillTable([S(1), S(1)], "x", false));
			Throws<ArgumentException>(() => new SkillTable([S(1, [2]), S(2, [1])], "x", false)); // cycle
			Throws<ArgumentException>(() => new SkillTable([S(1, [1])], "x", false));
			Throws<ArgumentException>(() => new SkillTable([S(1, [9])], "x", false)); // unknown prerequisite
			Throws<ArgumentException>(() => new SkillTable([S(1), S(2, [1], CharacterClass.Druid)], "x", false)); // cross-class prerequisite
			Throws<ArgumentException>(() => new SkillTable([S(1), S(2, [1, 1])], "x", false));
			Throws<ArgumentException>(() => new SkillTable([S(1), S(2), S(3), S(4), S(5, [1, 2, 3, 4])], "x", false));
			Throws<ArgumentException>(() => new SkillTable([S(0)], "x", false)); Throws<ArgumentException>(() => new SkillTable([new(1, "x", CharacterClass.Amazon, 100, 20, [])], "x", false));
			Throws<ArgumentException>(() => new SkillTable([new(1, "x", CharacterClass.Amazon, 1, 61, [])], "x", false));
		});

		test("derived life and mana apply only to the listed players before the first tick", () =>
		{
			var game = new GameSimulation(1, [new(new(1), new(1), new(384, 384)), new(new(2), new(1), new(512, 384)), new(new(3), new(1), new(640, 384), Kind: EntityKind.Monster, Health: 30, MaxHealth: 30)],
				new(new(1), 10, 10, Enumerable.Repeat(CollisionCell.Open, 100).ToArray()));
			var changed = game.WithPlayerResources([(new(1), 77, 33)]); var first = changed.GetEntity(new(1)); var second = changed.GetEntity(new(2));
			Check(first.Health == 77 && first.MaxHealth == 77 && first.Mana == 33 && first.MaxMana == 33 && second.Health == 100 && second.MaxMana == 60);
			Check(changed.ComputeStateHash() != game.ComputeStateHash() && GameSimulation.Restore(changed.CaptureSnapshot()).ComputeStateHash() == changed.ComputeStateHash());
			Throws<InvalidDataException>(() => game.WithPlayerResources([(new(3), 10, 10)])); // a monster
			Throws<InvalidDataException>(() => game.WithPlayerResources([(new(9), 10, 10)]));
			Throws<InvalidDataException>(() => game.WithPlayerResources([(new(1), 0, 10)]));
			Throws<InvalidDataException>(() => game.WithPlayerResources([(new(1), 10, -1)]));
			Throws<InvalidDataException>(() => game.WithPlayerResources([(new(1), 10, 10), (new(1), 20, 20)]));
			changed.Step(); Throws<InvalidDataException>(() => changed.WithPlayerResources([(new(1), 10, 10)]));
		});

		test("TXT tables load into the same rules and keep provenance for every source file", () =>
		{
			var loaded = CharacterTables.Load(Reader());
			Check(loaded.Progression.Original && loaded.Skills.Original && loaded.Sources.Count == 3 && loaded.Progression.Source.StartsWith("lod-1.10f; charstats "));
			foreach (var c in Enum.GetValues<CharacterClass>())
			{
				Check(loaded.Progression.Get(c) == Tables.Get(c) && loaded.Progression.MaxLevel(c) == 99);
				for (int level = 1; level <= 99; level++) Check(loaded.Progression.ExperienceFor(c, level) == Tables.ExperienceFor(c, level));
			}
			Check(loaded.Skills.All.Count == 21 && !loaded.Skills.TryGet(0, out _) && !loaded.Skills.TryGet(500, out _)); // monster skills are ignored
			foreach (var skill in Skills.All) Check(loaded.Skills.Get(skill.Id) is var s && s.Name == skill.Name && s.Class == skill.Class && s.RequiredLevel == skill.RequiredLevel && s.MaxLevel == skill.MaxLevel && s.Requires.SequenceEqual(skill.Requires));
			var p = Progression.Gain(loaded.Progression, Progression.Create(loaded.Progression, CharacterClass.Amazon), 5000).Progress;
			Progression.Validate(loaded.Progression, loaded.Skills, Progression.AddSkill(loaded.Skills, p, 101));
		});

		test("TXT tables reject unknown classes, gaps, bad numbers, unknown prerequisites and oversized input", () =>
		{
			void Bad(Func<string, byte[]> read) => Throws<InvalidDataException>(() => CharacterTables.Load(read));
			string stats = Charstats(), experience = Experience(), skills = SkillText();
			Bad(Reader(charstats: stats.Replace("Druid\t", "Dryad\t"))); // class missing and unknown
			Bad(Reader(charstats: stats + stats.Split('\n')[2] + "\n")); // duplicate class row
			Bad(Reader(charstats: string.Join('\n', stats.Split('\n').Where(l => !l.StartsWith("Assassin"))))); // class missing
			Bad(Reader(charstats: stats.Replace("\t5\n", "\t50\n"))); // too many attribute points per level
			Bad(Reader(charstats: stats.Replace("\t5\n", "\t-5\n")));
			Bad(Reader(experience: experience.Replace("MaxLvl", "Max"))); // no maximum level
			Bad(Reader(experience: string.Join('\n', experience.Split('\n').Where(l => !l.StartsWith("50\t"))))); // gap in the table
			Bad(Reader(experience: experience.Replace("\n1\t", "\n1x\t")));
			Bad(Reader(experience: experience.Replace("\n1\t" + Tables.ExperienceFor(CharacterClass.Amazon, 2), "\n1\t0"))); // not increasing
			Bad(Reader(experience: experience.Replace("\n0\t0\t", "\n0\t7\t"))); // level 1 needs experience
			Bad(Reader(skills: skills.Replace("\tama\t", "\txxx\t"))); // unknown class code
			Bad(Reader(skills: skills.Replace("Amazon II\t102\tama\t6\t20\tAmazon I", "Amazon II\t102\tama\t6\t20\tNo such skill")));
			Bad(Reader(skills: skills.Replace("Amazon II\t102\tama\t6\t20\tAmazon I", "Amazon II\t102\tama\t6\t20\tSorceress I"))); // cross-class prerequisite
			Bad(Reader(skills: SkillText("Amazon I\t999\tama\t1\t20\t\t\t\n"))); // duplicate name
			Bad(Reader(skills: SkillText("Extra\t101\tama\t1\t20\t\t\t\n"))); // duplicate id
			Bad(Reader(skills: skills.Replace("\t102\tama\t6\t20", "\t102\tama\t6\t99"))); // beyond the rank cap
			Bad(Reader(skills: skills.Replace("\t102\tama\t6\t20", "\t102\tama\t6\t")));
			Bad(Reader(skills: "skill\t*Id\tcharclass\treqlevel\tmaxlvl\treqskill1\treqskill2\treqskill3\nAttack\t0\t\t1\t20\t\t\t\n")); // no class skills
			Bad(Reader(skills: skills.Replace("\treqskill3", "\treqskillx"))); // missing column
			Throws<InvalidDataException>(() => CharacterTables.Load(Reader(), "lod-1.13c"));
			Throws<InvalidDataException>(() => CharacterTables.Load(path => path.EndsWith("skills.txt") ? new byte[ExcelTextTable.MaxBytes + 1] : Reader()(path)));
			Throws<InvalidDataException>(() => CharacterTables.Load(path => path.EndsWith("skills.txt") ? throw new InvalidDataException("missing") : Reader()(path)));
		});
	}
}
