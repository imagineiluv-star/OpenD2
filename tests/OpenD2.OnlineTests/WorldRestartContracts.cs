using System.Text.Json;
using System.Text.Json.Nodes;
using OpenD2.Core;
using OpenD2.Online;
using OpenD2.Server;

// M5-02c: what a cooperative world room promises across a server restart.
//   * Graceful stop saves, then discards accepted-but-unticked input: a portal that did not run is not replayed.
//   * A killed process (here: dispose without a checkpoint) returns the room to its last save. The client's belief
//     that it took a portal is wrong after that; it must fail closed (409) and recover from server state alone.
//   * Quest credit is saved with the game, so a rollback un-completes the quest and its credit together.
internal static class WorldRestartContracts
{
    private const string Password = "world-restart-contract-password";
    private static readonly WorldContent Content = CooperativeWorld.Content;
    private static void Check(bool value, string reason) { if (!value) throw new Exception("World restart: " + reason); }
    private static void Reject(int status, Action action)
    {
        try { action(); } catch (Rejected e) when (e.Status == status) { return; }
        throw new Exception("World restart: expected rejection " + status);
    }
    private static RoomView View(Realm realm, string token, Guid room) => realm.StreamState(token, room, inputProgress: true, world: true);
    private static EntityState Actor(RoomView view, uint id) => view.Entities.Single(e => e.Id.Value == id);
    private static InputRequest Input(RoomView view, CommandKind kind, int x = 0, uint target = 0) =>
        new(view.NextSequence, kind, x, Target: target, Region: Actor(view, view.Actor).Region.Value, RegionEpoch: view.World!.RegionEpoch);

    internal static void Run()
    {
        foreach (string mode in new[] { "realm", "open" })
        {
            string folder = Path.Combine(Path.GetTempPath(), "opend2-world-restart-" + Guid.NewGuid().ToString("N"));
            try
            {
                Gracefully(Path.Combine(folder, "graceful"), mode);
                Killed(Path.Combine(folder, "killed"), mode);
                QuestCredit(Path.Combine(folder, "quest"), mode);
                Console.WriteLine($"WORLD RESTART PASS {mode} graceful-discard/kill-rollback/fail-closed/exactly-once/quest-credit-atomic");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    // Two members, started and saved. Returns what a restart needs to log back in.
    private static (Guid Room, Guid Host, Guid Guest) Create(string folder, TimeProvider time, string mode)
    {
        using var realm = new Realm(folder, time, mode);
        var host = realm.Login(new("restart-host", Password), true).Token;
        var guest = realm.Login(new("restart-guest", Password), true).Token;
        var hostHero = realm.CreateCharacter(host, "RestartHost").Id; var guestHero = realm.CreateCharacter(guest, "RestartGuest").Id;
        var room = realm.CreateRoom(host, new(hostHero, "Restart", "", Content)).Id;
        realm.Join(guest, room, new(guestHero, "", World: Content)); realm.Start(host, room);
        return (room, hostHero, guestHero);
    }
    private static (string Host, string Guest) Reopen(Realm realm, (Guid Room, Guid Host, Guid Guest) ids)
    {
        string host = realm.Login(new("restart-host", Password), false).Token, guest = realm.Login(new("restart-guest", Password), false).Token;
        Reject(409, () => realm.State(host, ids.Room)); // a new session must state the world content again
        realm.Join(host, ids.Room, new(ids.Host, "", World: Content)); realm.Join(guest, ids.Room, new(ids.Guest, "", World: Content));
        return (host, guest);
    }

    // Accepted but not yet ticked input is retired on restart. The portal did not run and must not run later.
    private static void Gracefully(string folder, string mode)
    {
        var time = new TestTime(); var ids = Create(folder, time, mode);
        using (var realm = new Realm(folder, time, mode))
        {
            var (host, _) = Reopen(realm, ids);
            realm.Input(host, ids.Room, Input(View(realm, host, ids.Room), CommandKind.Interact, target: 200));
            realm.Checkpoint(); // the shutdown path: pending input is part of the save
        }
        using (var realm = new Realm(folder, time, mode))
        {
            var (host, guest) = Reopen(realm, ids);
            var restored = View(realm, host, ids.Room);
            Check(restored.NextSequence == 2 && restored.ProcessedSequence == 1, "accepted input is retired, not left pending");
            Check(Actor(restored, 1).Region.Value == 1 && restored.World!.RegionEpoch == 0, "the unticked portal did not run");
            for (int i = 0; i < 5; i++) realm.Tick();
            var later = View(realm, host, ids.Room);
            Check(Actor(later, 1).Region.Value == 1 && later.World!.RegionEpoch == 0, "a retired portal never runs after restart");
            Check(View(realm, guest, ids.Room).StateHash == later.StateHash, "both members observe the same room after restart");

            realm.Input(host, ids.Room, Input(later, CommandKind.Interact, target: 200)); realm.Tick();
            var taken = View(realm, host, ids.Room);
            Check(Actor(taken, 1).Region.Value == 2 && taken.World!.RegionEpoch > 0, "re-issued portal runs once");
            Reject(409, () => realm.Input(host, ids.Room, Input(later, CommandKind.Interact, target: 200))); // same input again: stale
            for (int i = 0; i < 5; i++) realm.Tick();
            Check(View(realm, host, ids.Room).World!.RegionEpoch == taken.World!.RegionEpoch, "the visit is not repeated");
        }
    }

    // A killed process leaves the last save. A client that saw the portal must be refused, then recover.
    private static void Killed(string folder, string mode)
    {
        var time = new TestTime(); var ids = Create(folder, time, mode);
        long epoch;
        using (var realm = new Realm(folder, time, mode)) // killed: no checkpoint
        {
            var (host, _) = Reopen(realm, ids);
            realm.Input(host, ids.Room, Input(View(realm, host, ids.Room), CommandKind.Interact, target: 200)); realm.Tick();
            var taken = View(realm, host, ids.Room);
            Check(Actor(taken, 1).Region.Value == 2, "the portal ran before the kill");
            epoch = taken.World!.RegionEpoch;
        }
        using (var realm = new Realm(folder, time, mode))
        {
            var (host, _) = Reopen(realm, ids);
            var rolledBack = View(realm, host, ids.Room);
            Check(Actor(rolledBack, 1).Region.Value == 1 && rolledBack.World!.RegionEpoch == 0 && rolledBack.NextSequence == 1 && rolledBack.Tick == 0,
                "an unsaved portal and its input cursor are rolled back to the last save");
            // The client still believes it is in the Field at its old visit epoch.
            Reject(409, () => realm.Input(host, ids.Room, new(rolledBack.NextSequence, CommandKind.SetMove, 1, Region: 2, RegionEpoch: epoch)));
            Check(View(realm, host, ids.Room).NextSequence == rolledBack.NextSequence, "the refused input changed nothing");
            // Recovering from server state alone, the portal runs exactly once.
            realm.Input(host, ids.Room, Input(rolledBack, CommandKind.Interact, target: 200)); realm.Tick();
            var again = View(realm, host, ids.Room);
            Check(Actor(again, 1).Region.Value == 2 && again.World!.RegionEpoch > 0, "the portal can be taken again after the rollback");
            realm.Checkpoint();
            epoch = again.World!.RegionEpoch;
        }
        using (var realm = new Realm(folder, time, mode)) // a saved visit survives; later unsaved input does not
        {
            var (host, _) = Reopen(realm, ids);
            var saved = View(realm, host, ids.Room);
            Check(Actor(saved, 1).Region.Value == 2 && saved.World!.RegionEpoch == epoch, "a saved portal and its visit epoch survive");
            realm.Input(host, ids.Room, Input(saved, CommandKind.SetMove, 1)); realm.Tick();
            Check(Actor(View(realm, host, ids.Room), 1).Position.X > Actor(saved, 1).Position.X, "movement after the save ran");
        }
        using (var realm = new Realm(folder, time, mode))
        {
            var (host, _) = Reopen(realm, ids);
            var after = View(realm, host, ids.Room);
            Check(Actor(after, 1).Region.Value == 2 && after.World!.RegionEpoch == epoch && after.NextSequence == 2, "unsaved movement is lost, the visit is kept");
            Reject(409, () => realm.Input(host, ids.Room, new(after.NextSequence - 1, CommandKind.SetMove, 1, Region: 2, RegionEpoch: epoch))); // old cursor
            realm.Input(host, ids.Room, Input(after, CommandKind.SetMove, 1)); // the lost input may simply be sent again
        }
    }

    // Completion and its credit share one save. A rollback undoes both, so the record is earned exactly once.
    private static void QuestCredit(string folder, string mode)
    {
        var time = new TestTime(); Guid room; var heroes = new Guid[3];
        string[] names = ["restart-quest-a", "restart-quest-b", "restart-quest-c"];
        using (var realm = new Realm(folder, time, mode))
        {
            var tokens = names.Select(n => realm.Login(new(n, Password), true).Token).ToArray();
            for (int i = 0; i < 3; i++) heroes[i] = realm.CreateCharacter(tokens[i], "Quest" + "ABC"[i]).Id;
            room = realm.CreateRoom(tokens[0], new(heroes[0], "Quest", "", Content)).Id;
            for (int i = 1; i < 3; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: Content));
            realm.Start(tokens[0], room); realm.Checkpoint(tokens[0], room);
        }
        string file = Path.Combine(folder, "realm.json");
        var json = JsonNode.Parse(File.ReadAllBytes(file))!;
        json["Rooms"]![0]!["Game"] = PersonalQuestContracts.ReadyGame();
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(json));

        string[] Login(Realm realm)
        {
            var tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            for (int i = 0; i < 3; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: Content));
            return tokens;
        }
        void Complete(Realm realm, string[] tokens)
        {
            // Quests are personal: the two living members each hand in their own; the dead third cannot.
            for (int i = 0; i < 2; i++)
            {
                var view = View(realm, tokens[i], room);
                Check(view.World!.Quest.Stage == QuestStage.ReadyToTurnIn, "the quest awaits turn-in");
                realm.Input(tokens[i], room, Input(view, CommandKind.Interact, target: 300));
            }
            realm.Tick();
            Check(View(realm, tokens[0], room).World!.Quest.Stage == QuestStage.Completed && View(realm, tokens[1], room).World!.Quest.Stage == QuestStage.Completed,
                "both quests complete");
        }
        using (var realm = new Realm(folder, time, mode)) // completes, then is killed before any save
        {
            var tokens = Login(realm); Complete(realm, tokens);
        }
        using (var realm = new Realm(folder, time, mode))
        {
            var tokens = Login(realm);
            Check(View(realm, tokens[0], room).World!.Quest.Stage == QuestStage.ReadyToTurnIn, "the unsaved completion is rolled back");
            realm.Close(tokens[0], room);
            Check(tokens.All(t => realm.Characters(t).Single().Quests is null && realm.Characters(t).Single().Victories == 0),
                "a rolled-back completion leaves no record and no victory");
        }
        // Same sequence again, this time saved: exactly one record.
        using (var realm = new Realm(folder, time, mode))
        {
            var tokens = names.Select(n => realm.Login(new(n, Password), false).Token).ToArray();
            room = realm.CreateRoom(tokens[0], new(heroes[0], "Quest2", "", Content)).Id;
            for (int i = 1; i < 3; i++) realm.Join(tokens[i], room, new(heroes[i], "", World: Content));
            realm.Start(tokens[0], room); realm.Checkpoint(tokens[0], room);
        }
        json = JsonNode.Parse(File.ReadAllBytes(file))!;
        json["Rooms"]![0]!["Game"] = PersonalQuestContracts.ReadyGame();
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(json));
        using (var realm = new Realm(folder, time, mode))
        {
            var tokens = Login(realm); Complete(realm, tokens); realm.Checkpoint();
        }
        using (var realm = new Realm(folder, time, mode))
        {
            var tokens = Login(realm);
            Check(View(realm, tokens[0], room).World!.Quest.Stage == QuestStage.Completed, "the saved completion survives");
            realm.Close(tokens[0], room);
            var records = tokens.Select(t => realm.Characters(t).Single()).ToArray();
            Check(records[0].Quests!.SequenceEqual([new QuestRecord(Content.Id, 1)]) && records[1].Quests!.SequenceEqual([new QuestRecord(Content.Id, 1)]) && records[2].Quests is null,
                "exactly one record for the living members after a kill and a replay");
        }
    }
}
