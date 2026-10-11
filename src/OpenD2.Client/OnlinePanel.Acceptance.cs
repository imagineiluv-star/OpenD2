using Godot;
using OpenD2.Online;

namespace OpenD2.Client;

public partial class OnlinePanel
{
    private readonly HashSet<string> acceptanceChecks = new(StringComparer.Ordinal);

    private void Acceptance(string stage)
    {
        acceptanceChecks.Add(stage);
        GD.Print("OPEND2_ACCEPTANCE_PASS " + stage);
    }

    // The harness transfers only public v1/v2 metadata through an atomic file.
    // It never transfers a password, bearer token or private key between clients.
    private static void PublishInvite(string evidence, string name, string invite)
    {
        string path = Path.Combine(evidence, name);
        File.WriteAllText(path + ".tmp", invite);
        File.Move(path + ".tmp", path);
    }

    private async Task<string> ReceiveInvite(string evidence, string name)
    {
        string path = Path.Combine(evidence, name);
        for (int i = 0; i < 300; i++)
        {
            if (File.Exists(path)) return File.ReadAllText(path);
            await Task.Delay(100, lifetime.Token);
        }
        throw new TimeoutException("Host invite was not received.");
    }

    private async Task CheckInviteConnection(string url, string certificate, string mode, string role, string evidence)
    {
        if (role == "host")
        {
            using var host = new OnlineClient(url, certificate, mode);
            PublishInvite(evidence, "connection-invite.json", new ConnectionInvite
            { Version = 1, Address = url, Mode = mode, RootCaSha256 = host.TrustedRootSha256 }.Encode());
            Acceptance("invite_generated");
        }
        string text = await ReceiveInvite(evidence, "connection-invite.json");
        var invite = ConnectionInvite.Parse(text);
        if (new Uri(invite.Address) != new Uri(url) || invite.Mode != mode)
            throw new InvalidDataException("Test invite changed the approved loopback destination or mode.");
        password.Text = "stale-test-value"; caFile = certificate;
        ApplyInvite(text);
        if (connectionInvite != invite || caFile.Length != 0 || password.Text.Length != 0 || signedIn)
            throw new InvalidDataException("Invite did not reset connection settings before login.");
        Acceptance("invite_applied");
        try { using var missing = CreateConnection(); throw new Exception("Missing invite CA accepted."); }
        catch (InvalidDataException) { Acceptance("ca_missing_rejected"); }
        caFile = Path.Combine(Path.GetDirectoryName(certificate)!, "other-ca.pem");
        try { using var wrong = CreateConnection(); throw new Exception("Wrong invite CA accepted."); }
        catch (InvalidDataException) { Acceptance("ca_mismatch_rejected"); }
        caFile = certificate;
        try { ApplyInvite("{broken"); throw new Exception("Malformed invite accepted."); }
        catch (InvalidDataException)
        {
            if (connectionInvite != invite || caFile != certificate || address.Text != invite.Address || SelectedMode != mode)
                throw new InvalidDataException("Malformed invite changed connection settings.");
            Acceptance("malformed_invite_preserves_settings");
        }
        serverMode.Select(mode == "open" ? 0 : 1);
        try { using var changed = CreateConnection(); throw new Exception("Edited invite mode accepted."); }
        catch (InvalidOperationException) { }
        serverMode.Select(mode == "open" ? 1 : 0);
        address.Text = "https://127.0.0.1:1";
        try { using var changed = CreateConnection(); throw new Exception("Edited invite address accepted."); }
        catch (InvalidOperationException) { }
        address.Text = invite.Address;
        Acceptance("invite_edits_rejected");
        await CheckServer(); // Same action bound to the product's Check server button.
        if (!status.Text.Contains(invite.RootCaSha256!, StringComparison.Ordinal))
            throw new InvalidDataException("Validated server CA was not displayed.");
        Acceptance("server_checked");
    }
}
