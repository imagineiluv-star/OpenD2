using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

if (args is ["--tls-server", var server, "--fixtures", var fixtures, "--output", var output])
{
    await TlsContracts.Run(Path.GetFullPath(server), Path.GetFullPath(fixtures), Path.GetFullPath(output));
    return;
}
if (args.Length != 0) throw new ArgumentException("Unknown online test arguments.");

static void Check(bool yes, string message) { if (!yes) throw new Exception(message); Console.WriteLine("PASS " + message); }
static void Reject(int code, Action action)
{
    try { action(); throw new Exception("Expected rejection " + code); } catch (Rejected r) when (r.Status == code) { }
}
string directory = Path.Combine(Path.GetTempPath(), "opend2-realm-test-" + Guid.NewGuid().ToString("N"));
var time = new TestTime(); Guid roomId, firstId; RoomView saved;
const string password = "unit-test-only-password";
try
{
    using (var realm = new Realm(directory, time))
    {
        var a = realm.Login(new("alice", password), true); var b = realm.Login(new("bob", password), true); var other = realm.Login(new("eve", password), true);
        Reject(401, () => realm.Login(new("alice", "wrong-password-value"), false));
        Reject(409, () => realm.Login(new("ALICE", password), true));
        Reject(401, () => realm.Characters("invalid-token"));
        var ca = realm.CreateCharacter(a.Token, "Warrior"); firstId = ca.Id; var cb = realm.CreateCharacter(b.Token, "Ranger");
        Reject(404, () => realm.DeleteCharacter(b.Token, ca.Id));
        var room = realm.CreateRoom(a.Token, new(ca.Id, "Arena", password)); roomId = room.Id;
        Reject(403, () => realm.Join(b.Token, roomId, new(cb.Id, "wrong-room-password")));
        realm.Join(b.Token, roomId, new(cb.Id, password));
        Reject(403, () => realm.State(other.Token, roomId)); Reject(403, () => realm.Start(b.Token, roomId));
        Reject(409, () => realm.DeleteCharacter(a.Token, ca.Id));
        realm.Start(a.Token, roomId);
        var initial = realm.State(a.Token, roomId);
        realm.Input(a.Token, roomId, new(1, CommandKind.SetMove, 1, 0)); realm.Tick();
        var after = realm.State(a.Token, roomId); var peer = realm.State(b.Token, roomId);
        Check(after.Entities.Single(e => e.Id.Value == 1).Position.X == initial.Entities.Single(e => e.Id.Value == 1).Position.X + 32, "server executes owned movement");
        Check(after.Entities.Single(e => e.Id.Value == 2).Position == initial.Entities.Single(e => e.Id.Value == 2).Position, "peer cannot be moved through own input");
        Check(after.StateHash == peer.StateHash && after.Tick == peer.Tick, "two clients observe identical authoritative state");
        Reject(409, () => realm.Input(a.Token, roomId, new(1, CommandKind.SetMove, -1, 0)));
        Reject(400, () => realm.Input(a.Token, roomId, new(2, CommandKind.SetMove, 999, 0)));
        time.Advance(1); realm.Tick();
        Check(realm.State(a.Token, roomId).Entities.Single(e => e.Id.Value == 1).MoveX == 0, "expired movement lease stops disconnected actor");
        for (int i = 0; i < 80; i++)
        {
            var view = realm.State(a.Token, roomId);
            if (view.Entities.Single(e => e.Id.Value == 100).IsAlive && view.Entities.Single(e => e.Id.Value == 1).IsAlive)
                realm.Input(a.Token, roomId, new(view.NextSequence, CommandKind.CastSkill, Target: 100));
            realm.Tick();
        }
        Check(realm.State(a.Token, roomId).Entities.Single(e => e.Id.Value == 100).Health < 60, "server resolves combat damage");
        var renewed = realm.Login(new("alice", password), false); Reject(401, () => realm.State(a.Token, roomId));
        var rejoined = realm.Join(renewed.Token, roomId, new(ca.Id, ""));
        Check(rejoined.Actor == 1 && rejoined.NextSequence > 1, "relogin preserves actor and sequence while revoking old session");
        realm.Checkpoint(renewed.Token, roomId); saved = realm.State(renewed.Token, roomId);
        var disk = File.ReadAllText(Path.Combine(directory, "realm.json"));
        Check(!disk.Contains(password) && !disk.Contains(renewed.Token), "passwords and session tokens are not persisted in plaintext");
        realm.Logout(b.Token); Reject(401, () => realm.Characters(b.Token));
    }
    using (var realm = new Realm(directory, time))
    {
        var a = realm.Login(new("alice", password), false);
        var room = realm.Join(a.Token, roomId, new(firstId, ""));
        Check(room.Tick == saved.Tick && room.Entities.SequenceEqual(saved.Entities.Select(e => e with { MoveX = 0, MoveY = 0 })) && room.Items.SequenceEqual(saved.Items), "server restart restores room actors combat and items");
        Check(realm.Characters(a.Token).Single().Id == firstId, "character identity survives restart");
        realm.Close(a.Token, roomId); Reject(404, () => realm.Close(a.Token, roomId)); realm.DeleteCharacter(a.Token, firstId);
        Check(realm.Characters(a.Token).Length == 0, "room close and character deletion are durable and non-repeatable");
        Directory.CreateDirectory(Path.Combine(directory, "realm.json.tmp"));
        try { realm.CreateCharacter(a.Token, "Failure"); throw new Exception("Expected I/O error"); } catch (UnauthorizedAccessException) { } catch (IOException) { }
        Reject(503, () => realm.Characters(a.Token));
        Console.WriteLine("PASS storage write failure closes admission");
    }
    using (var realm = new Realm(directory, time))
    {
        var a = realm.Login(new("alice", password), false);
        Check(realm.Characters(a.Token).Length == 0, "failed mutation was not committed to disk");
    }
    Console.WriteLine("ONLINE CONTRACTS PASS");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

sealed class TestTime : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
