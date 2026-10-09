using Godot;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SimulationPreview
{
	private ItemState? pickupTarget;
	private readonly CheckButton groundNames = new() { Text = "Loot names (L)", ButtonPressed = true };
	private void BuildGroundItems()
	{
		groundNames.Toggled += visible => { view.ShowGroundNames = visible; StopInput(); view.QueueRedraw(); };
		view.PickupRequested += ClickPickup;
		view.CancelRequested += StopInput;
		view.ToggleLootRequested += () => groundNames.ButtonPressed = !groundNames.ButtonPressed;
	}
	private ItemState? GroundItem(ItemId id)
	{
		foreach (var item in simulation.Items)
			if (item.Id == id && item.Location == ItemLocation.Ground && item.Region == current.Region) return item;
		return null;
	}
	private void ClickPickup(ItemId id)
	{
		if (menuOpen || verifying || paused || !current.IsAlive || !view.ShowGroundNames) return;
		StopInput();
		if (GroundItem(id) is not { } item) { status.Text = "Loot is no longer available."; return; }
		if (simulation.Collision!.HasMeleeLine(current.Position, item.Position)) { Submit(CommandKind.Pickup, item: id); return; }
		ClickMove(item.Position);
		if (route.Count == 0) return;
		pickupTarget = item; view.SelectedGroundItem = id;
		status.Text = "Approaching " + ItemCatalog.Get(item.Definition).Name + ". Right-click / Escape cancels.";
	}
	private bool FollowPickup()
	{
		if (pickupTarget is not { } target) return false;
		if (GroundItem(target.Id) != target)
		{ ClearRoute(); status.Text = "Loot changed; approach cancelled."; return true; }
		if (!simulation.Collision!.HasMeleeLine(current.Position, target.Position)) return false;
		StopInput(); Submit(CommandKind.Pickup, item: target.Id); return true;
	}
}

public partial class SimulationCanvas
{
	public event Action<ItemId>? PickupRequested;
	public event Action? CancelRequested;
	public event Action? ToggleLootRequested;
	public bool ShowGroundNames { get; set; } = true;
	public ItemId SelectedGroundItem { get; set; }
	private readonly record struct GroundLabel(ItemId Id, string Text, Vector2 Anchor, Rect2 Bounds);
	private readonly List<GroundLabel> groundLabels = new();
	private readonly List<ItemState> visibleLoot = new();
	private Vector2 ProjectLoot(GamePosition p) => terrainContent is null
		? Size / 2 + (new Vector2(p.X, p.Y) - DisplayPosition) / GameSimulation.UnitsPerTile * 40
		: Size / 2 + Iso(p.X - DisplayPosition.X, p.Y - DisplayPosition.Y);
	// Rebuilt for drawing AND input: resize, camera motion and removal cannot leave stale hit boxes.
	private void LayoutGroundLabels()
	{
		groundLabels.Clear(); visibleLoot.Clear();
		if (!ShowGroundNames || simulation is null) return;
		var viewport = new Rect2(Vector2.Zero, Size); const int height = 22, gap = 3;
		foreach (var item in simulation.Items)
			if (item.Location == ItemLocation.Ground && item.Region == simulation.ActiveRegion && viewport.HasPoint(ProjectLoot(item.Position))) visibleLoot.Add(item);
		visibleLoot.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
		foreach (var item in visibleLoot)
		{
			string text = ItemCatalog.Get(item.Definition).Name;
			float width = GetThemeDefaultFont().GetStringSize(text, fontSize: 14).X + 12;
			if (width > Size.X || height > Size.Y) continue;
			Vector2 anchor = ProjectLoot(item.Position);
			float x = Math.Clamp(anchor.X - width / 2, 0, Size.X - width);
			float start = Math.Clamp(anchor.Y + 10, 0, Size.Y - height);
			// Search both directions, keeping every displayed label wholly inside the canvas.
			for (int step = 0; step <= 2 * (int)(Size.Y / (height + gap)) + 2; step++)
			{
				int offset = (step + 1) / 2 * (step % 2 == 1 ? -1 : 1);
				float y = start + offset * (height + gap);
				if (y < 0 || y + height > Size.Y) continue;
				var bounds = new Rect2(x, y, width, height);
				if (groundLabels.Any(label => label.Bounds.Grow(gap / 2f).Intersects(bounds))) continue;
				groundLabels.Add(new(item.Id, text, anchor, bounds)); break;
			}
		}
	}
	private ItemId HitGroundLabel(Vector2 at)
	{
		LayoutGroundLabels();
		foreach (var label in groundLabels) if (label.Bounds.HasPoint(at)) return label.Id;
		return default;
	}
	private void DrawGroundLabels()
	{
		LayoutGroundLabels();
		foreach (var label in groundLabels)
		{
			var color = label.Id == SelectedGroundItem ? Colors.Gold : Colors.Cyan;
			DrawLine(label.Anchor, label.Bounds.GetCenter(), color.Darkened(0.5f));
			DrawRect(new Rect2(label.Anchor - new Vector2(3, 3), new Vector2(6, 6)), color);
			DrawRect(label.Bounds, new Color(0.02f, 0.03f, 0.05f, 0.95f));
			DrawRect(label.Bounds, color, false);
			DrawString(GetThemeDefaultFont(), label.Bounds.Position + new Vector2(6, 16), label.Text, fontSize: 14, modulate: color);
		}
	}
}
