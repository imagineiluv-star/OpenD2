namespace OpenD2.Core;

// Persistent character data carries placement/content, not an old room's actor/item IDs.
public readonly record struct CarriedItem(EntityId Owner, ItemDefinition Definition, ItemLocation Location, int Slot);

public sealed partial class GameSimulation
{
	public const int MaxCarriedItems = 256;
	public CarriedItem[] CaptureCarriedItems(EntityId owner)
	{
		if (GetEntity(owner).Kind != EntityKind.Player) throw new ArgumentException("Player inventory required.");
		return Items.ToArray().Where(i => i.Owner == owner && i.Location is ItemLocation.Inventory or ItemLocation.Equipped or ItemLocation.Belt)
			.Select(i => new CarriedItem(owner, i.Definition, i.Location, i.Slot)).ToArray();
	}
	// Returns a validated new owner; never partially mutates the destination game.
	// IDs are room-local. Namespace 2 cannot overlap equipment (0) or potion (1) drops.
	public GameSimulation WithCarriedItems(IReadOnlyList<CarriedItem> carried)
	{
		ArgumentNullException.ThrowIfNull(carried);
		if (Tick != 0 || PendingCommands != 0 || itemCount != 0 || carried.Count > MaxCarriedItems)
			throw new InvalidDataException("Carried items require an empty, unstarted game within budget.");
		var restored = new ItemState[carried.Count];
		for (int i = 0; i < carried.Count; i++)
		{
			var item = carried[i];
			if (item.Location is not (ItemLocation.Inventory or ItemLocation.Equipped or ItemLocation.Belt))
				throw new InvalidDataException("Only owned bag, equipment and belt items can travel.");
			restored[i] = new(new((2UL << 32) | (uint)(i + 1)), item.Definition, item.Location, item.Owner, item.Slot, default, default);
		}
		return Restore(CaptureSnapshot() with { Items = restored });
	}
	// Online room storage also verifies the old hash BEFORE upgrading rules.
	public static GameSimulation RestoreCompatible(SimulationSnapshot state, string stateHash)
	{
		if (state.RulesVersion is not (7 or RulesVersion)) throw new InvalidDataException("Unsupported room rules.");
		var game = state.RulesVersion == RulesVersion ? Restore(state) : RestoreLegacy(state);
		if (game.ComputeStateHash(state.RulesVersion) != stateHash) throw new InvalidDataException("Room checksum mismatch.");
		return state.RulesVersion == RulesVersion ? game : Restore(game.CaptureSnapshot());
	}
}
