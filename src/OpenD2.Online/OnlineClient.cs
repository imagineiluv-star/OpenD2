using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenD2.Online;

// No credential/token persistence, redirects or certificate validation bypass.
public sealed class OnlineClient : IDisposable
{
    private readonly HttpClient http;
    public OnlineClient(string address)
    {
        var uri = new Uri(address, UriKind.Absolute);
        if (uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new ArgumentException("Use HTTPS, or HTTP on loopback only.");
        http = new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
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
        var login = await Send<LoginResult>(HttpMethod.Post, register ? "v1/register" : "v1/login", credentials, cancellation);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
    }
    public void Dispose() => http.Dispose();
}
