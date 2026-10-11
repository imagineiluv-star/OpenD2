using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private ConnectionInvite? connectionInvite;
    private void AddInviteControls()
    {
        var row = new HFlowContainer(); AddChild(row);
        var input = new LineEdit { PlaceholderText = "Paste connection invite", MaxLength = ConnectionInvite.MaxCharacters,
            SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(input);
        Button(row, "Apply invite", () => { ApplyInvite(input.Text); input.Text = ""; return Task.CompletedTask; });
        Button(row, "Clear invite", () =>
        {
            if (signedIn) throw new InvalidOperationException("Log out before changing connection settings.");
            connectionInvite = null; status.Text = "Invite cleared. Check the address, mode and certificate trust before connecting.";
            return Task.CompletedTask;
        });
    }
    private void ApplyInvite(string text)
    {
        if (signedIn) throw new InvalidOperationException("Log out before applying a connection invite.");
        var invite = ConnectionInvite.Parse(text); // Validate before changing any controls.
        connectionInvite = invite; address.Text = invite.Address; serverMode.Select(invite.Mode == "open" ? 1 : 0);
        caFile = ""; password.Text = ""; roomPassword.Text = "";
        trust.Text = invite.RootCaSha256 is { } hash ? "Invite CA SHA-256: " + hash + ". Choose the matching public root CA and verify with the host separately." : "Invite trust: system certificates";
        status.Text = "Invite applied: " + invite.Address + " | " + invite.Mode + ". Review the destination, then Check server. No connection has been made. A LAN address still requires access to that network.";
    }
    private OnlineClient CreateConnection()
    {
        if (connectionInvite is not { } invite) return new OnlineClient(address.Text, caFile, SelectedMode);
        if (!Uri.TryCreate(address.Text, UriKind.Absolute, out var current) || current != new Uri(invite.Address) || SelectedMode != invite.Mode)
            throw new InvalidOperationException("Address or mode differs from the invite. Restore them or clear the invite before connecting.");
        return invite.CreateClient(caFile);
    }
    private string PersonalHostInvite()
    {
        if (!personalHost.Running || personalHost.Address is not { } url || !url.StartsWith("https://", StringComparison.Ordinal))
            throw new InvalidOperationException("Start a LAN host first.");
        return new ConnectionInvite { Version = 1, Address = url, Mode = "open", RootCaSha256 = personalHost.TrustedRootSha256 }.Encode();
    }
}
