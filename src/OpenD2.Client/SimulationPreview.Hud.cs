using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly ProgressBar healthBar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(160, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
	private readonly Label playerStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
	private readonly HFlowContainer diagnosticControls = new();
	private (int Health, int Maximum, bool Paused, RegionId Region)? lastHud;
	private void BuildHud()
	{
		var row = new HBoxContainer(); AddChild(row); row.AddChild(playerStatus); row.AddChild(healthBar);
		healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.65f, 0.12f, 0.17f) });
		healthBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.15f, 0.06f, 0.07f) });
	}
	public void SetDiagnosticsVisible(bool visible)
	{
		diagnosticControls.Visible = visible; details.Visible = visible;
	}
	private void RefreshHud()
	{
		if (simulation is null) return;
		var next = (current.Health, current.MaxHealth, paused, current.Region);
		if (lastHud == next) return;
		lastHud = next; healthBar.MaxValue = current.MaxHealth; healthBar.Value = current.Health;
		playerStatus.Text = $"{simulation.World!.GetRegion(current.Region).Name} · {(current.IsAlive ? paused ? "Paused" : "Playing" : "Defeated")} · HP {current.Health}/{current.MaxHealth}";
	}
	private void HudSmoke()
	{
		SetDiagnosticsVisible(true);
		if (!diagnosticControls.Visible || !details.Visible) throw new InvalidDataException("Diagnostics toggle smoke failed.");
		SetPaused(true); ShowFrame();
		if (!playerStatus.Text.Contains("Paused") || healthBar.Value != current.Health || healthBar.MaxValue != current.MaxHealth)
			throw new InvalidDataException("HUD state binding smoke failed.");
		SetDiagnosticsVisible(false); SetPaused(false); ShowFrame();
		if (details.Visible || diagnosticControls.Visible || !playerStatus.Text.Contains("Playing"))
			throw new InvalidDataException("HUD play state smoke failed.");
		GD.Print("OPEND2_M206_HUD_READY");
	}
}
