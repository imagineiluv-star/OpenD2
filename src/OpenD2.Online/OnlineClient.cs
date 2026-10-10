using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace OpenD2.Online;

// No credential/token persistence, redirects or certificate validation bypass.
public sealed class OnlineClient : IDisposable
{
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private readonly X509Certificate2? trustedRoot;
    private string? token;
    public string Mode { get; }
    public string? TrustedRootSha256 { get; }
    public string RoomTransport => http.BaseAddress!.Scheme == "https" ? "WSS" : "WS (loopback)";
    public OnlineClient(string address, string? caFile = null, string mode = "realm")
    {
        if (mode is not ("realm" or "open")) throw new ArgumentException("Unknown online mode.", nameof(mode));
        Mode = mode;
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
        catch { handler.Dispose(); trustedRoot?.Dispose(); lifetime.Dispose(); throw; }
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
        token = login.Token;
    }
    public async Task CheckServer(CancellationToken cancellation = default)
    {
        var health = await Send<ServerInfo>(HttpMethod.Get, "health", cancellation: cancellation);
        if (health.Protocol != 1 || health.Rules != OpenD2.Core.GameSimulation.RulesVersion || health.RoomStream != 1)
            throw new InvalidDataException("Server protocol or game rules do not match this client.");
        if (health.Mode != Mode) throw new InvalidDataException("Server mode does not match the selected character mode.");
    }
    public async IAsyncEnumerable<RoomView> WatchRoom(Guid room, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        cancellation = session.Token;
        if (token is null) throw new InvalidOperationException("Login first.");
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("opend2.room.v1");
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
        var uri = new UriBuilder(new Uri(http.BaseAddress!, $"v1/rooms/{room}/stream"))
            { Scheme = http.BaseAddress!.Scheme == "https" ? "wss" : "ws" };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            // Reuse the HTTPS handler, including its per-connection CA/hostname policy.
            try { await socket.ConnectAsync(uri.Uri, http, connect.Token); }
            catch (WebSocketException error) when ((int)socket.HttpStatusCode >= 400)
            { throw new HttpRequestException("Room stream handshake rejected.", error, socket.HttpStatusCode); }
        }
        var buffer = new byte[1024 * 1024];
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 16 };
        while (true)
        {
            int length = 0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(length), deadline.Token);
                if (part.MessageType != WebSocketMessageType.Text) throw new IOException("Room stream closed or sent a non-text message. Reconnect to the room.");
                length += part.Count;
                if (part.EndOfMessage) break;
                if (length == buffer.Length) throw new InvalidDataException("Room update exceeds 1 MiB.");
            }
            var update = JsonSerializer.Deserialize<RoomUpdate>(buffer.AsSpan(0, length), json) ?? throw new InvalidDataException("Empty room update.");
            if (update.Status != 200) throw new HttpRequestException(update.Error ?? "Room access ended.", null, (System.Net.HttpStatusCode)update.Status);
            if (update.State is not { } state || state.Id != room) throw new InvalidDataException("Wrong room update.");
            yield return state;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); http.Dispose(); trustedRoot?.Dispose(); lifetime.Dispose();
    }
}
