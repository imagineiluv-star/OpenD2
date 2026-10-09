using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly InventoryButton[] itemSlots = Enumerable.Range(0, InventoryLayout.Cells + 2).Select(_ => new InventoryButton
	{ ToggleMode = true, ClipText = true, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest, FocusMode = FocusModeEnum.All }).ToArray();
	private readonly Control bagGrid = new() { CustomMinimumSize = new Vector2(400, 160), SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly Button movingItem = new() { Text = "Move selected, then choose a cell", ToggleMode = true };
	private long inventoryGeneration;
	private float BagCellSize => Math.Max(40, bagGrid.Size.X / InventoryLayout.Width);
	private bool CanEditInventory => !verifying && !menuOpen && pendingRestart is null && current.IsAlive;
	private LegacyItemTextures? itemTextures;
	private readonly ItemId[] slotItems = new ItemId[InventoryLayout.Cells + 2];
	private readonly Label itemDetails = new() { Text = "Select an item to inspect it.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private ItemId selectedItem;
	private readonly VBoxContainer inventoryPanel = new();
	private readonly Button inventoryButton = new() { Text = "Inventory" };
	private void SetItemTextures(LegacyItemTextures? next)
	{
		foreach (var button in itemSlots.Concat(beltSlots)) button.Icon = null;
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
		inventoryPanel.AddChild(new Label { Text = "Inventory · 10×4 cells. Drag an item, or select → Move selected → choose a cell with Tab/Enter. Escape cancels.\nDrops use the target cell as the item's upper-left corner. Rejected moves keep both items.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		var equipment = new HBoxContainer(); inventoryPanel.AddChild(equipment); inventoryPanel.AddChild(bagGrid);
		bagGrid.Resized += PositionInventory;
		for (int i = 0; i < itemSlots.Length; i++)
		{
			int slot = i; var button = itemSlots[i]; button.AddThemeConstantOverride("icon_max_width", 40);
			if (i < 2) { button.CustomMinimumSize = new(180, 48); button.SizeFlagsHorizontal = SizeFlags.ExpandFill; equipment.AddChild(button); }
			else { button.AddThemeFontSizeOverride("font_size", 12); bagGrid.AddChild(button); }
			button.Pressed += () =>
			{
				if (!CanEditInventory) return;
				if (movingItem.ButtonPressed && slot >= 2 && selectedItem != default) { PlaceInventory(selectedItem, slot - 2); movingItem.SetPressedNoSignal(false); }
				else selectedItem = slotItems[slot];
				RefreshItems();
			};
			button.CancelMove = () => movingItem.SetPressedNoSignal(false);
			button.BeginDrag = () =>
			{
				if (!CanEditInventory || slotItems[slot] == default) return default;
				selectedItem = slotItems[slot]; movingItem.SetPressedNoSignal(false); RefreshItems();
				button.SetDragPreview(new Label { Text = ItemCatalog.Get(simulation.GetItem(selectedItem).Definition).Name });
				return new Godot.Collections.Dictionary { ["session"] = inventoryGeneration, ["item"] = (long)selectedItem.Value };
			};
			button.CanDrop = (point, data) => CanDropInventory(slot, point, data);
			button.Drop = (point, data) =>
			{
				if (!CanDropInventory(slot, point, data)) return;
				TryInventoryDrag(data, out var item); selectedItem = item;
				if (slot < 2) UseSelected(CommandKind.Equip); else PlaceInventory(item, DropCell(slot, point));
			};
		}
		inventoryPanel.AddChild(itemDetails); inventoryPanel.AddChild(gearInfo);
		var actions = new HFlowContainer(); inventoryPanel.AddChild(actions);
		foreach (var button in new[] { pickup, equip, unequip, drop, usePotion, movingItem }) actions.AddChild(button);
		usePotion.Pressed += () => { if (CanUsePotions) UseSelected(CommandKind.UseItem); };
		pickup.Pressed += PickupNearest; equip.Pressed += () => UseSelected(CommandKind.Equip);
		unequip.Pressed += () => UseSelected(CommandKind.Unequip); drop.Pressed += () => UseSelected(CommandKind.DropItem);
	}
	private int DropCell(int button, Vector2 point) => button - 2 + (int)(point.X / BagCellSize) + (int)(point.Y / BagCellSize) * InventoryLayout.Width;
	private bool TryInventoryDrag(Variant data, out ItemId item)
	{
		item = default; if (!CanEditInventory || data.VariantType != Variant.Type.Dictionary) return false;
		var fields = data.AsGodotDictionary();
		if (!fields.TryGetValue("session", out var session) || session.VariantType != Variant.Type.Int || session.AsInt64() != inventoryGeneration ||
			!fields.TryGetValue("item", out var id) || id.VariantType != Variant.Type.Int || id.AsInt64() <= 0) return false;
		item = new((ulong)id.AsInt64());
		foreach (var entry in simulation.Items) if (entry.Id == item) return entry.Owner == Player && entry.Location is ItemLocation.Inventory or ItemLocation.Equipped or ItemLocation.Belt;
		return false;
	}
	private bool CanDropInventory(int slot, Vector2 point, Variant data)
	{
		if (!TryInventoryDrag(data, out var id)) return false;
		var item = simulation.GetItem(id);
		return slot < 2 ? item.Location == ItemLocation.Inventory && (int)ItemCatalog.Get(item.Definition).Slot == slot : simulation.CanMoveItem(Player, id, DropCell(slot, point));
	}
	private void PlaceInventory(ItemId id, int slot)
	{
		if (!CanEditInventory || slot is < 0 or >= InventoryLayout.Cells) return;
		Submit(CommandKind.MoveItem, slot % InventoryLayout.Width, slot / InventoryLayout.Width, item: id);
	}
	private void PositionInventory()
	{
		float cell = BagCellSize; bagGrid.CustomMinimumSize = new(400, cell * InventoryLayout.Height);
		for (int i = 2; i < itemSlots.Length; i++)
		{
			int slot = i - 2; var button = itemSlots[i]; int width = 1, height = 1;
			if (simulation is not null && slotItems[i] != default)
			{
				var item = simulation.GetItem(slotItems[i]); var size = simulation.Inventory.Get(item.Definition);
				if (item.Slot == slot) { width = size.Width; height = size.Height; }
			}
			button.Position = new(slot % InventoryLayout.Width * cell, slot / InventoryLayout.Width * cell);
			button.Size = new(width * cell - 2, height * cell - 2);
		}
	}
	private void UseSelected(CommandKind kind)
	{
		if (selectedItem != default && CanEditInventory) Submit(kind, item: selectedItem);
	}
	private void RefreshItems()
	{
		Array.Clear(slotItems); int bagCount = 0, usedCells = 0; ItemState? selected = null;
		foreach (var item in simulation.Items)
		{
			if (item.Owner != Player) continue;
			if (item.Id == selectedItem) selected = item;
			if (item.Location == ItemLocation.Belt) continue;
			int slot = item.Location == ItemLocation.Inventory ? item.Slot + 2 : item.Slot;
			slotItems[slot] = item.Id;
			if (item.Location == ItemLocation.Inventory)
			{
				bagCount++; var size = simulation.Inventory.Get(item.Definition); usedCells += size.Width * size.Height;
				for (int y = 0; y < size.Height; y++) for (int x = 0; x < size.Width; x++) slotItems[2 + item.Slot + y * InventoryLayout.Width + x] = item.Id;
			}
		}
		if (selected is null) selectedItem = default;
		for (int i = 0; i < itemSlots.Length; i++)
		{
			string label = i < 2 ? ((EquipmentSlot)i).ToString() : $"{(i - 2) % InventoryLayout.Width + 1},{(i - 2) / InventoryLayout.Width + 1}";
			string name = slotItems[i] == default ? "Empty" : ItemCatalog.Get(simulation.GetItem(slotItems[i]).Definition).Name;
			itemSlots[i].Icon = slotItems[i] == default ? null : itemTextures?.Get(simulation.GetItem(slotItems[i]).Definition);
			itemSlots[i].Text = label + (slotItems[i] == default ? "" : "\n" + name); itemSlots[i].TooltipText = label + " · " + name;
			itemSlots[i].Visible = i < 2 || slotItems[i] == default || simulation.GetItem(slotItems[i]).Slot == i - 2;
			itemSlots[i].ZIndex = slotItems[i] == default ? 0 : 1;
			itemSlots[i].Disabled = !CanEditInventory; itemSlots[i].SetPressedNoSignal(selectedItem != default && slotItems[i] == selectedItem);
		}
		equip.Disabled = !CanEditInventory || selected is not { Location: ItemLocation.Inventory } || ItemCatalog.IsConsumable(selected.Value.Definition);
		unequip.Disabled = !CanEditInventory || selected is not { Location: ItemLocation.Equipped or ItemLocation.Belt } || simulation.FindInventorySpace(Player, selected.Value.Definition) < 0;
		drop.Disabled = !CanEditInventory || selected is not { Location: ItemLocation.Inventory or ItemLocation.Belt };
		movingItem.Disabled = !CanEditInventory || selected is null;
		if (selected is null) movingItem.SetPressedNoSignal(false);
		PositionInventory(); RefreshBelt(selected);
		if (selected is { } chosen)
		{
			var spec = ItemCatalog.Get(chosen.Definition); var footprint = simulation.Inventory.Get(chosen.Definition);
			itemDetails.Text = $"{spec.Name} · {chosen.Location}\n{(ItemCatalog.IsConsumable(chosen.Definition) ? $"Restores HP {spec.HealthRecovery} / MP {spec.ManaRecovery} instantly · one use" : $"{spec.Slot} | damage bonus +{spec.DamageBonus} | armor {spec.Armor}")}\nBag footprint {footprint.Width}×{footprint.Height} · code {(footprint.Code.Length == 0 ? "preview" : footprint.Code)}";
			if (legacyScene?.ItemDefinitions?.Bindings.TryGetValue(chosen.Definition, out var original) == true)
				itemDetails.Text += "\n\n" + ItemDefinitionText.Describe(original);
		}
		else itemDetails.Text = "Select an item to inspect it. Pick up nearby loot with F.";
		var stats = simulation.GetStats(Player);
		gearInfo.Text = $"Bag {bagCount} items · {usedCells}/{InventoryLayout.Cells} cells | Damage {stats.MinimumDamage}–{stats.MaximumDamage} | Armor {stats.Armor}";
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
