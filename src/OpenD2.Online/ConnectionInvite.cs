using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Online;

// Versions 1/2 are public locators. Version 3 carries a single-use room admission secret.
public sealed record ConnectionInvite
{
    public required int Version { get; init; }
    public required string Address { get; init; }
    public required string Mode { get; init; }
    public string? RootCaSha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? Room { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Invitation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? ExpiresAt { get; init; }
    public override string ToString() => $"Connection invite v{Version} ({Mode}, room {Room}); credentials omitted";
    public const int MaxCharacters = 4096;
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 4 };

    public void Validate()
    {
        if (Version is not (1 or 2 or 3) || Mode is not ("open" or "realm")) throw new InvalidDataException("Unsupported connection invite version or mode.");
        if ((Version == 1 && Room is not null) || (Version >= 2 && (Room is null || Room == Guid.Empty)))
            throw new InvalidDataException("Room invites require version 2 or 3 and a nonempty room ID.");
        if (Version == 3 ? Invitation is null || Invitation.Length != 64 || !Invitation.All(Uri.IsHexDigit) || ExpiresAt is null
            : Invitation is not null || ExpiresAt is not null)
            throw new InvalidDataException("Timed invites require version 3, a token and an expiry.");
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
