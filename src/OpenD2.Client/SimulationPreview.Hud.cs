using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private readonly ProgressBar healthBar = new() { ShowPercentage = false, CustomMinimumSize = new Vector2(160, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
	private readonly Label playerStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(240, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
	private readonly HFlowContainer diagnosticControls = new();
	private readonly LegacyHudView legacyHud = new();
	private (int Health, int Maximum, bool Paused, RegionId Region)? lastHud;
	private void BuildHud()
	{
		var row = new HBoxContainer(); playPanel.AddChild(row); row.AddChild(playerStatus); row.AddChild(healthBar);
		healthBar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.65f, 0.12f, 0.17f) });
		healthBar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.15f, 0.06f, 0.07f) });
	}
	private void BuildLegacyHud()
	{
		playPanel.AddChild(legacyHud);
		legacyHud.ActionRequested += role =>
		{
			if (verifying || menuOpen || pendingRestart is not null) return;
			if (role == HudRole.Menu) OpenMenu();
			else if (role == HudRole.Inventory) ToggleInventory();
		};
	}
	private void SetSceneArt(LegacyPlayScene? scene)
	{
		var prepared = scene?.HudArtwork is { } art ? new LegacyHudView.Prepared(art) : null;
		LegacyItemTextures? items = null;
		try { items = scene?.ItemArtwork is { } icons ? new(icons) : null; view.SetTerrain(scene); }
		catch { prepared?.Dispose(); items?.Dispose(); throw; }
		SetItemTextures(items);
		legacyHud.SetArtwork(prepared); healthBar.Visible = scene?.HudArtwork?.HasHealth != true;
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
		lastHud = next; healthBar.MaxValue = current.MaxHealth; healthBar.Value = current.Health; legacyHud.SetHealth(current.Health, current.MaxHealth);
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
	internal static LegacyHudRequest SampleHud() => new("data/global/palette/act1/pal.dat", 128, 32,
		[new("Health", "hud.dc6", 0, 0, 0), new("Menu", "hud.dc6", 0, 32, 0), new("Inventory", "hud.dc6", 0, 64, 0), new("Decoration", "hud.dc6", 0, 96, 0)]);
	private void HudArtworkSmoke()
	{
		if (!legacyHud.HasArtwork || !legacyHud.CheckTexture() || healthBar.Visible || legacyHud.HealthRows != 32)
			throw new InvalidDataException("HUD texture/full-health binding failed.");
		var snapshot = simulation.CaptureSnapshot();
		simulation = GameSimulation.Restore(snapshot with { Entities = snapshot.Entities.Select(e => e.Id == Player ? e with { Health = 50 } : e).ToArray() });
		recording = new(simulation); view.SetSimulation(simulation); previous = current = simulation.GetEntity(Player); ShowFrame();
		if (legacyHud.HealthRows != 16 || !playerStatus.Text.Contains("HP 50/100")) throw new InvalidDataException("HUD did not reflect restored health.");
		legacyHud.Size = new Vector2(128, 32);
		if (!legacyHud.ActivateAt(new(65, 1)) || inventoryPanel.Visible || !legacyHud.ActivateAt(new(65, 1)) || !inventoryPanel.Visible)
			throw new InvalidDataException("HUD inventory action failed.");
		SetPaused(true);
		if (!legacyHud.ActivateAt(new(33, 1)) || !menuOpen || !paused) throw new InvalidDataException("HUD menu did not pause play.");
		ContinueMenuSession(); if (!paused) throw new InvalidDataException("HUD menu lost the pre-existing pause.");
		// Half-width strip with vertical letterboxing, as during a container's resize pass.
		legacyHud.Size = new Vector2(64, 32);
		if (!legacyHud.ActivateAt(new(33, 16)) || inventoryPanel.Visible || legacyHud.ActivateAt(new(49, 16)) || legacyHud.ActivateAt(new(33, 1))) throw new InvalidDataException("Scaled HUD action geometry failed.");
		ToggleInventory();
		NewRun(); GD.Print("OPEND2_PLAY11_HUD_READY");
	}
}
