using OpenD2.Online;
using OpenD2.Server;

internal static class TimedInviteContracts
{
    internal static void Run()
    {
        foreach (string mode in new[] { "realm", "open" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-timed-invite-" + Guid.NewGuid().ToString("N"));
            const string password = "test-invite-password";
            var clock = new TestTime();
            Guid savedRoom = default, savedGuest = default;
            string restartSecret = "";
            void Reject(int status, Action action)
            {
                try { action(); } catch (Rejected error) when (error.Status == status) { return; }
                throw new Exception("Timed invite admission was not rejected: " + status);
            }
            try
            {
                using (var realm = new Realm(folder, clock, mode))
                {
                    var users = Enumerable.Range(0, 5).Select(i => realm.Login(new("timeduser" + i, password), true)).ToArray();
                    var chars = users.Select((u, i) => realm.CreateCharacter(u.Token, "TimedHero" + i)).ToArray();
                    string host = users[0].Token;
                    RoomView Create() => realm.CreateRoom(host, new(chars[0].Id, "TimedRoom", password));
                    var room = Create();
                    Reject(401, () => realm.IssueInvitation("", room.Id));
                    Reject(403, () => realm.IssueInvitation(users[1].Token, room.Id));
                    var old = realm.IssueInvitation(host, room.Id);
                    var current = realm.IssueInvitation(host, room.Id);
                    if (current.ExpiresAt != clock.GetUtcNow().AddMinutes(10) || old.Token == current.Token) throw new Exception("Invitation lifetime or rotation failed.");
                    Reject(403, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, "", old.Token)));
                    Reject(403, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, "", "not-a-token")));
                    Reject(404, () => realm.Join(users[1].Token, room.Id, new(chars[0].Id, "", current.Token)));
                    var otherRoom = realm.CreateRoom(users[2].Token, new(chars[2].Id, "OtherRoom", password));
                    Reject(403, () => realm.Join(users[1].Token, otherRoom.Id, new(chars[1].Id, password, current.Token)));
                    realm.Close(users[2].Token, otherRoom.Id);
                    realm.RevokeInvitation(host, room.Id);
                    Reject(403, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, password, current.Token)));
                    current = realm.IssueInvitation(host, room.Id); clock.Advance(600);
                    Reject(403, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, "", current.Token)));
                    current = realm.IssueInvitation(host, room.Id); clock.Advance(599);
                    int successes = 0, failures = 0;
                    Parallel.For(1, 3, i =>
                    {
                        try { realm.Join(users[i].Token, room.Id, new(chars[i].Id, "", current.Token)); Interlocked.Increment(ref successes); }
                        catch (Rejected error) when (error.Status == 403) { Interlocked.Increment(ref failures); }
                    });
                    if (successes != 1 || failures != 1) throw new Exception("Invitation was not single-use under concurrent admission.");
                    var joined = realm.State(host, room.Id).Members.Single(m => m.Character != chars[0].Id);
                    int winner = Array.FindIndex(chars, c => c.Id == joined.Character);
                    realm.Join(users[winner].Token, room.Id, new(chars[winner].Id, ""));
                    Reject(403, () => realm.RevokeInvitation(users[winner].Token, room.Id));
                    realm.Close(host, room.Id);

                    room = Create(); current = realm.IssueInvitation(host, room.Id);
                    for (int i = 1; i <= 3; i++) realm.Join(users[i].Token, room.Id, new(chars[i].Id, password));
                    Reject(409, () => realm.IssueInvitation(host, room.Id));
                    Reject(409, () => realm.Join(users[4].Token, room.Id, new(chars[4].Id, "", current.Token)));
                    realm.Leave(users[3].Token, room.Id);
                    realm.Join(users[4].Token, room.Id, new(chars[4].Id, "", current.Token)); // failed admission did not spend it
                    realm.Close(host, room.Id);

                    room = Create(); realm.Join(users[1].Token, room.Id, new(chars[1].Id, password));
                    current = realm.IssueInvitation(host, room.Id); realm.Leave(host, room.Id);
                    Reject(403, () => realm.Join(users[2].Token, room.Id, new(chars[2].Id, "", current.Token)));
                    realm.Close(users[1].Token, room.Id);
                    room = Create(); current = realm.IssueInvitation(host, room.Id); realm.Start(host, room.Id);
                    Reject(409, () => realm.IssueInvitation(host, room.Id));
                    Reject(403, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, "", current.Token)));
                    realm.Close(host, room.Id);
                    Reject(404, () => realm.Join(users[1].Token, room.Id, new(chars[1].Id, "", current.Token)));
                    room = Create(); savedRoom = room.Id; savedGuest = chars[1].Id;
                    restartSecret = realm.IssueInvitation(host, room.Id).Token;
                    realm.Checkpoint(host, room.Id);
                    if (File.ReadAllText(Path.Combine(folder, "realm.json")).Contains(restartSecret, StringComparison.Ordinal))
                        throw new Exception("Invitation secret persisted.");
                }
                using (var restarted = new Realm(folder, clock, mode))
                {
                    var guest = restarted.Login(new("timeduser1", password), false);
                    Reject(403, () => restarted.Join(guest.Token, savedRoom, new(savedGuest, "", restartSecret)));
                }
                Console.WriteLine("TIMED INVITE PASS " + mode + " rotation/revocation/expiry/concurrent-single-use/admission/host-transfer/start/close/restart/no-secret-storage");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
