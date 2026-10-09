namespace OpenD2.Core;

public readonly record struct ItemId(ulong Value);
public enum ItemDefinition { TrainingSword, TrainingVest, HealthPotion, ManaPotion }
public enum EquipmentSlot { Weapon, Body, None }
public enum ItemLocation { Ground, Inventory, Equipped, Belt, Consumed }
public enum ItemFailure { Interrupted, InvalidLocation, WrongOwner, WrongRegion, OutOfRange, Obstructed, InventoryFull, InvalidPlacement, NotConsumable, ResourceFull, AlreadyUsed, EmptyBelt }
public readonly record struct ItemSpec(string Name, EquipmentSlot Slot, int DamageBonus, int Armor, int HealthRecovery = 0, int ManaRecovery = 0);
public readonly record struct ItemState(ItemId Id, ItemDefinition Definition, ItemLocation Location,
	EntityId Owner, int Slot, RegionId Region, GamePosition Position);
public readonly record struct CombatStats(int MinimumDamage, int MaximumDamage, int Armor);

// Deliberately synthetic, immutable content. Rule changes require a RulesVersion bump.
public static class ItemCatalog
{
	public static bool IsConsumable(ItemDefinition definition) => definition is ItemDefinition.HealthPotion or ItemDefinition.ManaPotion;
	public static ItemSpec Get(ItemDefinition definition) => definition switch
	{
		ItemDefinition.TrainingSword => new("Training sword", EquipmentSlot.Weapon, 6, 0),
		ItemDefinition.TrainingVest => new("Training vest", EquipmentSlot.Body, 0, 2),
		ItemDefinition.HealthPotion => new("Health potion", EquipmentSlot.None, 0, 0, HealthRecovery: 40),
		ItemDefinition.ManaPotion => new("Mana potion", EquipmentSlot.None, 0, 0, ManaRecovery: 30),
		_ => throw new ArgumentOutOfRangeException(nameof(definition))
	};
}

public sealed partial class GameSimulation
{
	public const int InventoryCapacity = InventoryLayout.Cells;
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
	private ulong Occupied(EntityId owner, int except = -1, int other = -1)
	{
		ulong mask = 0;
		for (int i = 0; i < itemCount; i++)
			if (i != except && i != other && items[i].Owner == owner && items[i].Location == ItemLocation.Inventory) mask |= Inventory.Mask(items[i].Definition, items[i].Slot);
		return mask;
	}
	public int FindInventorySpace(EntityId owner, ItemDefinition definition) => Inventory.FirstFit(definition, Occupied(owner));
	public ItemId ItemAt(EntityId owner, int slot)
	{
		if (slot is < 0 or >= InventoryCapacity) return default;
		foreach (var item in Items)
			if (item.Owner == owner && item.Location == ItemLocation.Inventory && (Inventory.Mask(item.Definition, item.Slot) & (1UL << slot)) != 0) return item.Id;
		return default;
	}
	public bool CanMoveItem(EntityId owner, ItemId id, int slot)
	{
		int index = FindItem(id);
		return index >= 0 && items[index].Owner == owner && items[index].Location is ItemLocation.Inventory or ItemLocation.Equipped or ItemLocation.Belt && PlanMove(index, slot, out _, out _);
	}
	private bool PlanMove(int index, int slot, out int replaced, out int replacementSlot)
	{
		var item = items[index]; replaced = -1; replacementSlot = -1; ulong wanted = Inventory.Mask(item.Definition, slot);
		if (wanted == 0) return false;
		for (int i = 0; i < itemCount; i++)
			if (i != index && items[i].Owner == item.Owner && items[i].Location == ItemLocation.Inventory && (Inventory.Mask(items[i].Definition, items[i].Slot) & wanted) != 0)
			{ if (replaced >= 0) return false; replaced = i; }
		if (replaced < 0) return true;
		if (item.Location != ItemLocation.Inventory) return false;
		replacementSlot = item.Slot; ulong replacement = Inventory.Mask(items[replaced].Definition, replacementSlot);
		return replacement != 0 && (replacement & (wanted | Occupied(item.Owner, index, replaced))) == 0;
	}
	private void DropLoot(EntityState victim, long tick)
	{
		if (victim.Kind != EntityKind.Monster) return;
		// One equipment item and one potion per alive-to-dead transition; no respawn in this slice.
		// Separate typed ID namespace, stable across region visits, no combat RNG consumption.
		var id = new ItemId(victim.Id.Value);
		var definition = (victim.Id.Value & 1) == 0 ? ItemDefinition.TrainingSword : ItemDefinition.TrainingVest;
		items[itemCount++] = new(id, definition, ItemLocation.Ground, default, -1, victim.Region, victim.Position);
		EmitItem(tick, SimulationEventKind.ItemDropped, victim, id);
		var potion = PotionId(victim.Id);
		items[itemCount++] = new(potion, PotionDefinition(victim.Id), ItemLocation.Ground, default, -1, victim.Region, victim.Position);
		EmitItem(tick, SimulationEventKind.ItemDropped, victim, potion);
	}
	private void EmitItem(long tick, SimulationEventKind kind, EntityState actor, ItemId item, int value = 0)
	{ events[eventCount++] = new(tick, kind, actor.Id, actor.Region, actor.Position, actor.Position, value, Item: item); }
	private void ApplyItemCommand(int actorIndex, GameCommand command, long tick)
	{
		if (command.Kind is CommandKind.UseItem or CommandKind.BeltItem) { ApplyPotionCommand(actorIndex, command, tick); return; }
		var actor = entities[actorIndex]; int index = FindItem(command.Item); var item = items[index];
		ItemFailure? failure = !canAct[actorIndex] ? ItemFailure.Interrupted : null;
		int slot = -1, replaced = -1, replacementSlot = -1;
		if (failure is null && command.Kind == CommandKind.Pickup)
		{
			failure = item.Location != ItemLocation.Ground ? ItemFailure.InvalidLocation :
				item.Region != actor.Region ? ItemFailure.WrongRegion :
				DistanceSquared(actor.Position, item.Position) > (long)AttackRange * AttackRange ? ItemFailure.OutOfRange :
				Collision is null || !Collision.HasMeleeLine(actor.Position, item.Position) ? ItemFailure.Obstructed : null;
			if (failure is null && (slot = FindInventorySpace(actor.Id, item.Definition)) < 0) failure = ItemFailure.InventoryFull;
		}
		else if (failure is null)
		{
			failure = item.Owner != actor.Id ? ItemFailure.WrongOwner :
				!ValidTransferSource(item, command.Kind) ? ItemFailure.InvalidLocation : null;
			if (failure is null && command.Kind == CommandKind.Unequip && (slot = FindInventorySpace(actor.Id, item.Definition)) < 0) failure = ItemFailure.InventoryFull;
			if (failure is null && command.Kind == CommandKind.Equip)
			{
				slot = (int)ItemCatalog.Get(item.Definition).Slot;
				for (int i = 0; i < itemCount; i++)
					if (items[i].Owner == actor.Id && items[i].Location == ItemLocation.Equipped && items[i].Slot == slot) { replaced = i; break; }
				if (replaced >= 0)
				{
					ulong occupied = Occupied(actor.Id, index), mask = Inventory.Mask(items[replaced].Definition, item.Slot);
					replacementSlot = mask != 0 && (mask & occupied) == 0 ? item.Slot : Inventory.FirstFit(items[replaced].Definition, occupied);
					if (replacementSlot < 0) failure = ItemFailure.InventoryFull;
				}
			}
		}
		if (failure is null && command.Kind == CommandKind.MoveItem)
		{
			slot = command.Y * InventoryLayout.Width + command.X;
			if (!PlanMove(index, slot, out replaced, out replacementSlot)) failure = ItemFailure.InvalidPlacement;
		}
		if (failure is { } reason) { EmitItem(tick, SimulationEventKind.ItemFailed, actor, item.Id, (int)reason); return; }
		// Validate both rectangles first; rejected transfers never lose or duplicate an item.
		if (replaced >= 0) items[replaced] = items[replaced] with { Location = ItemLocation.Inventory, Slot = replacementSlot };
		items[index] = command.Kind switch
		{
			CommandKind.Pickup => item with { Location = ItemLocation.Inventory, Owner = actor.Id, Slot = slot, Region = default, Position = default },
			CommandKind.Equip => item with { Location = ItemLocation.Equipped, Slot = slot },
			CommandKind.Unequip or CommandKind.MoveItem => item with { Location = ItemLocation.Inventory, Slot = slot },
			CommandKind.DropItem => item with { Location = ItemLocation.Ground, Owner = default, Slot = -1, Region = actor.Region, Position = actor.Position },
			_ => throw new InvalidOperationException("Unexpected item command.")
		};
		EmitItem(tick, SimulationEventKind.ItemChanged, actor, item.Id, (int)command.Kind);
	}
	private static bool ValidTransferSource(ItemState item, CommandKind kind) => kind switch
	{
		CommandKind.MoveItem => item.Location is ItemLocation.Inventory or ItemLocation.Equipped or ItemLocation.Belt,
		CommandKind.Unequip => item.Location is ItemLocation.Equipped or ItemLocation.Belt,
		CommandKind.DropItem => item.Location is ItemLocation.Inventory or ItemLocation.Belt,
		CommandKind.Equip => item.Location == ItemLocation.Inventory && !ItemCatalog.IsConsumable(item.Definition),
		_ => false
	};
	private static bool IsItemCommand(CommandKind kind) => kind is CommandKind.Pickup or CommandKind.Equip or CommandKind.Unequip or CommandKind.DropItem or CommandKind.MoveItem or CommandKind.UseItem or CommandKind.BeltItem;
	private static void WriteItemId(BinaryWriter writer, ItemId id, int version)
	{ if (version >= 7) writer.Write(id.Value); else writer.Write(checked((uint)id.Value)); }
	private void WriteItems(BinaryWriter writer, int version)
	{
		writer.Write(itemCount);
		foreach (var item in Items)
		{
			WriteItemId(writer, item.Id, version); writer.Write((int)item.Definition); writer.Write((int)item.Location);
			writer.Write(item.Owner.Value); writer.Write(item.Slot); writer.Write(item.Region.Value); writer.Write(item.Position.X); writer.Write(item.Position.Y);
		}
	}
}
