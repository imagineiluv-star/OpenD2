using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenD2.Core;
using OpenD2.Online;

internal static class RoomFrameContracts
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private static RoomView Initial()
    {
        var game = Arena.Create(2);
        return new(Guid.NewGuid(), "DeltaRoom", true, true, 1, 1, game.Tick, game.ComputeStateHash(),
            [new(Guid.NewGuid(), "Host", 1), new(Guid.NewGuid(), "Guest", 2)], game.Entities.ToArray(),
            [new(new(2UL << 32), ItemDefinition.TrainingSword, ItemLocation.Inventory, new(1), 0, new(1), default),
             new(new(100), ItemDefinition.HealthPotion, ItemLocation.Ground, default, -1, new(1), new(1000, 1000))]);
    }
    private static bool Same(RoomView a, RoomView b) => a with { Members = b.Members, Entities = b.Entities, Items = b.Items } == b &&
        a.Members.SequenceEqual(b.Members) && a.Entities.SequenceEqual(b.Entities) && a.Items.SequenceEqual(b.Items);
    internal static async Task Run()
    {
        var start = Initial(); RoomView? previous = null, decoded = null;
        long payload = 0, equivalent = 0; int deltas = 0, full = 0;
        var current = start;
        for (long sequence = 1; sequence <= 400; sequence++)
        {
            current = current with { Tick = sequence, NextSequence = sequence, StateHash = sequence.ToString("x64"),
                Entities = current.Entities.Select((e, i) => i == 0 ? e with { Position = new((int)(768 + sequence), 768), Health = 90, Mana = (int)(sequence % 60) } : e).ToArray() };
            if (sequence % 30 == 0) current = current with { Items = current.Items.Select((item, i) => i == 0 ? item with { Location = item.Location == ItemLocation.Inventory ? ItemLocation.Equipped : ItemLocation.Inventory } : item).ToArray() };
            if (sequence % 40 == 0) current = current with { Items = current.Items.Reverse().ToArray() };
            if (sequence == 60) current = current with { Items = [.. current.Items, new(new(101), ItemDefinition.ManaPotion, ItemLocation.Ground, default, -1, new(1), default)] };
            if (sequence == 80) current = current with { Items = current.Items.Where(i => i.Id.Value != 101).ToArray() };
            if (sequence == 100) current = current with { Members = [current.Members[0] with { Name = "Renamed" }, current.Members[1]] };
            var frame = RoomFrames.Create(current, previous, sequence);
            if ((sequence - 1) % RoomFrames.FullInterval == 0 && frame.State is null) throw new Exception("Periodic full refresh missing.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(frame, json);
            frame = JsonSerializer.Deserialize<RoomFrame>(bytes, json)!;
            decoded = RoomFrames.Apply(frame, decoded, sequence - 1, start.Id);
            if (!Same(current, decoded)) throw new Exception("Delta reconstruction lost state or array order.");
            payload += bytes.Length; equivalent += JsonSerializer.SerializeToUtf8Bytes(new RoomUpdate(current), json).Length;
            if (frame.Delta is null) full++; else deltas++;
            previous = current;
        }
        if (deltas == 0 || payload >= equivalent) throw new Exception("Delta sample did not reduce payload.");
        Console.WriteLine($"ROOM DELTA PASS 400 exact reconstructions movement/combat/items/order/membership/refresh full={full} delta={deltas} JSON={payload}B v1-equivalent={equivalent}B");

        var changed = start with { Tick = 1, Entities = start.Entities.Select((e, i) => i == 0 ? e with { Health = 99 } : e).ToArray() };
        var patch = RoomFrames.Create(changed, start, 2);
        void Reject(RoomFrame frame, RoomView? baseline = null, long sequence = 1)
        {
            try { RoomFrames.Apply(frame, baseline ?? start, sequence, start.Id); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid room frame accepted.");
        }
        Reject(patch with { Sequence = 1 }); Reject(patch with { Sequence = 3 }); Reject(patch with { BaseSequence = 0 });
        Reject(patch with { State = start }); Reject(patch with { Delta = null }); Reject(patch with { Error = "ambiguous" });
        Reject(patch with { Delta = patch.Delta! with { Room = Guid.NewGuid() } });
        Reject(patch with { Delta = patch.Delta! with { Tick = -1 } });
        Reject(patch with { Delta = patch.Delta! with { NextSequence = 0 } });
        Reject(patch with { Delta = patch.Delta! with { Entities = [new(-1, start.Entities[0])] } });
        Reject(patch with { Delta = patch.Delta! with { Entities = [new(start.Entities.Length, start.Entities[0])] } });
        Reject(patch with { Delta = patch.Delta! with { Entities = [new(0, start.Entities[1])] } });
        Reject(patch with { Delta = patch.Delta! with { Entities = [new(0, start.Entities[0]), new(0, start.Entities[0])] } });
        Reject(patch with { Delta = patch.Delta! with { Items = [new(0, start.Items[1])] } });
        Reject(new(2, State: start with { Entities = new EntityState[GameSimulation.MaxEntities + 1] }));
        Reject(new(2, State: start with { Items = new ItemState[RoomFrames.MaxItems + 1] }));
        Reject(new(2, State: start with { Entities = [start.Entities[0], start.Entities[0]] }));
        Reject(new(2, State: start with { Items = [start.Items[0], start.Items[0]] }));
        Reject(new(2, State: start with { Id = Guid.NewGuid() }));
        Reject(new(2, State: start with { Tick = 1 }), start with { Tick = 2 });
        if (!Same(start, InitialWithIdentity(start))) throw new Exception("Rejected frame mutated its baseline.");
        Console.WriteLine("ROOM DELTA PASS sequence/base/budgets/identity/duplicates/atomic-rejection");
        await Wire(start, changed, patch);
    }
    private static RoomView InitialWithIdentity(RoomView original) => Initial() with { Id = original.Id, Members = original.Members };

    private static async Task Wire(RoomView start, RoomView changed, RoomFrame patch)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets();
        int version = 1, interest = 0; string[] messages = []; string? selected = null;
        app.MapGet("/health", () => version == 0
            ? (object)new { protocol = 1, rules = GameSimulation.RulesVersion, roomStream = 1, mode = "realm" }
            : new ServerInfo(1, GameSimulation.RulesVersion, 1, "realm", 2, 1, version, RoomInterest: interest));
        app.MapPost("/v1/login", () => new LoginResult("fixture-token", "fixture"));
        app.Map("/v1/rooms/{id:guid}/stream", async context =>
        {
            selected = version == 1 ? interest == 1 ? RoomInterest.Protocol : RoomFrames.Protocol : "opend2.room.v1";
            var data = messages;
            using var socket = await context.WebSockets.AcceptWebSocketAsync(selected);
            try
            {
                foreach (string message in data)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(message);
                    await socket.SendAsync(bytes.AsMemory(0, 8), WebSocketMessageType.Text, false, context.RequestAborted);
                    await socket.SendAsync(bytes.AsMemory(8), WebSocketMessageType.Text, true, context.RequestAborted);
                }
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        });
        await app.StartAsync();
        string full = JsonSerializer.Serialize(new RoomFrame(1, State: start), json), delta = JsonSerializer.Serialize(patch, json);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var capability in new[] { (0, 0), (2, 1), (1, 0), (1, 2), (1, 1) })
            {
                version = capability.Item1; interest = capability.Item2;
                messages = version == 1 ? [full, delta] : [JsonSerializer.Serialize(new RoomUpdate(start), json), JsonSerializer.Serialize(new RoomUpdate(changed), json)];
                using var client = new OnlineClient(app.Urls.Single()); await client.Login(new("fixture", "fixture-password"), false, deadline.Token);
                await using var feed = client.WatchRoom(start.Id, deadline.Token).GetAsyncEnumerator();
                if (!await feed.MoveNextAsync() || !Same(start, feed.Current) || !await feed.MoveNextAsync() || !Same(changed, feed.Current) ||
                    client.RoomPayloadBytes != messages.Sum(m => Encoding.UTF8.GetByteCount(m)) || client.RoomDeltaFrames != (version == 1 ? 1 : 0) ||
                    client.SupportsRoomInterest != (version == 1 && interest == 1) ||
                    selected != (version == 1 ? interest == 1 ? RoomInterest.Protocol : RoomFrames.Protocol : "opend2.room.v1")) throw new Exception("Fragmented v1/v2/v3 negotiation or reconstruction failed.");
            }
            version = 1;
            foreach (RoomFrame bad in new[] { patch with { BaseSequence = 0 }, patch with { Sequence = 1 }, patch with { Sequence = 3 } })
            {
                messages = [full, JsonSerializer.Serialize(bad, json)];
                using var client = new OnlineClient(app.Urls.Single()); await client.Login(new("fixture", "fixture-password"), false, deadline.Token);
                await using var feed = client.WatchRoom(start.Id, deadline.Token).GetAsyncEnumerator();
                if (!await feed.MoveNextAsync() || !Same(start, feed.Current)) throw new Exception("Initial wire baseline missing.");
                try { await feed.MoveNextAsync(); }
                catch (InvalidDataException) when (client.RoomFullFrames == 1 && client.RoomDeltaFrames == 0) { continue; }
                throw new Exception("Broken delta chain was silently accepted or skipped.");
            }
            foreach (string bad in new[] { delta, full.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":1"),
                full.Insert(1, "\"unknown\":true,"), JsonSerializer.Serialize(new RoomFrame(1, State: start, Delta: patch.Delta), json),
                new string(' ', 1024 * 1024 + 1) })
            {
                messages = [bad];
                using var client = new OnlineClient(app.Urls.Single()); await client.Login(new("fixture", "fixture-password"), false, deadline.Token);
                await using var feed = client.WatchRoom(start.Id, deadline.Token).GetAsyncEnumerator();
                try { await feed.MoveNextAsync(); }
                catch (Exception error) when (error is InvalidDataException or JsonException) { continue; }
                throw new Exception("Malformed/unbased/oversized wire frame accepted.");
            }
            Console.WriteLine("ROOM DELTA WIRE PASS fragmented-v1-v2-v3/unknown-interest-and-legacy-negotiation/actual-payload-bytes/strict-json/no-baseline/size");
        }
        finally { await app.StopAsync(); }
    }
}
