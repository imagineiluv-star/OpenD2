using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly LineEdit catalogUrl = new() { PlaceholderText = "Operator catalog HTTPS URL", MaxLength = 2048,
        SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(280, 0) };
    private string catalogCaFile = "";
    private void AddCatalogDownloadControls()
    {
        var row = new HFlowContainer(); AddChild(row); row.AddChild(catalogUrl);
        Button(row, "Download server list", () => DownloadCatalog(CatalogPath));
        var trustLabel = new Label { Text = "List download trust: system certificates", AutowrapMode = TextServer.AutowrapMode.WordSmart }; AddChild(trustLabel);
        var dialog = new FileDialog { Title = "Public root CA for list download", FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.pem,*.crt ; Public root CA certificate"] }; AddChild(dialog);
        dialog.FileSelected += path => UserAction(() =>
        {
            RequireCatalogLogout(); catalogCaFile = path;
            trustLabel.Text = "List download CA: " + Path.GetFileName(path) + ". Verify this public CA with the operator separately.";
            return Task.CompletedTask;
        });
        Button(row, "Choose list CA", () => { RequireCatalogLogout(); dialog.PopupCenteredRatio(0.7f); return Task.CompletedTask; });
        Button(row, "List: system trust", () => { RequireCatalogLogout(); catalogCaFile = ""; trustLabel.Text = "List download trust: system certificates"; return Task.CompletedTask; });
    }
    private async Task DownloadCatalog(string destination)
    {
        RequireCatalogLogout();
        string source = catalogUrl.Text, root = catalogCaFile;
        var next = await ServerCatalog.Download(source, root, lifetime.Token);
        ReplaceCatalog(next, destination);
        status.Text = "Server list downloaded from " + source + ". Review the entries and use a selected server. Your game destination and trust have not changed.";
    }
}
