using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly CheckButton hostLan = new() { Text = "LAN hosting (HTTPS)" };
    private readonly LineEdit hostIp = new() { PlaceholderText = "This PC's LAN IPv4 address" };
    private readonly SpinBox hostPort = new() { MinValue = 0, MaxValue = 65535, Value = 5443, Step = 1 };
    private readonly LineEdit hostPfx = new() { PlaceholderText = "Server PFX certificate", Editable = false };
    private readonly LineEdit hostPfxPassword = new() { PlaceholderText = "PFX password (not saved)", Secret = true, MaxLength = 1024 };
    private readonly LineEdit hostCa = new() { PlaceholderText = "Public root CA (empty = system trust)", Editable = false };
    private void AddLanHostControls()
    {
        AddChild(hostLan);
        var settings = new VBoxContainer { Visible = false }; AddChild(settings);
        hostLan.Toggled += enabled => settings.Visible = enabled;
        var binding = new HFlowContainer(); settings.AddChild(binding);
        binding.AddChild(hostIp); binding.AddChild(new Label { Text = "Port (0 = automatic)" }); binding.AddChild(hostPort);
        var ips = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; settings.AddChild(ips);
        void RefreshAddresses()
        {
            var addresses = LanHostSettings.LocalAddresses();
            ips.Text = "Local IPv4: " + (addresses.Length == 0 ? "none found" : string.Join(", ", addresses));
            if (hostIp.Text.Length == 0 && addresses.Length > 0) hostIp.Text = addresses[0];
        }
        Button(binding, "Refresh IPs", () => { RefreshAddresses(); return Task.CompletedTask; });
        RefreshAddresses();
        var files = new HFlowContainer(); settings.AddChild(files);
        files.AddChild(hostPfx); files.AddChild(hostPfxPassword);
        AddHostFilePicker(files, "Choose server PFX", "*.pfx,*.p12 ; Server certificate with private key", hostPfx);
        var roots = new HFlowContainer(); settings.AddChild(roots); roots.AddChild(hostCa);
        AddHostFilePicker(roots, "Choose public root CA", "*.pem,*.crt ; Public root CA certificate", hostCa);
        Button(roots, "Use system trust", () => { hostCa.Text = ""; return Task.CompletedTask; });
        Button(settings, "Copy connection invite", () =>
        {
            DisplayServer.ClipboardSet(PersonalHostInvite());
            status.Text = "Invite copied. Share only the public CA, never the PFX or its password. Guests paste it into Apply invite.";
            return Task.CompletedTask;
        });
        settings.AddChild(new Label { Text = "The PFX must cover the selected IP. Guests use Open mode, this HTTPS address and the public CA. Allow the selected TCP port in your firewall if needed. Router/NAT settings are not changed.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
    }
    private void AddHostFilePicker(Node row, string title, string filter, LineEdit target)
    {
        var dialog = new FileDialog { Title = title, FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, Filters = [filter] }; AddChild(dialog);
        dialog.FileSelected += path => target.Text = path;
        Button(row, title, () => { dialog.PopupCenteredRatio(0.7f); return Task.CompletedTask; });
    }
    private LanHostSettings? ReadLanHostSettings()
    {
        string secret = hostPfxPassword.Text; hostPfxPassword.Text = "";
        return hostLan.ButtonPressed ? new LanHostSettings { BindAddress = hostIp.Text.Trim(), Port = (int)hostPort.Value,
            CertificateFile = hostPfx.Text, CertificatePassword = secret, RootCaFile = hostCa.Text } : null;
    }
    private async Task CheckLanHostControls(string executable, string folder, string fixtures)
    {
        hostExecutable.Text = executable; hostLan.ButtonPressed = true; hostIp.Text = "127.0.0.1"; hostPort.Value = 0;
        hostPfx.Text = Path.Combine(fixtures, "valid.pfx"); hostCa.Text = Path.Combine(fixtures, "ca.pem");
        hostPfxPassword.Text = System.Environment.GetEnvironmentVariable("OPEND2_TEST_PFX_PASSWORD") ?? throw new InvalidDataException("Missing test PFX password.");
        try
        {
            await StartPersonalHostAt(folder);
            if (SelectedMode != "open" || !address.Text.StartsWith("https://127.0.0.1:", StringComparison.Ordinal) || caFile != hostCa.Text || hostPfxPassword.Text.Length != 0 || personalHost.TrustedRootSha256 is null)
                throw new InvalidDataException("LAN host TLS connection binding failed.");
            string inviteText = PersonalHostInvite();
            CheckCatalogControls(folder, inviteText);
            await CheckCatalogDownloadControls(folder, fixtures);
            if (connectionInvite?.Mode != "open" || caFile.Length != 0 || connectionInvite.RootCaSha256 != personalHost.TrustedRootSha256)
                throw new InvalidDataException("Invite UI binding failed.");
            try { using var missingCa = CreateConnection(); throw new Exception("Invite accepted missing CA."); }
            catch (InvalidDataException) { }
            try { ApplyInvite("{bad json}"); throw new Exception("Malformed invite accepted."); }
            catch (InvalidDataException) { }
            if (address.Text != personalHost.Address || connectionInvite.RootCaSha256 != personalHost.TrustedRootSha256)
                throw new InvalidDataException("Malformed invite changed connection settings.");
            serverMode.Select(0);
            try { using var changed = CreateConnection(); throw new Exception("Edited invite mode accepted."); }
            catch (InvalidOperationException) { }
            serverMode.Select(1); caFile = hostCa.Text;
            using (var invited = CreateConnection()) await invited.CheckServer(lifetime.Token);
            username.Text = "lanuitest"; password.Text = Guid.NewGuid().ToString("N"); await Login(true);
            await CheckSignedInCatalogControls();
            try { ApplyInvite(inviteText); throw new Exception("Signed-in invite change accepted."); }
            catch (InvalidOperationException) { }
            GD.Print("OPEND2_CONNECTION_INVITE_UI_PASS CLIPBOARD_GESTURE_NOT_RUN");
            var owner = await client!.Send<CharacterInfo>(System.Net.Http.HttpMethod.Post, "v1/characters", new NameRequest("InviteOwner"), lifetime.Token);
            string roomSecret = Guid.NewGuid().ToString("N");
            var invitedRoom = await client.Send<RoomView>(System.Net.Http.HttpMethod.Post, "v1/rooms", new RoomRequest(owner.Id, "InviteUI", roomSecret), lifetime.Token);
            Apply(invitedRoom);
            string roomInvite = CurrentRoomInvite();
            string timedInvite = await CreateTimedRoomInvite();
            Reset(); ApplyInvite(roomInvite); caFile = hostCa.Text;
            string guestPassword = Guid.NewGuid().ToString("N");
            username.Text = "inviteuitest"; password.Text = guestPassword; await Login(true);
            var guest = await client!.Send<CharacterInfo>(System.Net.Http.HttpMethod.Post, "v1/characters", new NameRequest("InviteGuest"), lifetime.Token);
            await Refresh(); characters.Select(Array.FindIndex(characterList, c => c.Id == guest.Id));
            try { await JoinInvitedRoom(); throw new Exception("Room invite bypassed password."); }
            catch (System.Net.Http.HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Forbidden) { }
            roomPassword.Text = roomSecret; await JoinInvitedRoom();
            if (state?.Id != invitedRoom.Id || state.Actor != 2) throw new InvalidDataException("Room invite UI joined wrong room.");
            GD.Print("OPEND2_ROOM_INVITE_UI_PASS CLIPBOARD_GESTURE_NOT_RUN");
            await client!.Send<object>(System.Net.Http.HttpMethod.Post, $"v1/rooms/{invitedRoom.Id}/leave", cancellation: lifetime.Token);
            Reset(); ApplyInvite(timedInvite); caFile = hostCa.Text; password.Text = guestPassword; await Login(false);
            await JoinInvitedRoom();
            if (state?.Id != invitedRoom.Id || connectionInvite?.Version != 2 || connectionInvite.Invitation is not null || roomPassword.Text.Length != 0)
                throw new InvalidDataException("Timed invite UI admission or secret clearing failed.");
            GD.Print("OPEND2_TIMED_INVITE_UI_PASS CLIPBOARD_GESTURE_NOT_RUN");
            await StopPersonalHost();
            if (signedIn || personalHost.Running) throw new InvalidDataException("LAN host stop failed.");
            GD.Print("OPEND2_LAN_HOST_UI_PASS LOOPBACK_TLS_NOT_PHYSICAL_LAN DIALOG_GESTURES_NOT_RUN");
        }
        finally
        {
            await personalHost.Stop(); Reset(); connectionInvite = null; hostLan.ButtonPressed = false;
            hostPfx.Text = ""; hostCa.Text = ""; hostPfxPassword.Text = "";
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
