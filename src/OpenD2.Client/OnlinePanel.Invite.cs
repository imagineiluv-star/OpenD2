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
        Button(row, "Copy room invite", () => { DisplayServer.ClipboardSet(CurrentRoomInvite()); status.Text = "Room invite copied. Send any room password separately. Guests must log in and choose their own character."; return Task.CompletedTask; });
        Button(row, "Copy 10-minute invite (host)", async () =>
        {
            DisplayServer.ClipboardSet(await CreateTimedRoomInvite());
            status.Text = "Single-use invite copied; valid for 10 minutes. It grants one authenticated player admission without the room password. Share privately. A new invite replaces the previous one.";
        });
        Button(row, "Revoke timed invite (host)", async () =>
        {
            RequireLogin(); if (state is null) throw new InvalidOperationException("Join a room first.");
            await client!.Send<object>(System.Net.Http.HttpMethod.Delete, $"v1/rooms/{state.Id}/invitation", cancellation: lifetime.Token);
            status.Text = "Outstanding timed invite revoked. Existing members stay in the room.";
        });
        Button(row, "Join invited room", JoinInvitedRoom);
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
        status.Text = "Invite applied: " + invite.Address + " | " + invite.Mode + (invite.Room is { } room ? " | Room " + room : "") + (invite.ExpiresAt is { } until ? " | Server expiry " + until.ToString("O") + "; single use, share privately" : "") + ". Review the destination, then Check server. No connection has been made. A LAN address still requires access to that network.";
    }
    private OnlineClient CreateConnection()
    {
        if (connectionInvite is not { } invite) return new OnlineClient(address.Text, caFile, SelectedMode);
        if (!Uri.TryCreate(address.Text, UriKind.Absolute, out var current) || current != new Uri(invite.Address) || SelectedMode != invite.Mode)
            throw new InvalidOperationException("Address or mode differs from the invite. Restore them or clear the invite before connecting.");
        return invite.CreateClient(caFile);
    }
    private string CurrentRoomInvite()
    {
        RequireLogin();
        if (state is null || connectedAddress is null) throw new InvalidOperationException("Join a room first.");
        return new ConnectionInvite { Version = 2, Address = connectedAddress, Mode = client!.Mode,
            RootCaSha256 = client.TrustedRootSha256, Room = state.Id }.Encode();
    }
    private async Task<string> CreateTimedRoomInvite()
    {
        var target = ConnectionInvite.Parse(CurrentRoomInvite()); // HTTPS check before issuing a secret.
        var issued = await Room<RoomInvitation>("invitation");
        return (target with { Version = 3, Invitation = issued.Token, ExpiresAt = issued.ExpiresAt }).Encode();
    }
    private async Task JoinInvitedRoom()
    {
        RequireLogin();
        if (connectionInvite is not { Room: { } room } invite) throw new InvalidOperationException("Apply a room invite before logging in.");
        if (!ConnectedTo(invite.Address) || client!.Mode != invite.Mode ||
            !string.Equals(client.TrustedRootSha256, invite.RootCaSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This session does not match the room invite. Log out and apply it again.");
        Apply(await client!.Send<RoomView>(System.Net.Http.HttpMethod.Post, $"v1/rooms/{room}/join",
            new JoinRequest(SelectedCharacter(), roomPassword.Text, invite.Invitation), lifetime.Token));
        // Successful admission no longer needs the single-use secret for reconnect.
        connectionInvite = invite with { Version = 2, Invitation = null, ExpiresAt = null };
        status.Text = "Joined invited room. Server admission checks passed.";
    }
    private string PersonalHostInvite()
    {
        if (!personalHost.Running || personalHost.Address is not { } url || !url.StartsWith("https://", StringComparison.Ordinal))
            throw new InvalidOperationException("Start a LAN host first.");
        return new ConnectionInvite { Version = 1, Address = url, Mode = "open", RootCaSha256 = personalHost.TrustedRootSha256 }.Encode();
    }
}
