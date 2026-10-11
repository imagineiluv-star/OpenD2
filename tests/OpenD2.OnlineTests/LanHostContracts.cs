using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OpenD2.Online;

internal static class LanHostContracts
{
    public static async Task Run(string executable, string fixtures, string password)
    {
        string data = Path.Combine(fixtures, "managed-lan-data");
        LanHostSettings Settings(string ip = "127.0.0.1", string pfx = "valid", string ca = "ca", string? secret = null, int port = 0) => new()
        { BindAddress = ip, Port = port, CertificateFile = Path.Combine(fixtures, pfx + ".pfx"), CertificatePassword = secret ?? password, RootCaFile = Path.Combine(fixtures, ca + ".pem") };
        using var host = new PersonalHost();
        var localIp = LanHostSettings.LocalAddresses().FirstOrDefault();
        foreach (string ip in localIp is null ? new[] { "127.0.0.1" } : new[] { "127.0.0.1", localIp })
        {
            var settings = Settings(ip);
            using (var unrelated = new TcpListener(IPAddress.Loopback, 0))
            {
                unrelated.Start();
                const string inherited = "Kestrel__Endpoints__Unexpected__Url";
                string? previous = Environment.GetEnvironmentVariable(inherited);
                const string catalogSetting = "server-catalog";
                string? previousCatalog = Environment.GetEnvironmentVariable(catalogSetting);
                Environment.SetEnvironmentVariable(catalogSetting, Path.Combine(fixtures, "missing-inherited-catalog.json"));
                Environment.SetEnvironmentVariable(inherited, "http://127.0.0.1:" + ((IPEndPoint)unrelated.LocalEndpoint).Port);
                try { await host.Start(executable, data, settings); }
                finally { Environment.SetEnvironmentVariable(inherited, previous); Environment.SetEnvironmentVariable(catalogSetting, previousCatalog); }
                Check(host.Running, "inherited HTTP endpoint cannot override managed TLS binding");
            }
            Check(host.Running && new Uri(host.Address!).Scheme == "https" && new Uri(host.Address!).Host == ip && host.TrustedRootSha256 is not null, "HTTPS endpoint and verified CA identity");
            using var owner = new OnlineClient(host.Address!, settings.RootCaFile, "open");
            using var guest = new OnlineClient(host.Address!, settings.RootCaFile, "open");
            try { await ServerCatalog.Download(host.Address + "/v1/server-catalog", settings.RootCaFile); throw new Exception("Managed host inherited catalog publication."); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            Check(true, "managed host ignores inherited catalog and does not publish it");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            var credentials = new Credentials("owner" + suffix, Guid.NewGuid().ToString("N"));
            await owner.Login(credentials, true);
            await guest.Login(new("guest" + suffix, Guid.NewGuid().ToString("N")), true);
            var c1 = await owner.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("HostHero" + suffix));
            var c2 = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("GuestHero" + suffix));
            var room = await owner.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(c1.Id, "LanRoom", ""));
            await guest.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/join", new JoinRequest(c2.Id, ""));
            await owner.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/start");
            await using (var stream = guest.WatchRoom(room.Id).GetAsyncEnumerator())
            {
                Check(await stream.MoveNextAsync() && stream.Current.Members.Length == 2 && stream.Current.Started && guest.RoomTransport == "WSS", "two authenticated clients share a managed WSS room");
                Check(await host.Stop(), "managed TLS host checkpoints with connected guest");
            }
            await host.Start(executable, data, settings);
            using var restored = new OnlineClient(host.Address!, settings.RootCaFile, "open");
            await restored.Login(credentials, false);
            var rejoined = await restored.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/join", new JoinRequest(c1.Id, ""));
            Check(rejoined.Started && rejoined.Members.Length == 2, "managed TLS host restores room and identities after restart");
            Check(await host.Stop(), "restart shutdown is graceful");
        }
        foreach (var settings in new[] { Settings(pfx: "wrong-host"), Settings(pfx: "expired"), Settings(secret: "incorrect-pfx-password"), Settings(ca: "other-ca") })
        {
            bool rejected = false;
            try { await host.Start(executable, data, settings); }
            catch (Exception error) when (error is InvalidDataException or CryptographicException or HttpRequestException) { rejected = true; }
            Check(rejected && !host.Running, "bad PFX, hostname, validity or trust rejects startup and leaves no child");
        }
        using (var occupied = new TcpListener(IPAddress.Loopback, 0))
        {
            occupied.Start(); int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            bool rejected = false;
            try { await host.Start(executable, data, Settings(port: port)); }
            catch (IOException) { rejected = true; }
            Check(rejected && !host.Running, "occupied port rejected without adopting another listener");
        }
        foreach (var settings in new[] { Settings(ip: "0.0.0.0"), Settings(ip: "203.0.113.254"), Settings(port: -1), Settings(port: 65536) })
        {
            bool rejected = false;
            try { settings.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "wildcard/nonlocal IP and invalid port rejected");
        }
        string launch = Settings().LaunchJson();
        Check(!launch.Contains("RootCaFile", StringComparison.Ordinal) && LanHostSettings.ReadLaunch(new StringReader(launch + "\n")).CertificatePassword == password, "bounded private launch protocol preserves settings without transmitting trust configuration");
        bool bounded = false;
        try { LanHostSettings.ReadLaunch(new StringReader(new string(' ', LanHostSettings.MaxLaunchCharacters + 1) + "\n")); }
        catch (InvalidDataException) { bounded = true; }
        Check(bounded, "oversized launch rejected");
        Check(Directory.GetFiles(data, "*", SearchOption.AllDirectories).All(file => !File.ReadAllText(file).Contains(password, StringComparison.Ordinal)), "PFX password absent from persistent host data");
        Console.WriteLine("LAN HOST CONTRACTS PASS interface=" + (localIp is null ? "NOT_RUN" : "PASS") + " physical_multi_pc=NOT_RUN");
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        Console.WriteLine("LAN HOST PASS " + message);
    }
}
