using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SceneArtworkEditor
{
	private sealed class ItemRow
	{
		public readonly CheckButton Enabled = new();
		public readonly LineEdit Path = new() { PlaceholderText = "Verified inventory DC6 path", MaxLength = 1023 };
		public readonly SpinBox Frame = Number(4095);
		public readonly Button Preview = new() { MouseFilter = MouseFilterEnum.Ignore, FocusMode = FocusModeEnum.None, ExpandIcon = true,
			TextureFilter = TextureFilterEnum.Nearest, ClipText = true, CustomMinimumSize = new Vector2(180, 48) };
	}
	private readonly CheckButton itemsEnabled = new() { Text = "Use item artwork" };
	private readonly LineEdit itemsPalette = new() { PlaceholderText = "Verified item palette path", MaxLength = 1023 };
	private readonly Button itemsInspect = new() { Text = "Preview item icons" };
	private readonly Dictionary<ItemDefinition, ItemRow> itemRows = new();
	private LegacyItemTextures? itemPreviewTextures;
	private void BuildItemEditor()
	{
		var toggle = new Button { Text = "Item artwork settings", ToggleMode = true }; form.AddChild(toggle);
		var fields = new VBoxContainer { Visible = false }; form.AddChild(fields); toggle.Toggled += value => fields.Visible = value;
		fields.AddChild(new Label { Text = "Choose an inventory DC6 frame for each preview item. Verify paths, palette and frames in the DC6 tab. Unmapped items keep their names.\nIcons scale to fit slots with transparency and aspect ratio preserved. Item stats use preview rules. The bag has 10×4 cells; the belt has four potion slots.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		fields.AddChild(itemsEnabled); fields.AddChild(itemsPalette);
		foreach (var definition in Enum.GetValues<ItemDefinition>())
		{
			var row = new ItemRow(); row.Preview.AddThemeConstantOverride("icon_max_width", 40); row.Enabled.Text = ItemCatalog.Get(definition).Name; itemRows.Add(definition, row);
			fields.AddChild(row.Enabled); fields.AddChild(row.Path);
			var frame = new HFlowContainer(); fields.AddChild(frame); frame.AddChild(new Label { Text = "Frame" }); frame.AddChild(row.Frame);
			fields.AddChild(row.Preview);
		}
		fields.AddChild(itemsInspect); itemsInspect.Pressed += () => _ = InspectItems();
		ShowItems(null, "data/global/palette/units/pal.dat");
	}
	private void SetItemPreview(LegacyItemTextures? next)
	{
		foreach (var pair in itemRows)
		{
			pair.Value.Preview.Icon = next?.Get(pair.Key);
			pair.Value.Preview.Text = ItemCatalog.Get(pair.Key).Name + (pair.Value.Preview.Icon is null ? " (no icon)" : "");
		}
		itemPreviewTextures?.Dispose(); itemPreviewTextures = next;
	}
	public override void _ExitTree() => SetItemPreview(null);
	private void ShowItems(LegacyItemRequest? request, string palettePath)
	{
		itemsEnabled.SetPressedNoSignal(request is not null); itemsPalette.Text = request?.PalettePath ?? palettePath;
		foreach (var pair in itemRows)
		{
			var icon = request?.Icons.SingleOrDefault(i => i.Definition == pair.Key.ToString()); var row = pair.Value;
			row.Enabled.SetPressedNoSignal(icon is not null); row.Path.Text = icon?.Path ?? ""; row.Frame.Value = icon?.Frame ?? 0;
		}
		SetItemPreview(null); SetItemsBusy(busy);
	}
	private LegacyItemRequest CaptureItems() => LegacyItemArt.Snapshot(new(itemsPalette.Text,
		itemRows.Where(p => p.Value.Enabled.ButtonPressed).Select(p => new LegacyItemIcon(p.Key.ToString(), p.Value.Path.Text, (int)p.Value.Frame.Value)).ToArray()));
	private void SetItemsBusy(bool value)
	{
		itemsEnabled.Disabled = value; itemsPalette.Editable = !value; itemsInspect.Disabled = value;
		foreach (var row in itemRows.Values) { row.Enabled.Disabled = value; row.Path.Editable = !value; row.Frame.Editable = !value; }
	}
	private async Task<bool> InspectItems(Func<string, byte[]>? read = null)
	{
		if (busy || source is null) return false;
		SetBusy(true);
		try
		{
			if (gameDirectory() != sourceDirectory) throw new InvalidDataException("Data directory changed. Reopen the scene before inspecting.");
			var request = CaptureItems(); string directory = sourceDirectory;
			var art = await Task.Run(() => LegacyItemArt.Load(request, read ?? (path => AssetDecoders.ReadFromInstall(directory, path))));
			if (!IsInstanceValid(this) || !IsInsideTree()) return false;
			SetItemPreview(new(art)); status.Text = "Item preview loaded. Preview again after edits; save a new scene copy to use the icons in play.";
			return true;
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Item preview failed; previous preview retained. " + error.Message; return false; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) SetBusy(false); }
	}
}
