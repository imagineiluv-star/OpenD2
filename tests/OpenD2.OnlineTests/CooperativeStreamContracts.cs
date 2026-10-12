using System.Net;
using OpenD2.Core;
using OpenD2.Online;

internal static class CooperativeStreamContracts
{
    internal static async Task Run(string url, string ca, string mode)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var stop = deadline.Token;
        const string password = "world-stream-contract-password";
        var credentials = new Credentials("world-stream-host", password);
        using var host = new OnlineClient(url, ca, mode);
        using var guest = new OnlineClient(url, ca, mode);
        await host.Login(credentials, true, stop);
        await guest.Login(new("world-stream-guest", password), true, stop);
        if (!host.SupportsWorldRooms || !guest.SupportsWorldRooms) throw new Exception("Missing world capability.");
        var content = CooperativeWorld.Content;
        var catalog = await host.Send<WorldContent[]>(HttpMethod.Get, "v1/worlds", cancellation: stop);
        if (!catalog.SequenceEqual([content])) throw new Exception("World catalog differs.");
        var a = await host.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("WorldStreamHost"), stop);
        var b = await guest.Send<CharacterInfo>(HttpMethod.Post, "v1/characters", new NameRequest("WorldStreamGuest"), stop);
        var room = await host.Send<RoomView>(HttpMethod.Post, "v1/rooms", new RoomRequest(a.Id, "WorldStream", "", content), stop);
        string path = $"v1/rooms/{room.Id}";
        async Task Reject(Func<Task> action)
        {
            try { await action(); } catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Conflict) { return; }
            throw new Exception("Expected world content/protocol/input rejection.");
        }
        await Reject(async () => await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, ""), stop));
        await guest.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(b.Id, "", World: content), stop);
        await using (var legacy = host.WatchRoom(room.Id, stop).GetAsyncEnumerator())
            await Reject(async () => await legacy.MoveNextAsync());
        var visible = await host.Send<RoomInfo[]>(HttpMethod.Get, "v1/rooms?includeWorlds=true", cancellation: stop);
        var oldList = await host.Send<RoomInfo[]>(HttpMethod.Get, "v1/rooms", cancellation: stop);
        if (!visible.Any(r => r.Id == room.Id && r.World == content) || oldList.Any(r => r.Id == room.Id)) throw new Exception("World room discovery negotiation failed.");
        await host.Send<RoomView>(HttpMethod.Post, path + "/start", cancellation: stop);
        await using var first = host.WatchRoom(room.Id, stop, content).GetAsyncEnumerator();
        await using var second = guest.WatchRoom(room.Id, stop, content).GetAsyncEnumerator();
        async Task<RoomView> Until(IAsyncEnumerator<RoomView> feed, Func<RoomView, bool> ready)
        {
            while (await feed.MoveNextAsync()) if (ready(feed.Current)) return feed.Current;
            throw new Exception("World stream ended.");
        }
        InputRequest Input(RoomView view, CommandKind kind, int x = 0, uint target = 0) => new(view.NextSequence, kind, x, Target: target,
            Region: view.Entities.Single(e => e.Id.Value == view.Actor).Region.Value, RegionEpoch: view.World!.RegionEpoch);
        var initial = await Until(first, _ => true); await Until(second, _ => true);
        await host.SendInput(room.Id, Input(initial, CommandKind.Interact, target: 300), stop);
        var quest = await Until(first, v => v.World!.Quest.Stage == QuestStage.Active);
        // Quests are personal: the guest's stream moves past the acceptance while their own quest stays available.
        await Until(second, v => v.Tick > quest.Tick && v.World!.Quest.Stage == QuestStage.Available);
        await host.SendInput(room.Id, Input(quest, CommandKind.Interact, target: 200), stop);
        var field = await Until(first, v => v.Entities.Single(e => e.Id.Value == 1).Region.Value == 2);
        var town = await Until(second, v => v.Entities.Single(e => e.Id.Value == 1).Region.Value == 2);
        if (field.World!.RegionEpoch <= 0 || field.ProcessedSequence != quest.NextSequence ||
            !field.Entities.Any(e => e.Kind == EntityKind.Monster) || town.Entities.Any(e => e.Kind == EntityKind.Monster) || town.World!.RegionEpoch != 0)
            throw new Exception("World WSS portal/party/interest/progress differs.");
        await Reject(async () => await host.SendInput(room.Id, new(field.NextSequence, CommandKind.SetMove, 1, Region: 1, RegionEpoch: 0), stop));
        await host.SendInputs(room.Id, [Input(field, CommandKind.SetMove, 1), Input(field, CommandKind.SetMove) with { Sequence = field.NextSequence + 1 }], stop);
        await Until(first, v => v.ProcessedSequence == field.NextSequence + 1);
        await Until(first, v => v.Tick > field.Tick + 2);
        if (host.RoomDeltaFrames == 0 || host.RoomFullFrames < 3) throw new Exception("World metadata/full/delta frames missing.");
        using var renewed = new OnlineClient(url, ca, mode);
        await renewed.Login(credentials, false, stop);
        await Reject(async () => await renewed.Send<RoomView>(HttpMethod.Get, path, cancellation: stop));
        var rejoined = await renewed.Send<RoomView>(HttpMethod.Post, path + "/join", new JoinRequest(a.Id, "", World: content), stop);
        await using var replacement = renewed.WatchRoom(room.Id, stop, content).GetAsyncEnumerator();
        var reconnected = await Until(replacement, _ => true);
        if (reconnected.World != rejoined.World || reconnected.Actor != 1 || renewed.RoomFullFrames != 1 || renewed.RoomDeltaFrames != 0)
            throw new Exception("World reconnect lost content/epoch/full baseline.");
        await renewed.Send<object>(HttpMethod.Post, path + "/close", cancellation: stop);
        Console.WriteLine($"COOPERATIVE WSS PASS {mode} catalog/legacy/auth/two-clients/quest/portal/interest/epoch/batch/full/delta/reconnect");
    }
}
