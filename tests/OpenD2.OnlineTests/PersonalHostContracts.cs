using System.Net.Http;
using OpenD2.Online;

internal static class PersonalHostContracts
{
    public static async Task Run(string executable)
    {
        string root = Path.Combine(Path.GetTempPath(), "opend2 personal host " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var host = new PersonalHost();
            using var peer = new PersonalHost();
            await host.Start(executable, Path.Combine(root, "data"));
            Check(host.Running && new Uri(host.Address!).IsLoopback, "ready on assigned loopback port");
            try { await host.Start(executable, Path.Combine(root, "other")); throw new Exception("Duplicate start accepted."); }
            catch (InvalidOperationException) { }
            string first = host.Address!;
            using var client = new OnlineClient(first, mode: "open");
            var credentials = new Credentials("hostowner", Guid.NewGuid().ToString("N"));
            await client.Login(credentials, true);
            var character = await client.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("SavedHero"));
            var room = await client.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(character.Id, "SavedRoom", ""));
            await client.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/start");
            await peer.Start(executable, Path.Combine(root, "peer"));
            Check(peer.Address != first, "concurrent independent hosts choose distinct ports");
            using (var conflict = new PersonalHost())
            {
                try { await conflict.Start(executable, Path.Combine(root, "data")); throw new Exception("Data lock bypassed."); }
                catch (IOException) { }
                Check(!conflict.Running && host.Running && peer.Running, "data conflict fails without stopping existing hosts");
            }
            await using var stream = client.WatchRoom(room.Id).GetAsyncEnumerator();
            Check(await stream.MoveNextAsync(), "active WebSocket stream before shutdown");
            Check(await host.Stop(), "graceful EOF shutdown with connected WebSocket");
            Check(!host.Running && host.Address is null && peer.Running, "stop targets only owned child");
            Check(await host.Stop(), "repeat stop is harmless");
            await host.Start(executable, Path.Combine(root, "data"));
            using var restored = new OnlineClient(host.Address!, mode: "open");
            await restored.Login(credentials, false);
            Check((await restored.Send<CharacterInfo[]>(HttpMethod.Get, "v1/characters")).Single().Id == character.Id, "character persists after stop/restart");
            var joined = await restored.Send<RoomView>(HttpMethod.Post, $"v1/rooms/{room.Id}/join", new JoinRequest(character.Id, ""));
            Check(joined.Started && joined.Id == room.Id, "active room checkpoint survives stop/restart");
            host.Dispose();
            Check(!host.Running && peer.Running, "client disposal closes only its own host");
            peer.Dispose();
            Check(!peer.Running, "disposal closes child pipe and waits for exit");
            using var invalid = new PersonalHost();
            try { await invalid.Start(Path.Combine(root, "missing-server"), root); throw new Exception("Missing executable accepted."); }
            catch (FileNotFoundException) { }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await invalid.Start(executable, Path.Combine(root, "cancelled"), cancel.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Check(!invalid.Running, "missing executable and cancellation leave no child");
            Console.WriteLine("PERSONAL HOST CONTRACTS PASS");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        Console.WriteLine("HOST PASS " + message);
    }
}
