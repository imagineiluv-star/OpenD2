using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private void PotionSmoke()
	{
		NewRun(); var state = simulation.CaptureSnapshot(); ItemId health = new ItemId((1UL << 32) | 2), mana = new ItemId((1UL << 32) | 3);
		simulation = GameSimulation.Restore(state with
		{
			Entities = state.Entities.Select(e => e.Id == Player ? e with { Health = 80, Mana = 20 } : e.Id.Value is 2 or 3 ? e with { Health = 0, Mode = MonsterMode.Dead } : e).ToArray(),
			Items = [new(health, ItemDefinition.HealthPotion, ItemLocation.Inventory, Player, 0, default, default), new(mana, ItemDefinition.ManaPotion, ItemLocation.Inventory, Player, 1, default, default)]
		});
		recording = new(simulation); view.SetSimulation(simulation); previous = current = simulation.GetEntity(Player);
		var art = LegacyItemArt.Load(new("data/global/palette/units/pal.dat", [new("HealthPotion", "potion.dc6", 0), new("ManaPotion", "potion.dc6", 0)]), p => p.EndsWith(".dc6") ? AssetPreview.SampleDc6() : new byte[768]);
		SetItemTextures(new(art)); RefreshItems();
		Variant Payload(ItemId id) => new Godot.Collections.Dictionary { ["session"] = inventoryGeneration, ["item"] = (long)id.Value };
		var hp = Payload(health); var mp = Payload(mana);
		if (!beltSlots[0]._CanDropData(Vector2.Zero, hp)) throw new InvalidDataException("Full-width potion drag rejected.");
		beltSlots[0]._DropData(Vector2.Zero, hp); RunTick(); RefreshItems();
		beltSlots[0]._DropData(Vector2.Zero, mp); RunTick(); RefreshItems();
		if (simulation.BeltAt(Player, 0) != mana || simulation.GetItem(health).Slot != 1 || beltSlots[0].Icon is null) throw new InvalidDataException("Belt swap/icon binding failed.");
		itemSlots[4]._DropData(Vector2.Zero, mp); RunTick(); RefreshItems();
		if (simulation.GetItem(mana).Slot != 2 || simulation.BeltAt(Player, 0) != default) throw new InvalidDataException("Belt-to-bag drag failed.");
		selectedItem = mana; beltDestination.Select(1); assignBelt.EmitSignal(BaseButton.SignalName.Pressed); RunTick(); RefreshItems();
		if (simulation.BeltAt(Player, 1) != mana) throw new InvalidDataException("Keyboard belt assignment failed.");
		HandleBeltKeys(2); HandleBeltKeys(2); RunTick(); ShowFrame();
		if (current.Mana != 50 || simulation.BeltAt(Player, 1) != default || !status.Text.Contains("restored 30")) throw new InvalidDataException("Belt key edge/use feedback failed.");
		HandleBeltKeys(0); HandleBeltKeys(2); RunTick(); if (!status.Text.Contains("EmptyBelt")) throw new InvalidDataException("Empty belt feedback failed.");
		selectedItem = health; RefreshItems(); usePotion.EmitSignal(BaseButton.SignalName.Pressed); RunTick(); ShowFrame();
		if (current.Health != 100 || simulation.GetItem(health).Location != ItemLocation.Consumed || selectedItem != default || itemSlots.Any(b => b.Icon is not null)) throw new InvalidDataException("Bag potion consumption/UI cleanup failed.");
		foreach (string gate in new[] { "menu", "pause", "verification" })
		{
			menuOpen = gate == "menu"; paused = gate == "pause"; verifying = gate == "verification"; string before = simulation.ComputeStateHash();
			beltUse[0].EmitSignal(BaseButton.SignalName.Pressed); usePotion.EmitSignal(BaseButton.SignalName.Pressed);
			if (simulation.ComputeStateHash() != before) throw new InvalidDataException("Blocked potion input changed state.");
		}
		menuOpen = paused = verifying = false; SetItemTextures(null); NewRun();
		if (TryInventoryDrag(hp, out _)) throw new InvalidDataException("Old potion drag crossed session boundary.");
		GD.Print("OPEND2_PLAY16_POTIONS_READY");
	}
}
