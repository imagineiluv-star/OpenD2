using Godot;
using OpenD2.Assets;
using OpenD2.Core;

namespace OpenD2.Client;

public partial class SceneArtworkEditor
{
	private async Task Smoke()
	{
		string folder = Path.Combine(Path.GetTempPath(), "opend2-art-setup-" + Guid.NewGuid().ToString("N"));
		try
		{
			if (!save.Disabled || !loadSaved.Disabled || form.Visible) throw new InvalidDataException("Empty artwork form exposed save/load actions.");
			var sample = MapPreview.SampleData();
			byte[] Read(string path) => path.EndsWith(".txt") ? ItemDefinitionSmoke.Read(path) : path.EndsWith(".ds1") ? sample.Ds1 : path.EndsWith(".dt1") ? sample.Dt1 : path.EndsWith(".dc6") ? AssetPreview.SampleDc6() : path.EndsWith(".dcc") ? Convert.FromHexString(AnimationPreview.SampleDcc) : sample.Colors;
			var request = LegacySceneSetup.Create(new(1, "lod-1.10f", "test.ds1", "data/global/palette/act1/pal.dat", ["test.dt1"]), "Art setup smoke", 1, 1, 6, 2, 2, 1);
			string original = Path.Combine(folder, "original.json"), copy = Path.Combine(folder, "copy.json");
			LegacySceneSetup.SaveNew(original, request, Read); byte[] before = File.ReadAllBytes(original);
			if (!await ReadScene(original, Read) || save.Disabled || !loadSaved.Disabled || !form.Visible) throw new InvalidDataException("Artwork form failed to open a terrain scene.");
			enabled.ButtonPressed = true; motions[0].Path.Text = "unfinished"; ShowActor(1); ShowActor(0);
			if (motions[0].Path.Text != "unfinished" || !enabled.ButtonPressed) throw new InvalidDataException("Actor switching lost an incomplete draft.");
			if (await SaveCopy(Read, copy) || File.Exists(copy) || !File.ReadAllBytes(original).SequenceEqual(before)) throw new InvalidDataException("Invalid artwork changed files.");
			for (int index = 0; index < 2; index++)
			{
				ShowActor(index); enabled.ButtonPressed = true;
				foreach (var motion in motions) motion.Show(new("test.dcc", "", "0,0,0,0,1,1,1,1", 12.34567));
			}
			ShowActor(0);
			if (Math.Abs(motions[0].Fps.Value - 12.34567) > 1e-9) throw new InvalidDataException("Artwork form rounded imported FPS.");
			ShowActor(source!.Actors.Length); enabled.ButtonPressed = true; npcFacing.Select(4);
			motions[0].Show(new("test.dcc", "", "0,0,0,0,1,1,1,1", 10));
			if (!npcFacing.Visible || !tabs.IsTabHidden(1)) throw new InvalidDataException("Guide editor did not restrict artwork to Idle.");
			ShowActor(0); ShowActor(source.Actors.Length);
			if (npcFacing.Selected != 4 || motions[0].Path.Text != "test.dcc") throw new InvalidDataException("Guide draft or facing was lost.");
			ShowHud(SimulationPreview.SampleHud(), "");
			if (!await InspectHud(Read) || !hudPreview.CheckTexture() || hudPreview.HealthRows != 16) throw new InvalidDataException("HUD form preview failed.");
			hudRows[0].Frame.Value = 999;
			if (await InspectHud(Read) || !hudPreview.CheckTexture() || await SaveCopy(Read, copy) || File.Exists(copy)) throw new InvalidDataException("Invalid HUD changed the prior preview or saved a copy.");
			hudRows[0].Frame.Value = 0;
			ShowItems(SimulationPreview.SampleItems(), "");
			if (!await InspectItems(Read) || itemPreviewTextures?.CheckTexture() != true || itemRows[ItemDefinition.TrainingSword].Preview.Icon is null || itemRows[ItemDefinition.TrainingVest].Preview.Icon is not null)
				throw new InvalidDataException("Item form preview/fallback failed.");
			var keptItems = itemPreviewTextures; itemRows[ItemDefinition.TrainingSword].Frame.Value = 999;
			if (await InspectItems(Read) || itemPreviewTextures != keptItems || await SaveCopy(Read, copy) || File.Exists(copy)) throw new InvalidDataException("Invalid item replaced the preview or saved a file.");
			itemRows[ItemDefinition.TrainingSword].Frame.Value = 0;
			if (!await LoadItemTables(Read) || itemTables?.Items.Count != 3) throw new InvalidDataException("Item definition browser did not load tables.");
			definitionsSearch.Text = "fws"; FilterDefinitions(); definitionsTarget.Select(0); ShowDefinition();
			if (definitionMatches.Length != 1 || definitionsApply.Disabled || !definitionsInfo.Text.Contains("Reference only")) throw new InvalidDataException("Item definition search failed.");
			ApplyDefinition();
			string iconPath = "data\\global\\items\\fixtureweapon.dc6";
			if (!definitionsEnabled.ButtonPressed || definitionCodes[ItemDefinition.TrainingSword].Text != "fws" || itemRows[ItemDefinition.TrainingSword].Path.Text != iconPath || !await InspectItems(Read))
				throw new InvalidDataException("Item definition did not populate a usable icon path.");
			definitionsTarget.Select(1); ShowDefinition(); ApplyDefinition();
			if (!definitionsApply.Disabled || definitionCodes[ItemDefinition.TrainingVest].Text.Length != 0) throw new InvalidDataException("Incompatible definition could be applied.");
			definitionsTarget.Select(0); ShowDefinition(); var keptTables = itemTables;
			if (await LoadItemTables(_ => throw new InvalidDataException("Synthetic missing table")) || itemTables != keptTables || definitionCodes[ItemDefinition.TrainingSword].Text != "fws" || itemRows[ItemDefinition.TrainingSword].Path.Text != iconPath)
				throw new InvalidDataException("Failed table read discarded edits or prior results.");
			definitionCodes[ItemDefinition.TrainingSword].Text = "nope";
			if (await SaveCopy(Read, copy) || File.Exists(copy)) throw new InvalidDataException("Unknown item code saved a scene.");
			definitionCodes[ItemDefinition.TrainingSword].Text = "fws";
			var saving = SaveCopy(Read, copy);
			if (!definitionsLoad.Disabled || definitionCodes[ItemDefinition.TrainingSword].Editable) throw new InvalidDataException("Definition save allowed concurrent edits.");
			if (!busy || !save.Disabled || !actor.Disabled || !motions[0].Preview.Disabled || !hudAdd.Disabled || hudRows[0].Path.Editable || !itemsInspect.Disabled || itemRows[ItemDefinition.TrainingSword].Path.Editable) throw new InvalidDataException("Artwork save allowed concurrent edits.");
			if (!await saving || savedFile != copy || loadSaved.Disabled) throw new InvalidDataException("Valid artwork copy could not be saved.");
			var loaded = LegacyPlayScene.Load(LegacySceneRequest.Read(copy), Read);
			if (loaded.NpcArtwork is null || loaded.NpcFacing != 4 || !PlaySceneReadiness.Check(loaded).ReadyForAllSpritesGuiCheck || loaded.Artwork.Count != 2 || !PlaySceneReadiness.Check(loaded).ReadyForSceneGuiCheck || !File.ReadAllBytes(original).SequenceEqual(before)) throw new InvalidDataException("Artwork copy lost actors or changed source JSON.");
			if (loaded.HudArtwork is null || !PlaySceneReadiness.Check(loaded).HudArtworkConfigured) throw new InvalidDataException("Saved HUD was lost.");
			if (loaded.ItemArtwork?.Icons.Count != 1 || PlaySceneReadiness.Check(loaded).ItemArtworkCount != 1) throw new InvalidDataException("Saved item artwork was lost.");
			if (loaded.ItemDefinitions?.Bindings[ItemDefinition.TrainingSword].Code != "fws" || PlaySceneReadiness.Check(loaded).ItemDefinitionCount != 1) throw new InvalidDataException("Saved item definition was lost.");
			string prior = savedFile; string info = sourceInfo.Text;
			if (await ReadScene(Path.Combine(folder, "missing.json"), Read) || savedFile != prior || sourceInfo.Text != info || source is null) throw new InvalidDataException("Failed artwork import discarded the current form.");
			RequestOpen(original); replace.Hide(); replace.EmitSignal(ConfirmationDialog.SignalName.Canceled);
			if (pendingFile.Length != 0 || savedFile != prior || sourceInfo.Text != info) throw new InvalidDataException("Cancelled artwork import changed the form.");
			if (!await ReadScene(copy, Read)) throw new InvalidDataException("Could not reopen the saved NPC art copy.");
			ShowActor(source!.Actors.Length);
			if (!enabled.ButtonPressed || npcFacing.Selected != 4 || motions[0].Path.Text != "test.dcc") throw new InvalidDataException("NPC artwork was not restored in the editor.");
			if (!hudEnabled.ButtonPressed || hudRows.Count != 4 || hudRows[2].Role.Selected != (int)HudRole.Inventory || hudWidth.Value != 128) throw new InvalidDataException("HUD form was not restored.");
			if (!itemsEnabled.ButtonPressed || !itemRows[ItemDefinition.TrainingSword].Enabled.ButtonPressed || itemRows[ItemDefinition.TrainingSword].Path.Text != iconPath || itemRows[ItemDefinition.TrainingVest].Enabled.ButtonPressed)
				throw new InvalidDataException("Item form was not restored.");
			if (!definitionsEnabled.ButtonPressed || definitionCodes[ItemDefinition.TrainingSword].Text != "fws" || itemTables is not null) throw new InvalidDataException("Definition form was not restored.");
			definitionsEnabled.SetPressedNoSignal(false); string withoutDefinitions = Path.Combine(folder, "without-definitions.json");
			if (!await SaveCopy(Read, withoutDefinitions) || LegacySceneRequest.Read(withoutDefinitions).ItemDefinitions is not null || LegacySceneRequest.Read(withoutDefinitions).ItemArtwork is null)
				throw new InvalidDataException("Definition disable changed artwork or retained bindings.");
			GD.Print("OPEND2_PLAY13_DEFINITION_SETUP_READY");
			itemsEnabled.SetPressedNoSignal(false); string withoutItems = Path.Combine(folder, "without-items.json");
			if (!await SaveCopy(Read, withoutItems) || LegacySceneRequest.Read(withoutItems).ItemArtwork is not null) throw new InvalidDataException("Item disable was not saved.");
			GD.Print("OPEND2_PLAY12_ITEM_SETUP_READY");
			hudEnabled.SetPressedNoSignal(false); string withoutHud = Path.Combine(folder, "without-hud.json");
			if (!await SaveCopy(Read, withoutHud) || LegacySceneRequest.Read(withoutHud).HudArtwork is not null) throw new InvalidDataException("HUD disable was not saved.");
			GD.Print("OPEND2_PLAY09_ART_SETUP_READY");
		}
		catch (Exception error) { GD.PushError("Artwork setup smoke failed: " + error); GetTree().Quit(1); }
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
			source = null; selectedActor = -1; drafts.Clear(); actor.Clear(); savedFile = ""; sourceDirectory = ""; sourceInfo.Text = ""; status.Text = ""; ShowHud(null, "data/global/palette/units/pal.dat"); ShowItems(null, "data/global/palette/units/pal.dat"); ShowDefinitions(null); form.Hide(); SetBusy(false);
		}
	}
}
