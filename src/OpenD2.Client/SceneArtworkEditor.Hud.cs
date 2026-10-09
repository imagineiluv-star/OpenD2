using Godot;
using OpenD2.Assets;

namespace OpenD2.Client;

public partial class SceneArtworkEditor
{
	private sealed class HudRow
	{
		public readonly VBoxContainer Root = new();
		public readonly OptionButton Role = new();
		public readonly LineEdit Path = new() { PlaceholderText = "Verified MPQ UI .dc6 path", MaxLength = 1023 };
		public readonly SpinBox Frame = Number(4095), X = Number(4095), Y = Number(1023);
		public readonly Button Remove = new() { Text = "Remove element" };
		public HudRow()
		{
			foreach (string role in Enum.GetNames<HudRole>()) Role.AddItem(role);
			Root.AddChild(Role); Root.AddChild(Path); var numbers = new HFlowContainer(); Root.AddChild(numbers);
			foreach (var pair in new[] { ("Frame", Frame), ("X", X), ("Y", Y) })
			{ numbers.AddChild(new Label { Text = pair.Item1 }); numbers.AddChild(pair.Item2); }
			Root.AddChild(Remove);
		}
		public LegacyHudElement Capture() => new(Role.GetItemText(Role.Selected), Path.Text, (int)Frame.Value, (int)X.Value, (int)Y.Value);
		public void Show(LegacyHudElement value)
		{ Role.Select((int)Enum.Parse<HudRole>(value.Role)); Path.Text = value.Path; Frame.Value = value.Frame; X.Value = value.X; Y.Value = value.Y; }
		public void SetBusy(bool busy) { Role.Disabled = busy; Path.Editable = !busy; Frame.Editable = X.Editable = Y.Editable = !busy; Remove.Disabled = busy; }
	}
	private static SpinBox Number(int max) => new() { MinValue = 0, MaxValue = max, Step = 1, CustomMinimumSize = new Vector2(100, 0) };
	private readonly CheckButton hudEnabled = new() { Text = "Use HUD artwork" };
	private readonly LineEdit hudPalette = new() { PlaceholderText = "Verified UI palette path", MaxLength = 1023 };
	private readonly SpinBox hudWidth = Number(4096), hudHeight = Number(1024);
	private readonly VBoxContainer hudRowsBox = new();
	private readonly List<HudRow> hudRows = new();
	private readonly Button hudAdd = new() { Text = "Add HUD element (up to 32)" };
	private readonly Button hudInspect = new() { Text = "Preview HUD at 50% health/mana" };
	private readonly LegacyHudView hudPreview = new();
	private void BuildHudEditor()
	{
		var toggle = new Button { Text = "HUD artwork settings", ToggleMode = true }; form.AddChild(toggle);
		var fields = new VBoxContainer { Visible = false }; form.AddChild(fields); toggle.Toggled += value => fields.Visible = value;
		fields.AddChild(new Label { Text = "Choose verified DC6 frames in the DC6 tab. Add elements in back-to-front order; X/Y are the frame's top-left on the HUD canvas.\nDecoration draws a static frame; Health and Mana clip from the bottom. Menu and Inventory use the existing actions. One of each action/Health/Mana, multiple decorations.\nCanvas 800×120 is an editable example. File offsets are not added. Skill icons, belt slots and original fonts are not connected.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		fields.AddChild(hudEnabled); fields.AddChild(hudPalette);
		var size = new HFlowContainer(); fields.AddChild(size);
		size.AddChild(new Label { Text = "Canvas width" }); size.AddChild(hudWidth); size.AddChild(new Label { Text = "height" }); size.AddChild(hudHeight);
		fields.AddChild(hudRowsBox); fields.AddChild(hudAdd); fields.AddChild(hudInspect); fields.AddChild(hudPreview);
		hudAdd.Pressed += () => { if (!busy && hudRows.Count < LegacyHudArt.MaxElements) AddHudRow(new("Decoration", "", 0, 0, 0)); };
		hudInspect.Pressed += () => _ = InspectHud();
		ShowHud(null, "data/global/palette/units/pal.dat");
	}
	private void AddHudRow(LegacyHudElement element)
	{
		var row = new HudRow(); row.Show(element); hudRows.Add(row); hudRowsBox.AddChild(row.Root);
		row.Remove.Pressed += () =>
		{
			if (busy) return;
			hudRows.Remove(row); hudRowsBox.RemoveChild(row.Root); row.Root.QueueFree(); SetHudBusy(false);
		};
		SetHudBusy(busy);
	}
	private void ShowHud(LegacyHudRequest? request, string palettePath)
	{
		foreach (var row in hudRows) { hudRowsBox.RemoveChild(row.Root); row.Root.QueueFree(); } hudRows.Clear();
		hudEnabled.SetPressedNoSignal(request is not null); hudPalette.Text = request?.PalettePath ?? palettePath;
		hudWidth.Value = request?.Width ?? 800; hudHeight.Value = request?.Height ?? 120;
		foreach (var element in request?.Elements ?? []) AddHudRow(element);
		hudPreview.SetArtwork(null); SetHudBusy(busy);
	}
	private LegacyHudRequest CaptureHud() => LegacyHudArt.Snapshot(new(hudPalette.Text, (int)hudWidth.Value, (int)hudHeight.Value, hudRows.Select(r => r.Capture()).ToArray()));
	private void SetHudBusy(bool value)
	{
		hudEnabled.Disabled = value; hudPalette.Editable = !value; hudWidth.Editable = hudHeight.Editable = !value;
		hudAdd.Disabled = value || hudRows.Count >= LegacyHudArt.MaxElements; hudInspect.Disabled = value;
		foreach (var row in hudRows) row.SetBusy(value);
	}
	private async Task<bool> InspectHud(Func<string, byte[]>? read = null)
	{
		if (busy || source is null) return false;
		SetBusy(true);
		try
		{
			if (gameDirectory() != sourceDirectory) throw new InvalidDataException("Data directory changed. Reopen the scene before inspecting.");
			var request = CaptureHud(); string directory = sourceDirectory;
			var art = await Task.Run(() => LegacyHudArt.Load(request, read ?? (path => AssetDecoders.ReadFromInstall(directory, path))));
			if (!IsInstanceValid(this) || !IsInsideTree()) return false;
			hudPreview.SetArtwork(new(art)); hudPreview.SetHealth(50, 100); hudPreview.SetMana(30, 60);
			status.Text = "HUD preview loaded at 50% health/mana. Later edits require previewing again. Save a new scene copy to use it in play. Original appearance and GUI QA remain unverified.";
			return true;
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "HUD preview failed; previous preview retained. " + error.Message; return false; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) SetBusy(false); }
	}
}
