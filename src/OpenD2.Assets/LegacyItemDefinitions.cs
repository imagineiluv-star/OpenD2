using System.Collections.ObjectModel;
using OpenD2.Core;

namespace OpenD2.Assets;

public sealed record LegacyItemBinding(string Definition, string Code);
public sealed record LegacyItemDefinitionsRequest(string Profile, LegacyItemBinding[] Bindings);

// Explicit associations for reference data and future rule migration. Never changes Core stats.
public sealed class LegacyItemDefinitions
{
	public ItemTables Tables { get; }
	public IReadOnlyDictionary<ItemDefinition, ItemBaseDefinition> Bindings { get; }
	private LegacyItemDefinitions(ItemTables tables, Dictionary<ItemDefinition, ItemBaseDefinition> bindings)
	{ Tables = tables; Bindings = new ReadOnlyDictionary<ItemDefinition, ItemBaseDefinition>(bindings); }
	public static LegacyItemDefinitionsRequest Snapshot(LegacyItemDefinitionsRequest request)
	{
		if (request is null || request.Profile != ItemTables.Profile || request.Bindings is null || request.Bindings.Length < 1 || request.Bindings.Length > Enum.GetValues<ItemDefinition>().Length)
			throw new InvalidDataException("Item definitions require lod-1.10f and at least one explicit binding.");
		var snapshot = request with { Bindings = request.Bindings.ToArray() }; var seen = new HashSet<ItemDefinition>();
		foreach (var binding in snapshot.Bindings)
		{
			if (binding is null || !Enum.TryParse<ItemDefinition>(binding.Definition, out var id) || !Enum.IsDefined(id) || binding.Definition != id.ToString() || !seen.Add(id))
				throw new InvalidDataException("Item definition bindings must reference unique preview catalog names.");
			ItemTables.Code(binding.Code);
		}
		return snapshot;
	}
	public static LegacyItemDefinitions Load(LegacyItemDefinitionsRequest request, Func<string, byte[]> read)
	{
		var snapshot = Snapshot(request); var tables = ItemTables.Load(read, snapshot.Profile);
		var bindings = new Dictionary<ItemDefinition, ItemBaseDefinition>();
		foreach (var binding in snapshot.Bindings)
		{
			var id = Enum.Parse<ItemDefinition>(binding.Definition); var item = tables.Get(binding.Code);
			if (!CanBind(id, item)) throw new InvalidDataException($"Item {item.Code} does not match the equipment category of {id}.");
			bindings.Add(id, item);
		}
		return new(tables, bindings);
	}
	public static bool CanBind(ItemDefinition id, ItemBaseDefinition item) => item.Equippable && (id switch
	{
		ItemDefinition.TrainingSword => item.Kind == ItemTableKind.Weapon && (item.BodyLoc1 is "rarm" or "larm" || item.BodyLoc2 is "rarm" or "larm"),
		ItemDefinition.TrainingVest => item.Kind == ItemTableKind.Armor && (item.BodyLoc1 == "tors" || item.BodyLoc2 == "tors"),
		_ => false
	});
}
