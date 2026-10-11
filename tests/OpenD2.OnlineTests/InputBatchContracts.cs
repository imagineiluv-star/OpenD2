using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class InputBatchContracts
{
    internal static async Task Run()
    {
        VerifyCore();
        foreach (string mode in new[] { "realm", "open" }) VerifyRealm(mode);
        await VerifyWire();
    }
    private static void Check(bool yes) { if (!yes) throw new Exception("Input batch contract failed."); }
    private static void Reject(int code, Action action)
    {
        try { action(); } catch (Rejected error) when (error.Status == code) { return; }
        throw new Exception("Expected batch rejection " + code);
    }
    private static void VerifyCore()
    {
        GameSimulation Create() => new(42, [new(new(1), new(1), new(768, 768))]);
        GameCommand Move(ulong sequence, long tick = 1) => new(tick, sequence, new(1), new(1), CommandKind.SetMove, 1);
        void Unchanged(GameSimulation game, GameCommand[] batch, CommandResult expected)
        {
            var before = game.CaptureSnapshot(); string hash = game.ComputeStateHash();
            Check(game.SubmitBatch(batch) == expected);
            var after = game.CaptureSnapshot();
            Check(hash == game.ComputeStateHash() && before.Inputs.SequenceEqual(after.Inputs) && before.PendingCommands.SequenceEqual(after.PendingCommands));
        }
        var game = Create();
        Unchanged(game, [], CommandResult.InvalidCommand);
        Unchanged(game, Enumerable.Range(1, 9).Select(i => Move((ulong)i)).ToArray(), CommandResult.InvalidCommand);
        Unchanged(game, [Move(1), Move(2) with { X = 999 }], CommandResult.InvalidCommand);
        Unchanged(game, [Move(1), Move(2) with { Actor = new(2) }], CommandResult.InvalidCommand);
        Unchanged(game, [Move(1), Move(2, 2)], CommandResult.InvalidCommand);
        Unchanged(game, [Move(1), Move(1)], CommandResult.StaleSequence);
        Unchanged(game, [Move(2), Move(1)], CommandResult.StaleSequence);
        Unchanged(game, [Move(1), Move(2) with { Kind = CommandKind.Pickup, X = 0, Item = new(99) }], CommandResult.InvalidTarget);
        var sequential = Create();
        var valid = Enumerable.Range(1, 8).Select(i => Move((ulong)i) with { X = i % 2 == 0 ? -1 : 1 }).ToArray();
        Check(game.SubmitBatch(valid) == CommandResult.Accepted);
        foreach (var command in valid) Check(sequential.Submit(command) == CommandResult.Accepted);
        Check(game.ComputeStateHash() == sequential.ComputeStateHash());
        game.Step(); sequential.Step();
        Check(game.ComputeStateHash() == sequential.ComputeStateHash() && game.GetEntity(new(1)).Position.X == 736);
        game = Create();
        for (ulong i = 1; i <= 127; i++) Check(game.Submit(Move(i)) == CommandResult.Accepted);
        Unchanged(game, [Move(128), Move(129)], CommandResult.TickFull);
        Check(game.Submit(Move(128)) == CommandResult.Accepted);
        game = Create();
        for (ulong i = 1; i <= 4095; i++) Check(game.Submit(Move(i, (long)((i - 1) / 128 + 1))) == CommandResult.Accepted);
        Unchanged(game, [Move(4096, 32), Move(4097, 32)], CommandResult.QueueFull);
        Check(game.Submit(Move(4096, 32)) == CommandResult.Accepted);
        Console.WriteLine("INPUT BATCH CORE PASS atomic-rejection/identity/sequence/tick-and-queue-capacity/sequential-equivalence");
    }
    private static void VerifyRealm(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-batch-" + Guid.NewGuid().ToString("N"));
        var time = new TestTime(); const string password = "batch-contract-password";
        Guid room, character;
        try
        {
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("batch-host", password), true);
                var other = realm.Login(new("batch-other", password), true);
                character = realm.CreateCharacter(host.Token, "BatchHero").Id;
                room = realm.CreateRoom(host.Token, new(character, "BatchRoom", "")).Id;
                InputBatchRequest valid = new([new(1, CommandKind.SetMove, -1), new(2, CommandKind.SetMove, 0, -1)]);
                Reject(409, () => realm.BatchInput(host.Token, room, valid));
                var initial = realm.Start(host.Token, room);
                Reject(401, () => realm.BatchInput("invalid", room, valid));
                Reject(403, () => realm.BatchInput(other.Token, room, valid));
                Reject(404, () => realm.BatchInput(host.Token, Guid.NewGuid(), valid));
                foreach (var batch in new InputBatchRequest[] { null!, new(null!), new([]), new([null!]),
                    new(Enumerable.Range(1, 9).Select(i => new InputRequest(i, CommandKind.SetMove)).ToArray()),
                    new([new(1, CommandKind.SetMove, -1), new(2, CommandKind.SetMove, 999)]) })
                    Reject(400, () => realm.BatchInput(host.Token, room, batch));
                foreach (var batch in new[] { new InputBatchRequest([new(1, CommandKind.SetMove), new(1, CommandKind.SetMove)]),
                    new([new(1, CommandKind.SetMove), new(3, CommandKind.SetMove)]), new([new(long.MaxValue, CommandKind.SetMove)]) })
                    Reject(409, () => realm.BatchInput(host.Token, room, batch));
                var rejected = realm.State(host.Token, room);
                Check(rejected.NextSequence == 1 && rejected.StateHash == initial.StateHash);
                var receipt = realm.BatchInput(host.Token, room, valid);
                Check(receipt == new InputReceipt(room, 1, 3, initial.Tick));
                Reject(409, () => realm.AcknowledgeInput(host.Token, room, valid.Inputs[1]));
                Reject(409, () => realm.BatchInput(host.Token, room, valid));
                realm.Tick();
                var moved = realm.State(host.Token, room).Entities.Single(e => e.Id.Value == 1);
                Check(moved.Position.X == 768 && moved.Position.Y == 736 && moved.MoveY == -1);
                time.Advance(1);
                Reject(400, () => realm.BatchInput(host.Token, room, new([new(3, CommandKind.SetMove, 1), new(4, CommandKind.SetMove, 999)])));
                realm.Tick();
                Check(realm.State(host.Token, room).Entities.Single(e => e.Id.Value == 1).MoveY == 0);
                Check(realm.AcknowledgeInput(host.Token, room, new(3, CommandKind.SetMove)).NextSequence == 4);
                realm.Tick(); realm.Checkpoint(host.Token, room);
                realm.Login(new("batch-host", password), false);
                Reject(401, () => realm.BatchInput(host.Token, room, new([new(4, CommandKind.SetMove)])));
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var login = realm.Login(new("batch-host", password), false);
                Check(realm.Join(login.Token, room, new(character, "")).NextSequence == 4);
                Check(realm.BatchInput(login.Token, room, new([new(4, CommandKind.SetMove), new(5, CommandKind.SetMove)])).NextSequence == 6);
                realm.Close(login.Token, room);
                Reject(404, () => realm.BatchInput(login.Token, room, new([new(6, CommandKind.SetMove)])));
            }
            Console.WriteLine($"INPUT BATCH REALM PASS {mode} authorization/atomicity/order/legacy-cursor/lease/restart");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task VerifyWire()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        int capability = 0, response = 0, calls = 0;
        Guid room = Guid.NewGuid();
        app.MapGet("/health", () => capability == 0
            ? (object)new { protocol = 1, rules = GameSimulation.RulesVersion, roomStream = 1 }
            : new ServerInfo(1, GameSimulation.RulesVersion, 1, InputBatch: capability));
        app.MapPost("/v1/rooms/{id:guid}/input/batch", (Guid id, InputBatchRequest batch) =>
        {
            calls++;
            if (response == 5) return Results.StatusCode(503);
            return Results.Json(new InputReceipt(response == 1 ? Guid.NewGuid() : id, response == 2 ? 0u : 1u,
                batch.Inputs[^1].Sequence + (response == 3 ? 0 : 1), response == 4 ? -1 : 4));
        });
        await app.StartAsync();
        try
        {
            using var client = new OnlineClient(app.Urls.Single());
            InputRequest[] inputs = [new(1, CommandKind.SetMove), new(2, CommandKind.Attack, Target: 100)];
            foreach (int version in new[] { 0, 2 })
            {
                capability = version; await client.CheckServer(); Check(!client.SupportsInputBatches);
                try { await client.SendInputs(room, inputs); throw new Exception("Unsupported batch sent."); } catch (NotSupportedException) { }
            }
            Check(calls == 0);
            capability = 1; await client.CheckServer(); Check(client.SupportsInputBatches);
            foreach (var invalid in new InputRequest[][] { [], [null!], [inputs[0], inputs[0]], [new(long.MaxValue, CommandKind.SetMove)],
                Enumerable.Range(1, 9).Select(i => new InputRequest(i, CommandKind.SetMove)).ToArray() })
            {
                try { await client.SendInputs(room, invalid); throw new Exception("Invalid batch sent."); } catch (ArgumentException) { }
            }
            Check(calls == 0 && await client.SendInputs(room, inputs) == new InputReceipt(room, 1, 3, 4));
            for (response = 1; response <= 5; response++)
            {
                try { await client.SendInputs(room, inputs); }
                catch (InvalidDataException) when (response < 5) { continue; }
                catch (HttpRequestException error) when (response == 5 && error.StatusCode == HttpStatusCode.ServiceUnavailable) { continue; }
                throw new Exception("Invalid batch receipt accepted.");
            }
            Check(calls == 6 && client.InputBatchRequests == 1);
            Console.WriteLine("INPUT BATCH WIRE PASS missing-and-unknown-capability/bounds/invalid-receipt/no-retry");
        }
        finally { await app.StopAsync(); }
    }
}
