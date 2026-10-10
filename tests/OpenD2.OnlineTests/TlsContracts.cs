using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OpenD2.Online;

internal static class TlsContracts
{
    internal static async Task Run(string server, string fixtures, string output)
    {
        Directory.CreateDirectory(fixtures);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixtures, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string password = Environment.GetEnvironmentVariable("OPEND2_TEST_PFX_PASSWORD") ?? throw new InvalidOperationException("Test certificate password is required.");
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        using var root = Root(rootKey, "OpenD2 ephemeral test CA", now);
        File.WriteAllText(Path.Combine(fixtures, "ca.pem"), root.ExportCertificatePem());
        using var otherKey = RSA.Create(2048);
        using var other = Root(otherKey, "Unrelated ephemeral CA", now);
        File.WriteAllText(Path.Combine(fixtures, "other-ca.pem"), other.ExportCertificatePem());
        foreach (string name in new[] { "valid", "wrong-host", "expired" })
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=OpenD2 TLS validation", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            var names = new SubjectAlternativeNameBuilder();
            if (name == "wrong-host") names.AddDnsName("wrong.opend2.invalid");
            else { names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost"); }
            request.CertificateExtensions.Add(names.Build());
            using var issued = request.Create(root, now.AddDays(-2), name == "expired" ? now.AddDays(-1) : now.AddHours(12), RandomNumberGenerator.GetBytes(16));
            using var leaf = issued.CopyWithPrivateKey(key);
            File.WriteAllBytes(Path.Combine(fixtures, name + ".pfx"), leaf.Export(X509ContentType.Pfx, password));
            if (name == "valid") File.WriteAllText(Path.Combine(fixtures, "leaf.pem"), leaf.ExportCertificatePem());
        }
        var checks = new List<string>();
        void Pass(string name) { checks.Add(name); Console.WriteLine("TLS PASS " + name); }
        string ca = Path.Combine(fixtures, "ca.pem");
        foreach (string name in new[] { "valid", "wrong-host", "expired" })
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            string url = "https://127.0.0.1:" + port;
            var start = new ProcessStartInfo(server) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--urls", url, "--data", Path.Combine(fixtures, "data-" + name) }) start.ArgumentList.Add(arg);
            start.Environment["Kestrel__Certificates__Default__Path"] = Path.Combine(fixtures, name + ".pfx");
            start.Environment["Kestrel__Certificates__Default__Password"] = password;
            // Validate the self-contained executable without any SDK/runtime search paths.
            foreach (string key in start.Environment.Keys.Where(k => k.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) || k.Equals("DOTNET_HOST_PATH", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Server did not start.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            try
            {
                bool listening = false;
                for (int i = 0; i < 200; i++)
                {
                    if (process.HasExited) throw new InvalidOperationException("TLS server exited: " + await stderr + await stdout);
                    try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port); listening = true; break; }
                    catch (SocketException) { await Task.Delay(100); }
                }
                if (!listening) throw new TimeoutException("TLS server startup.");
                if (name == "valid")
                {
                    using var client = new OnlineClient(url, ca);
                    await client.CheckServer();
                    if (client.TrustedRootSha256 != root.GetCertHashString(HashAlgorithmName.SHA256)) throw new Exception("CA fingerprint mismatch.");
                    await client.Login(new("tls-player", Convert.ToHexString(RandomNumberGenerator.GetBytes(16))), true);
                    var character = await client.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("TLSHero"));
                    var room = await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, "TLSRoom", ""));
                    if (!room.Host || room.Started || room.Actor != 1 || room.Members.Length != 1 || room.Members[0].Character != character.Id)
                        throw new Exception("TLS room ownership.");
                    await client.Send<object>(HttpMethod.Post, "v1/rooms/" + room.Id + "/close");
                    Pass("trusted TLS health, login, character and room via production OnlineClient");
                    await RejectTls(url, null); Pass("untrusted certificate rejected with system trust");
                    await RejectTls(url, Path.Combine(fixtures, "other-ca.pem")); Pass("wrong private CA rejected");
                    try { using var bad = new OnlineClient(url, Path.Combine(fixtures, "leaf.pem")); throw new Exception("Leaf accepted as CA."); }
                    catch (InvalidDataException) { Pass("leaf certificate rejected as root CA"); }
                    try { using var bad = new OnlineClient("http://example.invalid"); throw new Exception("Remote HTTP accepted."); }
                    catch (ArgumentException) { Pass("remote plaintext address rejected"); }
                    try { using var bad = new OnlineClient("http://127.0.0.1", ca); throw new Exception("CA with plaintext accepted."); }
                    catch (ArgumentException) { Pass("private CA cannot enable plaintext"); }
                    await StreamContracts.Run(url, ca, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "stream-contracts.json"));
                }
                else { await RejectTls(url, ca); Pass(name + " certificate rejected"); }
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await Task.WhenAll(stdout, stderr);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { result = "PASS", checks,
            scope = "extracted-server-and-production-dotnet-client-loopback-tls", manual_gui = "NOT_RUN", physical_multi_pc = "NOT_RUN",
            system_trust_modified = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    private static X509Certificate2 Root(RSA key, string name, DateTimeOffset now)
    {
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(now.AddDays(-3), now.AddDays(1));
    }
    private static async Task RejectTls(string url, string? ca)
    {
        using var client = new OnlineClient(url, ca);
        try { await client.CheckServer(); }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.SecureConnectionError) { return; }
        throw new Exception("Expected certificate validation failure.");
    }
}
