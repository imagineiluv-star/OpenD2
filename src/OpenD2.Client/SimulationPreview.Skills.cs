using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly OptionButton skillChoice = new();
	private readonly Button castSkill = new() { Text = "Cast nearest (Q)" };
	private readonly Label skillStatus = new();
	private bool skillDown;
	private bool SkillInputAllowed => simulation is not null && current.IsAlive && !paused && !verifying && !menuOpen && pendingRestart is null;
	private void BuildSkills()
	{
		var row = new HFlowContainer(); playPanel.AddChild(row);
		skillChoice.AddItem("No skill", (int)SkillId.None); skillChoice.AddItem("Power strike · 12 MP", (int)SkillId.PowerStrike);
		row.AddChild(skillChoice); row.AddChild(castSkill); row.AddChild(skillStatus);
		skillChoice.ItemSelected += index =>
		{
			if (SkillInputAllowed) Submit(CommandKind.SelectSkill, x: skillChoice.GetItemId((int)index));
			view.GrabFocus();
		};
		castSkill.Pressed += CastSelected;
		castSkill.TooltipText = "Power strike: melee range, weapon damage +12, 12 mana, 1 second cooldown. Mana recovers 1 per second. Cast once per Q press.";
	}
	private void CastSelected()
	{
		if (!SkillInputAllowed) return;
		StopInput(); AttackNearest(true); view.GrabFocus();
	}
	private void RefreshSkillControls()
	{
		skillChoice.Select((int)current.SelectedSkill); skillChoice.Disabled = !SkillInputAllowed;
		castSkill.Disabled = !SkillInputAllowed;
		skillStatus.Text = current.SelectedSkill == SkillId.None ? "Select a skill" : current.HitStun > 0 ? "Interrupted" :
			Math.Max(current.AttackCooldown, current.SkillCooldown) is var wait && wait > 0 ? $"Ready in {wait / 25.0:F1}s" :
			current.Mana < GameSimulation.PowerStrikeManaCost ? "Not enough mana" : "Power strike ready";
	}
}
