using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SceneArtworkEditor
{
	private readonly CheckButton definitionsEnabled = new() { Text = "Attach original item reference definitions" };
	private readonly CheckButton dimensionsEnabled = new() { Text = "Use bound item codes and sizes in the 10×4 bag" };
	private readonly Button definitionsLoad = new() { Text = "Read item TXT tables (LoD 1.10f profile)" };
	private readonly LineEdit definitionsSearch = new() { PlaceholderText = "Search original code or source name", MaxLength = 80 };
	private readonly OptionButton definitionsResults = new(), definitionsTarget = new();
	private readonly Button definitionsApply = new() { Text = "Use selected definition and inventory image path", Disabled = true };
	private readonly Label definitionsInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
	private readonly Dictionary<ItemDefinition, LineEdit> definitionCodes = new();
	private ItemTables? itemTables;
	private string itemTablesDirectory = "";
	private ItemBaseDefinition[] definitionMatches = [];
	private void BuildDefinitionEditor()
	{
		var toggle = new Button { Text = "Original item definitions", ToggleMode = true }; form.AddChild(toggle);
		var fields = new VBoxContainer { Visible = false }; form.AddChild(fields); toggle.Toggled += value => fields.Visible = value;
		fields.AddChild(new Label { Text = "Read weapons, armor, misc, itemtypes and bodylocs TXT from your owned data. Search a code/name and associate it with a preview item.\nThe 10×4 bag uses preview sizes by default. Enable bound codes/sizes below to use the table dimensions. Combat stats and equipment requirements remain preview rules. Names are source labels, not localized TBL names.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
		fields.AddChild(definitionsEnabled); fields.AddChild(dimensionsEnabled);
		foreach (var id in Enum.GetValues<ItemDefinition>().Where(id => !ItemCatalog.IsConsumable(id)))
		{
			var code = new LineEdit { PlaceholderText = "Original code (blank = unbound)", MaxLength = 4 }; definitionCodes.Add(id, code);
			fields.AddChild(new Label { Text = ItemCatalog.Get(id).Name }); fields.AddChild(code); definitionsTarget.AddItem(ItemCatalog.Get(id).Name, (int)id);
		}
		foreach (var control in new Control[] { definitionsLoad, definitionsSearch, definitionsResults, definitionsInfo, definitionsTarget, definitionsApply }) fields.AddChild(control);
		definitionsLoad.Pressed += () => _ = LoadItemTables(); definitionsSearch.TextChanged += _ => FilterDefinitions();
		definitionsResults.ItemSelected += _ => ShowDefinition(); definitionsTarget.ItemSelected += _ => ShowDefinition();
		definitionsApply.Pressed += ApplyDefinition; ShowDefinitions(null);
	}
	private void ShowDefinitions(LegacyItemDefinitionsRequest? request)
	{
		definitionsEnabled.SetPressedNoSignal(request is not null); dimensionsEnabled.SetPressedNoSignal(false);
		foreach (var pair in definitionCodes) pair.Value.Text = request?.Bindings.SingleOrDefault(b => b.Definition == pair.Key.ToString())?.Code ?? "";
		itemTables = null; itemTablesDirectory = ""; definitionsSearch.Text = ""; FilterDefinitions(); SetDefinitionsBusy(busy);
	}
	private LegacyItemDefinitionsRequest CaptureDefinitions() => LegacyItemDefinitions.Snapshot(new(ItemTables.Profile,
		definitionCodes.Where(p => p.Value.Text.Trim().Length > 0).Select(p => new LegacyItemBinding(p.Key.ToString(), p.Value.Text.Trim())).ToArray()));
	private void SetDefinitionsBusy(bool value)
	{
		definitionsEnabled.Disabled = value; dimensionsEnabled.Disabled = value; definitionsLoad.Disabled = value; definitionsSearch.Editable = !value; definitionsResults.Disabled = value; definitionsTarget.Disabled = value;
		foreach (var input in definitionCodes.Values) input.Editable = !value;
		ShowDefinition();
	}
	private void FilterDefinitions()
	{
		string search = definitionsSearch.Text.Trim();
		definitionMatches = itemTables?.Items.Values.Where(i => i.Code.Contains(search, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
			.OrderBy(i => i.Code, StringComparer.Ordinal).Take(100).ToArray() ?? [];
		definitionsResults.Clear();
		foreach (var item in definitionMatches) definitionsResults.AddItem($"{item.Code} · {item.Name} · {item.Kind}");
		ShowDefinition();
	}
	private void ShowDefinition()
	{
		definitionsApply.Disabled = true;
		if (definitionsResults.Selected < 0 || definitionsResults.Selected >= definitionMatches.Length)
		{ definitionsInfo.Text = itemTables is null ? "Read the tables to browse original definitions." : "No matching items."; return; }
		var item = definitionMatches[definitionsResults.Selected];
		definitionsInfo.Text = $"{itemTables!.Items.Count} definitions loaded; at most 100 search results shown.\n" + ItemDefinitionText.Describe(item);
		definitionsApply.Disabled = busy || itemTablesDirectory != gameDirectory() || !LegacyItemDefinitions.CanBind((ItemDefinition)definitionsTarget.GetSelectedId(), item);
	}
	private void ApplyDefinition()
	{
		if (busy || itemTables is null || itemTablesDirectory != gameDirectory() || sourceDirectory != gameDirectory() || definitionsResults.Selected < 0 || definitionsResults.Selected >= definitionMatches.Length) return;
		var id = (ItemDefinition)definitionsTarget.GetSelectedId(); var item = definitionMatches[definitionsResults.Selected];
		if (!LegacyItemDefinitions.CanBind(id, item)) return;
		definitionCodes[id].Text = item.Code; definitionsEnabled.SetPressedNoSignal(true);
		var row = itemRows[id]; row.Path.Text = item.InventoryPath; row.Frame.Value = 0; row.Enabled.SetPressedNoSignal(true); itemsEnabled.SetPressedNoSignal(true);
		status.Text = "Definition and table-derived icon path applied. Verify the palette and frame in Item artwork preview, then save a new scene copy. Previous preview remains until Preview is pressed.";
	}
	private async Task<bool> LoadItemTables(Func<string, byte[]>? read = null)
	{
		if (busy || source is null) return false;
		SetBusy(true);
		try
		{
			string directory = sourceDirectory;
			if (gameDirectory() != directory) throw new InvalidDataException("Data directory changed. Reopen the scene before reading tables.");
			var loaded = await Task.Run(() => ItemTables.Load(read ?? (path => AssetDecoders.ReadFromInstall(directory, path))));
			if (!IsInstanceValid(this) || !IsInsideTree()) return false;
			if (gameDirectory() != directory) throw new InvalidDataException("Data directory changed during the read. Previous definitions retained.");
			itemTables = loaded; itemTablesDirectory = directory; FilterDefinitions();
			status.Text = "Tables loaded. Select a result and matching preview item; Apply fills its code and icon path. Image files and game compatibility still require validation.";
			return true;
		}
		catch (Exception error) { if (IsInstanceValid(this) && IsInsideTree()) status.Text = "Item tables failed; previous results and edits retained. " + error.Message; return false; }
		finally { if (IsInstanceValid(this) && IsInsideTree()) SetBusy(false); }
	}
}
