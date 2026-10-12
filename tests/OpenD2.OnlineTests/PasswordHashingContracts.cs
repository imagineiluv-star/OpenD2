using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-03e: password hashing (PBKDF2, 600,000 iterations) takes tens to hundreds of milliseconds and must not run under the
// realm lock that Tick, input and stream reads share. The internal BeforeHashing seam holds a login at the point where it
// hashes; everything else must keep running, and the lock-free window must not let two registrations of one name both win.
internal static class PasswordHashingContracts
{
    private const string Password = "password-hashing-contract-password";
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Password hashing: " + reason); }
    private static async Task Within(TimeSpan limit, string name, Action action)
    {
        var task = Task.Run(action);
        if (await Task.WhenAny(task, Task.Delay(limit)) != task) throw new Exception($"Password hashing: {name} blocked behind a login that was hashing");
        await task;
    }
    private static void Reject(int status, Action action)
    {
        try { action(); } catch (Rejected error) when (error.Status == status) { return; }
        throw new Exception("Password hashing: expected rejection " + status);
    }

    internal static async Task Run()
    {
        foreach (string mode in new[] { "realm", "open" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-hashing-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var realm = new Realm(folder, new TestTime(), mode);
                var host = realm.Login(new("hashing-host", Password), true).Token;
                var hero = realm.CreateCharacter(host, "Hashing").Id;
                var room = realm.CreateRoom(host, new(hero, "Hashing", "")).Id; realm.Start(host, room);
                var limit = TimeSpan.FromSeconds(3);
                async Task RoomStaysLive()
                {
                    await Within(limit, "Tick", () => { for (int i = 0; i < 5; i++) realm.Tick(); });
                    await Within(limit, "State", () => realm.State(host, room));
                    await Within(limit, "Input", () => realm.Input(host, room, new(realm.State(host, room).NextSequence, CommandKind.SetMove, 1)));
                    await Within(limit, "CreateCharacter", () => realm.CreateCharacter(host, "Busy" + Guid.NewGuid().ToString("N")[..8]));
                }

                // 1. A registration that is hashing does not stall the room, and completes normally afterwards.
                {
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    realm.BeforeHashing = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("hash was never released"); };
                    var login = Task.Run(() => realm.Login(new("newcomer", Password), true));
                    Check(entered.Wait(TimeSpan.FromSeconds(10)), "registration never reached its hash");
                    await RoomStaysLive();
                    Check(!login.IsCompleted, "registration finished although its hash was held");
                    release.Set();
                    var session = await login.WaitAsync(TimeSpan.FromSeconds(10));
                    realm.BeforeHashing = null;
                    Check(realm.Characters(session.Token).Length == 0, "the new account works after its held registration finished");
                }

                // 2. The same for an ordinary login; the verdict still depends on the password.
                {
                    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
                    realm.BeforeHashing = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("hash was never released"); };
                    var wrong = Task.Run(() => realm.Login(new("newcomer", "not-the-right-password"), false));
                    Check(entered.Wait(TimeSpan.FromSeconds(10)), "login never reached its hash");
                    await RoomStaysLive();
                    release.Set();
                    try { await wrong.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Password hashing: wrong password accepted"); }
                    catch (Rejected error) when (error.Status == 401) { }
                    realm.BeforeHashing = null;
                    Check(realm.Login(new("newcomer", Password), false).Username == "newcomer", "the right password still logs in");
                    Reject(401, () => realm.Login(new("nobody-here", Password), false));
                }

                // 3. Two registrations of one name hash at the same time: exactly one wins, the other gets the usual 409.
                {
                    using var release = new ManualResetEventSlim(); int arrived = 0;
                    realm.BeforeHashing = () => { Interlocked.Increment(ref arrived); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("hash was never released"); };
                    var first = Task.Run(() => realm.Login(new("contested", Password), true));
                    var second = Task.Run(() => realm.Login(new("contested", Password), true));
                    for (int i = 0; i < 1000 && Volatile.Read(ref arrived) < 2; i++) await Task.Delay(10);
                    Check(Volatile.Read(ref arrived) == 2, "both registrations should be hashing at once");
                    release.Set();
                    var outcomes = new List<string>();
                    foreach (var task in new[] { first, second })
                        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); outcomes.Add("ok"); } catch (Rejected error) { outcomes.Add(error.Status.ToString()); }
                    realm.BeforeHashing = null;
                    Check(outcomes.Order().SequenceEqual(["409", "ok"]), "exactly one concurrent registration may win, got " + string.Join(",", outcomes));
                    Check(realm.Login(new("contested", Password), false).Username == "contested", "the winning account is the only one");
                }

                // 4. A name that is already taken is refused before any hash is computed (cheap 409 path).
                {
                    int hashes = 0; realm.BeforeHashing = () => Interlocked.Increment(ref hashes);
                    Reject(409, () => realm.Login(new("contested", Password), true));
                    realm.BeforeHashing = null;
                    Check(hashes == 0, "registering a taken name must not pay for a hash");
                }
                Console.WriteLine($"PASSWORD HASHING PASS {mode} hash-outside-lock/room-stays-live/verdict-unchanged/concurrent-registration-single-winner/cheap-409");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
