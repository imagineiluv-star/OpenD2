using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly InventoryButton[] beltSlots = Enumerable.Range(0, GameSimulation.BeltCapacity).Select(_ => new InventoryButton
	{ ToggleMode = true, ExpandIcon = true, TextureFilter = TextureFilterEnum.Nearest, CustomMinimumSize = new Vector2(100, 40) }).ToArray();
	private readonly Button[] beltUse = Enumerable.Range(0, GameSimulation.BeltCapacity).Select(i => new Button { Text = $"Use ({i + 1})" }).ToArray();
	private readonly Button usePotion = new() { Text = "Drink selected potion" }, assignBelt = new() { Text = "Put selected in belt" };
	private readonly OptionButton beltDestination = new();
	private readonly Label beltStatus = new();
	private int beltKeysDown;
	private bool CanUsePotions => CanEditInventory && !paused;
	private void BuildBelt()
	{
		playPanel.AddChild(new Label { Text = "Belt · 1–4 drink once per key press. Click a slot to select; drag potions between bag and belt." });
		var row = new HFlowContainer(); playPanel.AddChild(row);
		for (int i = 0; i < beltSlots.Length; i++)
		{
			int slot = i; var column = new VBoxContainer(); row.AddChild(column); column.AddChild(beltSlots[i]); column.AddChild(beltUse[i]);
			beltDestination.AddItem($"Belt {i + 1}", i);
			beltSlots[i].Pressed += () => { if (CanEditInventory) { selectedItem = simulation.BeltAt(Player, slot); RefreshItems(); } };
			beltUse[i].Pressed += () => DrinkBelt(slot);
			beltSlots[i].BeginDrag = () =>
			{
				var id = simulation.BeltAt(Player, slot); if (!CanEditInventory || id == default) return default;
				selectedItem = id; movingItem.SetPressedNoSignal(false); RefreshItems();
				beltSlots[slot].SetDragPreview(new Label { Text = ItemCatalog.Get(simulation.GetItem(id).Definition).Name });
				return new Godot.Collections.Dictionary { ["session"] = inventoryGeneration, ["item"] = (long)id.Value };
			};
			beltSlots[i].CanDrop = (_, data) => CanEditInventory && TryInventoryDrag(data, out var id) && simulation.CanBeltItem(Player, id, slot);
			beltSlots[i].Drop = (_, data) => { if (TryInventoryDrag(data, out var id) && simulation.CanBeltItem(Player, id, slot)) Submit(CommandKind.BeltItem, x: slot, item: id); };
		}
		var actions = new HFlowContainer(); playPanel.AddChild(actions); actions.AddChild(beltDestination); actions.AddChild(assignBelt); actions.AddChild(beltStatus);
		assignBelt.Pressed += () => { if (CanEditInventory && selectedItem != default) Submit(CommandKind.BeltItem, x: beltDestination.Selected, item: selectedItem); };
		assignBelt.TooltipText = "Select a potion in the bag or belt, then choose its belt slot. An occupied slot swaps back into the original position. Unequip returns a belt potion to the bag.";
	}
	private void DrinkBelt(int slot)
	{
		if (!CanUsePotions) return;
		Submit(CommandKind.UseBelt, x: slot); view.GrabFocus();
	}
	private void PollBeltKeys(bool active)
	{
		int down = 0;
		if (active) for (int i = 0; i < GameSimulation.BeltCapacity; i++) if (Input.IsKeyPressed((Key)((long)Key.Key1 + i))) down |= 1 << i;
		HandleBeltKeys(down);
	}
	private void HandleBeltKeys(int down)
	{
		for (int i = 0; i < GameSimulation.BeltCapacity; i++) if ((down & ~beltKeysDown & (1 << i)) != 0) DrinkBelt(i);
		beltKeysDown = down;
	}
	private void RefreshBelt(ItemState? selected)
	{
		for (int i = 0; i < beltSlots.Length; i++)
		{
			var id = simulation.BeltAt(Player, i); var item = id == default ? (ItemState?)null : simulation.GetItem(id);
			beltSlots[i].Text = $"{i + 1}: " + (item is { } p ? ItemCatalog.Get(p.Definition).Name : "Empty");
			beltSlots[i].Icon = item is { } icon ? itemTextures?.Get(icon.Definition) : null;
			beltSlots[i].SetPressedNoSignal(id != default && id == selectedItem); beltSlots[i].Disabled = !CanEditInventory;
		}
		assignBelt.Disabled = !CanEditInventory || selected is null || !ItemCatalog.IsConsumable(selected.Value.Definition);
		beltDestination.Disabled = !CanEditInventory; RefreshPotionControls();
	}
	private void RefreshPotionControls()
	{
		bool potionSelected = false;
		foreach (var item in simulation.Items) if (item.Id == selectedItem && item.Owner == Player && item.Location is ItemLocation.Inventory or ItemLocation.Belt) potionSelected = ItemCatalog.IsConsumable(item.Definition);
		usePotion.Disabled = !CanUsePotions || !potionSelected;
		foreach (var button in beltUse) button.Disabled = !CanUsePotions;
		beltStatus.Text = "HP +40 / MP +30 · full resource keeps potion";
	}
}
