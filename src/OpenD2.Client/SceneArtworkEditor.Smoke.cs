using Godot;
using OpenD2.Assets;

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
			byte[] Read(string path) => path.EndsWith(".ds1") ? sample.Ds1 : path.EndsWith(".dt1") ? sample.Dt1 : path.EndsWith(".dcc") ? Convert.FromHexString(AnimationPreview.SampleDcc) : sample.Colors;
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
			var saving = SaveCopy(Read, copy);
			if (!busy || !save.Disabled || !actor.Disabled || !motions[0].Preview.Disabled) throw new InvalidDataException("Artwork save allowed concurrent edits.");
			if (!await saving || savedFile != copy || loadSaved.Disabled) throw new InvalidDataException("Valid artwork copy could not be saved.");
			var loaded = LegacyPlayScene.Load(LegacySceneRequest.Read(copy), Read);
			if (loaded.Artwork.Count != 2 || !PlaySceneReadiness.Check(loaded).ReadyForSceneGuiCheck || !File.ReadAllBytes(original).SequenceEqual(before)) throw new InvalidDataException("Artwork copy lost actors or changed source JSON.");
			string prior = savedFile; string info = sourceInfo.Text;
			if (await ReadScene(Path.Combine(folder, "missing.json"), Read) || savedFile != prior || sourceInfo.Text != info || source is null) throw new InvalidDataException("Failed artwork import discarded the current form.");
			RequestOpen(original); replace.Hide(); replace.EmitSignal(ConfirmationDialog.SignalName.Canceled);
			if (pendingFile.Length != 0 || savedFile != prior || sourceInfo.Text != info) throw new InvalidDataException("Cancelled artwork import changed the form.");
			GD.Print("OPEND2_PLAY09_ART_SETUP_READY");
		}
		catch (Exception error) { GD.PushError("Artwork setup smoke failed: " + error); GetTree().Quit(1); }
		finally
		{
			if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
			source = null; selectedActor = -1; drafts.Clear(); actor.Clear(); savedFile = ""; sourceDirectory = ""; sourceInfo.Text = ""; status.Text = ""; form.Hide(); SetBusy(false);
		}
	}
}
