using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenD2.Online;

internal static class CatalogDownloadContracts
{
    internal static async Task Run(string fixtures, string password)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(Path.Combine(fixtures, "valid.pfx"), password)));
        await using var app = builder.Build();
        var catalog = new ServerCatalog(1, [new("Operator realm", "Test", "https://example.invalid", "realm")]);
        string json = JsonSerializer.Serialize(catalog);
        bool credentials = false; int targets = 0;
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("Authorization") || context.Request.Headers.ContainsKey("Cookie")) credentials = true;
            await next(context);
        });
        app.MapGet("/catalog.json", () => Results.Text(json, "application/json"));
        app.MapGet("/target", () => { Interlocked.Increment(ref targets); return Results.Text(json, "application/json"); });
        app.MapGet("/redirect", () => Results.Redirect("/target"));
        app.MapGet("/bad", () => Results.Text("{bad json}", "application/json"));
        app.MapGet("/secret", () => Results.Text(json.Replace("\"Name\":", "\"Password\":\"secret\",\"Name\":"), "application/json"));
        app.MapGet("/html", () => Results.Text(json, "text/html"));
        app.MapGet("/large", () => Results.Text(new string(' ', ServerCatalog.MaxBytes + 1), "application/json"));
        app.MapGet("/chunked", async context =>
        {
            context.Response.ContentType = "application/json"; await context.Response.StartAsync();
            await context.Response.Body.WriteAsync(new byte[ServerCatalog.MaxBytes + 1], context.RequestAborted);
        });
        app.MapGet("/slow", async context =>
        {
            context.Response.ContentType = "application/json"; await context.Response.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        await app.StartAsync();
        string origin = app.Urls.Single(), ca = Path.Combine(fixtures, "ca.pem");
        async Task Reject<T>(string address, string? root = null, CancellationToken cancellation = default) where T : Exception
        {
            try { await ServerCatalog.Download(address, root, cancellation); }
            catch (T) { return; }
            throw new Exception("Catalog download should reject " + typeof(T).Name);
        }
        try
        {
            var result = await ServerCatalog.Download(origin + "/catalog.json", ca);
            if (!result.Servers.SequenceEqual(catalog.Servers)) throw new Exception("TLS catalog changed metadata.");
            await Reject<HttpRequestException>(origin + "/catalog.json");
            await Reject<HttpRequestException>(origin + "/catalog.json", Path.Combine(fixtures, "other-ca.pem"));
            await Reject<InvalidDataException>(origin + "/catalog.json", Path.Combine(fixtures, "leaf.pem"));
            foreach (string path in new[] { "/bad", "/secret", "/html", "/large", "/chunked" })
                await Reject<InvalidDataException>(origin + path, ca);
            await Reject<HttpRequestException>(origin + "/redirect", ca);
            await Reject<HttpRequestException>(origin + "/missing", ca);
            foreach (string url in new[] { "http://127.0.0.1/list", "https://user:secret@example.invalid/list", origin + "/catalog.json?token=x",
                origin + "/catalog.json#fragment", "https://0.0.0.0/list", "https://[::]/list" })
                await Reject<InvalidDataException>(url, ca);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Reject<OperationCanceledException>(origin + "/slow", ca, cancel.Token);
            await Reject<OperationCanceledException>(origin + "/slow", ca); // whole-body deadline, not only response headers
            if (credentials || targets != 0) throw new Exception("Catalog leaked credentials or followed a redirect.");
            Console.WriteLine("CATALOG DOWNLOAD TLS PASS trust/schema/size/chunked/cancel/body-deadline/no-redirect/no-credentials");
        }
        finally { await app.StopAsync(); }
    }
}
