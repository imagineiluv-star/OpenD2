using System.Diagnostics;
using System.Net;
using OpenD2.Online;

internal static class CatalogPublishContracts
{
    internal static async Task Verify(string url, string ca, string path, ServerCatalog expected, string mode)
    {
        var downloaded = await ServerCatalog.Download(url + "/v1/server-catalog", ca);
        if (downloaded.Version != expected.Version || !downloaded.Servers.SequenceEqual(expected.Servers))
            throw new Exception("Published catalog did not preserve exact schema and metadata.");
        byte[] original = File.ReadAllBytes(path);
        try
        {
            File.WriteAllText(path, "{\"Password\":\"must-not-be-published\"}");
            var unchanged = await ServerCatalog.Download(url + "/v1/server-catalog", ca);
            if (!unchanged.Servers.SequenceEqual(expected.Servers)) throw new Exception("Publication re-read unvalidated file data.");
            File.Delete(path);
            unchanged = await ServerCatalog.Download(url + "/v1/server-catalog", ca);
            if (!unchanged.Servers.SequenceEqual(expected.Servers)) throw new Exception("Publication depends on per-request disk access.");
        }
        finally { File.WriteAllBytes(path, original); }
        using var client = new OnlineClient(url, ca, mode);
        try { await client.Send<object>(HttpMethod.Post, "v1/server-catalog", new { Name = "overwrite" }); throw new Exception("Catalog write endpoint exists."); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.MethodNotAllowed) { }
        Console.WriteLine("CATALOG PUBLISH TLS PASS " + mode + " anonymous-download/exact-schema/immutable-snapshot/no-write-api");
    }
    internal static async Task InvalidStartup(string executable, string fixtures)
    {
        string path = Path.Combine(fixtures, "invalid-published-catalog.json");
        string data = Path.Combine(fixtures, "invalid-catalog-data");
        File.WriteAllText(path, "{\"Version\":1,\"Servers\":[],\"Password\":\"must-not-be-published\"}");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--urls", "http://127.0.0.1:0", "--data", data, "--server-catalog", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Failed to start invalid catalog check.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            string output = await stdout + await stderr;
            if (process.ExitCode == 0 || Directory.Exists(data) || output.Contains("must-not-be-published", StringComparison.Ordinal))
                throw new Exception("Invalid catalog did not fail before store creation or disclosed raw content.");
            Console.WriteLine("CATALOG PUBLISH PASS invalid-startup/no-realm-side-effects/no-raw-content-log");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
        }
    }
}
