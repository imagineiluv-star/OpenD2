using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class InputReceiptContracts
{
    internal static async Task Run()
    {
        foreach (string mode in new[] { "realm", "open" }) VerifyRealm(mode);
        await VerifyCompatibility();
    }
    private static void VerifyRealm(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-input-" + Guid.NewGuid().ToString("N"));
        var time = new TestTime();
        const string password = "input-contract-password";
        Guid roomId, characterId;
        void Reject(int status, Action action)
        {
            try { action(); } catch (Rejected error) when (error.Status == status) { return; }
            throw new Exception("Expected input rejection " + status);
        }
        try
        {
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("input-host", password), true);
                var outsider = realm.Login(new("input-other", password), true);
                characterId = realm.CreateCharacter(host.Token, "InputHero").Id;
                roomId = realm.CreateRoom(host.Token, new(characterId, "Inputs", "")).Id;
                Reject(409, () => realm.AcknowledgeInput(host.Token, roomId, new(1, CommandKind.SetMove)));
                var initial = realm.Start(host.Token, roomId);
                Reject(401, () => realm.AcknowledgeInput("invalid", roomId, new(1, CommandKind.SetMove)));
                Reject(403, () => realm.AcknowledgeInput(outsider.Token, roomId, new(1, CommandKind.SetMove)));
                Reject(404, () => realm.AcknowledgeInput(host.Token, Guid.NewGuid(), new(1, CommandKind.SetMove)));
                Reject(400, () => realm.AcknowledgeInput(host.Token, roomId, new(1, CommandKind.SetMove, 999)));
                Reject(409, () => realm.AcknowledgeInput(host.Token, roomId, new(long.MaxValue, CommandKind.SetMove)));
                var ack = realm.AcknowledgeInput(host.Token, roomId, new(1, CommandKind.SetMove, -1));
                var accepted = realm.State(host.Token, roomId);
                if (ack != new InputReceipt(roomId, 1, 2, initial.Tick) || !accepted.Entities.SequenceEqual(initial.Entities) || !accepted.Items.SequenceEqual(initial.Items))
                    throw new Exception("Receipt must acknowledge acceptance before simulation execution.");
                Reject(409, () => realm.Input(host.Token, roomId, new(1, CommandKind.SetMove)));
                Reject(409, () => realm.AcknowledgeInput(host.Token, roomId, new(1, CommandKind.SetMove)));
                realm.Tick();
                if (realm.State(host.Token, roomId).Entities.Single(e => e.Id.Value == 1).Position.X != 736)
                    throw new Exception("Acknowledged movement was not applied on the next tick.");
                time.Advance(1); realm.Tick();
                if (realm.State(host.Token, roomId).Entities.Single(e => e.Id.Value == 1).MoveX != 0)
                    throw new Exception("Acknowledged input bypassed movement lease expiry.");
                var legacy = realm.Input(host.Token, roomId, new(2, CommandKind.SetMove));
                Reject(409, () => realm.AcknowledgeInput(host.Token, roomId, new(2, CommandKind.SetMove)));
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                int receiptBytes = JsonSerializer.SerializeToUtf8Bytes(ack, json).Length;
                int fullBytes = JsonSerializer.SerializeToUtf8Bytes(legacy, json).Length;
                if (receiptBytes * 4 >= fullBytes || legacy.NextSequence != 3) throw new Exception("Input receipt is not compact or legacy response changed.");
                realm.Checkpoint(host.Token, roomId);
                realm.Login(new("input-host", password), false);
                Reject(401, () => realm.AcknowledgeInput(host.Token, roomId, new(3, CommandKind.SetMove)));
                Console.WriteLine($"INPUT RECEIPT PASS {mode} authority/sequence/legacy/lease JSON-body={receiptBytes}B full={fullBytes}B (not wire bytes)");
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var login = realm.Login(new("input-host", password), false);
                var restored = realm.Join(login.Token, roomId, new(characterId, ""));
                if (restored.NextSequence != 3 || realm.AcknowledgeInput(login.Token, roomId, new(3, CommandKind.SetMove)).NextSequence != 4)
                    throw new Exception("Input cursor did not survive restart.");
                realm.Close(login.Token, roomId);
                Reject(404, () => realm.AcknowledgeInput(login.Token, roomId, new(4, CommandKind.SetMove)));
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static async Task VerifyCompatibility()
    {
        // A small old/new server fixture tests negotiation without executing any commands twice.
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        int version = 0, legacyCalls = 0, receiptCalls = 0, response = 0;
        Guid room = Guid.NewGuid();
        app.MapGet("/health", () => version == 0
            ? (object)new { protocol = 1, rules = GameSimulation.RulesVersion, roomStream = 1, mode = "realm", characterFileVersion = 2 }
            : new ServerInfo(1, GameSimulation.RulesVersion, 1, "realm", 2, version));
        app.MapPost("/v1/rooms/{id:guid}/input", (Guid id, InputRequest input) =>
        {
            legacyCalls++;
            return new RoomView(id, "Legacy", true, true, 1, input.Sequence + 1, 4, "", [], [], []);
        });
        app.MapPost("/v1/rooms/{id:guid}/input/ack", (Guid id, InputRequest input) =>
        {
            receiptCalls++;
            if (response == 5) return Microsoft.AspNetCore.Http.Results.StatusCode(503);
            return Microsoft.AspNetCore.Http.Results.Json(new InputReceipt(response == 1 ? Guid.NewGuid() : id,
                response == 2 ? 0u : 1u, response == 3 ? input.Sequence : input.Sequence + 1, response == 4 ? -1 : 4));
        });
        await app.StartAsync();
        try
        {
            using var client = new OnlineClient(app.Urls.Single());
            foreach (int capability in new[] { 0, 2, 1 })
            {
                version = capability; await client.CheckServer();
                var ack = await client.SendInput(room, new(1, CommandKind.SetMove));
                if (ack != new InputReceipt(room, 1, 2, 4) || client.SupportsInputReceipts != (version == 1))
                    throw new Exception("Input capability negotiation failed.");
            }
            for (response = 1; response <= 5; response++)
            {
                try { await client.SendInput(room, new(1, CommandKind.SetMove)); }
                catch (InvalidDataException) when (response < 5) { continue; }
                catch (HttpRequestException error) when (response == 5 && error.StatusCode == HttpStatusCode.ServiceUnavailable) { continue; }
                throw new Exception("Invalid input receipt accepted.");
            }
            if (legacyCalls != 2 || receiptCalls != 6) throw new Exception("A possibly accepted command was retried.");
            Console.WriteLine("INPUT RECEIPT PASS legacy/missing-and-unknown-capability/invalid-response/no-retry");
        }
        finally { await app.StopAsync(); }
    }
}
