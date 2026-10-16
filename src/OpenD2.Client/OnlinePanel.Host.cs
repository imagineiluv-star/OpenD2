using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly PersonalHost personalHost = new() { Diagnostic = message => GD.PushWarning(message) };
    private readonly LineEdit hostExecutable = new() { PlaceholderText = "Extracted OpenD2.Server executable", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Label hostStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private bool hostWasRunning;
    private void AddHostControls()
    {
        var row = new HFlowContainer(); AddChild(row); row.AddChild(hostExecutable);
        var picker = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
            Title = "Select OpenD2.Server from the extracted server package" }; AddChild(picker);
        picker.FileSelected += path => hostExecutable.Text = path;
        Button(row, "Choose server", () => { picker.PopupCenteredRatio(0.7f); return Task.CompletedTask; });
        Button(row, "Start personal host", StartPersonalHost);
        Confirm(row, "Stop personal host", "Save and stop your personal host? Its connected sessions will end. Saved rooms remain available after restart.", StopPersonalHost);
        AddLanHostControls();
        AddChild(hostStatus);
        hostStatus.Text = "Personal host: stopped. Default: this PC only. Enable LAN hosting for a selected local IPv4 address.";
    }
    private Task StartPersonalHost() => StartPersonalHostAt(ProjectSettings.GlobalizePath("user://personal-host/open-data"));
    private async Task StartPersonalHostAt(string data)
    {
        if (signedIn) throw new InvalidOperationException("Log out before starting your personal host.");
        var lan = ReadLanHostSettings();
        status.Text = "Starting personal host…";
        await personalHost.Start(hostExecutable.Text, data, lan, lifetime.Token);
        connectionInvite = null; serverMode.Select(1); address.Text = personalHost.Address!; caFile = lan?.RootCaFile ?? "";
        trust.Text = personalHost.TrustedRootSha256 is { } hash ? "Private CA SHA-256: " + hash : lan is null ? "Trust: local personal host (loopback)" : "Trust: system certificates";
        hostWasRunning = true;
        hostStatus.Text = "Personal host running — " + personalHost.Address + " | Data: " + data;
        status.Text = "Host ready. Register or log in, then choose a character and room. Closing this client stops the host.";
    }
    private async Task StopPersonalHost()
    {
        string? ownedAddress = personalHost.Address;
        bool clean = await personalHost.Stop();
        if (signedIn && ConnectedTo(ownedAddress)) Reset();
        hostWasRunning = false;
        hostStatus.Text = clean ? "Personal host stopped; saved data is retained." : "Personal host stopped unexpectedly or required force-stop. Latest progress may not be saved.";
        status.Text = hostStatus.Text;
    }
    private async Task CheckPersonalHostControls(string executable, string folder)
    {
        hostLan.ButtonPressed = false;
        hostExecutable.Text = executable;
        string secret = Guid.NewGuid().ToString("N");
        try
        {
            await StartPersonalHostAt(folder);
            if (SelectedMode != "open" || address.Text != personalHost.Address || caFile.Length != 0) throw new InvalidDataException("Host connection binding failed.");
            username.Text = "hostuitest"; password.Text = secret;
            await Login(true);
            await StopPersonalHost();
            if (signedIn || serverMode.Disabled || personalHost.Running) throw new InvalidDataException("Host stop did not clear owned session.");
            await StartPersonalHostAt(folder);
            password.Text = secret; await Login(false);
            await StopPersonalHost();
            GD.Print("OPEND2_PERSONAL_HOST_UI_PASS DIALOG_GESTURES_NOT_RUN");
        }
        finally { await personalHost.Stop(); Reset(); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private bool ConnectedTo(string? target) => connectedAddress is not null && target is not null && new Uri(connectedAddress) == new Uri(target);
    private void UpdateHostStatus()
    {
        if (!hostWasRunning || personalHost.Running) return;
        hostWasRunning = false;
        if (signedIn && ConnectedTo(personalHost.Address)) Reset();
        hostStatus.Text = "Personal host exited unexpectedly. Restart it to reconnect to the last saved checkpoint.";
    }
}
