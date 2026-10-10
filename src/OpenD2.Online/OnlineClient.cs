using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenD2.Online;

// No credential/token persistence, redirects or certificate validation bypass.
public sealed class OnlineClient : IDisposable
{
    private readonly HttpClient http;
    private readonly X509Certificate2? trustedRoot;
    public string? TrustedRootSha256 { get; }
    public OnlineClient(string address, string? caFile = null)
    {
        var uri = new Uri(address, UriKind.Absolute);
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new ArgumentException("Use HTTPS, or HTTP on loopback only.");
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        try
        {
            if (!string.IsNullOrWhiteSpace(caFile))
            {
                if (uri.Scheme != "https") throw new ArgumentException("A private CA requires HTTPS.");
                if (new FileInfo(caFile).Length > 65536) throw new InvalidDataException("CA file exceeds 64 KiB.");
                string pem = File.ReadAllText(caFile);
                if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal) || pem.Split("-----BEGIN CERTIFICATE-----").Length != 2)
                    throw new InvalidDataException("Select one public CA certificate, without a private key.");
                trustedRoot = X509Certificate2.CreateFromPem(pem);
                var constraints = trustedRoot.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
                var usage = trustedRoot.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
                if (constraints?.CertificateAuthority != true || usage is null || (usage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0 ||
                    !trustedRoot.SubjectName.RawData.SequenceEqual(trustedRoot.IssuerName.RawData))
                    throw new InvalidDataException("Select a self-issued root CA with certificate-signing usage.");
                var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
                policy.CustomTrustStore.Add(trustedRoot);
                policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // TLS server authentication
                handler.SslOptions.CertificateChainPolicy = policy;
                TrustedRootSha256 = trustedRoot.GetCertHashString(HashAlgorithmName.SHA256);
            }
            http = new(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        }
        catch { handler.Dispose(); trustedRoot?.Dispose(); throw; }
    }
    public async Task<T> Send<T>(HttpMethod method, string path, object? body = null, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            ApiError? error = null;
            try { error = await response.Content.ReadFromJsonAsync<ApiError>(cancellation); } catch (JsonException) { }
            throw new HttpRequestException(error?.Error ?? $"Server response {(int)response.StatusCode}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw new InvalidDataException("Empty server response.");
    }
    public async Task Login(Credentials credentials, bool register, CancellationToken cancellation = default)
    {
        await CheckServer(cancellation);
        var login = await Send<LoginResult>(HttpMethod.Post, register ? "v1/register" : "v1/login", credentials, cancellation);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
    }
    public async Task CheckServer(CancellationToken cancellation = default)
    {
        var health = await Send<ServerInfo>(HttpMethod.Get, "health", cancellation: cancellation);
        if (health.Protocol != 1 || health.Rules != OpenD2.Core.GameSimulation.RulesVersion)
            throw new InvalidDataException("Server protocol or game rules do not match this client.");
    }
    public void Dispose() { http.Dispose(); trustedRoot?.Dispose(); }
}
