using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private void CheckCatalogControls(string folder, string inviteText)
    {
        var host = ConnectionInvite.Parse(inviteText);
        var catalog = new ServerCatalog(1, [new("Test host", "Local", host.Address, host.Mode, host.RootCaSha256),
            new("Other host", "Other", "https://unreachable.example.invalid", "realm")]);
        string source = Path.Combine(folder, "catalog-source.json"), saved = Path.Combine(folder, "catalog-saved.json");
        string oldAddress = address.Text, oldCa = caFile;
        var oldInvite = connectionInvite;
        catalog.Save(source); ImportCatalog(source, saved);
        if (address.Text != oldAddress || caFile != oldCa || connectionInvite != oldInvite || signedIn)
            throw new InvalidDataException("Import changed connection settings.");
        SetCatalog(ServerCatalog.Load(saved)); // Simulate a new application loading its saved list.
        catalogRegions.Select(2); FilterCatalog();
        if (visibleServers.Length != 1 || visibleServers[0].Region != "Other" || address.Text != oldAddress)
            throw new InvalidDataException("Region filter changed destination or selected the wrong servers.");
        catalogRegions.Select(1); FilterCatalog();
        password.Text = "must-be-cleared"; roomPassword.Text = "must-be-cleared";
        ApplyCatalogSelection();
        if (address.Text != host.Address || SelectedMode != host.Mode || caFile.Length != 0 || password.Text.Length != 0 || roomPassword.Text.Length != 0 ||
            connectionInvite is null || connectionInvite.RootCaSha256 != host.RootCaSha256 || connectionInvite.Room is not null || connectionInvite.Invitation is not null)
            throw new InvalidDataException("Catalog selection failed to bind a clean connection.");
        var selected = connectionInvite;
        File.WriteAllText(source, "{bad json}");
        try { ImportCatalog(source, saved); throw new Exception("Malformed catalog imported."); } catch (InvalidDataException) { }
        if (connectionInvite != selected || visibleServers.Single().Region != "Local" || ServerCatalog.Load(saved).Servers.Length != 2)
            throw new InvalidDataException("Failed import damaged the current selection or saved list.");
        catalog.Save(source);
        string blocked = Path.Combine(folder, "blocked-catalog"); Directory.CreateDirectory(blocked);
        try { ImportCatalog(source, blocked); throw new Exception("Failed catalog write ignored."); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (connectionInvite != selected || visibleServers.Single().Region != "Local") throw new InvalidDataException("Failed storage changed UI.");
    }
    private async Task CheckCatalogDownloadControls(string folder, string fixtures)
    {
        var original = serverCatalog; var invite = connectionInvite;
        string gameCa = caFile, current = address.Text;
        string saved = Path.Combine(folder, "catalog-saved.json"), content = File.ReadAllText(saved);
        catalogUrl.Text = personalHost.Address + "/health";
        catalogCaFile = Path.Combine(fixtures, "other-ca.pem");
        try { await DownloadCatalog(saved); throw new Exception("Wrong download CA accepted."); }
        catch (System.Net.Http.HttpRequestException) { }
        catalogCaFile = Path.Combine(fixtures, "ca.pem");
        try { await DownloadCatalog(saved); throw new Exception("Health response accepted as server list."); }
        catch (InvalidDataException) { }
        if (serverCatalog != original || connectionInvite != invite || caFile != gameCa || address.Text != current || File.ReadAllText(saved) != content)
            throw new InvalidDataException("Failed download changed stored list, selection or game trust.");
        GD.Print("OPEND2_CATALOG_DOWNLOAD_UI_PASS TLS_FAILURE_PRESERVATION DIALOG_GESTURES_NOT_RUN");
    }
    private async Task CheckSignedInCatalogControls()
    {
        string current = address.Text;
        try { ApplyCatalogSelection(); throw new Exception("Signed-in catalog selection allowed."); } catch (InvalidOperationException) { }
        try { ImportCatalog("unused", "unused"); throw new Exception("Signed-in catalog import allowed."); } catch (InvalidOperationException) { }
        try { await DownloadCatalog("unused"); throw new Exception("Signed-in catalog download allowed."); } catch (InvalidOperationException) { }
        if (!signedIn || address.Text != current) throw new InvalidDataException("Catalog changed the active session.");
        GD.Print("OPEND2_SERVER_CATALOG_UI_PASS FILE_DIALOG_GESTURE_NOT_RUN");
    }
}
