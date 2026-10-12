using System.Text.RegularExpressions;

namespace OpenD2.Core;

// A monster kind as the original MonStats table describes it: a code, a name key, a level and the experience a kill is worth.
// Only the normal difficulty is modelled. Health, damage, resistances and drops stay with the entity or with later milestones.
// The numbers come from a MonsterTable: OpenD2.Assets.MonsterTables reads the owner's monstats.txt, and Preview is a synthetic
// stand-in that is NOT Diablo II data.
public sealed record MonsterDefinition(string Code, string Name, int Level, int Experience);

public sealed class MonsterTable
{
	public const int MaxMonsters = 4096, MaxLevel = 255, MaxExperience = 100_000_000;
	private static readonly Regex CodePattern = new("\\A[A-Za-z0-9_-]([A-Za-z0-9 _-]{0,30}[A-Za-z0-9_-])?\\z", RegexOptions.CultureInvariant);
	private readonly Dictionary<string, MonsterDefinition> kinds = new(StringComparer.OrdinalIgnoreCase);
	public IReadOnlyCollection<MonsterDefinition> All => kinds.Values;
	// "preview" for the built-in table, otherwise the profile and hash of the owner's file.
	public string Source { get; }
	public bool Original { get; }

	public MonsterTable(IEnumerable<MonsterDefinition> definitions, string source, bool original)
	{
		ArgumentNullException.ThrowIfNull(definitions); ArgumentNullException.ThrowIfNull(source);
		foreach (var kind in definitions.Take(MaxMonsters + 1))
		{
			if (kinds.Count >= MaxMonsters || kind is null || kind.Code is null || !CodePattern.IsMatch(kind.Code) ||
				kind.Name is not { Length: >= 1 and <= 64 } || kind.Name.Any(char.IsControl) ||
				kind.Level is < 0 or > MaxLevel || kind.Experience is < 0 or > MaxExperience || !kinds.TryAdd(kind.Code, kind))
				throw new ArgumentException($"Invalid, duplicate or excess monster kind: {kind?.Code}.", nameof(definitions));
		}
		if (kinds.Count == 0) throw new ArgumentException("A monster table cannot be empty.", nameof(definitions));
		Source = source; Original = original;
	}
	public bool TryGet(string code, out MonsterDefinition kind) => kinds.TryGetValue(code, out kind!);
	public MonsterDefinition Get(string code) => kinds.TryGetValue(code, out var kind) ? kind : throw new ArgumentException("Unknown monster kind.", nameof(code));

	// Two synthetic kinds so the structure can be exercised without original data. The first is worth what a 60-health monster was
	// worth under the earlier "experience = maximum health" placeholder, so existing rooms keep paying the same.
	public static MonsterTable Preview { get; } = new([new("preview-grunt", "Preview grunt", 1, 60), new("preview-brute", "Preview brute", 2, 100)], "preview", false);
}

// Which kind each monster of a built-in room is. It lives next to the content, not in the simulation: entity state, saves, hashes and
// the rules version do not change, and a kill's value is looked up by the server when it happens.
public sealed class MonsterRoster
{
	private readonly Dictionary<uint, string> kinds = new();
	public IReadOnlyDictionary<uint, string> Kinds => kinds;

	public MonsterRoster(IEnumerable<(uint Entity, string Code)> assignments)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		foreach (var (entity, code) in assignments.Take(GameSimulation.MaxEntities + 1))
			if (entity == 0 || code is null || kinds.Count >= GameSimulation.MaxEntities || !kinds.TryAdd(entity, code))
				throw new ArgumentException($"Invalid or duplicate roster entry: {entity}.", nameof(assignments));
	}
	public string? KindOf(EntityId id) => kinds.GetValueOrDefault(id.Value);

	// Every monster of the starting state has a kind that the table defines, and nothing else has one. A table that lacks one of
	// the roster's kinds (for example the owner's real table with a built-in preview world) is refused instead of paying nothing.
	public void Validate(MonsterTable table, IEnumerable<EntityState> entities)
	{
		ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(entities);
		var monsters = entities.Where(e => e.Kind == EntityKind.Monster).Select(e => e.Id.Value).ToHashSet();
		foreach (uint id in monsters)
			if (!kinds.TryGetValue(id, out var code)) throw new InvalidDataException($"Monster {id} has no kind in the roster.");
			else if (!table.TryGet(code, out _)) throw new InvalidDataException($"Monster {id} is a '{code}', which the monster table does not define ({table.Source}).");
		foreach (uint id in kinds.Keys)
			if (!monsters.Contains(id)) throw new InvalidDataException($"The roster names {id}, which is not a monster of this room.");
	}
}
