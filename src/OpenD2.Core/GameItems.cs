namespace OpenD2.Core;

public readonly record struct ItemId(uint Value);
public enum ItemDefinition { TrainingSword, TrainingVest }
public enum EquipmentSlot { Weapon, Body }
public enum ItemLocation { Ground, Inventory, Equipped }
public enum ItemFailure { Interrupted, InvalidLocation, WrongOwner, WrongRegion, OutOfRange, Obstructed, InventoryFull }
public readonly record struct ItemSpec(string Name, EquipmentSlot Slot, int DamageBonus, int Armor);
public readonly record struct ItemState(ItemId Id, ItemDefinition Definition, ItemLocation Location,
	EntityId Owner, int Slot, RegionId Region, GamePosition Position);
public readonly record struct CombatStats(int MinimumDamage, int MaximumDamage, int Armor);

// Deliberately synthetic, immutable content. Rule changes require a RulesVersion bump.
public static class ItemCatalog
{
	public static ItemSpec Get(ItemDefinition definition) => definition switch
	{
		ItemDefinition.TrainingSword => new("Training sword", EquipmentSlot.Weapon, 6, 0),
		ItemDefinition.TrainingVest => new("Training vest", EquipmentSlot.Body, 0, 2),
		_ => throw new ArgumentOutOfRangeException(nameof(definition))
	};
}

public sealed partial class GameSimulation
{
	public const int InventoryCapacity = 8;
	private readonly ItemState[] items;
	private int itemCount;
	public ReadOnlySpan<ItemState> Items => items.AsSpan(0, itemCount);
	public ItemState GetItem(ItemId id) => items[FindItem(id) is var index && index >= 0 ? index : throw new ArgumentException("Unknown item.", nameof(id))];
	private int FindItem(ItemId id)
	{
		for (int i = 0; i < itemCount; i++) if (items[i].Id == id) return i;
		return -1;
	}
	public CombatStats GetStats(EntityId id)
	{
		var actor = GetEntity(id); int bonus = 0, armor = 0;
		foreach (var item in Items)
			if (item.Owner == id && item.Location == ItemLocation.Equipped)
			{ var spec = ItemCatalog.Get(item.Definition); bonus += spec.DamageBonus; armor += spec.Armor; }
		return actor.Kind == EntityKind.Player ? new(14 + bonus, 20 + bonus, armor) : new(4, 7, armor);
	}
	private int FreeInventorySlot(EntityId owner)
	{
		uint used = 0;
		foreach (var item in Items)
			if (item.Owner == owner && item.Location == ItemLocation.Inventory) used |= 1u << item.Slot;
		for (int i = 0; i < InventoryCapacity; i++) if ((used & (1u << i)) == 0) return i;
		return -1;
	}
	private void DropLoot(EntityState victim, long tick)
	{
		if (victim.Kind != EntityKind.Monster) return;
		// One item per monster's alive-to-dead transition; no respawn in this slice.
		// Separate typed ID namespace, stable across region visits, no combat RNG consumption.
		var id = new ItemId(victim.Id.Value);
		var definition = (victim.Id.Value & 1) == 0 ? ItemDefinition.TrainingSword : ItemDefinition.TrainingVest;
		items[itemCount++] = new(id, definition, ItemLocation.Ground, default, -1, victim.Region, victim.Position);
		EmitItem(tick, SimulationEventKind.ItemDropped, victim, id);
	}
	private void EmitItem(long tick, SimulationEventKind kind, EntityState actor, ItemId item, int value = 0)
	{ events[eventCount++] = new(tick, kind, actor.Id, actor.Region, actor.Position, actor.Position, value, Item: item); }
	private void ApplyItemCommand(int actorIndex, GameCommand command, long tick)
	{
		var actor = entities[actorIndex]; int index = FindItem(command.Item); var item = items[index];
		ItemFailure? failure = !canAct[actorIndex] ? ItemFailure.Interrupted : null;
		int slot = -1, replaced = -1;
		if (failure is null && command.Kind == CommandKind.Pickup)
		{
			failure = item.Location != ItemLocation.Ground ? ItemFailure.InvalidLocation :
				item.Region != actor.Region ? ItemFailure.WrongRegion :
				DistanceSquared(actor.Position, item.Position) > (long)AttackRange * AttackRange ? ItemFailure.OutOfRange :
				Collision is null || !Collision.HasMeleeLine(actor.Position, item.Position) ? ItemFailure.Obstructed : null;
			if (failure is null && (slot = FreeInventorySlot(actor.Id)) < 0) failure = ItemFailure.InventoryFull;
		}
		else if (failure is null)
		{
			failure = item.Owner != actor.Id ? ItemFailure.WrongOwner :
				item.Location != (command.Kind == CommandKind.Unequip ? ItemLocation.Equipped : ItemLocation.Inventory) ? ItemFailure.InvalidLocation : null;
			if (failure is null && command.Kind == CommandKind.Unequip && (slot = FreeInventorySlot(actor.Id)) < 0) failure = ItemFailure.InventoryFull;
			if (failure is null && command.Kind == CommandKind.Equip)
			{
				slot = (int)ItemCatalog.Get(item.Definition).Slot;
				for (int i = 0; i < itemCount; i++)
					if (items[i].Owner == actor.Id && items[i].Location == ItemLocation.Equipped && items[i].Slot == slot) { replaced = i; break; }
			}
		}
		if (failure is { } reason) { EmitItem(tick, SimulationEventKind.ItemFailed, actor, item.Id, (int)reason); return; }
		// Validate first, then commit the complete transfer. Swaps reuse the incoming bag slot.
		if (replaced >= 0) items[replaced] = items[replaced] with { Location = ItemLocation.Inventory, Slot = item.Slot };
		items[index] = command.Kind switch
		{
			CommandKind.Pickup => item with { Location = ItemLocation.Inventory, Owner = actor.Id, Slot = slot, Region = default, Position = default },
			CommandKind.Equip => item with { Location = ItemLocation.Equipped, Slot = slot },
			CommandKind.Unequip => item with { Location = ItemLocation.Inventory, Slot = slot },
			CommandKind.DropItem => item with { Location = ItemLocation.Ground, Owner = default, Slot = -1, Region = actor.Region, Position = actor.Position },
			_ => throw new InvalidOperationException("Unexpected item command.")
		};
		EmitItem(tick, SimulationEventKind.ItemChanged, actor, item.Id, (int)command.Kind);
	}
	private static bool IsItemCommand(CommandKind kind) => kind is CommandKind.Pickup or CommandKind.Equip or CommandKind.Unequip or CommandKind.DropItem;
	private void WriteItems(BinaryWriter writer)
	{
		writer.Write(itemCount);
		foreach (var item in Items)
		{
			writer.Write(item.Id.Value); writer.Write((int)item.Definition); writer.Write((int)item.Location);
			writer.Write(item.Owner.Value); writer.Write(item.Slot); writer.Write(item.Region.Value); writer.Write(item.Position.X); writer.Write(item.Position.Y);
		}
	}
}
