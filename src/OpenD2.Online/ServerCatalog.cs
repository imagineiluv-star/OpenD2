using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Online;

// Operator-supplied connection metadata, never accounts, credentials or room grants.
public sealed record ServerEntry(string Name, string Region, string Address, string Mode, string? RootCaSha256 = null)
{
    public ConnectionInvite ToInvite() => new() { Version = 1, Address = Address, Mode = Mode, RootCaSha256 = RootCaSha256 };
}

public sealed record ServerCatalog(int Version, ServerEntry[] Servers)
{
    public const int MaxBytes = 128 * 1024, MaxServers = 64;
    private static readonly JsonSerializerOptions Json = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 5, WriteIndented = true };

    public void Validate()
    {
        if (Version != 1 || Servers is null || Servers.Length is < 1 or > MaxServers)
            throw new InvalidDataException("Server catalog requires version 1 and 1–64 servers.");
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in Servers)
        {
            if (server is null || !Label(server.Name, 64) || !Label(server.Region, 32))
                throw new InvalidDataException("Server names and regions must be short, nonempty labels without control characters.");
            server.ToInvite().Validate();
            if (!destinations.Add(server.Mode + "|" + new Uri(server.Address).AbsoluteUri))
                throw new InvalidDataException("Server catalog contains a duplicate address and mode.");
        }
    }
    private static bool Label(string? text, int max) => text is { Length: > 0 } && text.Length <= max && text == text.Trim() &&
        !text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or
            System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator);

    public static ServerCatalog Load(string path)
    {
        using var file = File.OpenRead(path);
        byte[] bytes = new byte[MaxBytes + 1];
        int count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        return Parse(bytes.AsSpan(0, count));
    }
    internal static ServerCatalog Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Server catalog is too large.");
        try
        {
            var catalog = JsonSerializer.Deserialize<ServerCatalog>(bytes, Json) ?? throw new InvalidDataException("Missing server catalog.");
            catalog.Validate(); return catalog;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid server catalog format."); }
    }
    public static async Task<ServerCatalog> Download(string address, string? caFile = null, CancellationToken cancellation = default)
    {
        if (address is null || address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Catalog URL must be HTTPS without credentials, query or fragment.");
        // Reuse the established TLS policy in an isolated, never-authenticated connection.
        string origin = uri.GetLeftPart(UriPartial.Authority);
        new ConnectionInvite { Version = 1, Address = origin, Mode = "realm" }.Validate();
        using var client = new OnlineClient(origin, caFile);
        return await client.ReadCatalog(uri.AbsoluteUri, cancellation);
    }
    public byte[] Encode()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Server catalog is too large.");
        return bytes;
    }
    public void Save(string path)
    {
        byte[] bytes = Encode();
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
