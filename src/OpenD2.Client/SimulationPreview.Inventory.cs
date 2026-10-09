using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly Button[] itemSlots = Enumerable.Range(0, 10).Select(_ => new Button
	{ ToggleMode = true, ClipText = true, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest,
		CustomMinimumSize = new Vector2(180, 48), SizeFlagsHorizontal = SizeFlags.ExpandFill }).ToArray();
	private LegacyItemTextures? itemTextures;
	private readonly ItemId[] slotItems = new ItemId[10];
	private readonly Label itemDetails = new() { Text = "Select an item to inspect it.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private ItemId selectedItem;
	private readonly VBoxContainer inventoryPanel = new();
	private readonly Button inventoryButton = new() { Text = "Inventory" };
	private void SetItemTextures(LegacyItemTextures? next)
	{
		foreach (var button in itemSlots) button.Icon = null;
		itemTextures?.Dispose(); itemTextures = next;
	}
	private void ToggleInventory()
	{
		if (verifying || menuOpen || pendingRestart is not null) return;
		StopInput(); inventoryPanel.Visible = !inventoryPanel.Visible;
		if (inventoryPanel.Visible) inventoryButton.GrabFocus(); else view.GrabFocus();
	}
	private void BuildInventory()
	{
		inventoryButton.Pressed += ToggleInventory; playPanel.AddChild(inventoryPanel);
		inventoryPanel.AddChild(new Label { Text = "Inventory & equipment — preview rules" });
		var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill }; inventoryPanel.AddChild(grid);
		for (int i = 0; i < itemSlots.Length; i++)
		{
			int slot = i; itemSlots[i].AddThemeConstantOverride("icon_max_width", 40); grid.AddChild(itemSlots[i]);
			itemSlots[i].Pressed += () => { selectedItem = slotItems[slot]; RefreshItems(); };
		}
		inventoryPanel.AddChild(itemDetails); inventoryPanel.AddChild(gearInfo);
		var actions = new HFlowContainer(); inventoryPanel.AddChild(actions);
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
			itemSlots[i].Icon = slotItems[i] == default ? null : itemTextures?.Get(simulation.GetItem(slotItems[i]).Definition);
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
			if (legacyScene?.ItemDefinitions?.Bindings.TryGetValue(chosen.Definition, out var original) == true)
				itemDetails.Text += "\n\n" + ItemDefinitionText.Describe(original);
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
	internal static LegacyItemRequest SampleItems() => new("data/global/palette/act1/pal.dat", [new("TrainingSword", "item.dc6", 0)]);
	private void ItemArtworkSmoke()
	{
		if (itemTextures?.CheckTexture() != true) throw new InvalidDataException("Item icon RGBA upload failed.");
		NewRun(); var snapshot = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(snapshot with { Entities = snapshot.Entities.Select(e => e.Id == new EntityId(2) ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(),
			Items = [new(new(2), ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 0, default, default)] });
		recording = new(simulation); view.SetSimulation(simulation); selectedItem = new(2); RefreshItems();
		if (!itemDetails.Text.Contains("Fixture sword [fws]") || !itemDetails.Text.Contains("Size 1×3") || !itemDetails.Text.Contains("7–13") || !itemDetails.Text.Contains("Reference only") || simulation.GetStats(Player).MinimumDamage != 14)
			throw new InvalidDataException("Original reference definition binding changed preview stats or omitted source values.");
		var texture = itemTextures.Get(ItemDefinition.TrainingSword);
		if (itemSlots[2].Icon != texture || itemSlots[0].Icon is not null || !itemSlots[2].Text.Contains("Training sword")) throw new InvalidDataException("Bag icon/name binding failed.");
		UseSelected(CommandKind.Equip); RunTick(); RefreshItems();
		if (itemSlots[0].Icon != texture || itemSlots[2].Icon is not null) throw new InvalidDataException("Equipped icon retained the old bag image.");
		UseSelected(CommandKind.Unequip); RunTick(); RefreshItems();
		if (itemSlots[2].Icon != texture || itemSlots[0].Icon is not null) throw new InvalidDataException("Unequip icon binding failed.");
		UseSelected(CommandKind.DropItem); RunTick(); RefreshItems();
		if (itemSlots.Any(b => b.Icon is not null)) throw new InvalidDataException("Dropped icon was retained.");
		Submit(CommandKind.Pickup, item: new(2)); RunTick(); RefreshItems();
		if (itemSlots[2].Icon != texture) throw new InvalidDataException("Pickup did not restore the icon.");
		// Icons follow definitions; missing mappings retain the catalog label.
		snapshot = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(snapshot with { Entities = snapshot.Entities.Append(snapshot.Entities.Single(e => e.Id == new EntityId(2)) with { Id = new(3) }).ToArray(),
			Inputs = snapshot.Inputs.Append(new CommandCursor(new(3), 0, 0)).ToArray(), Items = [new(new(3), ItemDefinition.TrainingVest, ItemLocation.Inventory, Player, 0, default, default)] });
		recording = new(simulation); view.SetSimulation(simulation); RefreshItems();
		if (itemSlots[2].Icon is not null || !itemSlots[2].Text.Contains("Training vest") || itemDetails.Text.Contains("Original reference")) throw new InvalidDataException("Missing icon fallback failed.");
		NewRun(); GD.Print("OPEND2_PLAY12_ITEMS_READY"); GD.Print("OPEND2_PLAY13_DEFINITIONS_READY");
	}
}
