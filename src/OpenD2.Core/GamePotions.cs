namespace OpenD2.Core;

public sealed partial class GameSimulation
{
	public const int BeltCapacity = 4;
	private readonly bool[] usedPotions;
	// Equipment keeps its original uint ID. A second drop namespace avoids collisions,
	// including monsters whose own IDs use the high bit of uint.
	private static ItemId PotionId(EntityId source) => new((1UL << 32) | source.Value);
	private static ItemDefinition PotionDefinition(EntityId source) => (source.Value & 1) == 0 ? ItemDefinition.HealthPotion : ItemDefinition.ManaPotion;
	private static bool ValidItemIdentity(ItemState item, bool legacy)
	{
		if ((uint)item.Id.Value == 0) return false;
		if (legacy) return item.Id.Value <= uint.MaxValue && !ItemCatalog.IsConsumable(item.Definition) && item.Location is not (ItemLocation.Belt or ItemLocation.Consumed);
		return item.Id.Value <= uint.MaxValue ? !ItemCatalog.IsConsumable(item.Definition) : item.Id.Value >> 32 == 1 && ItemCatalog.IsConsumable(item.Definition);
	}
	public ItemId BeltAt(EntityId owner, int slot)
	{
		if (slot is < 0 or >= BeltCapacity) return default;
		foreach (var item in Items) if (item.Owner == owner && item.Location == ItemLocation.Belt && item.Slot == slot) return item.Id;
		return default;
	}
	public bool CanBeltItem(EntityId owner, ItemId id, int slot)
	{
		int index = FindItem(id);
		return index >= 0 && slot is >= 0 and < BeltCapacity && items[index].Owner == owner && ItemCatalog.IsConsumable(items[index].Definition) && items[index].Location is ItemLocation.Inventory or ItemLocation.Belt;
	}
	private void UseBelt(int actorIndex, int slot, long tick)
	{
		var actor = entities[actorIndex]; var id = BeltAt(actor.Id, slot);
		if (id == default) { EmitItem(tick, SimulationEventKind.ItemFailed, actor, default, (int)(canAct[actorIndex] ? ItemFailure.EmptyBelt : ItemFailure.Interrupted)); return; }
		ApplyPotionCommand(actorIndex, new(tick, 1, actor.Id, actor.Region, CommandKind.UseItem, Item: id), tick);
	}
	private void ApplyPotionCommand(int actorIndex, GameCommand command, long tick)
	{
		var actor = entities[actorIndex]; int index = FindItem(command.Item); var item = items[index];
		ItemFailure? failure = !canAct[actorIndex] ? ItemFailure.Interrupted : item.Location == ItemLocation.Consumed ? ItemFailure.InvalidLocation :
			item.Owner != actor.Id ? ItemFailure.WrongOwner : item.Location is not (ItemLocation.Inventory or ItemLocation.Belt) ? ItemFailure.InvalidLocation :
			!ItemCatalog.IsConsumable(item.Definition) ? ItemFailure.NotConsumable : null;
		if (failure is null && command.Kind == CommandKind.UseItem)
		{
			var spec = ItemCatalog.Get(item.Definition);
			int health = Math.Min(actor.MaxHealth, actor.Health + spec.HealthRecovery), mana = Math.Min(actor.MaxMana, actor.Mana + spec.ManaRecovery);
			failure = usedPotions[actorIndex] ? ItemFailure.AlreadyUsed : health == actor.Health && mana == actor.Mana ? ItemFailure.ResourceFull : null;
			if (failure is null)
			{
				entities[actorIndex] = actor with { Health = health, Mana = mana, ManaRecoveryTicks = mana == actor.MaxMana ? 0 : actor.ManaRecoveryTicks };
				// Keep a bounded tombstone so queued/replayed IDs cannot resurrect a consumed item.
				items[index] = item with { Location = ItemLocation.Consumed, Owner = default, Slot = -1, Region = default, Position = default };
				usedPotions[actorIndex] = true;
				EmitItem(tick, SimulationEventKind.ItemConsumed, actor, item.Id, health - actor.Health + mana - actor.Mana); return;
			}
		}
		if (failure is { } reason) { EmitItem(tick, SimulationEventKind.ItemFailed, actor, item.Id, (int)reason); return; }
		// Both items are 1x1. Moving from a bag swaps the belt occupant into the exact
		// vacated bag cell; belt-to-belt moves exchange their slots atomically.
		int replaced = FindItem(BeltAt(actor.Id, command.X));
		if (replaced >= 0 && replaced != index) items[replaced] = items[replaced] with { Location = item.Location, Slot = item.Slot };
		items[index] = item with { Location = ItemLocation.Belt, Slot = command.X };
		EmitItem(tick, SimulationEventKind.ItemChanged, actor, item.Id, (int)CommandKind.BeltItem);
	}
}
