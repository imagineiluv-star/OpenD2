using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using OpenD2.Core;
using OpenD2.Online;

// The real server executable with --tables: it reads the owner's tables before it opens its storage, says so on /health, serves a
// character from them, and refuses a missing or malformed table with a message instead of starting on the preview. Runs against the
// packaged server in CI (--host-server). The tables are synthetic.
internal static class TableOptionContracts
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Table option: " + reason); }
    private sealed class Server(Process process) : IDisposable
    {
        public Process Process => process;
        public readonly List<string> Output = [];
        public void Dispose() { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } } catch (InvalidOperationException) { } process.Dispose(); }
    }
    private static Server Launch(string executable, string data, string? tables, int port)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "--urls", $"http://127.0.0.1:{port}", "--mode", "open", "--data", data })
            info.ArgumentList.Add(argument);
        if (tables is not null) { info.ArgumentList.Add("--tables"); info.ArgumentList.Add(tables); }
        var process = Process.Start(info) ?? throw new InvalidOperationException("The server did not start.");
        var server = new Server(process);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (server.Output) server.Output.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (server.Output) server.Output.Add(e.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        return server;
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static string Output(Server server) { lock (server.Output) return string.Join("\n", server.Output); }

    public static async Task Run(string executable)
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2 table option " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string tables = Path.Combine(root, "tables"); TableFixtures.Write(tables);
            int port = FreePort(); string address = $"http://127.0.0.1:{port}";
            using (var server = Launch(executable, Path.Combine(root, "data"), tables, port))
            {
                using var http = new HttpClient();
                string? health = null;
                for (int attempt = 0; attempt < 200 && health is null; attempt++)
                {
                    if (server.Process.HasExited) throw new Exception("Table option: the server exited early: " + Output(server));
                    try { health = await http.GetStringAsync(address + "/health"); } catch (HttpRequestException) { await Task.Delay(100); }
                }
                Check(health is not null && health.Contains("\"tables\":\"original\"") && health.Contains("\"characterFileVersion\":4"), "/health says the server runs on the owner's tables: " + health);
                Check(Output(server).Contains("Character tables: lod-1.10f; charstats "), "the startup line names the table set");
                using var client = new OnlineClient(address, mode: "open");
                await client.Login(new("table-option", Guid.NewGuid().ToString("N")), true);
                Check(client.TablesAreOriginal && client.SupportsCharacterProgress, "the client sees original tables");
                var hero = await client.CreateCharacter("OwnHero", CharacterClass.Barbarian);
                Check(hero.Progress!.Strength == ProgressionTables.Preview.Get(CharacterClass.Barbarian).Strength + 1, "a character is created from the owner's table");
                Check((await client.ListSkills()).Length == SkillTable.Preview.All.Count, "the skill list is served from the owner's table");
            }

            // A client-owned host never reads a directory that its environment (or launcher) names.
            Environment.SetEnvironmentVariable("tables", tables);
            try
            {
                using var personal = new PersonalHost();
                await personal.Start(executable, Path.Combine(root, "personal"));
                using var owner = new OnlineClient(personal.Address!, mode: "open");
                await owner.CheckServer();
                Check(!owner.TablesAreOriginal, "a managed host ignores a tables directory named in its environment");
                Check(await personal.Stop(), "the managed host stops gracefully");
            }
            finally { Environment.SetEnvironmentVariable("tables", null); }

            // Refusals happen before storage is opened and leave the process stopped with the reason on its output.
            foreach (var (name, setup) in new (string, Action<string>)[] {
                ("missing directory", folder => Directory.Delete(folder, true)),
                ("missing skills.txt", folder => File.Delete(Path.Combine(folder, "skills.txt"))),
                ("malformed charstats.txt", folder => File.WriteAllText(Path.Combine(folder, "charstats.txt"), "class\tstr\nNobody\t1\n")) })
            {
                string broken = Path.Combine(root, "broken-" + name.Replace(' ', '-')); TableFixtures.Write(broken); setup(broken);
                string data = Path.Combine(root, "data-" + name.Replace(' ', '-'));
                using var server = Launch(executable, data, broken, FreePort());
                Check(server.Process.WaitForExit(30000), $"{name}: the server stops");
                server.Process.WaitForExit(); // flush the redirected output
                Check(server.Process.ExitCode != 0, $"{name}: a failing exit code");
                Check(Output(server).Contains(Path.GetFileName(broken)) || Output(server).Contains("skills.txt") || Output(server).Contains("charstats.txt"), $"{name}: the output names what is wrong: {Output(server)}");
                Check(!Directory.Exists(data), $"{name}: no storage was created");
            }
            Console.WriteLine("TABLE OPTION PASS --tables on the real server: health, startup line, character from the owner's table, refusals before storage");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
