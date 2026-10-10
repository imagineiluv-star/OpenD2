using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private void RequireOpenProfile()
    {
        RequireLogin();
        if (client!.Mode != "open") throw new InvalidOperationException("Personal profile files require an Open server.");
        if (state is not null) throw new InvalidOperationException("Leave or close the room before transferring profiles.");
    }
    private void AddProfileControls()
    {
        var row = new HFlowContainer(); AddChild(row);
        Button(row, "Import Open profile", () => { ChooseProfile(false); return Task.CompletedTask; });
        Button(row, "Export Open profile", () => { ChooseProfile(true); return Task.CompletedTask; });
        AddChild(new Label { Text = "Open preview: profile files contain name and victories only, not items or quests. Host server must be started separately.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }
    private void ChooseProfile(bool export)
    {
        RequireOpenProfile();
        var owner = client;
        Guid? selected = export ? SelectedCharacter() : null;
        var dialog = new FileDialog
        {
            FileMode = export ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; OpenD2 personal profile"],
            Title = export ? "Export profile to a NEW file (name and victories only)" : "Import Open profile (name and victories only)"
        };
        AddChild(dialog);
        dialog.Canceled += () => dialog.QueueFree();
        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            UserAction(async () =>
            {
                if (client != owner || (selected is { } id && SelectedCharacter() != id))
                    throw new InvalidOperationException("Connection or character changed; choose the file again.");
                if (export) await ExportProfile(path); else await ImportProfile(path);
            });
        };
        dialog.PopupCenteredRatio(0.7f);
    }
    private async Task ExportProfile(string path)
    {
        RequireOpenProfile();
        await client!.ExportCharacter(SelectedCharacter(), path, lifetime.Token);
        status.Text = "Profile exported: " + Path.GetFileName(path) + ". Name and victories only; existing files are never replaced.";
    }
    private async Task ImportProfile(string path)
    {
        RequireOpenProfile();
        var imported = await client!.ImportCharacter(path, lifetime.Token);
        await Refresh();
        characters.Select(Array.FindIndex(characterList, c => c.Id == imported.Id));
        status.Text = "Imported " + imported.Name + ". Name and victories only; existing characters were not replaced.";
    }
    // Exercises the same selection and file operations used by the controls.
    // File-dialog gestures and physical GUI interaction remain manual acceptance.
    private async Task CheckProfileFiles(CharacterInfo original)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-profile-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string exported = Path.Combine(folder, "export.json"), copied = Path.Combine(folder, "copy.json");
            await ExportProfile(exported);
            var profile = OpenCharacterFile.Load(exported);
            if (profile.Name != original.Name || profile.Victories != original.Victories) throw new InvalidDataException("UI export mismatch.");
            OpenCharacterFile.Save(copied, profile with { Name = original.Name + "Copy" });
            await ImportProfile(copied);
            if (SelectedCharacter() == original.Id || characterList.Single(c => c.Id == SelectedCharacter()).Name != original.Name + "Copy")
                throw new InvalidDataException("UI import selection mismatch.");
            characters.Select(Array.FindIndex(characterList, c => c.Id == original.Id));
            GD.Print("OPEND2_OPEN_PROFILE_UI_PASS DIALOG_GESTURES_NOT_RUN");
        }
        finally { Directory.Delete(folder, true); }
    }
}
