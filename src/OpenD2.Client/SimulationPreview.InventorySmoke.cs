using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private void GridInventorySmoke()
	{
		NewRun(); var state = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(state with
		{
			Entities = state.Entities.Select(e => e.Id.Value is 2 or 3 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(),
			Items = [new(new(2), ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 0, default, default), new(new(3), ItemDefinition.TrainingVest, ItemLocation.Inventory, Player, 3, default, default)]
		});
		recording = new(simulation); view.SetSimulation(simulation); selectedItem = new(2); RefreshItems();
		if (slotItems[22] != new ItemId(2) || itemSlots[12].Visible || itemSlots[2].Size.Y < BagCellSize * 3 - 2.1f || !gearInfo.Text.Contains("9/40 cells"))
			throw new InvalidDataException("Grid footprint rendering/occupancy binding failed.");
		Variant payload = new Godot.Collections.Dictionary { ["session"] = inventoryGeneration, ["item"] = 2L };
		if (!itemSlots[7]._CanDropData(Vector2.Zero, payload)) throw new InvalidDataException("Valid grid drag rejected.");
		itemSlots[7]._DropData(Vector2.Zero, payload); RunTick(); RefreshItems();
		if (simulation.GetItem(new(2)).Slot != 5 || slotItems[7] != new ItemId(2)) throw new InvalidDataException("Grid drop did not submit a placement.");
		itemSlots[5]._DropData(Vector2.Zero, payload); RunTick(); RefreshItems();
		if (simulation.GetItem(new(2)).Slot != 3 || simulation.GetItem(new(3)).Slot != 5) throw new InvalidDataException("Grid drag swap failed.");
		Variant vest = new Godot.Collections.Dictionary { ["session"] = inventoryGeneration, ["item"] = 3L };
		string before = simulation.ComputeStateHash();
		if (itemSlots[11]._CanDropData(Vector2.Zero, vest)) throw new InvalidDataException("Out-of-bounds drag was allowed.");
		itemSlots[11]._DropData(Vector2.Zero, vest);
		if (simulation.ComputeStateHash() != before) throw new InvalidDataException("Rejected drag changed the session.");
		selectedItem = new(2); movingItem.SetPressedNoSignal(true); itemSlots[2].EmitSignal(BaseButton.SignalName.Pressed); RunTick(); RefreshItems();
		if (simulation.GetItem(new(2)).Slot != 0 || movingItem.ButtonPressed) throw new InvalidDataException("Keyboard cell activation did not move the selected item.");
		movingItem.SetPressedNoSignal(true); itemSlots[2]._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.Escape });
		if (movingItem.ButtonPressed) throw new InvalidDataException("Escape did not cancel placement.");
		itemSlots[0]._DropData(Vector2.Zero, payload); RunTick(); RefreshItems();
		if (simulation.GetItem(new(2)).Location != ItemLocation.Equipped) throw new InvalidDataException("Drag to equipment failed.");
		itemSlots[12]._DropData(Vector2.Zero, payload); RunTick(); RefreshItems();
		if (simulation.GetItem(new(2)).Location != ItemLocation.Inventory || simulation.GetItem(new(2)).Slot != 10) throw new InvalidDataException("Drag from equipment failed.");
		menuOpen = true;
		if (itemSlots[2]._CanDropData(Vector2.Zero, payload)) throw new InvalidDataException("Menu did not block grid dragging.");
		menuOpen = false; NewRun();
		if (TryInventoryDrag(payload, out _)) throw new InvalidDataException("A previous session's drag payload was accepted.");
		GD.Print("OPEND2_PLAY14_GRID_READY");
	}
}
