using System.Text.Json;
using OpenD2.Online;

internal static class ConnectionInviteContracts
{
    internal static void Run()
    {
        var invite = new ConnectionInvite { Version = 1, Address = "https://192.168.1.10:5443", Mode = "open", RootCaSha256 = new string('A', 64) };
        string text = invite.Encode();
        if (ConnectionInvite.Parse(text) != invite) throw new Exception("Invite round trip failed.");
        void Reject(string value)
        {
            try { ConnectionInvite.Parse(value); }
            catch (InvalidDataException) { return; }
            throw new Exception("Malformed invite accepted.");
        }
        foreach (string url in new[] { "http://127.0.0.1:5443", "https://0.0.0.0", "https://[::]", "https://user:secret@example.com", "https://example.com/path", "https://example.com?secret=x", "https://example.com#token", "https://example.com:0" })
            Reject(JsonSerializer.Serialize(invite with { Address = url }));
        Reject(JsonSerializer.Serialize(invite with { Version = 2 }));
        Reject(JsonSerializer.Serialize(invite with { Mode = "unknown" }));
        Reject(JsonSerializer.Serialize(invite with { RootCaSha256 = "bad" }));
        Reject(text.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"));
        Reject(text.Replace("\"Version\":1,", ""));
        Reject(text.Replace("\"Mode\":\"open\"", "\"Mode\":\"open\",\"Password\":\"secret\""));
        Reject("null"); Reject(new string('x', ConnectionInvite.MaxCharacters + 1));
        try { using var missing = invite.CreateClient(null); throw new Exception("Invite accepted missing private CA."); }
        catch (InvalidDataException) { }
        using var system = (invite with { Mode = "realm", RootCaSha256 = null }).CreateClient(null);
        if (system.Mode != "realm" || system.TrustedRootSha256 is not null) throw new Exception("System-trust invite changed mode/trust.");
        Console.WriteLine("INVITE PASS bounded strict metadata, HTTPS destination, mode and selected CA requirements");
    }
    internal static async Task VerifyTls(string url, string ca, string otherCa, string hash)
    {
        var invite = new ConnectionInvite { Version = 1, Address = url, Mode = "realm", RootCaSha256 = hash.ToLowerInvariant() };
        using (var client = ConnectionInvite.Parse(invite.Encode()).CreateClient(ca)) await client.CheckServer();
        foreach (var pair in new[] { (invite, otherCa), (invite with { RootCaSha256 = null }, ca) })
        {
            try { using var wrong = pair.Item1.CreateClient(pair.Item2); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Invite accepted mismatched trust.");
        }
        Console.WriteLine("INVITE TLS PASS matching CA and production health; wrong CA and system/private trust mismatch rejected");
    }
}
