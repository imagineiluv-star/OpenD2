using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Online;

// Public connection metadata only. An invite grants neither trust nor room access.
public sealed record ConnectionInvite
{
    public required int Version { get; init; }
    public required string Address { get; init; }
    public required string Mode { get; init; }
    public string? RootCaSha256 { get; init; }
    public const int MaxCharacters = 4096;
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 4 };

    public void Validate()
    {
        if (Version != 1 || Mode is not ("open" or "realm")) throw new InvalidDataException("Unsupported connection invite version or mode.");
        if (Address is null || Address.Length > 2048 || !Uri.TryCreate(Address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" || uri.Port <= 0 || string.IsNullOrEmpty(uri.Host) ||
            (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))))
            throw new InvalidDataException("Invite must contain a destination HTTPS address without credentials, query or path.");
        if (RootCaSha256 is not null && (RootCaSha256.Length != 64 || !RootCaSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invite CA fingerprint must be a SHA-256 hex value.");
    }
    public string Encode()
    {
        Validate();
        string text = JsonSerializer.Serialize(this, Json);
        if (text.Length > MaxCharacters) throw new InvalidDataException("Connection invite is too large.");
        return text;
    }
    public static ConnectionInvite Parse(string text)
    {
        if (text is null || text.Length > MaxCharacters) throw new InvalidDataException("Connection invite is too large.");
        try
        {
            var invite = JsonSerializer.Deserialize<ConnectionInvite>(text, Json) ?? throw new InvalidDataException("Missing connection invite.");
            invite.Validate(); return invite;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid connection invite format."); }
    }
    public OnlineClient CreateClient(string? caFile)
    {
        Validate();
        var client = new OnlineClient(Address, caFile, Mode);
        if (!string.Equals(RootCaSha256, client.TrustedRootSha256, StringComparison.OrdinalIgnoreCase))
        {
            client.Dispose();
            throw new InvalidDataException(RootCaSha256 is null ? "This invite requires system certificate trust. Clear the private CA selection." :
                "Selected public CA does not match the invite fingerprint. Obtain the public CA and verify its SHA-256 with the host separately.");
        }
        return client;
    }
}
