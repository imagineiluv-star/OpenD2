namespace OpenD2.Online;

public sealed partial class OnlineClient
{
    // Only ServerCatalog.Download uses this on a fresh connection without a login token.
    internal async Task<ServerCatalog> ReadCatalog(string address, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new HttpRequestException("Catalog download rejected: " + (int)response.StatusCode, null, response.StatusCode);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) || response.Content.Headers.ContentEncoding.Count != 0)
            throw new InvalidDataException("Catalog response must be uncompressed application/json.");
        if (response.Content.Headers.ContentLength > ServerCatalog.MaxBytes) throw new InvalidDataException("Server catalog is too large.");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        byte[] bytes = new byte[ServerCatalog.MaxBytes + 1];
        int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return ServerCatalog.Parse(bytes.AsSpan(0, count));
    }
}
