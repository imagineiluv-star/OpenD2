namespace OpenD2.Core;

// Diablo II character progression: seven classes, four attributes, experience levels, stat and skill points.
// The rules live here; the numbers live in a ProgressionTables/SkillTable loaded from the owner's CharStats, Experience and
// Skills tables (OpenD2.Assets.CharacterTables). Preview tables exist for play without original data and are NOT Diablo II values.
public enum CharacterClass { Amazon, Sorceress, Necromancer, Paladin, Barbarian, Druid, Assassin }
public enum CharacterStat { Strength, Dexterity, Vitality, Energy }

// Resource rates are in quarter points (the original tables store life/mana/stamina growth in 1/4 units).
public sealed record ClassDefinition(CharacterClass Class, int Strength, int Dexterity, int Vitality, int Energy,
	int BaseLife, int BaseMana, int BaseStamina, int LifePerLevel, int ManaPerLevel, int StaminaPerLevel,
	int LifePerVitality, int ManaPerEnergy, int StaminaPerVitality, int StatPointsPerLevel)
{
	public int Starting(CharacterStat stat) => stat switch
	{
		CharacterStat.Strength => Strength, CharacterStat.Dexterity => Dexterity, CharacterStat.Vitality => Vitality,
		CharacterStat.Energy => Energy, _ => throw new ArgumentOutOfRangeException(nameof(stat))
	};
}

public readonly record struct SkillRank(int Skill, int Level);
public readonly record struct CharacterResources(int Life, int Mana, int Stamina);

// Every attribute point and skill point a character holds is accounted for: granted by levels, spent, or still available.
public sealed record CharacterProgress(CharacterClass Class, int Level, uint Experience, int StatPoints, int SkillPoints,
	int Strength, int Dexterity, int Vitality, int Energy, SkillRank[] Skills)
{
	public int Get(CharacterStat stat) => stat switch
	{
		CharacterStat.Strength => Strength, CharacterStat.Dexterity => Dexterity, CharacterStat.Vitality => Vitality,
		CharacterStat.Energy => Energy, _ => throw new ArgumentOutOfRangeException(nameof(stat))
	};
	public int SkillLevel(int skill) => Skills.FirstOrDefault(r => r.Skill == skill).Level;
	// Value equality including the skill list: progress is compared after it has crossed a file or the wire, where array instances differ.
	public bool Equals(CharacterProgress? other) => other is not null && Class == other.Class && Level == other.Level && Experience == other.Experience &&
		StatPoints == other.StatPoints && SkillPoints == other.SkillPoints && Strength == other.Strength && Dexterity == other.Dexterity &&
		Vitality == other.Vitality && Energy == other.Energy && (Skills ?? []).AsSpan().SequenceEqual((other.Skills ?? []).AsSpan());
	public override int GetHashCode() => HashCode.Combine(Class, Level, Experience, StatPoints, SkillPoints, Strength, Dexterity, HashCode.Combine(Vitality, Energy, Skills?.Length ?? 0));
}

public sealed class ProgressionTables
{
	public const int LevelCap = 99, MaxStatPointsPerLevel = 10;
	// What a new character is granted for every level beyond the first.
	public const int SkillPointsPerLevel = 1;
	private readonly ClassDefinition[] classes;
	private readonly uint[][] experience;
	// "preview" for the built-in synthetic tables, otherwise the profile and hashes of the owner's files.
	public string Source { get; }
	public bool Original { get; }

	public ProgressionTables(IEnumerable<ClassDefinition> definitions, IReadOnlyDictionary<CharacterClass, uint[]> experienceByClass, string source, bool original)
	{
		ArgumentNullException.ThrowIfNull(definitions); ArgumentNullException.ThrowIfNull(experienceByClass); ArgumentNullException.ThrowIfNull(source);
		classes = new ClassDefinition[Enum.GetValues<CharacterClass>().Length]; experience = new uint[classes.Length][];
		foreach (var definition in definitions)
		{
			if (!Enum.IsDefined(definition.Class) || classes[(int)definition.Class] is not null) throw new ArgumentException("Duplicate or unknown class.", nameof(definitions));
			if (new[] { definition.Strength, definition.Dexterity, definition.Vitality, definition.Energy }.Any(v => v is < 1 or > 1000) ||
				new[] { definition.BaseLife, definition.BaseMana, definition.BaseStamina }.Any(v => v is < 1 or > 100000) ||
				new[] { definition.LifePerLevel, definition.ManaPerLevel, definition.StaminaPerLevel, definition.LifePerVitality, definition.ManaPerEnergy, definition.StaminaPerVitality }.Any(v => v is < 0 or > 400) ||
				definition.StatPointsPerLevel is < 0 or > MaxStatPointsPerLevel)
				throw new ArgumentException($"Invalid class definition: {definition.Class}.", nameof(definitions));
			classes[(int)definition.Class] = definition;
		}
		if (classes.Any(c => c is null)) throw new ArgumentException("All seven classes are required.", nameof(definitions));
		for (int i = 0; i < classes.Length; i++)
		{
			if (!experienceByClass.TryGetValue((CharacterClass)i, out var thresholds) || thresholds is null || thresholds.Length is < 2 or > LevelCap || thresholds[0] != 0)
				throw new ArgumentException($"Invalid experience table: {(CharacterClass)i}.", nameof(experienceByClass));
			for (int level = 1; level < thresholds.Length; level++)
				if (thresholds[level] <= thresholds[level - 1]) throw new ArgumentException($"Experience must strictly increase: {(CharacterClass)i} level {level + 1}.", nameof(experienceByClass));
			experience[i] = (uint[])thresholds.Clone();
		}
		Source = source; Original = original;
	}
	public ClassDefinition Get(CharacterClass cls) => Enum.IsDefined(cls) ? classes[(int)cls] : throw new ArgumentOutOfRangeException(nameof(cls));
	public int MaxLevel(CharacterClass cls) => experience[(int)Get(cls).Class].Length;
	// Total experience needed to reach a level; level 1 needs none.
	public uint ExperienceFor(CharacterClass cls, int level)
	{
		var table = experience[(int)Get(cls).Class];
		return level >= 1 && level <= table.Length ? table[level - 1] : throw new ArgumentOutOfRangeException(nameof(level));
	}

	// Plausible class-shaped numbers and a plain quadratic experience curve (level 2 at 500, level 99 at 2,425,500).
	// These are not read from Diablo II data and make no claim to equal it; load the owner's tables for the original values.
	public static ProgressionTables Preview { get; } = BuildPreview();
	private static ProgressionTables BuildPreview()
	{
		ClassDefinition Make(CharacterClass c, int str, int dex, int vit, int ene, int stamina, int lifeLevel, int manaLevel, int staminaLevel, int lifeVit, int manaEne, int staminaVit) =>
			new(c, str, dex, vit, ene, vit + 30, ene, stamina, lifeLevel, manaLevel, staminaLevel, lifeVit, manaEne, staminaVit, 5);
		var definitions = new[]
		{
			Make(CharacterClass.Amazon, 20, 25, 20, 15, 84, 8, 6, 4, 12, 6, 4),
			Make(CharacterClass.Sorceress, 10, 25, 10, 35, 74, 4, 8, 4, 8, 8, 4),
			Make(CharacterClass.Necromancer, 15, 25, 15, 25, 79, 6, 8, 4, 8, 8, 4),
			Make(CharacterClass.Paladin, 25, 20, 25, 15, 89, 8, 6, 4, 12, 6, 4),
			Make(CharacterClass.Barbarian, 30, 20, 25, 10, 92, 8, 4, 4, 16, 4, 4),
			Make(CharacterClass.Druid, 15, 20, 25, 20, 84, 6, 8, 4, 8, 8, 4),
			Make(CharacterClass.Assassin, 20, 20, 20, 25, 95, 8, 6, 5, 12, 6, 5),
		};
		var curve = Enumerable.Range(1, LevelCap).Select(level => (uint)(250 * level * (level - 1))).ToArray();
		return new(definitions, Enum.GetValues<CharacterClass>().ToDictionary(c => c, _ => curve), "preview", false);
	}
}

public sealed record SkillDefinition(int Id, string Name, CharacterClass Class, int RequiredLevel, int MaxLevel, int[] Requires);

public sealed class SkillTable
{
	public const int MaxSkills = 2048, MaxRankCap = 60;
	private readonly Dictionary<int, SkillDefinition> skills = new();
	public IReadOnlyCollection<SkillDefinition> All => skills.Values;
	public string Source { get; }
	public bool Original { get; }
	public SkillTable(IEnumerable<SkillDefinition> definitions, string source, bool original)
	{
		ArgumentNullException.ThrowIfNull(definitions); ArgumentNullException.ThrowIfNull(source);
		foreach (var skill in definitions.Take(MaxSkills + 1))
		{
			if (skills.Count >= MaxSkills || skill.Id <= 0 || !Enum.IsDefined(skill.Class) || skill.RequiredLevel is < 1 or > ProgressionTables.LevelCap ||
				skill.MaxLevel is < 1 or > MaxRankCap || skill.Requires is null || skill.Requires.Length > 3 || !skills.TryAdd(skill.Id, skill))
				throw new ArgumentException($"Invalid, duplicate or excess skill: {skill.Id}.", nameof(definitions));
		}
		foreach (var skill in skills.Values)
			foreach (int required in skill.Requires)
				if (required == skill.Id || !skills.TryGetValue(required, out var other) || other.Class != skill.Class || skill.Requires.Count(r => r == required) > 1)
					throw new ArgumentException($"Invalid prerequisite {required} for skill {skill.Id}.", nameof(definitions));
		// A prerequisite chain must end: follow each skill's requirements and fail on a cycle.
		foreach (var skill in skills.Values)
		{
			var seen = new HashSet<int>(); var stack = new Stack<int>([skill.Id]);
			while (stack.Count > 0)
				foreach (int next in skills[stack.Pop()].Requires)
				{
					if (next == skill.Id) throw new ArgumentException($"Cyclic prerequisite chain at skill {skill.Id}.", nameof(definitions));
					if (seen.Add(next)) stack.Push(next);
				}
		}
		Source = source; Original = original;
	}
	public bool TryGet(int id, out SkillDefinition skill) => skills.TryGetValue(id, out skill!);
	public SkillDefinition Get(int id) => skills.TryGetValue(id, out var skill) ? skill : throw new ArgumentException("Unknown skill.", nameof(id));

	// A small synthetic tree per class (a root, a level-6 skill needing it, a level-12 skill needing that) so the allocation
	// rules can be exercised without original data. The names are placeholders, not Diablo II skills.
	public static SkillTable Preview { get; } = new(Enum.GetValues<CharacterClass>().SelectMany(c =>
	{
		int baseId = ((int)c + 1) * 100;
		return new[]
		{
			new SkillDefinition(baseId + 1, $"{c} I", c, 1, 20, []),
			new SkillDefinition(baseId + 2, $"{c} II", c, 6, 20, [baseId + 1]),
			new SkillDefinition(baseId + 3, $"{c} III", c, 12, 20, [baseId + 2]),
		};
	}), "preview", false);
}

public static class Progression
{
	public static CharacterProgress Create(ProgressionTables tables, CharacterClass cls)
	{
		var d = tables.Get(cls);
		return new(cls, 1, 0, 0, 0, d.Strength, d.Dexterity, d.Vitality, d.Energy, []);
	}

	// Experience can raise several levels at once. At the level cap further experience is not accumulated.
	public static (CharacterProgress Progress, int LevelsGained) Gain(ProgressionTables tables, CharacterProgress progress, long experience)
	{
		ArgumentNullException.ThrowIfNull(progress);
		if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
		var d = tables.Get(progress.Class); int cap = tables.MaxLevel(progress.Class);
		uint capExperience = tables.ExperienceFor(progress.Class, cap);
		// Compare against the remaining room instead of adding first, so a huge award cannot overflow.
		long total = experience >= (long)capExperience - progress.Experience ? capExperience : progress.Experience + experience;
		int level = progress.Level;
		while (level < cap && total >= tables.ExperienceFor(progress.Class, level + 1)) level++;
		int gained = level - progress.Level;
		return (progress with { Level = level, Experience = (uint)total,
			StatPoints = progress.StatPoints + gained * d.StatPointsPerLevel, SkillPoints = progress.SkillPoints + gained * ProgressionTables.SkillPointsPerLevel }, gained);
	}

	// Preview rule until monster data exists in the simulation: a kill is worth the monster's maximum health.
	// This is not a Diablo II value (the original pays per monster type and level, and scales it by the level difference).
	public static uint PreviewKillExperience(int monsterMaxHealth) => (uint)Math.Clamp(monsterMaxHealth, 0, 100_000);

	public static CharacterProgress AddStat(CharacterProgress progress, CharacterStat stat)
	{
		ArgumentNullException.ThrowIfNull(progress);
		if (progress.StatPoints < 1) throw new InvalidOperationException("No stat points available.");
		return (stat switch
		{
			CharacterStat.Strength => progress with { Strength = progress.Strength + 1 },
			CharacterStat.Dexterity => progress with { Dexterity = progress.Dexterity + 1 },
			CharacterStat.Vitality => progress with { Vitality = progress.Vitality + 1 },
			CharacterStat.Energy => progress with { Energy = progress.Energy + 1 },
			_ => throw new ArgumentOutOfRangeException(nameof(stat))
		}) with { StatPoints = progress.StatPoints - 1 };
	}

	// Spends one skill point. The skill must belong to the class, the character must have reached its level, every prerequisite
	// must already have at least one point, and the skill may not exceed its own cap.
	public static CharacterProgress AddSkill(SkillTable skills, CharacterProgress progress, int skill)
	{
		ArgumentNullException.ThrowIfNull(progress);
		var definition = skills.Get(skill);
		if (progress.SkillPoints < 1) throw new InvalidOperationException("No skill points available.");
		if (definition.Class != progress.Class) throw new InvalidOperationException("The skill belongs to another class.");
		if (progress.Level < definition.RequiredLevel) throw new InvalidOperationException("The character level is too low for this skill.");
		if (definition.Requires.Any(r => progress.SkillLevel(r) < 1)) throw new InvalidOperationException("A prerequisite skill has no points.");
		int current = progress.SkillLevel(skill);
		if (current >= definition.MaxLevel) throw new InvalidOperationException("The skill is at its maximum level.");
		var ranks = progress.Skills.Where(r => r.Skill != skill).Append(new SkillRank(skill, current + 1)).OrderBy(r => r.Skill).ToArray();
		return progress with { Skills = ranks, SkillPoints = progress.SkillPoints - 1 };
	}

	// Life/mana/stamina: the class base at its starting attributes, plus growth per level and per attribute point above the start.
	public static CharacterResources Resources(ProgressionTables tables, CharacterProgress progress)
	{
		var d = tables.Get(progress.Class);
		int levels = progress.Level - 1, vit = progress.Vitality - d.Vitality, ene = progress.Energy - d.Energy;
		return new(d.BaseLife + (levels * d.LifePerLevel + vit * d.LifePerVitality) / 4,
			d.BaseMana + (levels * d.ManaPerLevel + ene * d.ManaPerEnergy) / 4,
			d.BaseStamina + (levels * d.StaminaPerLevel + vit * d.StaminaPerVitality) / 4);
	}

	// Rejects any progress that the rules above could not have produced. Used on everything loaded from disk or a file.
	public static void Validate(ProgressionTables tables, SkillTable skills, CharacterProgress progress)
	{
		if (progress is null || !Enum.IsDefined(progress.Class) || progress.Skills is null) throw new InvalidDataException("Invalid character progress.");
		var d = tables.Get(progress.Class); int cap = tables.MaxLevel(progress.Class);
		if (progress.Level < 1 || progress.Level > cap) throw new InvalidDataException("Character level is outside the class range.");
		uint minimum = tables.ExperienceFor(progress.Class, progress.Level);
		uint maximum = progress.Level == cap ? minimum : tables.ExperienceFor(progress.Class, progress.Level + 1) - 1;
		if (progress.Experience < minimum || progress.Experience > maximum) throw new InvalidDataException("Experience does not match the character level.");
		int added = 0;
		foreach (var stat in Enum.GetValues<CharacterStat>())
		{
			int extra = progress.Get(stat) - d.Starting(stat);
			if (extra < 0) throw new InvalidDataException("An attribute is below its class starting value.");
			added += extra;
		}
		if (progress.StatPoints < 0 || added + progress.StatPoints != (progress.Level - 1) * d.StatPointsPerLevel) throw new InvalidDataException("Attribute points do not add up to the points granted by levels.");
		int spent = 0, previous = int.MinValue;
		foreach (var rank in progress.Skills)
		{
			if (rank.Skill <= previous || !skills.TryGet(rank.Skill, out var definition) || definition.Class != progress.Class ||
				rank.Level < 1 || rank.Level > definition.MaxLevel || progress.Level < definition.RequiredLevel ||
				definition.Requires.Any(r => progress.SkillLevel(r) < 1))
				throw new InvalidDataException("Invalid skill rank, order or prerequisite.");
			previous = rank.Skill; spent += rank.Level;
		}
		if (progress.SkillPoints < 0 || spent + progress.SkillPoints != (progress.Level - 1) * ProgressionTables.SkillPointsPerLevel)
			throw new InvalidDataException("Skill points do not add up to the points granted by levels.");
	}
}
