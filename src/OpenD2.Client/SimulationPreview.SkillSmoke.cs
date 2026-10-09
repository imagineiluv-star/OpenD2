using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private void SkillSmoke()
	{
		NewRun(); var state = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == new EntityId(2) ? e with { Region = current.Region, Position = new(current.Position.X + 256, current.Position.Y), Health = 100, MaxHealth = 100, AttackCooldown = 20 } : e).ToArray() });
		recording = new(simulation); view.SetSimulation(simulation);
		castSkill.EmitSignal(BaseButton.SignalName.Pressed); RunTick(); ShowFrame();
		if (current.Mana != 48 || manaBar.Value != 48 || !playerStatus.Text.Contains("MP 48/60") || !skillStatus.Text.StartsWith("Ready in"))
			throw new InvalidDataException("Skill button, mana bar or cooldown binding failed.");
		castSkill.EmitSignal(BaseButton.SignalName.Pressed); RunTick(); ShowFrame();
		if (current.Mana != 48 || !status.Text.Contains("Cooldown")) throw new InvalidDataException("Repeated UI cast bypassed cooldown.");
		skillChoice.EmitSignal(OptionButton.SignalName.ItemSelected, 0L); RunTick(); ShowFrame();
		if (current.SelectedSkill != SkillId.None || skillChoice.Selected != 0 || skillStatus.Text != "Select a skill") throw new InvalidDataException("Skill selection command was not reflected.");
		skillChoice.EmitSignal(OptionButton.SignalName.ItemSelected, 1L); RunTick();
		state = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(state with { Entities = state.Entities.Select(e => e.Id == Player ? e with { Mana = 0, SkillCooldown = 0, AttackCooldown = 0 } : e).ToArray() });
		recording = new(simulation); view.SetSimulation(simulation); previous = current = simulation.GetEntity(Player); ShowFrame();
		castSkill.EmitSignal(BaseButton.SignalName.Pressed); RunTick(); ShowFrame();
		if (current.Mana != 0 || !skillStatus.Text.Contains("Not enough mana") || !status.Text.Contains("InsufficientMana")) throw new InvalidDataException("Insufficient mana UI failed.");
		foreach (string gate in new[] { "pause", "menu", "verification" })
		{
			paused = gate == "pause"; menuOpen = gate == "menu"; verifying = gate == "verification";
			string before = simulation.ComputeStateHash(); castSkill.EmitSignal(BaseButton.SignalName.Pressed); skillChoice.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
			if (simulation.ComputeStateHash() != before) throw new InvalidDataException("Blocked skill input changed state.");
		}
		paused = menuOpen = verifying = false;
		// A synthetic 32x32 DC6 frame checks mana clipping separately from health.
		var art = LegacyHudArt.Load(new("data/global/palette/units/pal.dat", 32, 32, [new("Mana", "mana.dc6", 0, 0, 0)]),
			path => path.EndsWith(".dc6") ? AssetPreview.SampleDc6() : new byte[768]);
		legacyHud.SetArtwork(new(art)); legacyHud.SetMana(30, 60); legacyHud.SetHealth(100, 100);
		if (!art.HasMana || legacyHud.ManaRows != 16 || legacyHud.ActivateAt(new(1, 1))) throw new InvalidDataException("Mana artwork role/clip failed.");
		legacyHud.SetMana(0, 0); if (legacyHud.ManaRows != 0) throw new InvalidDataException("Resource-less mana clip failed.");
		legacyHud.SetArtwork(null); NewRun(); GD.Print("OPEND2_PLAY15_SKILL_READY");
	}
}
