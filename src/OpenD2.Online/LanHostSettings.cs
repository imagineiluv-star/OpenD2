using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenD2.Online;

// Sent once over the owned child's stdin, never arguments, environment or disk.
public sealed class LanHostSettings
{
    public required string BindAddress { get; init; }
    public int Port { get; init; } = 5443;
    public required string CertificateFile { get; init; }
    public required string CertificatePassword { get; init; }
    [JsonIgnore] public string? RootCaFile { get; init; }
    public const int MaxLaunchCharacters = 32768;
    public static string[] LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString()).Distinct().Order().ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }
    public IPAddress Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            (!IPAddress.IsLoopback(ip) && !LocalAddresses().Contains(ip.ToString())))
            throw new ArgumentException("Choose an IPv4 address assigned to this PC.");
        if (Port is < 0 or > 65535) throw new ArgumentException("Port must be 0 (automatic) through 65535.");
        if (string.IsNullOrEmpty(CertificateFile) || CertificateFile.Length > 4096 || CertificatePassword is null || CertificatePassword.Length > 1024)
            throw new ArgumentException("Select a PFX file and a password of at most 1024 characters.");
        return ip;
    }
    public X509Certificate2 LoadCertificate()
    {
        var ip = Validate();
        if (new FileInfo(CertificateFile).Length > 1024 * 1024) throw new InvalidDataException("PFX exceeds 1 MiB.");
        // macOS does not support EphemeralKeySet for PFX loading; the runtime owns
        // its temporary keychain. Never request PersistKeySet or install a trust root.
        var storage = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(CertificateFile, CertificatePassword, storage);
        try
        {
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now ||
                !certificate.MatchesHostname(ip.ToString(), allowWildcards: false, allowCommonName: false) ||
                !certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")))
                throw new InvalidDataException("PFX must contain a valid server certificate/private key with the selected IP in its SAN.");
            return certificate;
        }
        catch { certificate.Dispose(); throw; }
    }
    public string LaunchJson()
    {
        Validate();
        string json = JsonSerializer.Serialize(this);
        if (json.Length > MaxLaunchCharacters) throw new InvalidDataException("Host settings exceed launch budget.");
        return json;
    }
    public static LanHostSettings ReadLaunch(TextReader reader)
    {
        var text = new System.Text.StringBuilder();
        while (true)
        {
            int next = reader.Read();
            if (next < 0) throw new EndOfStreamException("Host settings pipe closed.");
            if (next == '\n') break;
            if (text.Length == MaxLaunchCharacters) throw new InvalidDataException("Host settings exceed launch budget.");
            text.Append((char)next);
        }
        return JsonSerializer.Deserialize<LanHostSettings>(text.ToString(), new JsonSerializerOptions
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 4 })
            ?? throw new InvalidDataException("Missing LAN host settings.");
    }
}
