using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly Button[] itemSlots = Enumerable.Range(0, 10).Select(_ => new Button
	{ ToggleMode = true, ClipText = true, CustomMinimumSize = new Vector2(180, 48), SizeFlagsHorizontal = SizeFlags.ExpandFill }).ToArray();
	private readonly ItemId[] slotItems = new ItemId[10];
	private readonly Label itemDetails = new() { Text = "Select an item to inspect it.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private ItemId selectedItem;
	private void BuildInventory()
	{
		AddChild(new Label { Text = "Inventory & equipment — preview rules" });
		var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill }; AddChild(grid);
		for (int i = 0; i < itemSlots.Length; i++)
		{
			int slot = i; grid.AddChild(itemSlots[i]);
			itemSlots[i].Pressed += () => { selectedItem = slotItems[slot]; RefreshItems(); };
		}
		AddChild(itemDetails); AddChild(gearInfo);
		var actions = new HFlowContainer(); AddChild(actions);
		foreach (var button in new[] { pickup, equip, unequip, drop }) actions.AddChild(button);
		pickup.Pressed += PickupNearest; equip.Pressed += () => UseSelected(CommandKind.Equip);
		unequip.Pressed += () => UseSelected(CommandKind.Unequip); drop.Pressed += () => UseSelected(CommandKind.DropItem);
	}
	private void UseSelected(CommandKind kind)
	{
		if (selectedItem != default && !verifying) Submit(kind, item: selectedItem);
	}
	private void RefreshItems()
	{
		Array.Clear(slotItems); int bagCount = 0; ItemState? selected = null;
		foreach (var item in simulation.Items)
		{
			if (item.Owner != Player) continue;
			int slot = item.Location == ItemLocation.Inventory ? item.Slot + 2 : item.Slot;
			slotItems[slot] = item.Id;
			if (item.Location == ItemLocation.Inventory) bagCount++;
			if (item.Id == selectedItem) selected = item;
		}
		if (selected is null) selectedItem = default;
		for (int i = 0; i < itemSlots.Length; i++)
		{
			string label = i < 2 ? ((EquipmentSlot)i).ToString() : $"Bag {i - 1}";
			string name = slotItems[i] == default ? "Empty" : ItemCatalog.Get(simulation.GetItem(slotItems[i]).Definition).Name;
			itemSlots[i].Text = label + "\n" + name; itemSlots[i].TooltipText = itemSlots[i].Text;
			itemSlots[i].Disabled = verifying || slotItems[i] == default; itemSlots[i].SetPressedNoSignal(selectedItem != default && slotItems[i] == selectedItem);
		}
		equip.Disabled = verifying || selected is not { Location: ItemLocation.Inventory };
		unequip.Disabled = verifying || bagCount == GameSimulation.InventoryCapacity || selected is not { Location: ItemLocation.Equipped };
		drop.Disabled = verifying || selected is not { Location: ItemLocation.Inventory };
		if (selected is { } chosen)
		{
			var spec = ItemCatalog.Get(chosen.Definition);
			itemDetails.Text = $"{spec.Name} · {chosen.Location}\n{spec.Slot} | damage bonus +{spec.DamageBonus} | armor {spec.Armor}";
		}
		else itemDetails.Text = "Select an item to inspect it. Pick up nearby loot with F.";
		var stats = simulation.GetStats(Player);
		gearInfo.Text = $"Bag {bagCount}/{GameSimulation.InventoryCapacity} | Damage {stats.MinimumDamage}–{stats.MaximumDamage} | Armor {stats.Armor}";
	}
	private void InventorySmoke()
	{
		// Exercise actual command -> tick -> panel bindings without GUI input or original content.
		NewRun();
		var snapshot = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(snapshot with { Entities = snapshot.Entities.Select(e => e.Id == new EntityId(2) ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(), Items = [new(new(2), ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 0, default, default)] });
		recording = new(simulation); view.SetSimulation(simulation); selectedItem = new(2); RefreshItems();
		if (slotItems[2] != selectedItem || equip.Disabled || !unequip.Disabled) throw new InvalidDataException("Bag selection binding failed.");
		UseSelected(CommandKind.Equip); RunTick(); RefreshItems();
		if (slotItems[0] != selectedItem || !equip.Disabled || unequip.Disabled || !drop.Disabled || !itemDetails.Text.Contains("Equipped")) throw new InvalidDataException("Equipment binding failed.");
		UseSelected(CommandKind.Unequip); RunTick(); RefreshItems();
		if (slotItems[2] != selectedItem || drop.Disabled || !unequip.Disabled) throw new InvalidDataException("Unequip binding failed.");
		UseSelected(CommandKind.DropItem); RunTick(); RefreshItems();
		if (selectedItem != default || !drop.Disabled || slotItems.Any(id => id != default)) throw new InvalidDataException("Dropped selection was retained.");
		NewRun(); GD.Print("OPEND2_M206_PANELS_READY");
	}
}
