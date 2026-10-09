using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private void GroundItemSmoke()
	{
		var originalSize = view.Size; view.Size = new(460, 300);
		ItemId sword = new(2), potion = new((1UL << 32) | 2);
		GamePosition at = new(1408, 640);
		void Setup(bool full = false, bool unreachable = false)
		{
			NewRun();
			var cells = Arena().Cells.ToArray();
			if (unreachable) for (int y = 0; y < 10; y++) cells[y * 16 + 4] = CollisionCell.Blocked;
			var grid = new CollisionGrid(Region, 16, 10, cells);
			var layout = full ? new InventoryLayout([new(ItemDefinition.TrainingSword, "", 10, 4), new(ItemDefinition.TrainingVest, "", 2, 3)]) : InventoryLayout.Default;
			var world = new WorldDefinition([new("Loot test", grid)], [], new(new(10), Region, new(640, 384), "Guide"), [new(2)], "Loot test");
			var sample = new GameSimulation(1, [new(Player, Region, new(384, 640)), new(new(2), Region, at, Kind: EntityKind.Monster)], world: world, inventory: layout);
			var snapshot = sample.CaptureSnapshot();
			simulation = GameSimulation.Restore(snapshot with { Entities = snapshot.Entities.Select(e => e.Id.Value == 2 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(), Items = [
				full ? new(sword, ItemDefinition.TrainingSword, ItemLocation.Inventory, Player, 0, default, default) : new(sword, ItemDefinition.TrainingSword, ItemLocation.Ground, default, -1, Region, at),
				new(potion, ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, Region, at)] });
			recording = new(simulation); view.SetSimulation(simulation); previous = current = simulation.GetEntity(Player); ShowFrame();
		}
		void Advance(int count = 80)
		{
			for (int i = 0; i < count; i++)
			{
				var move = FollowRoute(true, 0, 0);
				if (move.X != requestedX || move.Y != requestedY)
				{ Submit(CommandKind.SetMove, move.X, move.Y); requestedX = move.X; requestedY = move.Y; }
				RunTick(); ShowFrame();
			}
		}
		Setup(); view.CheckGroundLabels(sword, potion);
		view.ClickGroundLabelForSmoke(potion); Advance();
		if (simulation.GetItem(potion).Location != ItemLocation.Inventory || simulation.GetItem(sword).Location != ItemLocation.Ground || pickupTarget is not null || current.MoveX != 0 || current.MoveY != 0)
			throw new InvalidDataException("Selected loot approach picked wrong item or failed to stop.");
		if (GameSimulation.Replay(recording.Baseline, recording.Commands, simulation.Tick).ComputeStateHash() != simulation.ComputeStateHash()) throw new InvalidDataException("Selected loot replay diverged.");
		view.CheckRemovedGroundLabel(potion);
		PickupNearest(); RunTick();
		if (simulation.GetItem(sword).Location != ItemLocation.Inventory) throw new InvalidDataException("Nearest pickup fallback failed.");
		foreach (string cancel in new[] { "right", "keys", "focus", "hide", "pause", "menu", "move", "session" })
		{
			Setup(); ClickPickup(potion);
			if (pickupTarget is null) throw new InvalidDataException("Loot target was not armed.");
			switch (cancel)
			{
				case "right": view._GuiInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right }); break;
				case "keys": FollowRoute(true, 1, 0); break;
				case "focus": FollowRoute(false, 0, 0); break;
				case "hide": view._GuiInput(new InputEventKey { Pressed = true, Keycode = Key.L }); groundNames.ButtonPressed = true; break;
				case "pause": pause.EmitSignal(BaseButton.SignalName.Pressed); SetPaused(false); break;
				case "menu": OpenMenu(); ContinueMenuSession(); break;
				case "move": ClickMove(new(640, 640)); break;
				case "session": NewRun(); break;
			}
			if (pickupTarget is not null || view.SelectedGroundItem != default) throw new InvalidDataException("Loot cancellation failed: " + cancel);
		}
		Setup(); ClickPickup(potion);
		// Simulate another accepted pickup before the next UI frame. No stale click may pick the sibling.
		var stale = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(stale with { Items = stale.Items.Select(i => i.Id == potion ? i with { Location = ItemLocation.Inventory, Owner = Player, Slot = 0, Region = default, Position = default } : i).ToArray() });
		recording = new(simulation); view.SetSimulation(simulation); FollowRoute(true, 0, 0);
		if (pickupTarget is not null || route.Count != 0) throw new InvalidDataException("Unavailable loot left a live route.");
		Setup(unreachable: true); ClickPickup(potion); Advance();
		if (pickupTarget is not null || simulation.GetItem(potion).Location != ItemLocation.Ground || !status.Text.Contains("Unreachable")) throw new InvalidDataException("Unreachable loot was not rejected.");
		Setup(full: true); ClickPickup(potion); Advance();
		if (simulation.GetItem(potion).Location != ItemLocation.Ground || pickupTarget is not null || !status.Text.Contains("InventoryFull")) throw new InvalidDataException("Full bag pickup did not stop with feedback.");
		if (recording.Commands.Count(c => c.Command.Kind == CommandKind.Pickup) != 1) throw new InvalidDataException("Full bag pickup retried automatically.");
		Setup(); ClickPickup(potion);
		for (int i = 0; i < 25; i++) { previous = current; ObserveRouteTick(); }
		if (pickupTarget is not null || !status.Text.Contains("blocked")) throw new InvalidDataException("Blocked loot approach did not expire.");
		// A portal transition cancels a target in the former region through the real tick path.
		NewRun(); var worldState = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(worldState with { Entities = worldState.Entities.Select(e => e.Id == Player ? e with { Position = new(1408, 384) } : e.Id.Value == 2 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(), Items = [new(potion, ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, Region, new(640, 640))] });
		recording = new(simulation); previous = current = simulation.GetEntity(Player); view.SetSimulation(simulation);
		ClickPickup(potion); Submit(CommandKind.Interact, target: new(11)); RunTick();
		if (current.Region != Dungeon || pickupTarget is not null || route.Count != 0) throw new InvalidDataException("Portal retained a loot approach.");
		view.Size = originalSize; NewRun(); GD.Print("OPEND2_PLAY17_GROUND_READY");
	}
}

public partial class SimulationCanvas
{
	internal void ClickGroundLabelForSmoke(ItemId id)
	{
		LayoutGroundLabels(); var label = groundLabels.Single(l => l.Id == id);
		_GuiInput(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left, Position = label.Bounds.GetCenter() });
	}
	internal void CheckRemovedGroundLabel(ItemId id)
	{
		LayoutGroundLabels(); if (groundLabels.Any(l => l.Id == id)) throw new InvalidDataException("Picked-up label still visible.");
	}
	internal void CheckGroundLabels(ItemId first, ItemId second)
	{
		var size = Size; Size = new(460, 300); LayoutGroundLabels();
		if (groundLabels.Count != 2 || groundLabels[0].Bounds.Intersects(groundLabels[1].Bounds) || HitGroundLabel(groundLabels.Single(l => l.Id == second).Bounds.GetCenter()) != second)
			throw new InvalidDataException("Overlapping loot labels or wrong hit priority.");
		ShowGroundNames = false;
		if (HitGroundLabel(new(230, 150)) != default || groundLabels.Count != 0) throw new InvalidDataException("Hidden loot remained clickable.");
		ShowGroundNames = true; var minimum = CustomMinimumSize; CustomMinimumSize = Vector2.Zero; Size = new(30, 20); LayoutGroundLabels();
		if (groundLabels.Count != 0) throw new InvalidDataException("Clipped labels retained hit boxes.");
		CustomMinimumSize = minimum; Size = new(460, 300); var display = DisplayPosition; DisplayPosition = new(-100000, -100000); LayoutGroundLabels();
		if (groundLabels.Count != 0) throw new InvalidDataException("Offscreen loot remained visible.");
		DisplayPosition = display; Size = size; LayoutGroundLabels();
		if (!groundLabels.Any(l => l.Id == first)) throw new InvalidDataException("Visible label was not restored.");
	}
}
