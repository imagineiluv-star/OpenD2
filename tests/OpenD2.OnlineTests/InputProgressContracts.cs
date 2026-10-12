using System.Text.Json;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

internal static class InputProgressContracts
{
    private static void Check(bool yes, string reason) { if (!yes) throw new Exception("Input progress: " + reason); }
    internal static void Run()
    {
        foreach (string mode in new[] { "realm", "open" }) Verify(mode);
    }
    private static void Verify(string mode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "opend2-progress-" + Guid.NewGuid().ToString("N"));
        const string password = "progress-contract-password";
        var time = new TestTime(); Guid room, character;
        try
        {
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("progress-host", password), true);
                var guest = realm.Login(new("progress-guest", password), true);
                character = realm.CreateCharacter(host.Token, "ProgressHost").Id;
                var peer = realm.CreateCharacter(guest.Token, "ProgressGuest").Id;
                room = realm.CreateRoom(host.Token, new(character, "Progress", "")).Id;
                realm.Join(guest.Token, room, new(peer, ""));
                RoomView View(string? token = null) => realm.StreamState(token ?? host.Token, room, interest: true, inputProgress: true);
                Check(View().ProcessedSequence == 0, "lobby starts at zero");
                realm.Start(host.Token, room);
                var first = View();
                realm.AcknowledgeInput(host.Token, room, new(10, CommandKind.SetMove, -1));
                realm.BatchInput(host.Token, room, new([new(11, CommandKind.SetMove, 1), new(12, CommandKind.SetMove, -1)]));
                var queued = View();
                Check(queued.ProcessedSequence == 0 && queued.NextSequence == 13 && queued.Tick == first.Tick, "accepted batch and sequence gaps are not consumed");
                Check(queued.Entities.SequenceEqual(first.Entities), "acceptance does not step actors");
                var legacy = realm.StreamState(host.Token, room, interest: true);
                Check(legacy.ProcessedSequence is null && realm.State(host.Token, room).ProcessedSequence is null &&
                    !JsonSerializer.Serialize(legacy).Contains("ProcessedSequence"), "old stream and HTTP omit new field");
                realm.Tick();
                var applied = View();
                Check(applied.ProcessedSequence == 12 && applied.Entities[0].Position.X == 736, "next tick consumes all same-tick inputs once");
                Check(View(guest.Token).ProcessedSequence == 0, "recipient cursor isolated");
                Check(applied.StateHash == View(guest.Token).StateHash, "cursor does not affect shared authoritative hash");
                time.Advance(1); realm.Tick();
                Check(View().ProcessedSequence == 12 && View().Entities[0].MoveX == 0, "server lease stop never advances client cursor");
                realm.Input(host.Token, room, new(20, CommandKind.SetMove, 1));
                Check(View().ProcessedSequence == 12, "legacy input is also queued");
                realm.Tick(); Check(View().ProcessedSequence == 20, "legacy input consumed");
                try { realm.BatchInput(host.Token, room, new([new(21, CommandKind.SetMove), new(22, CommandKind.SetMove, 999)])); throw new Exception("Invalid batch accepted"); }
                catch (Rejected e) when (e.Status == 400) { }
                Check(View().ProcessedSequence == 20 && View().NextSequence == 21, "rejection leaves both cursors unchanged");
                realm.AcknowledgeInput(host.Token, room, new(21, CommandKind.SetMove, -1));
                long tick = View().Tick;
                time.Advance(16); realm.Tick();
                Check(View().Tick == tick && View().ProcessedSequence == 21 && View().Entities[0].MoveX == 0, "idle discard retires input without executing it");
                realm.AcknowledgeInput(host.Token, room, new(22, CommandKind.SetMove, -1));
                Check(View().ProcessedSequence == 21, "pending restart input not yet consumed");
                realm.Checkpoint();
            }
            using (var realm = new Realm(folder, time, mode))
            {
                var host = realm.Login(new("progress-host", password), false);
                realm.Join(host.Token, room, new(character, ""));
                var restored = realm.StreamState(host.Token, room, inputProgress: true);
                Check(restored.ProcessedSequence == 22 && restored.NextSequence == 23 && restored.Entities[0].MoveX == 0, "restart discards saved pending input and retires it");
                realm.AcknowledgeInput(host.Token, room, new(23, CommandKind.SetMove, -1)); realm.Tick();
                Check(realm.StreamState(host.Token, room, inputProgress: true).ProcessedSequence == 23, "new input works after restart");
            }
            Console.WriteLine($"INPUT PROGRESS PASS {mode} queued/applied/batch/gaps/recipient/lease/rejection/idle/restart/legacy/hash");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
