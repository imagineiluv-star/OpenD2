using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly OptionButton catalogRegions = new();
    private readonly OptionButton catalogServers = new() { FitToLongestItem = false, ClipText = true,
        CustomMinimumSize = new(280, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private ServerCatalog? serverCatalog;
    private ServerEntry[] visibleServers = [];
    private string CatalogPath => Path.Combine(OS.GetUserDataDir(), "servers.json");
    private void AddCatalogControls()
    {
        var row = new HFlowContainer(); AddChild(row);
        row.AddChild(new Label { Text = "Saved servers" }); row.AddChild(catalogRegions); row.AddChild(catalogServers);
        var dialog = new FileDialog { Title = "Import operator server list", FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.json ; OpenD2 server catalog"] }; AddChild(dialog);
        dialog.FileSelected += path => UserAction(() => { ImportCatalog(path, CatalogPath); return Task.CompletedTask; });
        Button(row, "Import server list", () => { RequireCatalogLogout(); dialog.PopupCenteredRatio(0.7f); return Task.CompletedTask; });
        Button(row, "Use selected server", () => { ApplyCatalogSelection(); return Task.CompletedTask; });
        AddCatalogDownloadControls();
        catalogRegions.ItemSelected += _ => FilterCatalog();
        SetCatalog(null);
        try { if (File.Exists(CatalogPath)) SetCatalog(ServerCatalog.Load(CatalogPath)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { status.Text = "Saved server list could not be loaded. Import a valid list to replace it. " + e.Message; }
    }
    private void RequireCatalogLogout()
    {
        if (signedIn) throw new InvalidOperationException("Log out before changing the server list or destination.");
    }
    private void ImportCatalog(string source, string destination)
    {
        RequireCatalogLogout();
        var next = ServerCatalog.Load(source);
        ReplaceCatalog(next, destination);
        status.Text = "Server list saved. Select a region and server, then Use selected server. Importing does not connect or change certificate trust.";
    }
    private void ReplaceCatalog(ServerCatalog next, string destination)
    {
        lifetime.Token.ThrowIfCancellationRequested(); RequireCatalogLogout();
        next.Save(destination); // Preserve the current list and connection if validation or storage fails.
        SetCatalog(next);
    }
    private void SetCatalog(ServerCatalog? catalog)
    {
        serverCatalog = catalog; catalogRegions.Clear(); catalogRegions.AddItem("All regions");
        foreach (string region in (catalog?.Servers ?? []).Select(s => s.Region).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            catalogRegions.AddItem(region);
        catalogRegions.Select(0); FilterCatalog();
    }
    private void FilterCatalog()
    {
        string? region = catalogRegions.Selected > 0 ? catalogRegions.GetItemText(catalogRegions.Selected) : null;
        visibleServers = (serverCatalog?.Servers ?? []).Where(s => region is null || s.Region == region).ToArray();
        catalogServers.Clear();
        foreach (var server in visibleServers) catalogServers.AddItem($"{server.Name} | {server.Region} | {server.Mode} | {server.Address}");
        if (visibleServers.Length > 0) catalogServers.Select(0);
    }
    private void ApplyCatalogSelection()
    {
        RequireCatalogLogout();
        if (catalogServers.Selected < 0 || catalogServers.Selected >= visibleServers.Length)
            throw new InvalidOperationException("Import a server list and select a server first.");
        ApplyInvite(visibleServers[catalogServers.Selected].ToInvite().Encode());
    }
}
